using System.Management.Automation;
using System.Net;
using System.Text;
using Mgx.Cmdlets.Base;
using Mgx.Engine.Http;
using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests;

/// <summary>
/// An export writes a fresh run to a temp and moves it over the output, so a checkpoint from
/// an interrupted export counts items that are in the temp, not in the output. Recovery has to
/// know which file holds them: an output left over from an EARLIER export satisfies "the output
/// exists" while holding none of them.
/// </summary>
[Collection("Pipeline")]
public class ExportCheckpointIntegrityTests
{
    private const string Page1 = """
    {"value":[{"id":"u1"},{"id":"u2"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=P2"}
    """;
    private const string Page2 = """
    {"value":[{"id":"u3"}]}
    """;
    private const string ServerError = """{"error":{"code":"InternalServerError","message":"boom"}}""";

    private const string EmptyPage = """
    {"value":[],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=P3"}
    """;
    private const string LastPage = """
    {"value":[{"id":"u4"},{"id":"u5"}]}
    """;

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _steps = new();
        private readonly object _lock = new();
        public List<string> Requests { get; } = [];

        public void Queue(HttpStatusCode status, string body)
        {
            lock (_lock) _steps.Enqueue((status, body));
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            (HttpStatusCode Status, string Body) step;
            lock (_lock)
            {
                Requests.Add(request.RequestUri!.ToString());
                step = _steps.Count > 0 ? _steps.Dequeue() : (HttpStatusCode.InternalServerError, ServerError);
            }
            return Task.FromResult(new HttpResponseMessage(step.Status)
            {
                RequestMessage = request,
                Content = new StringContent(step.Body, Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>
    /// Three pages with the middle one empty behind a nextLink, which is how a page boundary is
    /// reached with no item written since the fetch. Every answer takes a real asynchronous hop:
    /// a socket read always yields and Task.FromResult never does, and the thread the iterator
    /// resumes on is what these pages are here to settle.
    /// </summary>
    private sealed class EmptyMiddlePageHandler : HttpMessageHandler
    {
        private readonly object _lock = new();
        public List<string> Urls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (_lock) { Urls.Add(url); }

            await Task.Yield();

            var body = url.Contains("skiptoken=P3", StringComparison.Ordinal) ? LastPage
                : url.Contains("skiptoken=P2", StringComparison.Ordinal) ? EmptyPage
                : Page1;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private static long? Export(string outputPath, string? checkpointPath, bool all, int top = 0,
        string uri = "/users")
    {
        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        ps.AddScript("function Get-MgContext { [PSCustomObject]@{ TenantId = 'test-tenant-00000000-0000-0000-0000-000000000000' } }");
        ps.Invoke();
        ps.Commands.Clear();

        var cmd = ps.AddCommand("Export-MgxCollection")
                    .AddParameter("Uri", uri)
                    .AddParameter("OutputFile", outputPath);
        if (checkpointPath != null) cmd.AddParameter("CheckpointPath", checkpointPath);
        if (all) cmd.AddParameter("All");
        if (top > 0) cmd.AddParameter("Top", top);
        try
        {
            foreach (var r in ps.Invoke())
                if (r?.BaseObject is Mgx.Cmdlets.Models.MgxExportResult summary)
                    return summary.ItemCount;
        }
        catch (CmdletInvocationException) { }
        return null;
    }

    /// <summary>
    /// An export whose warnings and error records the caller reads, rather than only the item
    /// count Export hands back. Nothing is swallowed here: a run that dies off the pipeline
    /// thread has to fail the test that says it does not.
    /// </summary>
    private static (List<string> Warnings, int Errors) ExportReporting(
        string outputPath, string checkpointPath)
    {
        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        ps.AddScript("function Get-MgContext { [PSCustomObject]@{ TenantId = 'test-tenant-00000000-0000-0000-0000-000000000000' } }");
        ps.Invoke();
        ps.Commands.Clear();

        ps.AddCommand("Export-MgxCollection")
          .AddParameter("Uri", "/users")
          .AddParameter("OutputFile", outputPath)
          .AddParameter("CheckpointPath", checkpointPath)
          .AddParameter("All");
        ps.Invoke();
        return ([.. ps.Streams.Warning.Select(w => w.Message)], ps.Streams.Error.Count);
    }

    private static string[] Lines(string path) => File.Exists(path) ? File.ReadAllLines(path) : [];

    private static string NewDir() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-export-ckpt-{Guid.NewGuid():N}")).FullName;

    /// <summary>
    /// A narrow export, then a full one that dies, then the documented resume. The dead run's
    /// first page went to a temp and its checkpoint counted those items; the output still holds
    /// only the narrow export's. Resuming has to not treat that output as though it already
    /// held them.
    /// </summary>
    [Fact]
    public void A_resume_does_not_count_an_earlier_exports_output_as_this_ones_items()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // Run 1: a one-item probe export. Completes, no checkpoint.
            handler.Queue(HttpStatusCode.OK, Page1);
            Assert.Equal(1, Export(output, checkpointPath: null, all: false, top: 1));
            Assert.Equal(["{\"id\":\"u1\"}"], Lines(output));

            // Run 2: the full export. Page 1 is written and checkpointed; page 2 fails.
            handler.Queue(HttpStatusCode.OK, Page1);
            handler.Queue(HttpStatusCode.InternalServerError, ServerError);
            Export(output, checkpoint, all: true);

            var cp = PaginationCheckpoint.Load(checkpoint);
            Assert.NotNull(cp);
            Assert.Equal(2, cp!.ItemsCollected);   // it counted u1 and u2...
            Assert.Equal(["{\"id\":\"u1\"}"], Lines(output));  // ...which are not in the output

            // Run 3: the documented resume. Only the page the dead run never reached.
            handler.Queue(HttpStatusCode.OK, Page2);
            var reported = Export(output, checkpoint, all: true);

            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"], Lines(output));
            Assert.Equal(3, reported);
            Assert.Empty(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A fresh checkpointed export that dies on a transient error. The checkpoint survives and
    /// counts items that exist only in the temp, so the temp has to survive with it - deleting
    /// it leaves the checkpoint naming a missing file, and the next run starts the export over.
    /// A kill or a Ctrl-C already kept the data; a handled error must not be the one way to
    /// lose the position.
    /// </summary>
    [Fact]
    public void A_transient_failure_keeps_the_temp_the_checkpoint_names()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            handler.Queue(HttpStatusCode.OK, Page1);
            handler.Queue(HttpStatusCode.InternalServerError, ServerError);
            Export(output, checkpoint, all: true);

            var cp = PaginationCheckpoint.Load(checkpoint);
            Assert.NotNull(cp);
            Assert.NotNull(cp!.TempFile);
            Assert.True(File.Exists(Path.Combine(dir, cp.TempFile!)),
                "the checkpoint names a temp that is no longer on disk");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// And the run after that failure resumes: it promotes the kept temp, fetches the page the
    /// checkpoint recorded rather than the first one, and ends with every item exactly once.
    /// </summary>
    [Fact]
    public void An_export_resumes_after_a_transient_failure_rather_than_starting_over()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            handler.Queue(HttpStatusCode.OK, Page1);
            handler.Queue(HttpStatusCode.InternalServerError, ServerError);
            Export(output, checkpoint, all: true);

            handler.Queue(HttpStatusCode.OK, Page2);
            var before = handler.Requests.Count;
            var reported = Export(output, checkpoint, all: true);

            Assert.Contains("skiptoken=P2", handler.Requests[before]);
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"], Lines(output));
            Assert.Equal(3, reported);
            Assert.False(File.Exists(checkpoint));
            Assert.Empty(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The last step of a completed export is the move that puts its temp over the output, and
    /// that move can fail with every page already fetched: a destination another process holds
    /// open, a read-only file, a share that dropped. Deleting the checkpoint before it made
    /// that failure read as "nothing was resumable", and the temp holding the whole export was
    /// deleted with it - hours of enumeration, at the one moment all of it was on disk. The two
    /// files have to be left exactly as an interruption leaves them, and the next run has to
    /// resume from them.
    /// </summary>
    [Fact]
    public void A_failed_promotion_keeps_the_checkpoint_and_the_temp_holding_the_export()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // Something at the output path the promotion cannot replace, so both pages are
            // fetched and written and the move at the end of the run is what fails.
            Directory.CreateDirectory(output);

            handler.Queue(HttpStatusCode.OK, Page1);
            handler.Queue(HttpStatusCode.OK, Page2);
            Assert.Null(Export(output, checkpoint, all: true));

            Assert.True(File.Exists(checkpoint),
                "the failed promotion deleted the position the next run resumes from");
            var cp = PaginationCheckpoint.Load(checkpoint);
            Assert.NotNull(cp);
            Assert.NotNull(cp!.TempFile);
            var temp = Path.Combine(dir, cp.TempFile!);
            Assert.True(File.Exists(temp),
                "the failed promotion deleted the temp holding every item the export fetched");
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(temp));

            // With the occupier gone: the temp is promoted, one page is fetched - the one the
            // checkpoint recorded - and the export is not enumerated again from the start.
            Directory.Delete(output);
            handler.Queue(HttpStatusCode.OK, Page2);
            var before = handler.Requests.Count;
            var reported = Export(output, checkpoint, all: true);

            Assert.Equal(1, handler.Requests.Count - before);
            Assert.Contains("skiptoken=P2", handler.Requests[before]);
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"], Lines(output));
            Assert.Equal(3, reported);
            Assert.False(File.Exists(checkpoint));
            Assert.Empty(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The ordinary re-baseline: the same export run again over its own previous output. A
    /// resume must not leave the previous run's rows in front of this run's.
    /// </summary>
    [Fact]
    public void A_resume_replaces_the_previous_export_rather_than_appending_to_it()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            handler.Queue(HttpStatusCode.OK, Page1);
            handler.Queue(HttpStatusCode.OK, Page2);
            Assert.Equal(3, Export(output, checkpointPath: null, all: true));

            handler.Queue(HttpStatusCode.OK, Page1);
            handler.Queue(HttpStatusCode.InternalServerError, ServerError);
            Export(output, checkpoint, all: true);

            handler.Queue(HttpStatusCode.OK, Page2);
            var before = handler.Requests.Count;
            var reported = Export(output, checkpoint, all: true);

            Assert.Contains("skiptoken=P2", handler.Requests[before]);
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"], Lines(output));
            Assert.Equal(3, reported);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A surviving temp is adopted rather than abandoned, even though an output from an earlier
    /// export is sitting next to it. The temp is what a hard kill leaves - the process dies
    /// before the cleanup that an ordinary failure runs - so it is staged directly here.
    /// </summary>
    [Fact]
    public void A_surviving_temp_is_promoted_over_an_earlier_exports_output()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            handler.Queue(HttpStatusCode.OK, Page1);
            Assert.Equal(1, Export(output, checkpointPath: null, all: false, top: 1));

            // What a kill -9 leaves partway through a fresh checkpointed run.
            var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            var bytes = "{\"id\":\"u1\"}\n{\"id\":\"u2\"}\n";
            File.WriteAllText(temp, bytes);
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = Path.GetFileName(temp),
                OutputFile = output,
                DataLength = new FileInfo(temp).Length
            }.Save(checkpoint);

            handler.Queue(HttpStatusCode.OK, Page2);
            Export(output, checkpoint, all: true);

            // The temp's items replaced the earlier export's output; they are not behind it.
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"], Lines(output));
            Assert.False(File.Exists(temp));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A checkpoint from a release that recorded neither the temp name nor a length cannot say
    /// where its items are, and both shapes reach this code: a run that was appending, whose
    /// items are in the output, and a fresh run killed mid-flight, whose items are in a temp
    /// while the output still holds a PREVIOUS export. Assuming the first appended the rest of
    /// the enumeration onto the earlier export.
    /// </summary>
    [Fact]
    public void A_checkpoint_that_cannot_say_where_its_items_are_exports_again_rather_than_appending()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // A previous export completed and left its output behind.
            File.WriteAllText(output, "{\"id\":\"old1\"}\n{\"id\":\"old2\"}\n{\"id\":\"old3\"}\n");
            // A later fresh run was killed: its items are in a temp, not in that output.
            var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, "{\"id\":\"u1\"}\n{\"id\":\"u2\"}\n");
            // The shape a pre-2.1.0 release wrote: no temp name, no length.
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0
            }.Save(checkpoint);

            handler.Queue(HttpStatusCode.OK, Page1);
            handler.Queue(HttpStatusCode.OK, Page2);
            var reported = Export(output, checkpoint, all: true);

            // Replaced, not appended: the previous export's rows are gone and each id appears once.
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"], Lines(output));
            Assert.Equal(3, reported);
            Assert.DoesNotContain(Lines(output), l => l.Contains("old"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The same shape as the delta case: an export resumed partway into a page has its first
    /// items dropped by the iterator before it sees them, so a checkpoint saved while still on
    /// that page has to count them too. Counting only what this run wrote makes the next resume
    /// skip too few and write the difference twice.
    /// </summary>
    [Fact]
    public void An_export_resumed_into_a_page_does_not_duplicate_it_on_a_second_interruption()
    {
        const string RefusedLink = "https://not-graph.example.com/v1.0/users?$skiptoken=P2";
        const string Page2Link = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2";

        static string Item(int i) => $"{{\"id\":\"p{i}\"}}";
        static string BigPage(string nextLink) =>
            $"{{\"value\":[{string.Join(",", Enumerable.Range(0, 1000).Select(Item))}],"
            + $"\"@odata.nextLink\":\"{nextLink}\"}}";

        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // One real run, only so the checkpoint on disk carries the exact URL the cmdlet
            // builds for these parameters.
            handler.Queue(HttpStatusCode.OK, Page1);
            handler.Queue(HttpStatusCode.InternalServerError, ServerError);
            Export(output, checkpoint, all: true);
            var resource = PaginationCheckpoint.Load(checkpoint)!.Resource;
            foreach (var t in Directory.GetFiles(dir, "out.jsonl.*.tmp")) File.Delete(t);

            // Ctrl-C 500 items into the first page: that run promoted what it had written and
            // recorded the output, 500 items in, all 500 from the page in flight.
            File.WriteAllLines(output, Enumerable.Range(0, 500).Select(Item));
            new PaginationCheckpoint
            {
                Resource = resource,
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P1",
                ItemsCollected = 500,
                PageItemsAlreadyWritten = 500,
                TempFile = null,
                DataLength = new FileInfo(output).Length,
            }.Save(checkpoint);

            // The resumed run writes the other 500 items, saves the mid-page checkpoint item
            // 1000 triggers, and is then refused the page's nextLink - so it dies before the
            // page boundary and that mid-page save is what survives.
            handler.Queue(HttpStatusCode.OK, BigPage(RefusedLink));
            Export(output, checkpoint, all: true);

            Assert.Equal(1000, Lines(output).Length);
            var mid = PaginationCheckpoint.Load(checkpoint)!;
            Assert.Equal(1000, mid.ItemsCollected);
            Assert.Equal(1000, mid.PageItemsAlreadyWritten);

            handler.Queue(HttpStatusCode.OK, BigPage(Page2Link));
            handler.Queue(HttpStatusCode.OK, """{"value":[{"id":"tail"}]}""");
            Export(output, checkpoint, all: true);

            var lines = Lines(output);
            Assert.Equal(lines.Length, lines.Distinct().Count());
            Assert.Equal(1001, lines.Length);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// A checkpoint is untrusted input once it is on disk. A negative recorded length reached
    /// SetLength, which rejects it with an exception the recovery path does not catch - so the
    /// run died naming neither the checkpoint nor the file, and left the checkpoint behind to
    /// fail the same way on every retry.
    /// </summary>
    [Fact]
    public void A_checkpoint_recording_a_negative_length_does_not_wedge_the_export()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            File.WriteAllText(output, "{\"id\":\"u1\"}\n{\"id\":\"u2\"}\n");
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = null,
                DataLength = -1
            }.Save(checkpoint);

            handler.Queue(HttpStatusCode.OK, Page1);
            handler.Queue(HttpStatusCode.OK, Page2);
            var reported = Export(output, checkpoint, all: true);

            // The unusable checkpoint is discarded and the export runs from the beginning.
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"], Lines(output));
            Assert.Equal(3, reported);
            Assert.False(File.Exists(checkpoint));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A fresh run sweeps leftover temps, and "leftover" is an assumption: a second export
    /// running against the same output owns a file matching the same glob right now. Windows
    /// refuses to delete a file held open and so declined by accident; Unix deletes it, and the
    /// other run keeps writing into an unlinked inode and loses everything it fetched.
    /// </summary>
    [Fact]
    public void A_fresh_export_does_not_delete_a_temp_another_run_is_writing()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var live = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // Another export holds its temp open, exactly as StreamWriter does.
            using (var held = new FileStream(live, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes("{\"id\":\"inflight\"}\n");
                held.Write(bytes, 0, bytes.Length);
                held.Flush();

                handler.Queue(HttpStatusCode.OK, Page1);
                handler.Queue(HttpStatusCode.OK, Page2);
                Export(output, checkpointPath: null, all: true);

                Assert.True(File.Exists(live), "the other run's temp was deleted out from under it");
            }

            Assert.Equal("{\"id\":\"inflight\"}", File.ReadAllLines(live).Single());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A checkpoint written by a release that recorded no output file, against the output it
    /// was in fact collecting into. The files corroborate it - the output is there and holds
    /// the bytes it counted - so an upgrade partway through an export still resumes.
    /// </summary>
    [Fact]
    public void A_checkpoint_from_before_outputs_were_recorded_resumes_against_its_own_output()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            File.WriteAllText(output, "{\"id\":\"u1\"}\n{\"id\":\"u2\"}\n");
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = null,
                DataLength = new FileInfo(output).Length,
            }.Save(checkpoint);

            handler.Queue(HttpStatusCode.OK, Page2);
            var before = handler.Requests.Count;
            var reported = Export(output, checkpoint, all: true);

            Assert.Contains("skiptoken=P2", handler.Requests[before]);
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"], Lines(output));
            Assert.Equal(3, reported);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The layout a pre-2.1.0 release left when a fresh export was killed: a checkpoint naming
    /// no temp and recording no length, its items in a temp beside an output that was never
    /// promoted. Deciding ownership from what such a checkpoint records refuses it for want of
    /// anything recorded, and the export then swept the temp holding those items and enumerated
    /// the collection again. What is on disk decides it instead - a temp carrying this output's
    /// own name, and no output for its items to be confused with.
    /// </summary>
    [Fact]
    public void A_checkpoint_from_before_temps_were_recorded_adopts_the_temp_holding_its_items()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // A fresh run's first page, in the temp it died holding.
            var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, "{\"id\":\"u1\"}\n{\"id\":\"u2\"}\n");
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0
            }.Save(checkpoint);

            handler.Queue(HttpStatusCode.OK, Page2);
            var before = handler.Requests.Count;
            var reported = Export(output, checkpoint, all: true);

            // Resumed onto the recovered items rather than fetching them a second time.
            Assert.Contains("skiptoken=P2", handler.Requests[before]);
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"], Lines(output));
            Assert.Equal(3, reported);
            Assert.False(File.Exists(temp), "the temp its items came from was left behind");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The same shape of checkpoint, against an output it was never measured against. Nothing
    /// in it names a file, and a null read as "mine" let a second export act on the first
    /// one's position - here by declaring the temp it names unusable and deleting the
    /// checkpoint, which is the one thing that made the first export's items unreachable.
    /// </summary>
    [Fact]
    public void A_checkpoint_from_before_outputs_were_recorded_is_not_this_exports_by_default()
    {
        var dir = NewDir();
        var mine = Path.Combine(dir, "users.jsonl");
        var theirs = Path.Combine(dir, "other.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // An export to users.jsonl collects page 1 into its temp and dies on page 2.
            handler.Queue(HttpStatusCode.OK, Page1);
            Export(mine, checkpoint, all: true);
            var temp = Assert.Single(Directory.GetFiles(dir, "users.jsonl.*.tmp"));

            // Its checkpoint, as the release before this one would have written it.
            var upgraded = PaginationCheckpoint.Load(checkpoint)!;
            upgraded.OutputFile = null;
            upgraded.Save(checkpoint);

            // A second export shares the checkpoint path and fails on its first page.
            Export(theirs, checkpoint, all: true);

            Assert.True(File.Exists(checkpoint), "the other export's position was deleted");
            Assert.True(File.Exists(temp), "the other export's temp was taken");

            // The first export still resumes onto what it had already collected.
            handler.Queue(HttpStatusCode.OK, Page2);
            var reported = Export(mine, checkpoint, all: true);

            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"], Lines(mine));
            Assert.Equal(3, reported);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The same export, resumed with -Uri typed differently. Graph answers "/users" and
    /// "/Users" from one collection, so both runs enumerate the same thing - but the recorded
    /// resource was compared ordinally, which made the second run a different export: the
    /// resume was refused, the collection was fetched again from its first page, and the
    /// caller was told their checkpoint belonged to something else.
    /// </summary>
    [Fact]
    public void A_checkpoint_written_under_another_spelling_of_the_resource_still_resumes()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            handler.Queue(HttpStatusCode.OK, Page1);
            Export(output, checkpoint, all: true, uri: "/users");

            handler.Queue(HttpStatusCode.OK, Page2);
            var before = handler.Requests.Count;
            var reported = Export(output, checkpoint, all: true, uri: "/Users");

            Assert.Contains("skiptoken=P2", handler.Requests[before]);
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"], Lines(output));
            Assert.Equal(3, reported);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The endpoint a session reports is not stable between runs: it comes back with a trailing
    /// slash, through a gateway prefix, or from a sovereign cloud. None of that changes which
    /// resource an export enumerates, but comparing whole URLs made it change whose checkpoint
    /// the file on disk was - so the resume was refused and the collection re-enumerated from
    /// the first page.
    /// </summary>
    [Fact]
    public void A_checkpoint_written_under_another_spelling_of_the_endpoint_still_resumes()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();

            using (MgxTransportScope.Inject(handler, endpoint: "https://graph.microsoft.com/"))
            {
                handler.Queue(HttpStatusCode.OK, Page1);
                Export(output, checkpoint, all: true);
            }

            var cp = PaginationCheckpoint.Load(checkpoint);
            Assert.NotNull(cp);
            Assert.Contains("//v1.0/users", cp!.Resource);

            using (MgxTransportScope.Inject(handler, endpoint: "https://graph.microsoft.com"))
            {
                handler.Queue(HttpStatusCode.OK, Page2);
                var before = handler.Requests.Count;
                var reported = Export(output, checkpoint, all: true);

                Assert.Contains("skiptoken=P2", handler.Requests[before]);
                Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"], Lines(output));
                Assert.Equal(3, reported);
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A checkpoint the export cannot write, over pages whose middle one is empty behind a
    /// nextLink. The page-boundary callback runs on whichever thread the iterator resumed on,
    /// and a page that writes nothing resumes on the thread pool - so warning about the disk
    /// from there ended the export at that page, taking the pages behind it with it. The
    /// message is buffered on the client's warning channel and written by a drain on the
    /// pipeline thread: one per failed save, and every page still reaches the output.
    /// </summary>
    [Fact]
    public void A_failed_boundary_save_warns_and_the_export_goes_on()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            // Every save into this path fails: Save stages its bytes under this name first, and
            // no account writes a file over a directory. The directory holding it is not sealed
            // instead, the way a read-only disk would seal it, because the export writes its
            // output there too and that write is not what is under test.
            Directory.CreateDirectory(checkpoint + ".tmp");

            var handler = new EmptyMiddlePageHandler();
            using var transport = MgxTransportScope.Inject(handler);
            var (warnings, errors) = ExportReporting(output, checkpoint);

            Assert.Equal(0, errors);

            // Both boundaries carrying a nextLink try to save, and both fail.
            Assert.Equal(2, warnings.Count(
                w => w.StartsWith("Checkpoint save failed (page boundary)", StringComparison.Ordinal)));

            // The empty page sits behind the second request, so an export that died reporting
            // its failed save never asks for the third - and the output ends two items short.
            Assert.Equal(3, handler.Urls.Count);
            Assert.Contains("skiptoken=P3", handler.Urls[2]);
            Assert.Equal(
                ["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u4\"}", "{\"id\":\"u5\"}"],
                Lines(output));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
