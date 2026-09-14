using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Mgx.Engine.Errors;
using Mgx.Engine.Http;
using Mgx.Engine.Models;
using Polly.Timeout;

namespace Mgx.IntegrationTests;

/// <summary>
/// Every fault in the catalog, injected at subrequest 23 of a 25-operation batch - the third
/// subrequest of the second chunk, after the first chunk's twenty were answered. A status lands
/// inside the 200 envelope, which is the only place Graph can put one; a transport failure lands
/// on that chunk's own POST, because a sub-response has no way to carry one. What each case
/// expects comes from <see cref="MgxErrorPolicy"/>: the item loop asks it about the sub-response's
/// status with the item's own method, and the pipeline asks it about the POST, which is a POST.
/// </summary>
[Collection("Pipeline")]
public class FaultInjectionBatchTests
{
    private const int Operations = 25;
    private const int ChunkSize = 20;
    private const string TargetUrl = "/users/u23";

    /// <summary>What every armed fault in this file is called, so a theory can assert by name
    /// that <see cref="MockHttpHandler.UnansweredRules"/> does not carry it.</summary>
    private const string FaultName = "fault-chunk-2";

    private static readonly ResilientGraphClientOptions Options = new()
    {
        NoRateLimit = true,
        NoAdaptivePacing = true,
        MaxRetryAttempts = 1,
        AttemptTimeoutSeconds = 1,
        TotalTimeoutSeconds = 30,
    };

    private static readonly TimeSpan ShortBodyRead = TimeSpan.FromMilliseconds(250);

    /// <summary>The envelope stops in the middle of a sub-response.</summary>
    private const string TruncatedEnvelope = """{"responses":[{"id":"1","status":200""";

    private static List<BatchOperation> AllOperations() =>
        [.. Enumerable.Range(1, Operations).Select(i => new BatchOperation($"/users/u{i}"))];

    /// <summary>
    /// Answers a $batch envelope from the envelope that was sent: 200 for every subrequest,
    /// except the one whose url is <paramref name="faultUrl"/>. Keyed on the url rather than the
    /// sub-response id because a retry re-numbers the ids - the item that failed comes back as
    /// id 1 - and the url is what identifies it across attempts.
    /// </summary>
    private static HttpResponseMessage Envelope(MockRequest request, string? faultUrl, int faultStatus)
    {
        using var sent = JsonDocument.Parse(request.BodyText!);
        var items = new List<string>();
        foreach (var subrequest in sent.RootElement.GetProperty("requests").EnumerateArray())
        {
            var id = subrequest.GetProperty("id").GetString();
            var url = subrequest.GetProperty("url").GetString();
            var faulted = faultUrl != null && string.Equals(url, faultUrl, StringComparison.Ordinal);
            var status = faulted ? faultStatus : 200;
            // Retry-After on the throttled sub-response, so the item loop's backoff is the
            // server's and not an exponential one this suite would have to wait out.
            var headers = faulted && status == 429 ? ",\"headers\":{\"Retry-After\":\"0\"}" : "";
            var body = faulted
                ? ",\"body\":{\"error\":{\"code\":\"Injected_" + status + "\"}}"
                : ",\"body\":{\"id\":\"" + url!.Split('/')[^1] + "\"}";
            items.Add("{\"id\":\"" + id + "\",\"status\":" + status + headers + body + "}");
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"responses\":[" + string.Join(",", items) + "]}", Encoding.UTF8, "application/json")
        };
    }

    /// <summary>The chunk carrying the target subrequest, on any attempt - a retry re-sends it alone.</summary>
    private static bool CarriesTheTarget(MockRequest request) =>
        request.BodyText?.Contains("\"" + TargetUrl + "\"", StringComparison.Ordinal) == true;

    private static MockHttpHandler TwoChunks(Action<MockResponseRule<MockHttpHandler>> armSecondChunk)
    {
        var handler = new MockHttpHandler();
        armSecondChunk(handler.When(CarriesTheTarget, FaultName).OnAttempt(1));
        handler.When(CarriesTheTarget).Respond(r => Envelope(r, faultUrl: null, faultStatus: 0));
        // Everything else: the first chunk, answered in full.
        handler.When(_ => true).Respond(r => Envelope(r, faultUrl: null, faultStatus: 0));
        return handler;
    }

    private static void ArmAtTheTargetSubrequest(MockResponseRule<MockHttpHandler> rule, FaultEntry entry)
    {
        switch (entry.Shape)
        {
            case FaultShape.Status:
                // Graph answers $batch with 200 and puts the subrequest's failure in its own
                // sub-response, so this is the only shape a status can take here.
                rule.Respond(r => Envelope(r, TargetUrl, entry.Status));
                break;
            case FaultShape.Transport:
                rule.Throw(entry.Transport!());
                break;
            case FaultShape.HeldPastAttemptTimeout:
                rule.AfterDelay(TimeSpan.FromSeconds(5))
                    .Respond(r => Envelope(r, faultUrl: null, faultStatus: 0));
                break;
            case FaultShape.StalledBody:
                rule.RespondStalled(HttpStatusCode.OK, prefix: """{"responses":[""");
                break;
            case FaultShape.TruncatedJson:
                rule.Respond(HttpStatusCode.OK, TruncatedEnvelope);
                break;
            default:
                throw new InvalidOperationException(
                    $"{entry.Kind} does not take a wire shape in a batch; it needs its own case.");
        }
    }

    private static async Task<BatchExecutionResult> RunBatchAsync(MockHttpHandler handler)
    {
        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, Options)
        {
            BodyReadTimeout = ShortBodyRead
        };
        var batchClient = new GraphBatchClient(client);
        return await batchClient.ExecuteBatchIndexedAsync(AllOperations());
    }

    private static int StatusOf(BatchExecutionResult result, string url) =>
        result.Results.Single(r => r.Operation.Url == url).Response.Status;

    [Theory]
    [MemberData(nameof(FaultCatalog.AllKinds), MemberType = typeof(FaultCatalog))]
    public async Task Each_fault_at_the_target_subrequest(FaultKind kind)
    {
        var entry = FaultCatalog.Entry(kind);
        var cell = entry.At(InjectionPoint.BatchSubrequest);
        if (cell.Coverage == FaultCoverage.NotApplicable)
        {
            FaultInjectionRows.AssertNotApplicable(entry, InjectionPoint.BatchSubrequest);
            return;
        }

        ResiliencePipelineFactory.Reset();
        try
        {
            var handler = TwoChunks(rule => ArmAtTheTargetSubrequest(rule, entry));
            var result = await RunBatchAsync(handler);
            Assert.DoesNotContain(FaultName, handler.UnansweredRules);

            if (entry.Shape == FaultShape.Status)
                AssertAFaultedSubResponse(entry, handler, result, cell.Coverage);
            else
                AssertARefusedChunk(entry, handler, result);
        }
        finally
        {
            ResiliencePipelineFactory.Reset();
        }
    }

    /// <summary>
    /// A status inside the envelope. The item loop asks the same policy the request pipeline
    /// does, with the item's own method - GET here, so idempotent.
    /// </summary>
    private static void AssertAFaultedSubResponse(
        FaultEntry entry, MockHttpHandler handler, BatchExecutionResult result, FaultCoverage coverage)
    {
        Assert.Null(result.ChunkFailure);
        Assert.Equal(Operations, result.Results.Count);
        Assert.Empty(result.NotSent);

        // Every subrequest but the target was answered 200, in both chunks.
        foreach (var (operation, response) in result.Results.Where(r => r.Operation.Url != TargetUrl))
            Assert.InRange(response.Status, 200, 299);

        if (FaultCatalog.RetriedAsABatchItem(entry, "GET"))
        {
            // The retry is on the wire: chunk 1, chunk 2, and chunk 2 again carrying the one item.
            Assert.Equal(3, handler.RequestCount);
            Assert.Equal(1, result.Telemetry.ItemRetries);
            Assert.InRange(StatusOf(result, TargetUrl), 200, 299);
            Assert.Equal(Operations, result.Telemetry.Succeeded);
            Assert.Equal(0, result.Telemetry.Failed);
            Assert.Contains(TargetUrl, handler.CapturedRequests[2].BodyText);
            Assert.DoesNotContain("/users/u21", handler.CapturedRequests[2].BodyText);
        }
        else
        {
            // One POST per chunk and no more: the item keeps the status the server gave it, and
            // the batch-level retry pass does not pick up a status the policy will not retry.
            Assert.Equal(2, handler.RequestCount);
            Assert.Equal(0, result.Telemetry.ItemRetries);
            Assert.Equal(0, result.Telemetry.BatchLevelRetries);
            Assert.Equal(entry.Status, StatusOf(result, TargetUrl));
            Assert.Equal(Operations - 1, result.Telemetry.Succeeded);
            Assert.Equal(1, result.Telemetry.Failed);

            if (coverage == FaultCoverage.Pinned)
                AssertThePin(entry, handler);
        }
    }

    /// <summary>
    /// What the two pinned status rows add over the plain non-retryable case: an expired token is
    /// not refreshed and re-sent, and a read-after-write 404 is not waited out.
    /// </summary>
    private static void AssertThePin(FaultEntry entry, MockHttpHandler handler)
    {
        switch (entry.Kind)
        {
            case FaultKind.ExpiredToken:
                Assert.Equal(MgxErrorClass.Authentication, entry.Class);
                // Two POSTs, both answered 200 at the envelope level: nothing re-authenticated
                // and sent the chunk again.
                Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK], handler.ServedStatusCodes);
                break;
            case FaultKind.ConsistencyDelay:
                // MgxErrorClass.Consistency is declared for the read-after-write window and has
                // no producer; a 404 is a 404, and the item loop does not retry one.
                Assert.Equal(MgxErrorClass.NotFound, entry.Class);
                Assert.False(FaultCatalog.RetriedAsABatchItem(entry, "GET"));
                break;
            default:
                Assert.Fail($"{entry.Kind} is pinned inside a batch envelope with nothing to record.");
                break;
        }
    }

    /// <summary>
    /// A transport failure on the chunk's own POST. The POST is not idempotent, so the policy
    /// does not retry it whatever the class - and the chunk before it keeps everything the server
    /// answered, which is the record of what it applied.
    /// </summary>
    private static void AssertARefusedChunk(
        FaultEntry entry, MockHttpHandler handler, BatchExecutionResult result)
    {
        Assert.False(FaultCatalog.RetriedOnANonIdempotentRequest(entry));
        Assert.Equal(2, handler.RequestCount);
        Assert.NotNull(result.ChunkFailure);
        AssertTheFaultSurfaced(entry, result.ChunkFailure!);
        Assert.Equal(Operations, result.Results.Count);

        var answered = result.Results.Take(ChunkSize).ToList();
        var refused = result.Results.Skip(ChunkSize).ToList();

        // The first chunk's twenty are not unmade by the second chunk's failure.
        Assert.All(answered, r => Assert.InRange(r.Response.Status, 200, 299));
        Assert.Equal(ChunkSize, result.Telemetry.Succeeded);

        // The refused chunk's POST went out, so none of its items may read as never sent: the
        // difference between a request that may have reached the service and one that did not.
        Assert.Equal(Operations - ChunkSize, refused.Count);
        Assert.All(refused, r =>
        {
            Assert.NotEqual(GraphBatchClient.NotSentStatus, r.Response.Status);
            Assert.True(r.Response.Status >= 400,
                $"{r.Operation.Url} came back {r.Response.Status}, which reads as a success");
        });
        Assert.Empty(result.NotSent);
    }

    /// <summary>
    /// The armed fault itself, as the caller gets it. A chunk failure is handed back on the
    /// result rather than thrown, so this asks of <see cref="BatchExecutionResult.ChunkFailure"/>
    /// what the pagination theory asks of the exception the iterator raises: the kind that was
    /// armed has to be the kind that surfaced, or the row proves only that something went wrong.
    /// </summary>
    private static void AssertTheFaultSurfaced(FaultEntry entry, Exception failure)
    {
        switch (entry.Shape)
        {
            case FaultShape.Transport:
                // Straight through: the send throws, the POST is not retried, and the exception
                // the mock raised is the one the result carries, reset SocketException and all.
                var reset = Assert.IsType<HttpRequestException>(failure);
                Assert.Equal(entry.Transport!().Message, reset.Message);
                Assert.IsType<SocketException>(reset.InnerException);
                break;
            case FaultShape.HeldPastAttemptTimeout:
                // The pipeline's own per-attempt timeout, not the server's answer: the held
                // response never arrives, so what surfaces names the timeout that gave up on it.
                var timedOut = Assert.IsType<TimeoutRejectedException>(failure);
                Assert.Equal(TimeSpan.FromSeconds(Options.AttemptTimeoutSeconds), timedOut.Timeout);
                break;
            case FaultShape.StalledBody:
                // Headers arrived, so the timeout is the body read's and says so.
                var stalled = Assert.IsType<HttpRequestException>(failure);
                Assert.Equal(ResilientGraphClient.BodyReadTimedOutMessage, stalled.Message);
                break;
            case FaultShape.TruncatedJson:
                // The envelope is a 200 that stops mid-document; the parse is what fails, and
                // it fails inside the responses array rather than at the top of the envelope.
                var parse = Assert.IsAssignableFrom<JsonException>(failure);
                Assert.Contains("responses", parse.Message, StringComparison.Ordinal);
                break;
            default:
                Assert.Fail($"{entry.Kind} surfaced as {failure.GetType().Name} with no case to check it.");
                break;
        }
    }
}
