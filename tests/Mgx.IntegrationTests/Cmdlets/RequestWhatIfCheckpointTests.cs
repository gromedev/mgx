using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Management.Automation.Runspaces;
using System.Net;
using System.Security;
using System.Text;
using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests;

/// <summary>
/// -WhatIf on a list run carrying -CheckpointPath. The read path saves the next position over
/// that file at every page boundary, deletes it on completion, and deletes it where it turns
/// out to describe another enumeration - so previewing a run destroyed the position the caller
/// resumes from. The reads are not what is gated: a GET under -WhatIf is sent, pages and emits
/// its objects, which is the convention Invoke-MgxRequest documents.
/// </summary>
[Collection("Pipeline")]
public class RequestWhatIfCheckpointTests
{
    private const string Page1 = """
    {"value":[{"id":"u1"},{"id":"u2"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=P2"}
    """;
    private const string Page2 = """
    {"value":[{"id":"u3"}]}
    """;
    private const string NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2";

    private const string EmptyPage = """
    {"value":[],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=P3"}
    """;
    private const string LastPage = """
    {"value":[{"id":"u4"},{"id":"u5"}]}
    """;

    /// <summary>
    /// Two pages, plus a snapshot of the checkpoint file as each request arrives. The
    /// page-boundary save lands between the first response and the second request and the
    /// completion delete takes it away again, so mid-run is the only point from which a test
    /// can see whether a run wrote a checkpoint at all.
    /// </summary>
    private sealed class PagingHandler(string? checkpointPath) : HttpMessageHandler
    {
        private readonly object _lock = new();
        public List<string> Urls { get; } = [];
        public List<bool> CheckpointExisted { get; } = [];
        public List<string?> CheckpointContent { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (_lock)
            {
                Urls.Add(url);
                var existed = checkpointPath != null && File.Exists(checkpointPath);
                CheckpointExisted.Add(existed);
                CheckpointContent.Add(existed ? File.ReadAllText(checkpointPath!) : null);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(
                    url.Contains("skiptoken", StringComparison.Ordinal) ? Page2 : Page1,
                    Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>
    /// Three pages with the middle one empty behind a nextLink, which is how a page boundary is
    /// reached with no item yielded since the fetch. Every answer takes a real asynchronous hop:
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

    /// <summary>
    /// Page 1 has items and a nextLink; whatever answers the request that follows is not JSON at
    /// all. Awaits Task.Yield() for the same reason the empty-page handler does: every answer
    /// takes a real asynchronous hop, so the page-boundary callback - which fires once page 1's
    /// two items are both yielded, before the second request goes out - runs on whichever thread
    /// that hop left it on rather than synchronously on the caller's.
    /// </summary>
    private sealed class MalformedSecondPageHandler : HttpMessageHandler
    {
        private readonly object _lock = new();
        public List<string> Urls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (_lock) { Urls.Add(url); }

            await Task.Yield();

            var body = url.Contains("skiptoken", StringComparison.Ordinal)
                ? "not a collection envelope at all"
                : Page1;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    /// <summary>The lines -WhatIf writes go to the host, so a test that reads them needs one.</summary>
    private sealed class RecordingHost : PSHost
    {
        private readonly Guid _id = Guid.NewGuid();
        public RecordingHostUI Recorder { get; } = new();
        public override string Name => "MgxTestHost";
        public override Version Version => new(1, 0);
        public override Guid InstanceId => _id;
        public override PSHostUserInterface UI => Recorder;
        public override CultureInfo CurrentCulture => CultureInfo.InvariantCulture;
        public override CultureInfo CurrentUICulture => CultureInfo.InvariantCulture;
        public override void EnterNestedPrompt() { }
        public override void ExitNestedPrompt() { }
        public override void NotifyBeginApplication() { }
        public override void NotifyEndApplication() { }
        public override void SetShouldExit(int exitCode) { }
    }

    private sealed class RecordingHostUI : PSHostUserInterface
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
        public override SecureString ReadLineAsSecureString() => new();
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

    private static string NewDir() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-req-whatif-{Guid.NewGuid():N}")).FullName;

    /// <summary>
    /// Walks the collection and hands back the lines the host was given, how many objects the
    /// run emitted, whether it errored, the warnings it wrote, and the ids of the errors it
    /// wrote.
    /// </summary>
    private static (List<string> Lines, int Items, bool HadErrors, List<string> Warnings, List<string> Errors) Enumerate(
        string? checkpoint, bool whatIf)
    {
        var host = new RecordingHost();
        using var runspace = RunspaceFactory.CreateRunspace(host);
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.InvokeMgxRequest).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        var cmd = ps.AddCommand("Invoke-MgxRequest")
                    .AddParameter("Uri", "/users")
                    .AddParameter("All")
                    .AddParameter("PageSize", 2);
        if (checkpoint != null) cmd.AddParameter("CheckpointPath", checkpoint);
        if (whatIf) cmd.AddParameter("WhatIf", true);
        var output = ps.Invoke();
        return (host.Recorder.Lines, output.Count, ps.HadErrors,
            [.. ps.Streams.Warning.Select(w => w.Message)],
            [.. ps.Streams.Error.Select(e => e.FullyQualifiedErrorId)]);
    }

    /// <summary>
    /// The URL this enumeration asks for. A checkpoint is the run's own only when the resource
    /// it records matches that string exactly, and writing the query shape into the test would
    /// pin the URL builder instead of the gate.
    /// </summary>
    private static string EnumerationUrl()
    {
        var handler = new PagingHandler(checkpointPath: null);
        using var transport = MgxTransportScope.Inject(handler);
        Enumerate(checkpoint: null, whatIf: false);
        return handler.Urls[0];
    }

    /// <summary>
    /// A checkpoint another enumeration left over the same -CheckpointPath. A real run refuses
    /// it, deletes it and pages from the start; the preview pages from the start too - same
    /// requests, same objects - and leaves the caller's file exactly as it found it.
    /// </summary>
    [Fact]
    public void WhatIf_leaves_another_enumerations_checkpoint_byte_identical()
    {
        var dir = NewDir();
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/groups?$top=2",
                NextLink = "https://graph.microsoft.com/v1.0/groups?$skiptoken=P2",
                ItemsCollected = 2,
            }.Save(checkpoint);
            var before = File.ReadAllBytes(checkpoint);

            var handler = new PagingHandler(checkpoint);
            using var transport = MgxTransportScope.Inject(handler);
            var (lines, items, hadErrors, _, _) = Enumerate(checkpoint, whatIf: true);

            Assert.False(hadErrors);

            // The reads are not what the gate withholds: both pages were fetched and every
            // object went down the pipeline.
            Assert.Equal(2, handler.Urls.Count);
            Assert.Equal(3, items);

            // The file is what it withholds, and the caller's is where it was.
            Assert.True(File.Exists(checkpoint), "-WhatIf deleted the caller's checkpoint");
            Assert.Equal(before, File.ReadAllBytes(checkpoint));

            // Named on the way past, so the caller running the preview is told which file a
            // real run would have written.
            Assert.Contains(lines,
                l => l.Contains("Save resume checkpoint") && l.Contains(checkpoint));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A completion marker (NextLink null) left by this enumeration's own previous run. A real
    /// run takes that as "already finished" and deletes it before paging from the start again;
    /// the preview takes the same route through the same Delete call, gated the same way as the
    /// page-boundary save and the mismatched-resource branch above - and mayWriteCheckpoint is
    /// the only thing standing between it and deleting a marker -WhatIf is not supposed to touch.
    /// </summary>
    [Fact]
    public void WhatIf_leaves_a_completion_marker_checkpoint_byte_identical()
    {
        var dir = NewDir();
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            new PaginationCheckpoint
            {
                Resource = EnumerationUrl(),
                NextLink = null,
                ItemsCollected = 7,
            }.Save(checkpoint);
            var before = File.ReadAllBytes(checkpoint);

            var handler = new PagingHandler(checkpoint);
            using var transport = MgxTransportScope.Inject(handler);
            var (_, items, hadErrors, _, _) = Enumerate(checkpoint, whatIf: true);

            Assert.False(hadErrors);

            // A completion marker carries no position to resume from, so both pages are asked
            // for and all three objects go down the pipeline either way.
            Assert.Equal(2, handler.Urls.Count);
            Assert.Equal(3, items);

            Assert.True(File.Exists(checkpoint), "-WhatIf deleted the caller's completion-marker checkpoint");
            Assert.Equal(before, File.ReadAllBytes(checkpoint));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// This enumeration's own checkpoint, but its nextLink is one NextLinkValidator refuses (here,
    /// relative rather than https) - the SSRF gate, not a stale or foreign resource. A real run
    /// cannot resume from a link it will not follow, so it deletes the checkpoint and pages from
    /// the start; the preview takes the same branch through the same Delete call, and withholds it
    /// the same way.
    /// </summary>
    [Fact]
    public void WhatIf_leaves_a_refused_nextlink_checkpoint_byte_identical()
    {
        var dir = NewDir();
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            new PaginationCheckpoint
            {
                Resource = EnumerationUrl(),
                NextLink = "pages/4",
                ItemsCollected = 7,
            }.Save(checkpoint);
            var before = File.ReadAllBytes(checkpoint);

            var handler = new PagingHandler(checkpoint);
            using var transport = MgxTransportScope.Inject(handler);
            var (_, items, hadErrors, _, _) = Enumerate(checkpoint, whatIf: true);

            Assert.False(hadErrors);

            // The refused link carries no usable position either, so this run pages from the
            // start the same as the completion-marker case above.
            Assert.Equal(2, handler.Urls.Count);
            Assert.Equal(3, items);

            Assert.True(File.Exists(checkpoint),
                "-WhatIf deleted the caller's checkpoint with a nextLink the validator refuses");
            Assert.Equal(before, File.ReadAllBytes(checkpoint));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The same run with nothing at -CheckpointPath to begin with. A preview that leaves a
    /// checkpoint behind has written a resume position for an enumeration that never happened,
    /// and one that writes and then deletes has still had the caller's file for the length of
    /// the run - so the page boundary, not the end, is where this is settled.
    /// </summary>
    [Fact]
    public void WhatIf_writes_no_checkpoint_of_its_own()
    {
        var dir = NewDir();
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new PagingHandler(checkpoint);
            using var transport = MgxTransportScope.Inject(handler);
            var (_, items, hadErrors, _, _) = Enumerate(checkpoint, whatIf: true);

            Assert.False(hadErrors);
            Assert.Equal(3, items);
            Assert.Equal(2, handler.CheckpointExisted.Count);
            Assert.All(handler.CheckpointExisted,
                existed => Assert.False(existed, "-WhatIf saved a checkpoint of its own"));
            Assert.False(File.Exists(checkpoint));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A checkpoint this enumeration wrote. The preview is of the run the caller would make
    /// next, and that run resumes - so the preview resumes too, and asks only for the page the
    /// checkpoint stopped at. What it cannot do is finish by deleting the position it read.
    /// </summary>
    [Fact]
    public void WhatIf_resumes_from_a_matching_checkpoint_without_consuming_it()
    {
        var dir = NewDir();
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            new PaginationCheckpoint
            {
                Resource = EnumerationUrl(),
                NextLink = NextLink,
                ItemsCollected = 2,
            }.Save(checkpoint);
            var before = File.ReadAllBytes(checkpoint);

            var handler = new PagingHandler(checkpoint);
            using var transport = MgxTransportScope.Inject(handler);
            var (_, items, hadErrors, _, _) = Enumerate(checkpoint, whatIf: true);

            Assert.False(hadErrors);
            Assert.Single(handler.Urls);
            Assert.Contains("skiptoken=P2", handler.Urls[0]);
            Assert.Equal(1, items);
            Assert.True(File.Exists(checkpoint), "-WhatIf deleted the checkpoint it resumed from");
            Assert.Equal(before, File.ReadAllBytes(checkpoint));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The run the previews above describe. Nothing about the checkpoint changes for a real
    /// run: the next position is saved at the page boundary, and the file is gone once the
    /// enumeration completes.
    /// </summary>
    [Fact]
    public void A_real_run_saves_at_the_page_boundary_and_deletes_on_completion()
    {
        var dir = NewDir();
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new PagingHandler(checkpoint);
            using var transport = MgxTransportScope.Inject(handler);
            var (_, items, hadErrors, _, _) = Enumerate(checkpoint, whatIf: false);

            Assert.False(hadErrors);
            Assert.Equal(3, items);
            Assert.False(handler.CheckpointExisted[0]);
            Assert.True(handler.CheckpointExisted[1], "no checkpoint was saved at the page boundary");
            Assert.Contains("skiptoken=P2", handler.CheckpointContent[1]!);
            Assert.False(File.Exists(checkpoint));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The same real run, against a checkpoint it cannot write. The page-boundary callback runs
    /// on whichever thread the iterator resumed on, and a page whose value is empty resumes on
    /// the thread pool with no yield in between - so a cmdlet API call from there throws
    /// PSInvalidOperationException, and warning the caller about the disk ended the enumeration
    /// at the empty page instead. The message goes onto the client's buffered warning channel
    /// and the pipeline thread's drain writes it: one per failed save, and the pages behind the
    /// empty one are still asked for and still emitted.
    /// </summary>
    [Fact]
    public void A_failed_boundary_save_warns_and_the_enumeration_goes_on()
    {
        var dir = NewDir();
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            BlockSaves(dir, checkpoint);

            var handler = new EmptyMiddlePageHandler();
            using var transport = MgxTransportScope.Inject(handler);
            var (_, items, hadErrors, warnings, _) = Enumerate(checkpoint, whatIf: false);

            Assert.False(hadErrors);

            // Both boundaries that carry a nextLink try to save, and both fail against a
            // checkpoint that cannot be written.
            var failures = warnings
                .Where(w => w.StartsWith("Checkpoint save failed", StringComparison.Ordinal))
                .ToList();
            Assert.Equal(2, failures.Count);

            // The empty page sits behind the second request, so a run that died reporting its
            // failed save never asks for the third - and the two items on it never arrive.
            Assert.Equal(3, handler.Urls.Count);
            Assert.Contains("skiptoken=P3", handler.Urls[2]);
            Assert.Equal(4, items);
        }
        finally
        {
            RestoreMode(dir);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The same blocked checkpoint, but the run this time does not reach the successful exit
    /// the previous test measures: page 1's boundary save fails same as before, and what
    /// answers the next request is not JSON at all. That exit is the JsonException catch, which
    /// returns straight after writing its error record - there is no next item left to carry the
    /// per-item drain, and until the enumeration loop's exits all shared a single finally, that
    /// catch was one of three that returned without draining, so the buffered "Checkpoint save
    /// failed" warning stayed on the queue and was never written.
    /// </summary>
    [Fact]
    public void A_failed_boundary_save_warns_even_when_the_next_page_does_not_parse()
    {
        var dir = NewDir();
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            BlockSaves(dir, checkpoint);

            var handler = new MalformedSecondPageHandler();
            using var transport = MgxTransportScope.Inject(handler);
            var (_, items, hadErrors, warnings, errors) = Enumerate(checkpoint, whatIf: false);

            Assert.True(hadErrors);
            var errorId = Assert.Single(errors);
            Assert.StartsWith("MalformedJsonResponse", errorId);

            var failures = warnings
                .Where(w => w.StartsWith("Checkpoint save failed", StringComparison.Ordinal))
                .ToList();
            Assert.Single(failures);

            // Page 1's two items were already on the pipeline before the second request's body
            // failed to parse.
            Assert.Equal(2, items);
        }
        finally
        {
            RestoreMode(dir);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// Makes every checkpoint save into this directory fail, whatever the host allows. A mode
    /// the account cannot write is the honest version of it, but root ignores mode bits and
    /// Windows expresses this through ACLs; where a canary shows the mode does not bite, the
    /// name Save stages its bytes under is taken by a directory instead, which no account can
    /// write a file over.
    /// </summary>
    private static void BlockSaves(string dir, string checkpoint)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                var canary = Path.Combine(dir, "writable.probe");
                File.WriteAllText(canary, "");
                File.Delete(canary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
            RestoreMode(dir);
        }

        Directory.CreateDirectory(checkpoint + ".tmp");
    }

    private static void RestoreMode(string dir)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
