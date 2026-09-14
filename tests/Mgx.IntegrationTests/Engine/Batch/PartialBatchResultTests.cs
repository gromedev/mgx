using System.Management.Automation;
using System.Management.Automation.Host;
using System.Net;
using Mgx.Engine.Http;
using Mgx.Engine.Models;

namespace Mgx.IntegrationTests;

/// <summary>
/// A batch larger than Graph's 20-request limit is split into chunks and sent one after another.
/// If a later chunk's POST fails outright, the earlier chunks have already been applied - the
/// writes happened, on the server. Losing their results loses the only record of what landed,
/// which is exactly what a caller needs in order to know what to do next.
/// </summary>
[Collection("Pipeline")]
public class PartialBatchResultTests
{
    private static string BatchResponse(int firstId, int count, int status) =>
        "{ \"responses\": [" + string.Join(",", Enumerable.Range(firstId, count).Select(i =>
            $"{{ \"id\": \"{i}\", \"status\": {status}, \"body\": {{ \"id\": \"u{i}\" }} }}")) + "] }";

    /// <summary>First POST answers a full chunk; every later POST is a hard failure.</summary>
    private sealed class FirstChunkOnlyHandler : HttpMessageHandler
    {
        public int Posts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Posts++;
            if (Posts == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(BatchResponse(1, 20, 200),
                        System.Text.Encoding.UTF8, "application/json")
                });
            }
            // Not a per-item failure inside a 200 envelope - the POST itself fails.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                RequestMessage = request,
                Content = new StringContent(
                    "{\"error\":{\"code\":\"BadRequest\",\"message\":\"batch rejected\"}}",
                    System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public async Task A_failed_chunk_does_not_discard_the_chunks_that_already_succeeded()
    {
        ResiliencePipelineFactory.Reset();
        var wire = new FirstChunkOnlyHandler();
        using var httpClient = new HttpClient(wire);
        using var client = new ResilientGraphClient(httpClient,
            new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 1 });
        var batchClient = new GraphBatchClient(client);

        // 30 operations = two chunks. The first is applied; the second is refused.
        var ops = Enumerable.Range(1, 30)
            .Select(i => new BatchOperation($"/users/u{i}", "PATCH", null))
            .ToList();

        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);

        // The 20 writes in chunk one happened. Their outcomes must survive the second chunk's
        // failure, or the caller cannot tell an applied write from one that never ran.
        var known = result.Results.Count(r => r.Response is not null);
        Assert.True(known >= 20,
            $"only {known} of 30 operations came back; the first chunk's 20 results were discarded");
    }

    /// <summary>
    /// Answers the chunk in part - some items applied, the rest throttled - and then refuses the
    /// retry that carries the throttled ones. The items the first answer covered were applied on
    /// the server whatever happens to the retry.
    /// </summary>
    private sealed class HalfLandsThenRefusesHandler : HttpMessageHandler
    {
        private int _posts;
        public int Posts => Volatile.Read(ref _posts);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _posts) == 1)
            {
                var body = "{ \"responses\": [" + string.Join(",", Enumerable.Range(1, 20).Select(i =>
                    $"{{ \"id\": \"{i}\", \"status\": {(i <= 15 ? 204 : 429)} }}")) + "] }";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                RequestMessage = request,
                Content = new StringContent(
                    "{\"error\":{\"code\":\"BadRequest\",\"message\":\"batch rejected\"}}",
                    System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public async Task A_chunk_whose_retry_is_refused_keeps_the_items_the_server_answered()
    {
        ResiliencePipelineFactory.Reset();
        var wire = new HalfLandsThenRefusesHandler();
        using var httpClient = new HttpClient(wire);
        using var client = new ResilientGraphClient(httpClient,
            new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 1 });
        var batchClient = new GraphBatchClient(client);

        var ops = Enumerable.Range(1, 20)
            .Select(i => new BatchOperation($"/users/u{i}", "PATCH", null))
            .ToList();

        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);

        Assert.NotNull(result.ChunkFailure);
        // The server answered these 15 with 204: it applied them. A later attempt of the same
        // chunk being refused does not unmake that.
        for (int i = 0; i < 15; i++)
            Assert.Equal(204, result.Results[i].Response.Status);
        // The five the refusal carried were sent and may have been applied, so they read as a
        // failure of the request - never as an operation that was not attempted.
        for (int i = 15; i < 20; i++)
            Assert.Equal(400, result.Results[i].Response.Status);
        Assert.Empty(result.NotSent);

        // The chunk that failed is the chunk that met the throttling. Counted only off the
        // chunks that returned normally, a run throttled into a refusal reported no throttling
        // at all - and no retries, and no time spent waiting on either.
        Assert.Equal(5, result.Telemetry.ThrottleEncounters);
        Assert.Equal(5, result.Telemetry.ItemRetries);
        Assert.True(result.Telemetry.TotalRetryDelayMs > 0,
            "the retry delay this chunk waited out was not reported");
    }

    [Fact]
    public void Items_a_refused_chunk_already_had_answers_for_stay_out_of_the_dead_letter_file()
    {
        var deadLetter = Path.Combine(Path.GetTempPath(), $"mgx-dl-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (MgxTransportScope.Inject(new HalfLandsThenRefusesHandler()))
            {
                using var ps = System.Management.Automation.PowerShell.Create();
                ps.AddCommand("Import-Module")
                  .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Batch.InvokeMgxBatchRequest).Assembly);
                ps.Invoke();
                ps.Commands.Clear();
                ps.AddScript("function Get-MgContext { [PSCustomObject]@{ TenantId = 'test-tenant-00000000-0000-0000-0000-000000000000' } }");
                ps.Invoke();
                ps.Commands.Clear();

                ps.AddCommand("Invoke-MgxBatchRequest")
                  .AddParameter("Uri", Enumerable.Range(1, 20).Select(i => $"/users/u{i}").ToArray())
                  .AddParameter("Method", "PATCH")
                  .AddParameter("DeadLetterPath", deadLetter);
                ps.Invoke();
            }

            // The dead-letter file is documented as re-pipeable. A line for a write the server
            // confirmed is an instruction to send it a second time.
            var lines = File.Exists(deadLetter) ? File.ReadAllLines(deadLetter) : [];
            Assert.Equal(5, lines.Length);
            for (int i = 1; i <= 15; i++)
                Assert.DoesNotContain(lines, l => l.Contains($"\"/users/u{i}\"", StringComparison.Ordinal));
            for (int i = 16; i <= 20; i++)
                Assert.Contains(lines, l => l.Contains($"\"/users/u{i}\"", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(deadLetter)) File.Delete(deadLetter);
        }
    }

    /// <summary>
    /// Three chunks: one applied, one refused, one never sent. The counts have to add up to
    /// something a reader can act on - "60 succeeded, 0 failed" over a run that sent one chunk
    /// is worse than no summary at all.
    /// </summary>
    [Fact]
    public async Task Operations_that_were_never_sent_count_as_neither_succeeded_nor_failed()
    {
        ResiliencePipelineFactory.Reset();
        using var httpClient = new HttpClient(new FirstChunkOnlyHandler());
        using var client = new ResilientGraphClient(httpClient,
            new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 1 });
        var batchClient = new GraphBatchClient(client);

        var ops = Enumerable.Range(1, 60)
            .Select(i => new BatchOperation($"/users/u{i}", "PATCH", null))
            .ToList();

        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);

        Assert.Equal(60, result.Telemetry.TotalRequests);
        Assert.Equal(20, result.Telemetry.Succeeded);
        Assert.Equal(20, result.Telemetry.Failed);
        Assert.Equal(20, result.NotSent.Count);
    }

    /// <summary>
    /// Both runs stop, and stopping is not what tells them apart. In the first the refusal came
    /// before the batch-level retry pass, which was then skipped for every candidate. In the
    /// second the pass had already POSTed twenty items and applied fifteen of them when the
    /// follow-up carrying the other five was refused. The line the caller sees turns on which
    /// of those happened, not on the stop they share: told the pass never went out, nobody has
    /// reason to check the tenant before creating fifteen users a second time.
    /// </summary>
    [Fact]
    public void A_retry_pass_that_went_out_is_not_reported_as_one_that_was_withheld()
    {
        var withheld = Say(new FirstChunkOnlyHandler(), 60, "PATCH");
        var sent = Say(new RetryPassLandsThenIsRefusedHandler(), 20, "POST",
            new System.Collections.Hashtable { ["displayName"] = "u" });

        Assert.Contains("the retry pass was withheld", withheld.Warning, StringComparison.Ordinal);
        // Nothing of the pass reached the wire, so the run credits itself with none of it.
        Assert.DoesNotContain("Batch-level retries", withheld.Summary, StringComparison.Ordinal);

        Assert.DoesNotContain("withheld", sent.Warning, StringComparison.Ordinal);
        Assert.Contains("The retry pass was sent and then refused", sent.Warning, StringComparison.Ordinal);
        Assert.Contains(" Batch-level retries: 20.", sent.Summary, StringComparison.Ordinal);

        // Neither run took its items through every attempt they had, so neither may say it did.
        Assert.DoesNotContain("after all retry attempts", withheld.Warning, StringComparison.Ordinal);
        Assert.DoesNotContain("after all retry attempts", sent.Warning, StringComparison.Ordinal);
    }

    /// <summary>Answers every chunk in full; five items of the first come back 404.</summary>
    private sealed class AnswersEveryChunkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = "{ \"responses\": [" + string.Join(",", Enumerable.Range(1, 20).Select(i =>
                $"{{ \"id\": \"{i}\", \"status\": {(i <= 15 ? 200 : 404)} }}")) + "] }";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>
    /// Nothing stopped this run: every chunk was POSTed and answered, and the items that failed
    /// had whatever attempts they qualified for. The warning says so, and says nothing about
    /// operations left unsent, because there were none.
    /// </summary>
    [Fact]
    public void A_run_that_was_not_stopped_still_reports_its_failures_as_retried()
    {
        using (MgxTransportScope.Inject(new AnswersEveryChunkHandler()))
        {
            using var ps = System.Management.Automation.PowerShell.Create();
            ps.AddCommand("Import-Module")
              .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Batch.InvokeMgxBatchRequest).Assembly);
            ps.Invoke();
            ps.Commands.Clear();
            ps.AddScript("function Get-MgContext { [PSCustomObject]@{ TenantId = 'test-tenant-00000000-0000-0000-0000-000000000000' } }");
            ps.Invoke();
            ps.Commands.Clear();

            ps.AddCommand("Invoke-MgxBatchRequest")
              .AddParameter("Uri", Enumerable.Range(1, 20).Select(i => $"/users/u{i}").ToArray())
              .AddParameter("Method", "GET");
            ps.Invoke();

            var warning = Assert.Single(ps.Streams.Warning,
                w => w.Message.Contains("batch items failed", StringComparison.Ordinal)).Message;
            Assert.Contains("5 of 20 batch items failed.", warning, StringComparison.Ordinal);
            Assert.Contains("after all retry attempts", warning, StringComparison.Ordinal);
            Assert.DoesNotContain("were not sent", warning, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The dead-letter file takes the refused and the never-sent alike, and the line that says
    /// what was written has to keep them apart: one may already have been applied on the server
    /// and the other certainly was not, which is the whole of what a caller re-piping the file
    /// is deciding about.
    /// </summary>
    [Fact]
    public void The_dead_letter_line_does_not_call_an_unsent_operation_a_failed_one()
    {
        var deadLetter = Path.Combine(Path.GetTempPath(), $"mgx-dl-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (MgxTransportScope.Inject(new FirstChunkOnlyHandler()))
            {
                using var ps = System.Management.Automation.PowerShell.Create();
                ps.AddCommand("Import-Module")
                  .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Batch.InvokeMgxBatchRequest).Assembly);
                ps.Invoke();
                ps.Commands.Clear();
                ps.AddScript("function Get-MgContext { [PSCustomObject]@{ TenantId = 'test-tenant-00000000-0000-0000-0000-000000000000' } }");
                ps.Invoke();
                ps.Commands.Clear();

                ps.AddCommand("Invoke-MgxBatchRequest")
                  .AddParameter("Uri", Enumerable.Range(1, 60).Select(i => $"/users/u{i}").ToArray())
                  .AddParameter("Method", "PATCH")
                  .AddParameter("DeadLetterPath", deadLetter)
                  .AddParameter("Verbose", true);
                ps.Invoke();

                Assert.Equal(40, File.ReadAllLines(deadLetter).Length);
                Assert.Contains(ps.Streams.Verbose, v =>
                    v.Message.Contains("Wrote 20 failed and 20 not-sent items to dead-letter file",
                        StringComparison.Ordinal));
            }
        }
        finally
        {
            if (File.Exists(deadLetter)) File.Delete(deadLetter);
        }
    }

    /// <summary>
    /// Answers both items of the only chunk and leaves the "status" member off the second - the
    /// truncated-or-rewritten envelope the count check names, one member short instead of one
    /// sub-response short.
    /// </summary>
    private sealed class OmitsAStatusHandler : HttpMessageHandler
    {
        private int _posts;
        public int Posts => Volatile.Read(ref _posts);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _posts);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(
                    "{ \"responses\": [ { \"id\": \"1\", \"status\": 204, \"body\": null }, "
                    + "{ \"id\": \"2\", \"body\": null } ] }",
                    System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>
    /// The POST went out and the server answered it. Whatever the envelope failed to say about
    /// the second write, the one thing it cannot be is a write that never left - which is what a
    /// status deserialized to 0 says, that being NotSentStatus itself.
    /// </summary>
    [Fact]
    public async Task An_item_answered_without_a_status_is_not_handed_back_as_never_sent()
    {
        ResiliencePipelineFactory.Reset();
        var wire = new OmitsAStatusHandler();
        using var httpClient = new HttpClient(wire);
        using var client = new ResilientGraphClient(httpClient,
            new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 1 });
        var batchClient = new GraphBatchClient(client);

        var ops = Enumerable.Range(1, 2)
            .Select(i => new BatchOperation($"/users/u{i}", "PATCH", null))
            .ToList();

        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);

        Assert.Equal(1, wire.Posts);
        Assert.NotEqual(GraphBatchClient.NotSentStatus, result.Results[1].Response.Status);
        Assert.True(result.Results[1].Response.Status >= 400,
            $"a write the server answered came back as {result.Results[1].Response.Status}");
        Assert.Empty(result.NotSent);

        // The item the envelope did answer keeps that answer, as it does for every other shape
        // of chunk failure.
        Assert.Equal(204, result.Results[0].Response.Status);
        Assert.Equal(1, result.Telemetry.Succeeded);
        Assert.Equal(1, result.Telemetry.Failed);

        var failure = Assert.IsType<InvalidOperationException>(result.ChunkFailure);
        Assert.Contains("without a status", failure.Message);
    }

    /// <summary>Answers the first chunk in full, then an envelope that answers nobody.</summary>
    private sealed class SecondChunkGarbledHandler : HttpMessageHandler
    {
        private int _posts;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = Interlocked.Increment(ref _posts) == 1
                ? BatchResponse(1, 20, 200)
                : "{ \"responses\": [ { \"id\": \"1\", \"status\": 200 } ] }";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>
    /// An envelope that does not answer what was sent is a protocol violation, and it is
    /// reported as one. It is not a reason to discard the chunk the server did answer.
    /// </summary>
    [Fact]
    public async Task A_chunk_answered_with_a_garbled_envelope_keeps_the_chunk_before_it()
    {
        ResiliencePipelineFactory.Reset();
        using var httpClient = new HttpClient(new SecondChunkGarbledHandler());
        using var client = new ResilientGraphClient(httpClient,
            new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 1 });
        var batchClient = new GraphBatchClient(client);

        var ops = Enumerable.Range(1, 40)
            .Select(i => new BatchOperation($"/users/u{i}", "PATCH", null))
            .ToList();

        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);

        var failure = Assert.IsType<InvalidOperationException>(result.ChunkFailure);
        Assert.Contains("count mismatch", failure.Message);
        for (int i = 0; i < 20; i++)
            Assert.Equal(200, result.Results[i].Response.Status);
        for (int i = 20; i < 40; i++)
            Assert.Equal(503, result.Results[i].Response.Status);
    }

    /// <summary>
    /// Answers the first chunk, throttles the second through every attempt it has, and then
    /// fails the follow-up batch that carries the throttled items - the retry pass, which runs
    /// once every chunk has been sent.
    /// </summary>
    private sealed class RefusesTheRetryPassHandler(bool garbled) : HttpMessageHandler
    {
        private int _posts;

        private static string Throttled(int count) =>
            "{ \"responses\": [" + string.Join(",", Enumerable.Range(1, count).Select(i =>
                $"{{ \"id\": \"{i}\", \"status\": 429, \"headers\": {{ \"Retry-After\": \"1\" }} }}")) + "] }";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var post = Interlocked.Increment(ref _posts);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (body.Contains("/users/u1\"", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(BatchResponse(1, 20, 204),
                        System.Text.Encoding.UTF8, "application/json")
                };
            }
            // The second chunk's own four attempts; the fifth POST of its items is the
            // follow-up batch.
            if (post <= 5)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(Throttled(20),
                        System.Text.Encoding.UTF8, "application/json")
                };
            }
            // Two shapes of failure: a refusal with a status, and an envelope that answers
            // one of the twenty requests it was sent.
            return garbled
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent("{ \"responses\": [ { \"id\": \"1\", \"status\": 204 } ] }",
                        System.Text.Encoding.UTF8, "application/json")
                }
                : new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    RequestMessage = request,
                    Content = new StringContent(
                        "{\"error\":{\"code\":\"BadRequest\",\"message\":\"batch rejected\"}}",
                        System.Text.Encoding.UTF8, "application/json")
                };
        }
    }

    /// <summary>
    /// The retry pass runs after every chunk has been sent, so by the time it fails the run
    /// holds the outcome of the whole batch. Throwing hands back the failure and nothing else:
    /// twenty writes the server confirmed, and twenty it throttled, are lost with the array.
    /// </summary>
    [Fact]
    public async Task A_refused_retry_pass_keeps_what_the_run_already_knew()
    {
        ResiliencePipelineFactory.Reset();
        using var httpClient = new HttpClient(new RefusesTheRetryPassHandler(garbled: false));
        using var client = new ResilientGraphClient(httpClient,
            new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 1 });
        var batchClient = new GraphBatchClient(client);

        var ops = Enumerable.Range(1, 40)
            .Select(i => new BatchOperation($"/users/u{i}", "PATCH", null))
            .ToList();

        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);

        Assert.IsType<GraphServiceException>(result.ChunkFailure);
        Assert.Equal(40, result.Results.Count);
        // The first chunk was applied. The retried items keep the last thing the server said
        // about them, which is what they are: throttled, not retried, still to be re-submitted.
        for (int i = 0; i < 20; i++)
            Assert.Equal(204, result.Results[i].Response.Status);
        for (int i = 20; i < 40; i++)
            Assert.Equal(429, result.Results[i].Response.Status);
        Assert.Equal(20, result.Telemetry.Succeeded);
        Assert.Equal(20, result.Telemetry.Failed);
        Assert.Empty(result.NotSent);
    }

    /// <summary>
    /// The other shape the retry pass fails in. A truncated envelope is an InvalidOperationException,
    /// which the cmdlet does not catch, so the run's results left EndProcessing unreported: no
    /// output objects, no error records, nothing on the pipeline but the exception.
    /// </summary>
    [Fact]
    public void A_garbled_retry_pass_still_reports_the_run()
    {
        using (MgxTransportScope.Inject(new RefusesTheRetryPassHandler(garbled: true)))
        {
            using var ps = System.Management.Automation.PowerShell.Create();
            ps.AddCommand("Import-Module")
              .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Batch.InvokeMgxBatchRequest).Assembly);
            ps.Invoke();
            ps.Commands.Clear();
            ps.AddScript("function Get-MgContext { [PSCustomObject]@{ TenantId = 'test-tenant-00000000-0000-0000-0000-000000000000' } }");
            ps.Invoke();
            ps.Commands.Clear();

            ps.AddCommand("Invoke-MgxBatchRequest")
              .AddParameter("Uri", Enumerable.Range(1, 40).Select(i => $"/users/u{i}").ToArray())
              .AddParameter("Method", "PATCH");
            var output = ps.Invoke();

            Assert.Equal(40, output.Count);
            Assert.Contains(ps.Streams.Error,
                e => e.FullyQualifiedErrorId.StartsWith("BatchChunkFailed", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Answers the $batch POST with a status Graph never sends. A proxy or a captive portal
    /// between the client and Graph does, and the chunk is refused either way.
    /// </summary>
    private sealed class NotModifiedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified)
            {
                RequestMessage = request,
                Content = new StringContent("<html>not modified</html>",
                    System.Text.Encoding.UTF8, "text/html")
            });
    }

    /// <summary>
    /// A refusal below 400 is still a refusal. Recorded verbatim it put every item of the
    /// chunk in the range a success is counted in, so twenty writes that never reached Graph
    /// were reported as twenty that had.
    /// </summary>
    [Fact]
    public async Task A_chunk_refused_with_a_3xx_gives_its_items_a_failing_status()
    {
        ResiliencePipelineFactory.Reset();
        using var httpClient = new HttpClient(new NotModifiedHandler());
        using var client = new ResilientGraphClient(httpClient,
            new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 1 });
        var batchClient = new GraphBatchClient(client);

        var ops = Enumerable.Range(1, 20)
            .Select(i => new BatchOperation($"/users/u{i}", "PATCH", null))
            .ToList();

        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);

        Assert.NotNull(result.ChunkFailure);
        Assert.All(result.Results, r => Assert.True(r.Response.Status >= 400,
            $"a refused write was recorded as status {r.Response.Status}"));
        Assert.Equal(0, result.Telemetry.Succeeded);
        Assert.Equal(20, result.Telemetry.Failed);
        Assert.Empty(result.NotSent);
    }

    /// <summary>
    /// The dead-letter file keeps nothing under 400, so the same 3xx left the run with no
    /// record of the twenty writes to re-submit.
    /// </summary>
    [Fact]
    public void Items_refused_with_a_3xx_are_written_to_the_dead_letter_file()
    {
        var deadLetter = Path.Combine(Path.GetTempPath(), $"mgx-dl-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (MgxTransportScope.Inject(new NotModifiedHandler()))
            {
                using var ps = System.Management.Automation.PowerShell.Create();
                ps.AddCommand("Import-Module")
                  .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Batch.InvokeMgxBatchRequest).Assembly);
                ps.Invoke();
                ps.Commands.Clear();
                ps.AddScript("function Get-MgContext { [PSCustomObject]@{ TenantId = 'test-tenant-00000000-0000-0000-0000-000000000000' } }");
                ps.Invoke();
                ps.Commands.Clear();

                ps.AddCommand("Invoke-MgxBatchRequest")
                  .AddParameter("Uri", Enumerable.Range(1, 20).Select(i => $"/users/u{i}").ToArray())
                  .AddParameter("Method", "PATCH")
                  .AddParameter("DeadLetterPath", deadLetter);
                ps.Invoke();
            }

            var lines = File.Exists(deadLetter) ? File.ReadAllLines(deadLetter) : [];
            Assert.Equal(20, lines.Length);
        }
        finally
        {
            if (File.Exists(deadLetter)) File.Delete(deadLetter);
        }
    }

    /// <summary>Counts requests; answers an empty batch envelope.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(BatchResponse(1, 2, 200),
                    System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>
    /// The help says of -WhatIf: "Shows what would happen if the cmdlet runs. The cmdlet is not
    /// run." A read-only batch changes nothing on the server, but it still spends resource units,
    /// can be throttled, and emits objects into the pipeline - so running it is not "not run",
    /// and it is not what the caller asked for.
    /// </summary>
    [Fact]
    public void WhatIf_does_not_send_a_read_only_batch()
    {
        var wire = new CountingHandler();
        using (MgxTransportScope.Inject(wire))
        {
            using var ps = System.Management.Automation.PowerShell.Create();
            ps.AddCommand("Import-Module")
              .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Batch.InvokeMgxBatchRequest).Assembly);
            ps.Invoke();
            ps.Commands.Clear();
            ps.AddScript("function Get-MgContext { [PSCustomObject]@{ TenantId = 'test-tenant-00000000-0000-0000-0000-000000000000' } }");
            ps.Invoke();
            ps.Commands.Clear();

            ps.AddCommand("Invoke-MgxBatchRequest")
              .AddParameter("Uri", new[] { "/users/u1", "/users/u2" })
              .AddParameter("Method", "GET")
              .AddParameter("WhatIf", true);
            var output = ps.Invoke();

            Assert.Equal(0, wire.Requests);
            Assert.Empty(output);
        }
    }

    /// <summary>
    /// Throttles a whole chunk until its per-item retries are gone, so every item goes into the
    /// batch-level retry pass. That pass's first attempt applies most of them and throttles the
    /// rest; the follow-up carrying the rest is refused. The items the pass applied are writes
    /// the server confirmed, and the only place they are recorded is the answer it gave.
    /// </summary>
    private sealed class RetryPassLandsThenIsRefusedHandler : HttpMessageHandler
    {
        private int _posts;
        public int Posts => Volatile.Read(ref _posts);

        private static string Throttled(int count) =>
            "{ \"responses\": [" + string.Join(",", Enumerable.Range(1, count).Select(i =>
                $"{{ \"id\": \"{i}\", \"status\": 429, \"headers\": {{ \"Retry-After\": \"1\" }} }}")) + "] }";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var post = Interlocked.Increment(ref _posts);
            // Four posts exhaust the chunk's own retries, leaving all 20 items throttled and
            // eligible for the batch-level pass.
            if (post <= 4)
                return Task.FromResult(Ok(Throttled(20)));
            // The pass's own first attempt: fifteen creates applied, five still throttled.
            if (post == 5)
            {
                var body = "{ \"responses\": [" + string.Join(",", Enumerable.Range(1, 20).Select(i =>
                    i <= 15
                        ? $"{{ \"id\": \"{i}\", \"status\": 201, \"body\": {{ \"id\": \"u{i}\" }} }}"
                        : $"{{ \"id\": \"{i}\", \"status\": 429, \"headers\": {{ \"Retry-After\": \"1\" }} }}"))
                    + "] }";
                return Task.FromResult(Ok(body));
            }
            // The follow-up carrying the five is refused outright.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                RequestMessage = request,
                Content = new StringContent(
                    "{\"error\":{\"code\":\"BadRequest\",\"message\":\"batch rejected\"}}",
                    System.Text.Encoding.UTF8, "application/json")
            });

            HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    /// <summary>
    /// The batch-level retry pass is a send like any other, and the server answers it. When a
    /// later attempt of that pass is refused, the items the earlier attempt applied still have
    /// to come back with the status the server gave them: reported with the throttle that sent
    /// them into the pass, fifteen confirmed creates read as work still outstanding, and the
    /// dead-letter file the caller replays creates them a second time.
    /// </summary>
    [Fact]
    public async Task Items_the_retry_pass_applied_keep_that_answer_when_its_follow_up_is_refused()
    {
        ResiliencePipelineFactory.Reset();
        var wire = new RetryPassLandsThenIsRefusedHandler();
        using var httpClient = new HttpClient(wire);
        using var client = new ResilientGraphClient(httpClient,
            new ResilientGraphClientOptions { NoRateLimit = true, MaxRetryAttempts = 1 });
        var batchClient = new GraphBatchClient(client);

        var ops = Enumerable.Range(1, 20)
            .Select(i => new BatchOperation("/users", "POST",
                System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                    $"{{\"displayName\":\"u{i}\"}}")))
            .ToList();

        var result = await batchClient.ExecuteBatchIndexedAsync(ops, CancellationToken.None);

        Assert.Equal(6, wire.Posts);
        Assert.NotNull(result.ChunkFailure);
        // The fifteen the pass created. The server said 201; nothing after that unsays it.
        for (int i = 0; i < 15; i++)
            Assert.Equal(201, result.Results[i].Response.Status);
        // The five the refused follow-up carried keep the last thing the server said about
        // them - a throttle, which is a failure, and never a status below 400.
        for (int i = 15; i < 20; i++)
            Assert.Equal(429, result.Results[i].Response.Status);
        Assert.Empty(result.NotSent);
        Assert.Equal(15, result.Telemetry.Succeeded);
        Assert.Equal(5, result.Telemetry.Failed);
    }

    // ------------------------------------------------------------------------------------
    // What a run says about itself, transcribed per scenario.
    //
    // Four lines carry it: the dead-letter line, the chunk-failure target, the verbose summary
    // and the warning - the last being all a caller running without -Verbose sees. They are
    // read together by whoever is deciding what to re-submit, so each one being right on its
    // own is not enough; they have to agree as a set.
    // ------------------------------------------------------------------------------------

    /// <summary>The four lines of one run, as the streams carried them.</summary>
    private sealed record Prose(
        string Summary,
        string? Warning,
        string? ChunkFailureTarget,
        string? DeadLetterLine,
        int DeadLetterLines,
        IReadOnlyList<string> Verbose);

    /// <summary>
    /// Drives the cmdlet over <paramref name="wire"/> and collects the four lines. Always with a
    /// dead-letter path and -Verbose, so every scenario is asked the same question.
    /// </summary>
    private static Prose Say(HttpMessageHandler wire, int count, string method, object? body = null)
    {
        var deadLetter = Path.Combine(Path.GetTempPath(), $"mgx-dl-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (MgxTransportScope.Inject(wire))
            {
                using var ps = System.Management.Automation.PowerShell.Create();
                ps.AddCommand("Import-Module")
                  .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Batch.InvokeMgxBatchRequest).Assembly);
                ps.Invoke();
                ps.Commands.Clear();
                ps.AddScript("function Get-MgContext { [PSCustomObject]@{ TenantId = 'test-tenant-00000000-0000-0000-0000-000000000000' } }");
                ps.Invoke();
                ps.Commands.Clear();

                var cmd = ps.AddCommand("Invoke-MgxBatchRequest")
                  .AddParameter("Uri", Enumerable.Range(1, count).Select(i => $"/users/u{i}").ToArray())
                  .AddParameter("Method", method)
                  .AddParameter("DeadLetterPath", deadLetter)
                  .AddParameter("Verbose", true);
                if (body != null) cmd.AddParameter("Body", body);
                ps.Invoke();

                var verbose = ps.Streams.Verbose.Select(v => v.Message).ToList();
                return new Prose(
                    Summary: Assert.Single(verbose, v => v.StartsWith("Batch: ", StringComparison.Ordinal)),
                    Warning: ps.Streams.Warning.Select(w => w.Message)
                        .FirstOrDefault(w => w.Contains("batch items failed", StringComparison.Ordinal)),
                    ChunkFailureTarget: ps.Streams.Error
                        .Where(e => e.FullyQualifiedErrorId.StartsWith("BatchChunkFailed", StringComparison.Ordinal))
                        .Select(e => e.TargetObject as string)
                        .FirstOrDefault(),
                    DeadLetterLine: verbose.FirstOrDefault(v => v.StartsWith("Wrote ", StringComparison.Ordinal)),
                    DeadLetterLines: File.Exists(deadLetter) ? File.ReadAllLines(deadLetter).Length : 0,
                    Verbose: verbose);
            }
        }
        finally
        {
            if (File.Exists(deadLetter)) File.Delete(deadLetter);
        }
    }

    /// <summary>Answers every chunk in full, every item applied.</summary>
    private sealed class AppliesEveryItemHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(BatchResponse(1, 20, 204),
                    System.Text.Encoding.UTF8, "application/json")
            });
    }

    /// <summary>
    /// Nothing failed, so three of the four lines have nothing to say and do not say it. The
    /// warning in particular: a run that worked must not put one on the stream at all.
    /// </summary>
    [Fact]
    public void A_clean_run_says_so_and_says_nothing_else()
    {
        var prose = Say(new AppliesEveryItemHandler(), 20, "PATCH");

        Assert.Contains("Batch: 20 succeeded, 0 failed out of 20 requests", prose.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("not sent", prose.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Batch-level retries", prose.Summary, StringComparison.Ordinal);
        Assert.Null(prose.Warning);
        Assert.Null(prose.ChunkFailureTarget);
        Assert.Null(prose.DeadLetterLine);
        Assert.Equal(0, prose.DeadLetterLines);
    }

    /// <summary>
    /// The refused chunk is the last one, so nothing was left unsent and no line mentions
    /// anything that was. The retry pass genuinely never went out: the run stopped before it.
    /// </summary>
    [Fact]
    public void A_chunk_refused_with_nothing_behind_it_reports_no_unsent_work()
    {
        var prose = Say(new FirstChunkOnlyHandler(), 25, "PATCH");

        Assert.Contains("Batch: 20 succeeded, 5 failed out of 25 requests", prose.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("not sent", prose.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Batch-level retries", prose.Summary, StringComparison.Ordinal);
        Assert.Equal(
            "5 of 25 batch items failed."
            + " A chunk failed, so the run stopped sending and the retry pass was withheld."
            + " Check $Error for details on each item.",
            prose.Warning);
        Assert.Equal("5 of 25 operations failed, 0 were not sent", prose.ChunkFailureTarget);
        Assert.StartsWith("Wrote 5 failed items to dead-letter file: ", prose.DeadLetterLine, StringComparison.Ordinal);
        Assert.Equal(5, prose.DeadLetterLines);
    }

    /// <summary>
    /// A chunk behind the refused one never went out, and all four lines have to keep it apart
    /// from the writes that were refused: one of those may have been applied on the server and
    /// the other certainly was not.
    /// </summary>
    [Fact]
    public void Operations_never_sent_are_named_by_every_line_that_counts_them()
    {
        var prose = Say(new FirstChunkOnlyHandler(), 60, "PATCH");

        Assert.Contains("Batch: 20 succeeded, 20 failed, 20 not sent out of 60 requests", prose.Summary, StringComparison.Ordinal);
        Assert.Equal(
            "20 of 60 batch items failed and 20 were not sent."
            + " A chunk failed, so the run stopped sending and the retry pass was withheld."
            + " Check $Error for details on each item.",
            prose.Warning);
        Assert.Equal("20 of 60 operations failed, 20 were not sent", prose.ChunkFailureTarget);
        Assert.StartsWith("Wrote 20 failed and 20 not-sent items to dead-letter file: ",
            prose.DeadLetterLine, StringComparison.Ordinal);
        Assert.Equal(40, prose.DeadLetterLines);
    }

    /// <summary>
    /// Throttles the chunk through every attempt it has, then answers the batch-level pass in
    /// full: fifteen applied, five not found. Nothing stopped the run.
    /// </summary>
    private sealed class ThrottlesThenAnswersThePassHandler : HttpMessageHandler
    {
        private int _posts;
        public int Posts => Volatile.Read(ref _posts);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var post = Interlocked.Increment(ref _posts);
            var body = post <= 4
                ? "{ \"responses\": [" + string.Join(",", Enumerable.Range(1, 20).Select(i =>
                    $"{{ \"id\": \"{i}\", \"status\": 429, \"headers\": {{ \"Retry-After\": \"1\" }} }}")) + "] }"
                : "{ \"responses\": [" + string.Join(",", Enumerable.Range(1, 20).Select(i =>
                    $"{{ \"id\": \"{i}\", \"status\": {(i <= 15 ? 204 : 404)} }}")) + "] }";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>
    /// The pass went out and the server answered it. Nothing stopped the run, so the five that
    /// failed had every attempt they qualified for and the warning says so.
    /// </summary>
    [Fact]
    public void A_retry_pass_that_ran_to_the_end_leaves_the_run_unstopped()
    {
        var wire = new ThrottlesThenAnswersThePassHandler();
        var prose = Say(wire, 20, "PATCH");

        Assert.Equal(5, wire.Posts);
        Assert.Contains("Batch: 15 succeeded, 5 failed out of 20 requests", prose.Summary, StringComparison.Ordinal);
        Assert.Contains(" Batch-level retries: 20.", prose.Summary, StringComparison.Ordinal);
        Assert.Equal(
            "5 of 20 batch items failed."
            + " They failed after all retry attempts."
            + " Check $Error for details on each item.",
            prose.Warning);
        Assert.Null(prose.ChunkFailureTarget);
        Assert.StartsWith("Wrote 5 failed items to dead-letter file: ", prose.DeadLetterLine, StringComparison.Ordinal);
        Assert.Equal(5, prose.DeadLetterLines);
    }

    /// <summary>
    /// The pass carried twenty items, applied fifteen of them, and was refused on the follow-up
    /// carrying the other five. Twenty went out on the wire, which is what the count reports.
    /// </summary>
    [Fact]
    public void A_retry_pass_refused_after_it_applied_work_still_counts_what_it_sent()
    {
        var wire = new RetryPassLandsThenIsRefusedHandler();
        var prose = Say(wire, 20, "POST", new System.Collections.Hashtable { ["displayName"] = "u" });

        Assert.Equal(6, wire.Posts);
        Assert.Contains("Batch: 15 succeeded, 5 failed out of 20 requests", prose.Summary, StringComparison.Ordinal);
        Assert.Contains(" Batch-level retries: 20.", prose.Summary, StringComparison.Ordinal);
        Assert.Equal(
            "5 of 20 batch items failed."
            + " The retry pass was sent and then refused, so the run stopped sending."
            + " Check $Error for details on each item.",
            prose.Warning);
        Assert.Equal("5 of 20 operations failed, 0 were not sent", prose.ChunkFailureTarget);
        Assert.StartsWith("Wrote 5 failed items to dead-letter file: ", prose.DeadLetterLine, StringComparison.Ordinal);
        Assert.Equal(5, prose.DeadLetterLines);
    }

    /// <summary>
    /// Throttles both chunks through every attempt they have, so all forty items become
    /// candidates for the batch-level pass - two chunks of it - and refuses the first chunk
    /// that pass sends. The loop breaks there, so the second chunk of the pass never goes out.
    /// </summary>
    private sealed class RefusesTheFirstChunkOfThePassHandler : HttpMessageHandler
    {
        private int _posts;
        public int Posts => Volatile.Read(ref _posts);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Four attempts each for two chunks exhausts the per-chunk retries of both.
            if (Interlocked.Increment(ref _posts) <= 8)
            {
                var body = "{ \"responses\": [" + string.Join(",", Enumerable.Range(1, 20).Select(i =>
                    $"{{ \"id\": \"{i}\", \"status\": 429, \"headers\": {{ \"Retry-After\": \"1\" }} }}")) + "] }";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                RequestMessage = request,
                Content = new StringContent(
                    "{\"error\":{\"code\":\"BadRequest\",\"message\":\"batch rejected\"}}",
                    System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>
    /// Forty items were candidates for the pass and twenty of them were POSTed: the refusal of
    /// the first chunk ends the pass, and the twenty behind it are never re-sent. Candidacy is
    /// its own line and says forty; what went out is the count, and it is not the same number.
    /// </summary>
    [Fact]
    public void A_retry_pass_refused_on_its_first_chunk_never_sends_the_second()
    {
        var wire = new RefusesTheFirstChunkOfThePassHandler();
        var prose = Say(wire, 40, "PATCH");

        // Eight per-chunk attempts and one POST of the pass. The pass's second chunk is the
        // POST that would make this nine, and it never happened.
        Assert.Equal(9, wire.Posts);
        Assert.Contains("Batch: 0 succeeded, 40 failed out of 40 requests", prose.Summary, StringComparison.Ordinal);
        // Twenty, not forty: the second chunk of the pass is the POST that never happened.
        Assert.Contains(" Batch-level retries: 20.", prose.Summary, StringComparison.Ordinal);
        Assert.Equal(
            "40 of 40 batch items failed."
            + " The retry pass was sent and then refused, so the run stopped sending."
            + " Check $Error for details on each item.",
            prose.Warning);
        Assert.Equal("40 of 40 operations failed, 0 were not sent", prose.ChunkFailureTarget);
        Assert.StartsWith("Wrote 40 failed items to dead-letter file: ", prose.DeadLetterLine, StringComparison.Ordinal);
        Assert.Equal(40, prose.DeadLetterLines);
        // Candidacy is stated separately and is not in question: forty items did exhaust their
        // per-chunk retries, whatever the pass then managed to send.
        Assert.Contains(prose.Verbose, v =>
            v.Contains("Batch-level retry: 40 items exhausted per-chunk retries", StringComparison.Ordinal));
    }

    /// <summary>The lines -WhatIf writes go to the host, so a test that reads them needs one.</summary>
    private sealed class GateHost : PSHost
    {
        private readonly Guid _id = Guid.NewGuid();
        public GateHostUI Recorder { get; } = new();
        public override string Name => "MgxBatchGateHost";
        public override Version Version => new(1, 0);
        public override Guid InstanceId => _id;
        public override PSHostUserInterface UI => Recorder;
        public override System.Globalization.CultureInfo CurrentCulture => System.Globalization.CultureInfo.InvariantCulture;
        public override System.Globalization.CultureInfo CurrentUICulture => System.Globalization.CultureInfo.InvariantCulture;
        public override void EnterNestedPrompt() { }
        public override void ExitNestedPrompt() { }
        public override void NotifyBeginApplication() { }
        public override void NotifyEndApplication() { }
        public override void SetShouldExit(int exitCode) { }
    }

    private sealed class GateHostUI : PSHostUserInterface
    {
        public List<string> Lines { get; } = [];
        public override PSHostRawUserInterface? RawUI => null;
        public override void Write(string value) => Lines.Add(value);
        public override void Write(ConsoleColor f, ConsoleColor b, string value) => Lines.Add(value);
        public override void WriteLine(string value) => Lines.Add(value);
        public override void WriteErrorLine(string value) => Lines.Add(value);
        public override void WriteDebugLine(string value) => Lines.Add(value);
        public override void WriteVerboseLine(string value) => Lines.Add(value);
        public override void WriteWarningLine(string value) => Lines.Add(value);
        public override void WriteProgress(long sourceId, ProgressRecord record) { }
        public override string ReadLine() => string.Empty;
        public override System.Security.SecureString ReadLineAsSecureString() => new();
        public override Dictionary<string, PSObject> Prompt(
            string caption, string message, System.Collections.ObjectModel.Collection<FieldDescription> descriptions) => [];
        public override int PromptForChoice(
            string caption, string message,
            System.Collections.ObjectModel.Collection<ChoiceDescription> choices, int defaultChoice) => defaultChoice;
        public override PSCredential PromptForCredential(
            string caption, string message, string userName, string targetName) => PSCredential.Empty;
        public override PSCredential PromptForCredential(
            string caption, string message, string userName, string targetName,
            PSCredentialTypes allowedCredentialTypes, PSCredentialUIOptions options) => PSCredential.Empty;
    }

    /// <summary>
    /// Runs the batch under -WhatIf over per-item methods and returns the line the gate wrote.
    /// The wire is armed and counted: a gate that lets the batch through is a different failure
    /// from one that describes it wrongly, and the two have to be told apart.
    /// </summary>
    private static (string Line, int Requests) GateSays(params string[] methods)
    {
        var wire = new CountingHandler();
        using (MgxTransportScope.Inject(wire))
        {
            var host = new GateHost();
            using var runspace = System.Management.Automation.Runspaces.RunspaceFactory.CreateRunspace(host);
            runspace.Open();
            using var ps = System.Management.Automation.PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddCommand("Import-Module")
              .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Batch.InvokeMgxBatchRequest).Assembly);
            ps.Invoke();
            ps.Commands.Clear();
            ps.AddScript("function Get-MgContext { [PSCustomObject]@{ TenantId = 'test-tenant-00000000-0000-0000-0000-000000000000' } }");
            ps.Invoke();
            ps.Commands.Clear();

            ps.AddCommand("Invoke-MgxBatchRequest")
              .AddParameter("Uri", methods
                  .Select((m, i) => (object)new System.Collections.Hashtable
                  {
                      ["Url"] = $"/users/u{i + 1}",
                      ["Method"] = m
                  })
                  .ToArray())
              .AddParameter("WhatIf", true);
            ps.Invoke();

            return (Assert.Single(host.Recorder.Lines, l => l.Contains("What if:", StringComparison.Ordinal)),
                wire.Requests);
        }
    }

    /// <summary>
    /// -WhatIf is the one surface whose whole purpose is stating what is about to happen, and
    /// the target it states is the batch's own account of itself. The total is deliberately the
    /// whole batch, reads included - they spend resource units and emit output - but a write
    /// verb across that total names requests the batch does not hold: one DELETE among nineteen
    /// reads announced twenty deletions, and five creates among fifteen reads announced twenty
    /// creates, byte-identical to a batch of twenty that really was all creates.
    /// </summary>
    [Theory]
    // A single write among reads: the verb is the alarming part, and it belonged to one item.
    [InlineData(1, "DELETE", 19, "Send batch\" on target \"20 requests (1 DELETE) via $batch\"")]
    [InlineData(5, "POST", 15, "Send batch\" on target \"20 requests (5 POST) via $batch\"")]
    // No reads to distinguish: the whole batch is that one method, and the verb is the total.
    [InlineData(20, "POST", 0, "Send batch\" on target \"POST 20 requests via $batch\"")]
    [InlineData(0, "POST", 20, "Send batch\" on target \"GET 20 requests via $batch\"")]
    public void The_gate_names_the_writes_a_batch_holds_not_the_batch_as_writes(
        int writes, string writeMethod, int reads, string expected)
    {
        var methods = Enumerable.Repeat(writeMethod, writes)
            .Concat(Enumerable.Repeat("GET", reads))
            .ToArray();

        var (line, requests) = GateSays(methods);

        Assert.Contains(expected, line, StringComparison.Ordinal);
        Assert.Equal(0, requests);
    }

    /// <summary>
    /// Runs the batch under -WhatIf over per-item bodies and returns the gate's line, the error
    /// records it wrote and what reached the wire. -WhatIf's whole purpose is saying what would
    /// happen, so a gate that describes requests the run would refuse is the defect, and the
    /// records naming them are half of the answer.
    /// </summary>
    private static (string Line, string[] Errors, int Requests) GateSaysOfBodies(
        params object[] items)
    {
        var wire = new CountingHandler();
        using (MgxTransportScope.Inject(wire))
        {
            var host = new GateHost();
            using var runspace = System.Management.Automation.Runspaces.RunspaceFactory.CreateRunspace(host);
            runspace.Open();
            using var ps = System.Management.Automation.PowerShell.Create();
            ps.Runspace = runspace;
            ps.AddCommand("Import-Module")
              .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Batch.InvokeMgxBatchRequest).Assembly);
            ps.Invoke();
            ps.Commands.Clear();
            ps.AddScript("function Get-MgContext { [PSCustomObject]@{ TenantId = 'test-tenant-00000000-0000-0000-0000-000000000000' } }");
            ps.Invoke();
            ps.Commands.Clear();

            ps.AddCommand("Invoke-MgxBatchRequest")
              .AddParameter("Uri", items)
              .AddParameter("WhatIf", true);
            ps.Invoke();

            return (
                Assert.Single(host.Recorder.Lines, l => l.Contains("What if:", StringComparison.Ordinal)),
                ps.Streams.Error.Select(e => e.Exception.Message).ToArray(),
                wire.Requests);
        }
    }

    /// <summary>
    /// The gate over a batch two of whose items will not be sent: one body the marker guard
    /// refuses, one the JSON parser refuses. The count used to be everything that was piped
    /// in, so the line announced three requests where the run would make one - at the one
    /// surface whose purpose is saying what is about to happen - and said nothing at all about
    /// the two it would refuse. The validation and the guard run before the gate now, so the
    /// count is what would be sent and the refusals are named beside it.
    /// </summary>
    [Fact]
    public void The_gate_names_the_count_that_will_be_sent_and_the_refusals()
    {
        var (line, errors, requests) = GateSaysOfBodies(
            new System.Collections.Hashtable
            {
                ["Url"] = "/users/u1", ["Method"] = "PATCH",
                ["Body"] = new System.Collections.Hashtable { ["displayName"] = "sent" }
            },
            new System.Collections.Hashtable
            {
                ["Url"] = "/users/u2", ["Method"] = "PATCH",
                ["Body"] = new System.Collections.Hashtable { ["passwordProfile"] = "***REDACTED***" }
            },
            new System.Collections.Hashtable
            {
                ["Url"] = "/users/u3", ["Method"] = "PATCH", ["Body"] = "not json at all"
            });

        // One request, singular, and the two refusals as their own count.
        Assert.Contains("Send batch\" on target \"PATCH 1 request via $batch; 2 refused\"",
            line, StringComparison.Ordinal);

        // The refusals are error records, written under -WhatIf as without it: a body this run
        // will not send is not an action the gate governs.
        Assert.Equal(2, errors.Length);
        Assert.Contains(errors, e => e.Contains("PATCH /users/u2", StringComparison.Ordinal)
                                     && e.Contains("redaction marker", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("PATCH /users/u3", StringComparison.Ordinal)
                                     && e.Contains("not valid JSON", StringComparison.Ordinal));

        Assert.Equal(0, requests);
    }

    /// <summary>
    /// Nothing refused, so nothing is named: the line a batch with no refusals gets is the one
    /// it always got, and the clause is not appended empty.
    /// </summary>
    [Fact]
    public void The_gate_names_no_refusals_when_there_are_none()
    {
        var (line, errors, requests) = GateSaysOfBodies(
            new System.Collections.Hashtable
            {
                ["Url"] = "/users/u1", ["Method"] = "PATCH",
                ["Body"] = new System.Collections.Hashtable { ["displayName"] = "a" }
            },
            new System.Collections.Hashtable
            {
                ["Url"] = "/users/u2", ["Method"] = "PATCH",
                ["Body"] = new System.Collections.Hashtable { ["displayName"] = "b" }
            });

        Assert.Contains("Send batch\" on target \"PATCH 2 requests via $batch\"",
            line, StringComparison.Ordinal);
        Assert.DoesNotContain("refused", line, StringComparison.Ordinal);
        Assert.Empty(errors);
        Assert.Equal(0, requests);
    }

    /// <summary>
    /// Several write methods alongside reads keep the shape they already had: the total, then
    /// the writes by method. It states a total and a subset of it, and nothing in it is a
    /// count of requests that are not there.
    /// </summary>
    [Fact]
    public void The_gate_keeps_naming_each_write_method_of_a_mixed_batch()
    {
        var methods = Enumerable.Repeat("POST", 5)
            .Concat(Enumerable.Repeat("PATCH", 3))
            .Concat(Enumerable.Repeat("GET", 12))
            .ToArray();

        var (line, requests) = GateSays(methods);

        Assert.Contains("Send batch\" on target \"20 requests (5 POST, 3 PATCH) via $batch\"",
            line, StringComparison.Ordinal);
        Assert.Equal(0, requests);
    }
}
