using System.Management.Automation;
using System.Net;
using System.Text;
using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests;

/// <summary>
/// A checkpoint's recorded length is a byte offset into "the output", and the recovery path
/// applies it to whatever -OutputFile the run names. Nothing in a checkpoint used to say which
/// file those bytes were counted in, so one -CheckpointPath reused by two exports cut a file
/// the checkpoint knew nothing about - mid-line, with the resumed pages appended onto the torn
/// byte, and no warning on any stream.
/// </summary>
[Collection("Pipeline")]
public class ExportSharedCheckpointTests
{
    private const string UsersPage1 = """
    {"value":[{"id":"u1"},{"id":"u2"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=P2"}
    """;
    private const string UsersPage2 = """
    {"value":[{"id":"u3"}]}
    """;
    // The same page with a continuation behind it, so a resumed run writes and flushes a page
    // before the request after it can be held open.
    private const string UsersPage2Continued = """
    {"value":[{"id":"u3"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=P3"}
    """;
    private const string UsersPage3 = """
    {"value":[{"id":"u4"}]}
    """;
    private const string GroupsPage1 = """
    {"value":[{"id":"g1"},{"id":"g2"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/groups?$skiptoken=P2"}
    """;
    private const string GroupsPage2 = """
    {"value":[{"id":"g3"}]}
    """;
    private const string ServerError = """{"error":{"code":"InternalServerError","message":"boom"}}""";
    private const string TopUnsupported = """
    {"error":{"code":"Request_UnsupportedQuery","message":"$top is not supported on this endpoint"}}
    """;

    private static readonly string[] OldExport =
        ["{\"id\":\"old-0000001\"}", "{\"id\":\"old-0000002\"}", "{\"id\":\"old-0000003\"}"];

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _steps = new();
        private readonly object _lock = new();
        private int _served;

        /// <summary>How many requests this handler has answered. A run that stops adds none.</summary>
        public int Served { get { lock (_lock) return _served; } }

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
                _served++;
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
    /// Refuses the automatic $top on the page after the first: page one answers with the option
    /// on it, the nextLink that follows comes back Request_UnsupportedQuery, and the retry
    /// without it finds the whole collection in a single page. An attempt loop that goes round
    /// with a page-boundary checkpoint already on disk, and finishes without saving one.
    /// </summary>
    private sealed class TopRefusedAfterPageOneHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            var (status, body) = url.Contains("skiptoken=P2", StringComparison.Ordinal)
                ? (HttpStatusCode.BadRequest, TopUnsupported)
                : url.Contains("$top=", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, UsersPage1)
                    : (HttpStatusCode.OK, UsersPage2);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>
    /// A scripted handler that holds one request open until it is let go: the run parked there
    /// has written and flushed everything the pages before it carried, and is appending still.
    /// </summary>
    private sealed class GatedHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _steps = new();
        private readonly int _gateOn;
        private readonly object _lock = new();
        private int _served;

        /// <param name="gateOn">Which request, counting from one, is held at the gate.</param>
        internal GatedHandler(int gateOn) => _gateOn = gateOn;

        internal void Queue(HttpStatusCode status, string body)
        {
            lock (_lock) _steps.Enqueue((status, body));
        }

        /// <summary>Set the moment the request being gated arrives.</summary>
        internal ManualResetEventSlim Arrived { get; } = new(false);

        /// <summary>Set by the test to let the parked request answer.</summary>
        internal ManualResetEventSlim Released { get; } = new(false);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int n;
            (HttpStatusCode Status, string Body) step;
            lock (_lock)
            {
                n = ++_served;
                step = _steps.Count > 0 ? _steps.Dequeue() : (HttpStatusCode.OK, UsersPage2);
            }
            if (n == _gateOn)
            {
                Arrived.Set();
                Released.Wait(cancellationToken);
            }
            return Task.FromResult(new HttpResponseMessage(step.Status)
            {
                RequestMessage = request,
                Content = new StringContent(step.Body, Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>
    /// Runs an export on this thread and parks it inside the window the hold exists to close:
    /// past the reconcile, which has claimed the output and cut it back, and short of the writer
    /// that used to open a handle of its own a page fetch later. The verbose the resume announces
    /// itself with is written from exactly there, and a subscriber to that stream blocks the
    /// pipeline where it is raised.
    /// </summary>
    private static (string[] Warnings, ErrorRecord[] Errors) ExportParkedAtResume(
        string uri, string outputPath, string checkpointPath,
        ManualResetEventSlim arrived, ManualResetEventSlim released)
    {
        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection).Assembly);
        ps.Invoke();
        ps.Commands.Clear();

        ps.Streams.Verbose.DataAdding += (_, e) =>
        {
            if (e.ItemAdded is VerboseRecord v
                && v.Message.Contains("Resuming from checkpoint", StringComparison.Ordinal)
                && !arrived.IsSet)
            {
                arrived.Set();
                released.Wait();
            }
        };

        ps.AddCommand("Export-MgxCollection")
          .AddParameter("Uri", uri)
          .AddParameter("OutputFile", outputPath)
          .AddParameter("CheckpointPath", checkpointPath)
          .AddParameter("All")
          .AddParameter("Verbose");
        List<ErrorRecord> errors = [];
        try { ps.Invoke(); }
        catch (CmdletInvocationException ex) { errors.Add(ex.ErrorRecord); }
        errors.AddRange(ps.Streams.Error);
        return ([.. ps.Streams.Warning.Select(w => w.Message)], [.. errors]);
    }

    /// <summary>
    /// An export built and waiting to be let go: the runspace is up and the module imported
    /// before <paramref name="ready"/> is set, so what <paramref name="go"/> releases is two runs
    /// arriving at the reconcile together rather than two runspaces starting up.
    /// </summary>
    private static (string[] Warnings, ErrorRecord[] Errors) ExportReleasedWith(
        string uri, string outputPath, string checkpointPath,
        ManualResetEventSlim ready, ManualResetEventSlim go)
    {
        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection).Assembly);
        ps.Invoke();
        ps.Commands.Clear();

        ps.AddCommand("Export-MgxCollection")
          .AddParameter("Uri", uri)
          .AddParameter("OutputFile", outputPath)
          .AddParameter("CheckpointPath", checkpointPath)
          .AddParameter("All");

        ready.Set();
        go.Wait();
        List<ErrorRecord> errors = [];
        try { ps.Invoke(); }
        catch (CmdletInvocationException ex) { errors.Add(ex.ErrorRecord); }
        errors.AddRange(ps.Streams.Error);
        return ([.. ps.Streams.Warning.Select(w => w.Message)], [.. errors]);
    }

    /// <summary>
    /// Answers by URL rather than from a queue, and holds the first request that arrives until it
    /// is let go. Two runs racing over one command line each get the page they ask for however
    /// they interleave, and the one that reaches the wire - which is the one that got past the
    /// reconcile - keeps the output for as long as the test needs it to.
    /// </summary>
    private sealed class GatedUsersByUrlHandler : HttpMessageHandler
    {
        private int _served;

        /// <summary>How many requests this handler has answered or is holding.</summary>
        internal int Served => Volatile.Read(ref _served);

        /// <summary>Set the moment the first request arrives.</summary>
        internal ManualResetEventSlim Arrived { get; } = new(false);

        /// <summary>Set by the test to let the parked request answer.</summary>
        internal ManualResetEventSlim Released { get; } = new(false);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _served) == 1)
            {
                Arrived.Set();
                Released.Wait(cancellationToken);
            }
            var body = request.RequestUri!.ToString()
                .Contains("skiptoken=P2", StringComparison.Ordinal) ? UsersPage2 : UsersPage1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static long? Export(string uri, string outputPath, string checkpointPath)
    {
        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection).Assembly);
        ps.Invoke();
        ps.Commands.Clear();

        ps.AddCommand("Export-MgxCollection")
          .AddParameter("Uri", uri)
          .AddParameter("OutputFile", outputPath)
          .AddParameter("CheckpointPath", checkpointPath)
          .AddParameter("All");
        try
        {
            foreach (var r in ps.Invoke())
                if (r?.BaseObject is Mgx.Cmdlets.Models.MgxExportResult summary)
                    return summary.ItemCount;
        }
        catch (CmdletInvocationException) { }
        return null;
    }

    private static void ExportNoCheckpoint(string uri, string outputPath)
    {
        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        ps.AddCommand("Export-MgxCollection")
          .AddParameter("Uri", uri)
          .AddParameter("OutputFile", outputPath)
          .AddParameter("All");
        ps.Invoke();
    }

    private static string[] ExportWarnings(string uri, string outputPath, string checkpointPath)
    {
        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection).Assembly);
        ps.Invoke();
        ps.Commands.Clear();

        ps.AddCommand("Export-MgxCollection")
          .AddParameter("Uri", uri)
          .AddParameter("OutputFile", outputPath)
          .AddParameter("CheckpointPath", checkpointPath)
          .AddParameter("All");
        try { ps.Invoke(); }
        catch (CmdletInvocationException) { }
        return [.. ps.Streams.Warning.Select(w => w.Message)];
    }

    /// <summary>
    /// The same export with the error stream captured too. A run that will not touch the two
    /// files the checkpoint stands for ends on a terminating error, and the id and the sentence
    /// are the whole of what the caller has to act on. Both places PowerShell can put that
    /// record are read, since which one it uses depends on how the command was added.
    /// </summary>
    private static (string[] Warnings, ErrorRecord[] Errors) ExportRun(string uri,
        string outputPath, string checkpointPath)
    {
        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection).Assembly);
        ps.Invoke();
        ps.Commands.Clear();

        ps.AddCommand("Export-MgxCollection")
          .AddParameter("Uri", uri)
          .AddParameter("OutputFile", outputPath)
          .AddParameter("CheckpointPath", checkpointPath)
          .AddParameter("All");
        List<ErrorRecord> errors = [];
        try { ps.Invoke(); }
        catch (CmdletInvocationException ex) { errors.Add(ex.ErrorRecord); }
        errors.AddRange(ps.Streams.Error);
        return ([.. ps.Streams.Warning.Select(w => w.Message)], [.. errors]);
    }

    /// <summary>
    /// Everything a run that stops may not have moved: the checkpoint, the temp it names, the
    /// output if there is one, and the directory's own entry list - which is what says no temp
    /// of the stopped run's outlived it.
    /// </summary>
    private sealed class DiskState
    {
        public byte[] Checkpoint = [];
        public byte[] Temp = [];
        public byte[]? Output;
        public string[] Entries = [];
    }

    private static DiskState ReadDisk(string dir, string checkpointPath, string temp,
        string outputPath) => new()
    {
        Checkpoint = File.ReadAllBytes(checkpointPath),
        Temp = ReadShared(temp),
        Output = File.Exists(outputPath) ? ReadShared(outputPath) : null,
        Entries = [.. Directory.GetFiles(dir).Select(f => Path.GetFileName(f))
                            .OrderBy(n => n, StringComparer.Ordinal)],
    };

    /// <summary>
    /// The same, for the shape a resumed export leaves: its items are in the output itself and
    /// the checkpoint names no temp, so there is no third file to read.
    /// </summary>
    private static DiskState ReadDisk(string dir, string checkpointPath, string outputPath) => new()
    {
        Checkpoint = File.ReadAllBytes(checkpointPath),
        Output = ReadShared(outputPath),
        Entries = [.. Directory.GetFiles(dir).Select(f => Path.GetFileName(f))
                            .OrderBy(n => n, StringComparer.Ordinal)],
    };

    private static void AssertUnmoved(DiskState before, DiskState after)
    {
        Assert.Equal(before.Entries, after.Entries);
        Assert.Equal(before.Checkpoint, after.Checkpoint);
        Assert.Equal(before.Temp, after.Temp);
        Assert.Equal(before.Output, after.Output);
    }

    /// <summary>
    /// Reads a file a live run still holds open, offering the sharing that holder asked for.
    /// Windows checks the sharing both ways round - what the reader asks of the file, and what
    /// it will let a handle already on it go on doing - so File.ReadAllBytes, which asks for
    /// FileShare.Read and nothing more, is refused by a run holding the file to write it.
    /// </summary>
    private static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    private static string NewDir() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-export-shared-{Guid.NewGuid():N}")).FullName;

    /// <summary>
    /// A FIFO at <paramref name="path"/>, which is one of the things a caller can leave standing
    /// where -OutputFile points. .NET has no call for it, so the platform's own is used.
    /// </summary>
    private static void Mkfifo(string path)
    {
        using var mkfifo = System.Diagnostics.Process.Start("/usr/bin/mkfifo", [path]);
        Assert.NotNull(mkfifo);
        mkfifo.WaitForExit();
        Assert.Equal(0, mkfifo.ExitCode);
    }

    /// <summary>
    /// What each refusal says. A checkpoint recording another export's output is a second
    /// export over one -CheckpointPath, and naming that is what the caller acts on. One
    /// recording no output at all is not: it was believed while the files beside it
    /// corroborated it, and a single export whose temp has since gone, or whose output has been
    /// replaced, reaches the same refusal. Reporting a different export there names a cause the
    /// caller can go and check and find nothing behind.
    /// </summary>
    [Fact]
    public void A_refusal_reports_the_cause_it_can_show()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            File.WriteAllLines(output, OldExport);

            // Written before outputs were recorded, counting more bytes than this output holds.
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = null,
                OutputFile = null,
                DataLength = new FileInfo(output).Length + 4096,
            }.Save(checkpoint);

            var uncorroborated = ExportWarnings("/users", output, checkpoint);
            Assert.Contains(uncorroborated, w => w.Contains("no longer corroborate"));
            Assert.DoesNotContain(uncorroborated, w => w.Contains("belongs to a different export"));

            // The same position, recording an output this run is not writing to.
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = null,
                OutputFile = Path.Combine(dir, "elsewhere.jsonl"),
                DataLength = 24,
            }.Save(checkpoint);

            Assert.Contains(ExportWarnings("/users", output, checkpoint),
                w => w.Contains("belongs to a different export"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// Two exports of the same resource to different output files, sharing one checkpoint path.
    /// The second run's output must not be cut back to the first run's byte count.
    /// </summary>
    [Fact]
    public void A_checkpoint_from_another_export_does_not_cut_this_ones_output()
    {
        var dir = NewDir();
        var outA = Path.Combine(dir, "outA.jsonl");
        var outB = Path.Combine(dir, "outB.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // Export A dies on page 2, twice: the second run promotes the temp, so the
            // checkpoint it leaves records a length with no temp to explain it.
            handler.Queue(HttpStatusCode.OK, UsersPage1);
            Export("/users", outA, checkpoint);
            Export("/users", outA, checkpoint);

            var cp = PaginationCheckpoint.Load(checkpoint)!;
            Assert.Null(cp.TempFile);
            Assert.Equal(new FileInfo(outA).Length, cp.DataLength);

            // Export B: same resource, its own output, which already holds an earlier export.
            File.WriteAllLines(outB, OldExport);
            handler.Queue(HttpStatusCode.OK, UsersPage1);
            handler.Queue(HttpStatusCode.OK, UsersPage2);
            var reported = Export("/users", outB, checkpoint);

            var lines = File.ReadAllLines(outB);
            Assert.All(lines, l => Assert.StartsWith("{\"id\":\"", l));
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"], lines);
            Assert.Equal(3, reported);

            // A's own output is untouched by B.
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}"], File.ReadAllLines(outA));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A checkpoint left by an export of a DIFFERENT resource, against the same output name.
    /// The resource is compared before anything is cut, so a run that then fails leaves the
    /// output it never replaced exactly as it found it.
    /// </summary>
    [Fact]
    public void A_checkpoint_for_another_resource_does_not_destroy_an_output_the_run_never_replaces()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // An interrupted export of /groups, resumed once so its checkpoint names no temp.
            handler.Queue(HttpStatusCode.OK, GroupsPage1);
            Export("/groups", output, checkpoint);
            Export("/groups", output, checkpoint);
            Assert.Equal(2, File.ReadAllLines(output).Length);

            // A later export replaces the output without touching that checkpoint.
            File.WriteAllLines(output, OldExport);

            // An export of /users to the same file, which fails on its first page.
            var reported = Export("/users", output, checkpoint);

            Assert.Null(reported);
            Assert.Equal(OldExport, File.ReadAllLines(output));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A checkpoint that is not this export's is refused, and the refusal used to depend on
    /// deleting it - an unchecked delete, so a checkpoint in a directory this account cannot
    /// write survived, the loop below decided to append from it merely existing, and the other
    /// export's remaining pages were appended onto a file that had never held its first ones.
    /// </summary>
    [Fact]
    public void A_refused_checkpoint_that_cannot_be_deleted_appends_nothing_foreign()
    {
        // The checkpoint sits in a directory the run cannot write, which is what makes its
        // deletion fail. Windows expresses that through ACLs, not a mode.
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        var vault = Directory.CreateDirectory(Path.Combine(dir, "vault")).FullName;
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(vault, "run.checkpoint");
        try
        {
            File.WriteAllLines(output, OldExport);
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = null,
                OutputFile = Path.Combine(dir, "elsewhere.jsonl"),
                DataLength = new FileInfo(output).Length
            }.Save(checkpoint);
            File.SetUnixFileMode(vault,
                UnixFileMode.UserRead | UnixFileMode.UserExecute);

            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);
            handler.Queue(HttpStatusCode.OK, UsersPage1);
            handler.Queue(HttpStatusCode.OK, UsersPage2);
            var reported = Export("/users", output, checkpoint);

            var lines = File.ReadAllLines(output);
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"], lines);
            Assert.Equal(3, reported);
            Assert.DoesNotContain(lines, l => l.Contains("old-"));
        }
        finally
        {
            try
            {
                File.SetUnixFileMode(vault,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// What the refusal leaves behind. The checkpoint and the staging file beside it are the
    /// other export's resume position, and deleting them cost that export its progress while
    /// this run went on to recreate the same collision at its first page boundary.
    /// </summary>
    [Fact]
    public void A_refused_checkpoint_and_its_staging_file_are_left_where_they_are()
    {
        var dir = NewDir();
        var outA = Path.Combine(dir, "outA.jsonl");
        var outB = Path.Combine(dir, "outB.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // Export A dies on page 2 and leaves its position behind.
            handler.Queue(HttpStatusCode.OK, UsersPage1);
            Export("/users", outA, checkpoint);
            var before = File.ReadAllBytes(checkpoint);
            // A save that died between writing the staging file and renaming it.
            var staged = checkpoint + ".tmp";
            File.WriteAllBytes(staged, before);

            // Export B, its own output, sharing A's checkpoint path, failing on its first page.
            Export("/users", outB, checkpoint);

            Assert.True(File.Exists(checkpoint), "the other export's resume position was deleted");
            Assert.Equal(before, File.ReadAllBytes(checkpoint));
            Assert.True(File.Exists(staged), "the other export's staging file was deleted");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// Two exports of the same resource to files that share a name in different directories,
    /// over one checkpoint path. The name is all a leaf comparison sees, so the second export
    /// adopted the first one's position and cut its file to the first one's byte count.
    /// </summary>
    [Fact]
    public void A_checkpoint_for_the_same_name_in_another_directory_does_not_cut_this_ones_output()
    {
        var dir = NewDir();
        var outA = Path.Combine(Directory.CreateDirectory(Path.Combine(dir, "a")).FullName, "users.jsonl");
        var outB = Path.Combine(Directory.CreateDirectory(Path.Combine(dir, "b")).FullName, "users.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // Export A dies on page 2, twice: the second run promotes the temp, so the
            // checkpoint it leaves records a length with no temp to explain it.
            handler.Queue(HttpStatusCode.OK, UsersPage1);
            Export("/users", outA, checkpoint);
            Export("/users", outA, checkpoint);

            var cp = PaginationCheckpoint.Load(checkpoint)!;
            Assert.Null(cp.TempFile);
            Assert.Equal(new FileInfo(outA).Length, cp.DataLength);

            // Export B: same resource, same file name, its own directory, and its own earlier
            // export already sitting there.
            File.WriteAllLines(outB, OldExport);
            handler.Queue(HttpStatusCode.OK, UsersPage1);
            handler.Queue(HttpStatusCode.OK, UsersPage2);
            var reported = Export("/users", outB, checkpoint);

            var lines = File.ReadAllLines(outB);
            Assert.All(lines, l => Assert.StartsWith("{\"id\":\"", l));
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"], lines);
            Assert.Equal(3, reported);
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}"], File.ReadAllLines(outA));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A checkpoint for the same path under different query options - another -Top, another
    /// -Filter - counts a different enumeration. Comparing paths and not queries let it through
    /// the guard, so the output was cut to its byte count before the exact comparison that
    /// refuses ever ran, and a run that then failed left the cut behind.
    /// </summary>
    [Fact]
    public void A_checkpoint_whose_query_differs_does_not_cut_the_output_before_it_is_refused()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            File.WriteAllLines(output, OldExport);
            var before = File.ReadAllBytes(output);
            new PaginationCheckpoint
            {
                // Same path, half the page: an export of /users this one is not making.
                Resource = "https://graph.microsoft.com/v1.0/users?$top=500",
                NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = null,
                OutputFile = output,
                DataLength = 24,
            }.Save(checkpoint);
            var checkpointBefore = File.ReadAllBytes(checkpoint);

            // This export fails on its first page, so anything the output loses, it loses here.
            var reported = Export("/users", output, checkpoint);

            Assert.Null(reported);
            Assert.Equal(before, File.ReadAllBytes(output));
            Assert.Equal(checkpointBefore, File.ReadAllBytes(checkpoint));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// How far "it is left as it is" has to reach. The refused checkpoint's items are in the
    /// temp it names, and the stale-temp sweep a few lines later deleted every temp beside the
    /// output - including that one. The export it belongs to then came back to a position
    /// pointing at a file that is gone and enumerated the collection again from the first page,
    /// which for a long export is the whole cost the refusal was written to avoid.
    /// </summary>
    [Fact]
    public void A_refused_checkpoints_temp_survives_the_run_that_refused_it()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // Export A dies on page 2: its two items are in its temp, the output was never
            // promoted, and the checkpoint names both.
            handler.Queue(HttpStatusCode.OK, UsersPage1);
            Export("/users", output, checkpoint);

            var cp = PaginationCheckpoint.Load(checkpoint)!;
            Assert.NotNull(cp.TempFile);
            var temp = Path.Combine(dir, cp.TempFile);
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}"], File.ReadAllLines(temp));
            var before = File.ReadAllBytes(checkpoint);

            // Export B: another collection, the same -OutputFile and -CheckpointPath, failing
            // before its first page boundary - so nothing it does on a boundary or at
            // completion is what answers here.
            Assert.Null(Export("/groups", output, checkpoint));

            Assert.Equal(before, File.ReadAllBytes(checkpoint));
            Assert.True(File.Exists(temp),
                "the refused checkpoint's items were swept away by the run that refused it");

            // A, back where it left off, rather than at page 1.
            handler.Queue(HttpStatusCode.OK, UsersPage2);
            Assert.Equal(3, Export("/users", output, checkpoint));
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(output));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// How far "spare the temps a refused checkpoint names" reaches. The sweep only ever
    /// deletes names a run gives its own temp - the output's name, a dot, 32 hex digits,
    /// ".tmp" - so a refused checkpoint naming "users.jsonl.{guid}.tmp" beside an output called
    /// "users" names a file this sweep could not touch either way. Skipping on it bought that
    /// file nothing and cost this output its own orphans, which the pre-length adoption path
    /// then picks up on a line count alone.
    /// </summary>
    [Fact]
    public void A_refused_temp_this_sweep_could_never_reach_does_not_spare_this_outputs_orphans()
    {
        var dir = NewDir();
        var mine = Path.Combine(dir, "users");
        var theirs = Path.Combine(dir, "users.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // The export to "users" collects page one into its temp and dies on page two.
            handler.Queue(HttpStatusCode.OK, UsersPage1);
            Export("/users", mine, checkpoint);
            var orphan = Assert.Single(Directory.GetFiles(dir, "users.*.tmp"));

            // The export next door takes the shared checkpoint over: it refuses what it finds,
            // saves its own position at its first page boundary, and dies on page two. The
            // first export's temp is now an orphan - nothing on disk describes it.
            handler.Queue(HttpStatusCode.OK, UsersPage1);
            Export("/users", theirs, checkpoint);
            var theirTemp = Assert.Single(Directory.GetFiles(dir, "users.jsonl.*.tmp"));

            // "users" again. It refuses a checkpoint naming a temp beside a different output,
            // and that name must not stand in the way of its own sweep.
            handler.Queue(HttpStatusCode.OK, UsersPage1);
            handler.Queue(HttpStatusCode.OK, UsersPage2);
            Assert.Equal(3, Export("/users", mine, checkpoint));

            Assert.False(File.Exists(orphan),
                "a temp name the sweep could never reach suppressed this output's sweep");
            Assert.True(File.Exists(theirTemp),
                "the refused checkpoint's own staging file was deleted");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
    /// <summary>
    /// What a refusing run leaves behind when it succeeds. The completion path deletes the
    /// checkpoint on the way out - the position it describes is spent - and after a refusal the
    /// file on disk is not this run's to spend: an export whose whole collection fits in one
    /// page never writes one of its own, so the file deleted there was the other export's, the
    /// temp the sweep had just been told to spare belonged to nothing, and that export came
    /// back to page one.
    /// </summary>
    [Fact]
    public void A_refused_checkpoint_survives_a_refusing_export_that_completes()
    {
        var dir = NewDir();
        var outA = Path.Combine(dir, "outA.jsonl");
        var outB = Path.Combine(dir, "outB.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // Export A dies on page 2: its two items are in its temp, the output was never
            // promoted, and the checkpoint names both.
            handler.Queue(HttpStatusCode.OK, UsersPage1);
            Export("/users", outA, checkpoint);

            var cp = PaginationCheckpoint.Load(checkpoint)!;
            Assert.NotNull(cp.TempFile);
            var temp = Path.Combine(dir, cp.TempFile);
            var before = File.ReadAllBytes(checkpoint);

            // Export B: its own output over A's checkpoint path, and one page of its own
            // collection is all there is - so it refuses A's position as another export's,
            // saves none of its own, and reaches the completion path.
            handler.Queue(HttpStatusCode.OK, GroupsPage2);
            var refusal = ExportWarnings("/groups", outB, checkpoint);
            Assert.Contains(refusal, w => w.Contains("belongs to a different export"));
            Assert.Equal(["{\"id\":\"g3\"}"], File.ReadAllLines(outB));

            Assert.True(File.Exists(checkpoint), "the other export's resume position was deleted");
            Assert.Equal(before, File.ReadAllBytes(checkpoint));
            Assert.True(File.Exists(temp),
                "the refused checkpoint's items were left with nothing describing them");

            // A, back where it left off, rather than at page 1.
            handler.Queue(HttpStatusCode.OK, UsersPage2);
            Assert.Equal(3, Export("/users", outA, checkpoint));
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(outA));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The other half: a refusal is not a standing exemption from the delete. A run that
    /// refuses what it finds and then saves its own position at its first page boundary has
    /// taken the path over, and what the completion path deletes is that. Left standing, it is
    /// a position into an enumeration that has finished, naming a temp released a line later -
    /// which every later export over that path refuses in turn, warning about a second export
    /// that is not there.
    /// </summary>
    [Fact]
    public void A_refusing_export_that_saved_its_own_checkpoint_deletes_it_on_completion()
    {
        var dir = NewDir();
        var outA = Path.Combine(dir, "outA.jsonl");
        var outB = Path.Combine(dir, "outB.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            handler.Queue(HttpStatusCode.OK, UsersPage1);
            Export("/users", outA, checkpoint);
            Assert.True(File.Exists(checkpoint));

            // Export B: two pages, so it refuses A's position and then writes its own over the
            // same path at the boundary between them.
            handler.Queue(HttpStatusCode.OK, UsersPage1);
            handler.Queue(HttpStatusCode.OK, UsersPage2);
            Assert.Equal(3, Export("/users", outB, checkpoint));

            Assert.False(File.Exists(checkpoint),
                "a position into a finished enumeration was left on disk");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// And over a retry, where the attempt that saved the checkpoint is not the attempt that
    /// finishes. An endpoint refusing the automatic $top sends the loop round again with a
    /// boundary checkpoint of this run's already on disk, and the retry - everything it asks
    /// for arriving in one page - saves none. Read per attempt, "this run never wrote over the
    /// file it refused" is true of the retry and false of the run, and the position left behind
    /// is the run's own.
    /// </summary>
    [Fact]
    public void A_checkpoint_this_run_saved_before_a_retry_is_deleted_when_the_retry_completes()
    {
        var dir = NewDir();
        var outA = Path.Combine(dir, "outA.jsonl");
        var outB = Path.Combine(dir, "outB.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            // Export A dies on page 2 and leaves its position behind.
            var handler = new ScriptedHandler();
            using (MgxTransportScope.Inject(handler))
            {
                handler.Queue(HttpStatusCode.OK, UsersPage1);
                Export("/users", outA, checkpoint);
                Assert.True(File.Exists(checkpoint));
            }

            // Export B refuses it, saves its own at its first page boundary, and is sent round
            // the attempt loop by the refusal of $top on the page after.
            using (MgxTransportScope.Inject(new TopRefusedAfterPageOneHandler()))
            {
                Assert.Equal(1, Export("/users", outB, checkpoint));
            }

            Assert.False(File.Exists(checkpoint),
                "the position this run saved before its retry was left on disk");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// And where a refusal is not about ownership at all, and where refusing is not enough. A
    /// checkpoint whose named temp another run still holds counts rows that are in that temp and
    /// in no other file, and it is the only thing on disk that counts them - so the holder
    /// deletes it itself if it finishes, and keeps its temp for exactly as long as this file is
    /// there if it does not. Exporting anyway is what takes it: the first page boundary saves
    /// this run's own position over the same path, the completion door deletes that as its own,
    /// and the holder's temp is left an orphan for the next sweep. There is no pass this run can
    /// make that leaves the pair standing, so it makes none.
    /// </summary>
    [Fact]
    public void A_held_temp_stops_the_export_before_anything_is_written()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // A run of this export dies on page 2: the two items it collected are in its temp,
            // and the checkpoint beside it records this output and this enumeration.
            handler.Queue(HttpStatusCode.OK, UsersPage1);
            Export("/users", output, checkpoint);
            var temp = Assert.Single(Directory.GetFiles(dir, "out.jsonl.*.tmp"));

            // It is not dead. It still holds that temp, the way a run between pages does, with
            // the counted rows in it - and the rest of the collection is on the wire, one page
            // of it, which is all the run below would have needed.
            var counted = File.ReadAllText(temp);
            using var live = new StreamWriter(temp, append: false);
            live.Write(counted);
            live.Flush();

            handler.Queue(HttpStatusCode.OK, UsersPage2);
            var before = ReadDisk(dir, checkpoint, temp, output);
            var servedBefore = handler.Served;
            var (warnings, errors) = ExportRun("/users", output, checkpoint);
            var after = ReadDisk(dir, checkpoint, temp, output);

            var stop = Assert.Single(errors);
            Assert.StartsWith("CheckpointTempHeld", stop.FullyQualifiedErrorId,
                StringComparison.Ordinal);
            Assert.Contains("Another export is still writing the temp file",
                stop.Exception.Message);
            Assert.Contains(checkpoint, stop.Exception.Message);
            Assert.Contains("the 2 items it records are that run's", stop.Exception.Message);
            Assert.Contains("This run stops here; nothing was written.", stop.Exception.Message);
            Assert.Contains(
                "Wait for that export to finish, or give this one its own -OutputFile and "
                + "-CheckpointPath.",
                stop.Exception.Message);

            // Nothing that promises to leave both files and then replaces them.
            Assert.DoesNotContain(warnings, w => w.Contains("exported from the beginning"));
            Assert.DoesNotContain(warnings, w => w.Contains("Both files are left as they are"));

            // And nothing happened: no request, no output, and both files exactly as the holder
            // left them - down to the directory's entry list, so no temp of this run's outlived
            // it either.
            Assert.Equal(servedBefore, handler.Served);
            AssertUnmoved(before, after);
            Assert.False(File.Exists(output), "the stopped run published an output");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The same disk with two pages on the wire instead of one, which is the shape that made
    /// leaving the file alone worth nothing: a run that reaches one page boundary saves its own
    /// position over the very path it has just promised to leave, and walks out of a door that
    /// deletes that as this run's own. A run that stops has no page boundary to reach.
    /// </summary>
    [Fact]
    public void A_held_temp_stops_the_export_that_would_have_paged_twice()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            handler.Queue(HttpStatusCode.OK, UsersPage1);
            Export("/users", output, checkpoint);
            var temp = Assert.Single(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}"], File.ReadAllLines(temp));

            var counted = File.ReadAllText(temp);
            using var live = new StreamWriter(temp, append: false);
            live.Write(counted);
            live.Flush();

            // A first page with a continuation, so the run would save a boundary checkpoint of
            // its own, and a second that finishes the collection.
            handler.Queue(HttpStatusCode.OK, UsersPage1);
            handler.Queue(HttpStatusCode.OK, UsersPage2);
            var before = ReadDisk(dir, checkpoint, temp, output);
            var servedBefore = handler.Served;
            var (_, errors) = ExportRun("/users", output, checkpoint);
            var after = ReadDisk(dir, checkpoint, temp, output);

            Assert.StartsWith("CheckpointTempHeld", Assert.Single(errors).FullyQualifiedErrorId,
                StringComparison.Ordinal);
            Assert.Equal(servedBefore, handler.Served);
            AssertUnmoved(before, after);
            Assert.Equal(2, PaginationCheckpoint.Load(checkpoint)!.ItemsCollected);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The whole cost of getting that wrong, on one disk. Export A holds its temp with two rows
    /// in it; export B over the same command line stops; A then dies, leaving the two files B
    /// left alone. A's re-run is what recovers those rows - it promotes the temp over the output
    /// and appends the rest of the enumeration - and it can only do that while the checkpoint
    /// counting them is there.
    /// </summary>
    [Fact]
    public void The_rows_a_held_temp_holds_come_back_after_the_export_that_stopped()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // A collects page one into its temp and is still going.
            handler.Queue(HttpStatusCode.OK, UsersPage1);
            Export("/users", output, checkpoint);
            var temp = Assert.Single(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}"], File.ReadAllLines(temp));

            var counted = File.ReadAllText(temp);
            using (var live = new StreamWriter(temp, append: false))
            {
                live.Write(counted);
                live.Flush();

                // B stops. Nothing is queued for it, and it asks for nothing.
                var (_, errors) = ExportRun("/users", output, checkpoint);
                Assert.StartsWith("CheckpointTempHeld",
                    Assert.Single(errors).FullyQualifiedErrorId, StringComparison.Ordinal);
            }

            // A dies, leaving exactly the two files B left alone.
            Assert.True(File.Exists(checkpoint), "B took A's position with it");
            Assert.True(File.Exists(temp), "B took A's temp with it");

            // A's re-run. It promotes the two rows and resumes from the page its own checkpoint
            // recorded, so the request below is the nextLink and not page one - which is what
            // tells a recovery from a fresh export here.
            handler.Queue(HttpStatusCode.OK, UsersPage2);
            var warnings = ExportWarnings("/users", output, checkpoint);

            Assert.Contains(warnings, w => w.Contains("Recovered 2 items"));
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(output));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The other half of the same refusal, where there is no second export at all: a temp this
    /// account cannot open read-write. The advice - grant write access, run again - is the only
    /// thing that recovers those rows, so the run giving it must still leave the position they
    /// are counted by. It stops rather than export over it. This follows the advice and measures
    /// what comes back.
    /// </summary>
    [Fact]
    public void An_unopenable_temp_stops_the_export_and_says_how_to_recover_it()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            handler.Queue(HttpStatusCode.OK, UsersPage1);
            Export("/users", output, checkpoint);
            var temp = Assert.Single(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}"], File.ReadAllLines(temp));

            // 0444: the owner cannot open it read-write either, which is the claim failing on
            // permissions rather than on another handle.
            File.SetUnixFileMode(temp, UnixFileMode.UserRead);

            var before = ReadDisk(dir, checkpoint, temp, output);
            var servedBefore = handler.Served;
            var (_, errors) = ExportRun("/users", output, checkpoint);
            var after = ReadDisk(dir, checkpoint, temp, output);

            var stop = Assert.Single(errors);
            Assert.StartsWith("CheckpointTempUnopenable", stop.FullyQualifiedErrorId,
                StringComparison.Ordinal);
            Assert.Contains(Path.GetFileName(temp), stop.Exception.Message);
            Assert.Contains("cannot be opened for writing by this account", stop.Exception.Message);
            Assert.Contains("not on sharing", stop.Exception.Message);
            Assert.Contains("This run stops here; nothing was written.", stop.Exception.Message);
            Assert.Contains(
                "Grant write access to that file and run again to recover them, or remove it "
                + "and the checkpoint to export afresh.",
                stop.Exception.Message);
            Assert.Equal(servedBefore, handler.Served);
            AssertUnmoved(before, after);
            Assert.False(File.Exists(output), "the stopped run published an output");

            // Follow it.
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            handler.Queue(HttpStatusCode.OK, UsersPage2);
            var warnings = ExportWarnings("/users", output, checkpoint);

            Assert.Contains(warnings, w => w.Contains("Recovered 2 items"));
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(output));
        }
        finally
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                    File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                Directory.Delete(dir, true);
            }
            catch { }
        }
    }
    /// <summary>
    /// The disk a resumed export leaves at a page boundary. One run dies with its items in a
    /// temp; the next promotes that temp over the output, appends the page it resumed onto,
    /// saves its position against the output itself - there is no temp left to name - and dies
    /// too. That last position is the one shape a shared -CheckpointPath offers no temp to
    /// refuse a second run by.
    /// </summary>
    private static void ResumedAndAppending(ScriptedHandler handler, string output,
        string checkpoint)
    {
        handler.Queue(HttpStatusCode.OK, UsersPage1);
        Export("/users", output, checkpoint);
        handler.Queue(HttpStatusCode.OK, UsersPage1);
        Export("/users", output, checkpoint);
    }

    /// <summary>
    /// The refusal one file over from the temp claim, and the one it could not reach. A run that
    /// has resumed once writes straight into -OutputFile and saves a position naming no temp at
    /// all, so a second run over the same command line found nothing in the checkpoint to refuse
    /// it by: it judged the file its own, cut it back to the recorded offset under a live writer
    /// and appended, and the holder's next write landed past the hole that left. There is no
    /// pass this run can make that leaves the pair standing either - the rows the checkpoint
    /// counts are in the output now - so it makes none.
    /// </summary>
    [Fact]
    public void A_held_output_stops_the_export_before_anything_is_written()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            ResumedAndAppending(handler, output, checkpoint);

            // The premise, off the file: this position counts its items into the output and
            // names no temp, which is the shape the claim on the temp has nothing to say about.
            var cp = PaginationCheckpoint.Load(checkpoint)!;
            Assert.Null(cp.TempFile);
            Assert.Equal(Path.GetFullPath(output), cp.OutputFile);
            Assert.Equal(new FileInfo(output).Length, cp.DataLength);

            // Export A is not dead. It holds the output the way a resumed run does - appending,
            // sharing reads - and has written one more row since its last save, so the file is
            // longer than the checkpoint records and cutting it back leaves a hole under it.
            handler.Queue(HttpStatusCode.OK, UsersPage2);
            DiskState before, after;
            int servedBefore;
            string[] warnings;
            ErrorRecord[] errors;
            using (var live = new StreamWriter(
                       new FileStream(output, FileMode.Append, FileAccess.Write, FileShare.Read)))
            {
                live.WriteLine("{\"id\":\"u9-live\"}");
                live.Flush();
                Assert.True(new FileInfo(output).Length > cp.DataLength,
                    "the holder wrote nothing past the recorded length, so no cut would show");

                before = ReadDisk(dir, checkpoint, output);
                servedBefore = handler.Served;
                (warnings, errors) = ExportRun("/users", output, checkpoint);

                var stop = Assert.Single(errors);
                Assert.StartsWith("CheckpointOutputHeld", stop.FullyQualifiedErrorId,
                    StringComparison.Ordinal);
                Assert.Equal(ErrorCategory.ResourceBusy, stop.CategoryInfo.Category);
                Assert.Contains($"Another export is still writing '{output}'",
                    stop.Exception.Message);
                Assert.Contains(checkpoint, stop.Exception.Message);
                Assert.Contains($"records {cp.ItemsCollected} items into", stop.Exception.Message);
                Assert.Contains("would cut that file back under it", stop.Exception.Message);
                Assert.Contains("This run stops here; nothing was written.", stop.Exception.Message);
                Assert.Contains(
                    "Wait for that export to finish, or give this one its own -OutputFile and "
                    + "-CheckpointPath.",
                    stop.Exception.Message);

                // And not the sentence for a file whose contents this run never looked at.
                Assert.DoesNotContain(warnings, w => w.Contains("no longer holds"));
                Assert.DoesNotContain(warnings, w => w.Contains("exported from the beginning"));

                // Nothing ran: no request, and the two files and the directory's entry list
                // exactly as the holder left them. Read after the record and not before it, so
                // a run that took the checkpoint with it fails on the answer it did not give.
                after = ReadDisk(dir, checkpoint, output);
                Assert.Equal(servedBefore, handler.Served);
                AssertUnmoved(before, after);

                // A goes on, and the row it writes next lands where its last one ended.
                live.WriteLine("{\"id\":\"u10-live\"}");
                live.Flush();
            }

            // Ordinal, both of them: the culture-aware comparisons treat a NUL as ignorable,
            // which is the one byte this is looking for.
            var published = File.ReadAllText(output);
            Assert.False(published.Contains('\0'),
                "the cut left a hole for the holder's next write to land past");
            Assert.EndsWith("{\"id\":\"u9-live\"}" + Environment.NewLine
                + "{\"id\":\"u10-live\"}" + Environment.NewLine, published,
                StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The other half of the same claim, where there is no second export at all: the output the
    /// checkpoint counts its items into cannot be opened for writing by this account. It was
    /// reported as a file that no longer holds those items - a reading of the file's contents,
    /// given about a file this run had not been able to open - and the position counting them
    /// was deleted on the strength of it. The advice is the only thing that recovers them, so
    /// the run giving it leaves both files standing. This follows the advice and measures what
    /// comes back.
    /// </summary>
    [Fact]
    public void An_unopenable_output_stops_the_export_and_says_how_to_recover_it()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            ResumedAndAppending(handler, output, checkpoint);
            var cp = PaginationCheckpoint.Load(checkpoint)!;
            Assert.Null(cp.TempFile);
            var recorded = cp.DataLength!.Value;

            // The rows the checkpoint counts are there, and a row written after its last save
            // with them - so a run that can open the file cuts back to the recorded length.
            File.AppendAllText(output, "{\"id\":\"u9-past-the-save\"}" + Environment.NewLine);
            Assert.True(new FileInfo(output).Length > recorded);

            // 0444: the owner cannot open it read-write either, which is the claim failing on
            // permissions rather than on another handle.
            File.SetUnixFileMode(output, UnixFileMode.UserRead);

            handler.Queue(HttpStatusCode.OK, UsersPage2);
            var before = ReadDisk(dir, checkpoint, output);
            var servedBefore = handler.Served;
            var (warnings, errors) = ExportRun("/users", output, checkpoint);

            var stop = Assert.Single(errors);
            Assert.StartsWith("CheckpointOutputUnopenable", stop.FullyQualifiedErrorId,
                StringComparison.Ordinal);
            Assert.Equal(ErrorCategory.PermissionDenied, stop.CategoryInfo.Category);
            Assert.Contains($"'{output}' could not be opened for writing:", stop.Exception.Message);
            Assert.Contains("is denied", stop.Exception.Message);
            // The reason's own trailing period is trimmed before it is quoted, so the sentence
            // this joins into is punctuated once, not twice.
            Assert.DoesNotContain(".. ", stop.Exception.Message);
            Assert.Contains("is denied. The resume checkpoint", stop.Exception.Message);
            Assert.Contains($"The resume checkpoint at '{checkpoint}' records",
                stop.Exception.Message);
            Assert.Contains("This run stops here; nothing was written.", stop.Exception.Message);
            Assert.Contains(
                "Grant write access to that file and run again to resume from it, or remove it "
                + "and the checkpoint to export afresh.",
                stop.Exception.Message);

            // Not said about a file whose contents this run could not read, and not acted on.
            // The disk is read after the record and not before it, so a run that deleted the
            // position fails on the answer it did not give.
            Assert.DoesNotContain(warnings, w => w.Contains("no longer holds"));
            Assert.Equal(servedBefore, handler.Served);
            AssertUnmoved(before, ReadDisk(dir, checkpoint, output));
            Assert.True(File.Exists(checkpoint),
                "the position counting the items the advice recovers was deleted");

            // Follow it. The run that comes back cuts the output to the recorded length and
            // appends the page it resumes onto, so the row written past the last save goes and
            // nothing is written twice.
            File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            handler.Queue(HttpStatusCode.OK, UsersPage2);
            var resumed = ExportWarnings("/users", output, checkpoint);

            Assert.DoesNotContain(resumed, w => w.Contains("no longer holds"));
            Assert.Equal(
                ["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u1\"}", "{\"id\":\"u2\"}",
                 "{\"id\":\"u3\"}"],
                File.ReadAllLines(output));
        }
        finally
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                    File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                Directory.Delete(dir, true);
            }
            catch { }
        }
    }

    /// <summary>
    /// The answer the claim must not swallow. An output shorter than the length the checkpoint
    /// records is not the file that checkpoint describes - the items it counts are in no file at
    /// all - and nothing holds it and nothing refuses to open it. That still takes today's
    /// route: say so, delete the position, and export from the beginning.
    /// </summary>
    [Fact]
    public void A_stale_length_output_still_exports_from_the_beginning()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            ResumedAndAppending(handler, output, checkpoint);
            var cp = PaginationCheckpoint.Load(checkpoint)!;
            Assert.Null(cp.TempFile);

            // The output is replaced by one that does not hold those items.
            File.WriteAllText(output, "{\"id\":\"short\"}" + Environment.NewLine);
            Assert.True(new FileInfo(output).Length < cp.DataLength);

            handler.Queue(HttpStatusCode.OK, UsersPage1);
            handler.Queue(HttpStatusCode.OK, UsersPage2);
            var (warnings, errors) = ExportRun("/users", output, checkpoint);

            Assert.Empty(errors);
            Assert.Contains(warnings,
                w => w.Contains($"'{output}' no longer holds the {cp.ItemsCollected} items"));
            Assert.False(File.Exists(checkpoint), "the stale position was left on disk");
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(output));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The disk a resumed export leaves at a page boundary, with every item in the output
    /// exactly once. One run dies with its items in a temp; the next promotes that temp over the
    /// output, saves its position against the output itself - there is no temp left to name -
    /// and dies at its first request. That last position is the one shape a shared
    /// -CheckpointPath offers no temp to refuse a second run by.
    /// </summary>
    private static void PromotedAndAppending(ScriptedHandler handler, string output,
        string checkpoint)
    {
        handler.Queue(HttpStatusCode.OK, UsersPage1);
        Export("/users", output, checkpoint);
        Export("/users", output, checkpoint);
    }

    /// <summary>
    /// Two exports over one command line, both past the stat the reconcile makes. The claim on
    /// the output was asked for and let go inside the reconcile and the writer opened its own
    /// handle a page fetch later, so for that window neither run held the file: both passed the
    /// claim, both cut it back to the recorded offset, both appended, and the file came back
    /// holding more lines than belonged in it with the checkpoint deleted over the lot. The
    /// claim is the handle now, so the run that takes it keeps it across that window, and the
    /// second run meets a file the first one has.
    ///
    /// A is parked in the window itself. The verbose announcing the resume is written after the
    /// reconcile - the claim taken, the row past the last save cut away - and before the writer
    /// is opened, which is exactly where the two runs used to overlap.
    /// </summary>
    [Fact]
    public void Two_exports_released_together_leave_one_with_the_output()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        // A real second thread, not a scheduled task: the two runs have to be able to be inside
        // the cmdlet at the same time, and joining one is not a blocking wait on work the test
        // framework might have scheduled behind it.
        (string[] Warnings, ErrorRecord[] Errors) aResult = default;
        Exception? aFailed = null;
        Thread? a = null;
        using var arrived = new ManualResetEventSlim(false);
        using var released = new ManualResetEventSlim(false);
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            PromotedAndAppending(handler, output, checkpoint);

            var cp = PaginationCheckpoint.Load(checkpoint)!;
            Assert.Null(cp.TempFile);
            Assert.Equal(Path.GetFullPath(output), cp.OutputFile);
            var recorded = cp.DataLength!.Value;
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}"], File.ReadAllLines(output));
            Assert.Equal(new FileInfo(output).Length, recorded);

            // A row written after the last save, so the cut A makes is visible from outside.
            File.AppendAllText(output, "{\"id\":\"u9-past-the-save\"}" + Environment.NewLine);
            Assert.True(new FileInfo(output).Length > recorded);

            handler.Queue(HttpStatusCode.OK, UsersPage2);
            var servedBefore = handler.Served;

            a = new Thread(() =>
            {
                try
                {
                    aResult = ExportParkedAtResume("/users", output, checkpoint,
                        arrived, released);
                }
                catch (Exception ex) { aFailed = ex; }
            });
            a.Start();
            Assert.True(arrived.Wait(TimeSpan.FromSeconds(60)),
                "export A never reached the window between its reconcile and its writer");

            // Past the reconcile: the row written after the last save has been cut away. And
            // short of the writer: no page has been asked for, so nothing has been appended.
            Assert.Equal(recorded, new FileInfo(output).Length);
            Assert.Equal(servedBefore, handler.Served);

            var (warnings, errors) = ExportRun("/users", output, checkpoint);

            var stop = Assert.Single(errors);
            Assert.StartsWith("CheckpointOutputHeld", stop.FullyQualifiedErrorId,
                StringComparison.Ordinal);
            Assert.Equal(ErrorCategory.ResourceBusy, stop.CategoryInfo.Category);
            Assert.Contains($"Another export is still writing '{output}'", stop.Exception.Message);
            Assert.Contains($"records {cp.ItemsCollected} items into", stop.Exception.Message);
            Assert.Contains("This run stops here; nothing was written.", stop.Exception.Message);
            Assert.DoesNotContain(warnings, w => w.Contains("no longer holds"));

            // B sent no request and took nothing with it: the position A resumes from is still
            // on disk, and the file is still exactly what A cut it back to.
            Assert.Equal(servedBefore, handler.Served);
            Assert.True(File.Exists(checkpoint), "B deleted the position A is resuming from");
            Assert.Equal(recorded, new FileInfo(output).Length);

            released.Set();
            Assert.True(a.Join(TimeSpan.FromSeconds(60)), "export A never finished");
            Assert.Null(aFailed);
            Assert.Empty(aResult.Errors);

            // One run had the file. Every item once, in order, and the position spent.
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(output));
            Assert.False(File.Exists(checkpoint), "the completed export left its position behind");
        }
        finally
        {
            released.Set();
            a?.Join(TimeSpan.FromSeconds(30));
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// What a caller reading the output while a resumed export appends to it gets. The hold is
    /// FileShare.None on Unix because that is the only mode .NET backs with LOCK_EX, and LOCK_EX
    /// is the only lock that refuses a second writer there - LOCK_SH, which every other mode
    /// takes and which the writer's own open used to be, does not. The cost is that it refuses
    /// every .NET opener including a reader, and the help says so; tail and cat take no lock at
    /// all and read the file as they always did. Windows needs no such trade: the handle's own
    /// write access refuses a second writer, so FileShare.Read can let a reader keep looking.
    /// </summary>
    [Fact]
    public void A_resumed_export_holds_the_output_against_a_writer_and_tail_still_reads_it()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        (string[] Warnings, ErrorRecord[] Errors) aResult = default;
        Exception? aFailed = null;
        Thread? a = null;
        var gate = new GatedHandler(gateOn: 2);
        try
        {
            {
                // The setup runs on its own wire, so the gate below counts only A's requests.
                var handler = new ScriptedHandler();
                using var scoped = MgxTransportScope.Inject(handler);
                PromotedAndAppending(handler, output, checkpoint);
                Assert.Null(PaginationCheckpoint.Load(checkpoint)!.TempFile);
            }

            // A page with a continuation, then the page held at the gate. So by the time A is
            // parked it has written and flushed a row of its own and is appending still.
            gate.Queue(HttpStatusCode.OK, UsersPage2Continued);
            gate.Queue(HttpStatusCode.OK, UsersPage3);
            using var transport = MgxTransportScope.Inject(gate);

            a = new Thread(() =>
            {
                try { aResult = ExportRun("/users", output, checkpoint); }
                catch (Exception ex) { aFailed = ex; }
            });
            a.Start();
            Assert.True(gate.Arrived.Wait(TimeSpan.FromSeconds(60)),
                "export A never reached its second request");

            // The property the hold exists for, on both platforms: no second writer.
            var refusedWriter = Assert.ThrowsAny<IOException>(() =>
                new FileStream(output, FileMode.Append, FileAccess.Write, FileShare.Read));
            Assert.Contains("being used by another process", refusedWriter.Message);

            if (OperatingSystem.IsWindows())
            {
                using var reader = new FileStream(output, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite);
                Assert.True(reader.Length > 0);
            }
            else
            {
                var refusedReader = Assert.ThrowsAny<IOException>(() =>
                    new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                Assert.Contains("being used by another process", refusedReader.Message);

                // And what the help promises instead. tail takes no lock, so the rows the run
                // has written so far come back exactly as they are on disk - the two it resumed
                // over and the one it has appended since.
                using var tail = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "tail",
                        ArgumentList = { "-c", "4096", output },
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    });
                Assert.NotNull(tail);
                var read = tail!.StandardOutput.ReadToEnd();
                Assert.True(tail.WaitForExit(30_000), "tail did not finish");
                Assert.Equal(0, tail.ExitCode);
                Assert.Equal("{\"id\":\"u1\"}" + Environment.NewLine
                    + "{\"id\":\"u2\"}" + Environment.NewLine
                    + "{\"id\":\"u3\"}" + Environment.NewLine, read);
            }

            gate.Released.Set();
            Assert.True(a.Join(TimeSpan.FromSeconds(60)), "export A never finished");
            Assert.Null(aFailed);
            Assert.Empty(aResult.Errors);

            // And the file is everyone's again the moment the writing ends.
            using (var after = new FileStream(output, FileMode.Open, FileAccess.ReadWrite,
                       FileShare.None))
            {
                Assert.True(after.Length > 0);
            }
            Assert.Equal(
                ["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}", "{\"id\":\"u4\"}"],
                File.ReadAllLines(output));
        }
        finally
        {
            gate.Released.Set();
            a?.Join(TimeSpan.FromSeconds(30));
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// An output in a directory this account cannot search. File.Exists answers false for it -
    /// the same answer it gives for a file that is not there - so the run reported that the
    /// output no longer held the items the checkpoint counts, and deleted the position counting
    /// them, over a whole file it had never opened. The open is what decides now, and being
    /// refused on permissions is not the same answer as finding nothing.
    ///
    /// And which permission. The refusal is an access failure naming the file, so the stop
    /// quoted the runtime's words for it and told the caller to grant write access to that file
    /// - a file they cannot stat, let alone chmod, through a parent that refuses to be looked
    /// into. The grant that opens it is on the directory, and the run says which directory.
    /// Told from a mode on the file, which reaches the same catch, by whether the entry can be
    /// asked about at all: a directory missing its search bit refuses every name under it, and
    /// one that has the bit answers an absent name with ENOENT. Not by whether the directory
    /// itself is there - a stat of it needs the search bit on ITS parent, so a mode-000
    /// directory answers Directory.Exists true. This follows the advice and measures what comes
    /// back.
    /// </summary>
    [Fact]
    public void An_output_whose_directory_cannot_be_searched_stops_the_export()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        var outDir = Directory.CreateDirectory(Path.Combine(dir, "out")).FullName;
        var cpDir = Directory.CreateDirectory(Path.Combine(dir, "cp")).FullName;
        var output = Path.Combine(outDir, "out.jsonl");
        var checkpoint = Path.Combine(cpDir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            PromotedAndAppending(handler, output, checkpoint);
            var cp = PaginationCheckpoint.Load(checkpoint)!;
            Assert.Null(cp.TempFile);
            var contents = File.ReadAllBytes(output);
            var position = File.ReadAllBytes(checkpoint);

            // 000: the directory cannot be searched, so nothing in it can be reached by name.
            // That the stat now says false is the premise - it is the answer this used to be
            // decided on. Where it still says true the account is root, a mode says nothing, and
            // there is nothing here to measure.
            File.SetUnixFileMode(outDir, 0);
            if (File.Exists(output)) return;

            handler.Queue(HttpStatusCode.OK, UsersPage2);
            var servedBefore = handler.Served;
            var (warnings, errors) = ExportRun("/users", output, checkpoint);

            var stop = Assert.Single(errors);
            Assert.StartsWith("CheckpointOutputUnopenable", stop.FullyQualifiedErrorId,
                StringComparison.Ordinal);
            Assert.Equal(ErrorCategory.PermissionDenied, stop.CategoryInfo.Category);
            Assert.Contains($"'{output}' could not be opened for writing: the directory "
                + $"'{outDir}' cannot be searched.", stop.Exception.Message);
            Assert.Contains($"The resume checkpoint at '{checkpoint}' records",
                stop.Exception.Message);
            Assert.Contains("This run stops here; nothing was written.", stop.Exception.Message);
            Assert.Contains(
                "Grant access to that directory and run again to resume from it, or remove the "
                + "checkpoint to export afresh.",
                stop.Exception.Message);

            // Not the runtime's words for a mode on the file, and not the advice about it: that
            // file is the one thing here the caller cannot reach to change or remove.
            Assert.DoesNotContain("is denied", stop.Exception.Message);
            Assert.DoesNotContain("Grant write access to that file", stop.Exception.Message);
            // The reason carries no period of its own, so the sentence it joins is punctuated
            // once.
            Assert.DoesNotContain(".. ", stop.Exception.Message);

            // Not a reading of a file it never opened, and nothing acted on it.
            Assert.DoesNotContain(warnings, w => w.Contains("no longer holds"));
            Assert.Equal(servedBefore, handler.Served);
            Assert.True(File.Exists(checkpoint),
                "the position counting the items the advice recovers was deleted");
            Assert.Equal(position, File.ReadAllBytes(checkpoint));

            // Follow it. The grant is on the directory, and the run that comes back resumes into
            // the file the checkpoint records.
            File.SetUnixFileMode(outDir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.Equal(contents, File.ReadAllBytes(output));

            handler.Queue(HttpStatusCode.OK, UsersPage2);
            var resumed = ExportWarnings("/users", output, checkpoint);
            Assert.DoesNotContain(resumed, w => w.Contains("from the beginning"));
            Assert.False(File.Exists(checkpoint));
        }
        finally
        {
            try
            {
                File.SetUnixFileMode(outDir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.Delete(dir, true);
            }
            catch { }
        }
    }

    /// <summary>
    /// A directory standing where the output should be. It is not a file, so File.Exists said
    /// no and the run took the same route it takes for an output that is not there at all -
    /// warning that the file no longer held the items, and deleting the position. Opening a
    /// directory read-write is refused on both platforms, and that is an answer.
    /// </summary>
    [Fact]
    public void A_directory_standing_at_the_output_path_stops_the_export()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            PromotedAndAppending(handler, output, checkpoint);
            var cp = PaginationCheckpoint.Load(checkpoint)!;
            Assert.Null(cp.TempFile);
            var position = File.ReadAllBytes(checkpoint);

            File.Delete(output);
            Directory.CreateDirectory(output);

            handler.Queue(HttpStatusCode.OK, UsersPage2);
            var servedBefore = handler.Served;
            var (warnings, errors) = ExportRun("/users", output, checkpoint);

            var stop = Assert.Single(errors);
            Assert.StartsWith("CheckpointOutputUnopenable", stop.FullyQualifiedErrorId,
                StringComparison.Ordinal);
            // A directory is not a permission, whatever errno the open came back with: no grant
            // makes one writable at an offset, and the way out is to take it off the path.
            Assert.Equal(ErrorCategory.InvalidOperation, stop.CategoryInfo.Category);
            Assert.Contains($"'{output}' could not be opened for writing: a directory stands at "
                + "the path.", stop.Exception.Message);
            Assert.Contains("This run stops here; nothing was written.", stop.Exception.Message);
            Assert.Contains(
                "Remove it (or point -OutputFile elsewhere) and run again to resume from it, or "
                + "remove the checkpoint to export afresh.",
                stop.Exception.Message);
            Assert.DoesNotContain("Grant write access", stop.Exception.Message);

            Assert.DoesNotContain(warnings, w => w.Contains("no longer holds"));
            Assert.Equal(servedBefore, handler.Served);
            Assert.True(File.Exists(checkpoint), "the position was deleted over a directory");
            Assert.Equal(position, File.ReadAllBytes(checkpoint));
            Assert.True(Directory.Exists(output));
            Assert.Empty(Directory.GetFileSystemEntries(output));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The disk an interrupted fresh export leaves: its items are in a temp beside the output,
    /// the checkpoint names that temp, and there is no output at all yet. The run after it
    /// promotes the temp over the output and appends onto what it promoted.
    /// </summary>
    private static void InterruptedIntoATemp(ScriptedHandler handler, string output,
        string checkpoint)
    {
        handler.Queue(HttpStatusCode.OK, UsersPage1);
        Export("/users", output, checkpoint);
    }

    /// <summary>
    /// The other route that ends in an append, one file over. A run whose checkpoint names a
    /// temp promotes it over the output and then appends onto it - and used to hold nothing
    /// between the move that put the rows there and the writer's own open a page fetch later.
    /// The checkpoint it repoints in that gap names no temp, which is the shape a second run
    /// over the same command line reads as an output it may cut back and append to. So the
    /// promotion takes the file it promoted into, and the second run meets a file the first
    /// one has.
    /// </summary>
    [Fact]
    public void A_promoting_export_holds_the_output_it_promoted_into()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        (string[] Warnings, ErrorRecord[] Errors) aResult = default;
        Exception? aFailed = null;
        Thread? a = null;
        using var arrived = new ManualResetEventSlim(false);
        using var released = new ManualResetEventSlim(false);
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            InterruptedIntoATemp(handler, output, checkpoint);

            // The premise: the items are in a temp, the checkpoint names it, and nothing is at
            // -OutputFile for anyone to be holding.
            var before = PaginationCheckpoint.Load(checkpoint)!;
            Assert.NotNull(before.TempFile);
            Assert.False(File.Exists(output));

            handler.Queue(HttpStatusCode.OK, UsersPage2);
            var servedBefore = handler.Served;

            a = new Thread(() =>
            {
                try
                {
                    aResult = ExportParkedAtResume("/users", output, checkpoint,
                        arrived, released);
                }
                catch (Exception ex) { aFailed = ex; }
            });
            a.Start();
            Assert.True(arrived.Wait(TimeSpan.FromSeconds(60)),
                "export A never reached the window between its promotion and its writer");

            // Past the promotion: the rows are in the output, the temp is gone, and the position
            // on disk names no temp - which is exactly what a second run reads as an output it
            // may take. And short of the writer: no page has been asked for.
            //
            // Measured off the directory entry, since the run holds the file and a reader of it
            // is refused until the run ends. What it holds is read at the end, when it does not.
            var promotedBytes = ("{\"id\":\"u1\"}" + Environment.NewLine
                + "{\"id\":\"u2\"}" + Environment.NewLine).Length;
            Assert.Equal(promotedBytes, new FileInfo(output).Length);
            var repointed = PaginationCheckpoint.Load(checkpoint)!;
            Assert.Null(repointed.TempFile);
            Assert.Equal(promotedBytes, repointed.DataLength);
            Assert.Equal(["out.jsonl", "run.checkpoint"],
                Directory.GetFiles(dir).Select(Path.GetFileName)
                         .OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal(servedBefore, handler.Served);

            var (warnings, errors) = ExportRun("/users", output, checkpoint);

            var stop = Assert.Single(errors);
            Assert.StartsWith("CheckpointOutputHeld", stop.FullyQualifiedErrorId,
                StringComparison.Ordinal);
            Assert.Equal(ErrorCategory.ResourceBusy, stop.CategoryInfo.Category);
            Assert.Contains($"Another export is still writing '{output}'", stop.Exception.Message);
            Assert.DoesNotContain(warnings, w => w.Contains("no longer holds"));

            // B sent no request and took nothing with it.
            Assert.Equal(servedBefore, handler.Served);
            Assert.True(File.Exists(checkpoint), "B deleted the position A is resuming from");

            released.Set();
            Assert.True(a.Join(TimeSpan.FromSeconds(60)), "export A never finished");
            Assert.Null(aFailed);
            Assert.Empty(aResult.Errors);

            // One run had the file. Every item once, in order, and the position spent.
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(output));
            Assert.False(File.Exists(checkpoint), "the completed export left its position behind");
        }
        finally
        {
            released.Set();
            a?.Join(TimeSpan.FromSeconds(30));
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A -OutputFile the open fails on for a reason no list of access errnos carries. The
    /// failures were told apart the other way round - a short list meant "cannot be opened" and
    /// everything else meant a second run - so a symlink loop at the output path stopped the
    /// export with "Another export is still writing", and the caller was told to wait for a run
    /// that could not have existed: nothing can open that path, this export included.
    /// </summary>
    [Fact]
    public void A_symlink_loop_at_the_output_path_stops_the_export_as_unopenable()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            ResumedAndAppending(handler, output, checkpoint);
            var cp = PaginationCheckpoint.Load(checkpoint)!;
            Assert.Null(cp.TempFile);

            // The output's name now points at itself, one link along. ELOOP, which is neither a
            // sharing violation nor a permission the caller could grant.
            File.Delete(output);
            var other = Path.Combine(dir, "other.jsonl");
            File.CreateSymbolicLink(output, other);
            File.CreateSymbolicLink(other, output);

            handler.Queue(HttpStatusCode.OK, UsersPage2);
            var checkpointBefore = File.ReadAllBytes(checkpoint);
            var servedBefore = handler.Served;
            var (warnings, errors) = ExportRun("/users", output, checkpoint);

            var stop = Assert.Single(errors);
            Assert.StartsWith("CheckpointOutputUnopenable", stop.FullyQualifiedErrorId,
                StringComparison.Ordinal);
            // Not a permission, and not the category a caller reads as one: nothing about this
            // path is granted, and InvalidOperation is what says so.
            Assert.Equal(ErrorCategory.InvalidOperation, stop.CategoryInfo.Category);
            Assert.Contains($"'{output}' could not be opened for writing:", stop.Exception.Message);
            // The runtime's own ELOOP wording, not a guess at "on permissions" - and nothing in
            // it a caller could grant, so the advice that follows does not offer to.
            Assert.Contains("Too many levels of symbolic links", stop.Exception.Message);
            // No trailing period on this reason, so trimming one off it is a no-op: the single
            // period the sentence joins on is the one it always had.
            Assert.Contains($"Too many levels of symbolic links : '{output}'. The resume "
                + "checkpoint", stop.Exception.Message);
            Assert.Contains($"The resume checkpoint at '{checkpoint}' records",
                stop.Exception.Message);
            Assert.DoesNotContain("Another export is still writing", stop.Exception.Message);
            Assert.DoesNotContain("Grant write access", stop.Exception.Message);
            Assert.Contains(
                "Fix that and run again to resume from it, or remove it and the checkpoint to "
                + "export afresh.",
                stop.Exception.Message);
            Assert.Contains("This run stops here; nothing was written.", stop.Exception.Message);

            // Nothing ran, and the position counting the items is still there.
            Assert.Equal(servedBefore, handler.Served);
            Assert.DoesNotContain(warnings, w => w.Contains("no longer holds"));
            Assert.Equal(checkpointBefore, File.ReadAllBytes(checkpoint));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The other shape that got past both guards: a FIFO at the output path. It opens read-write
    /// without blocking, so the claim answered Taken, and the first question asked of the handle
    /// afterwards - how long is the file - threw a NotSupportedException out of the run with the
    /// FIFO still open. A stream with no length and no offset to write at is a file this run
    /// cannot resume into, decided at the open and let go there.
    /// </summary>
    [Fact]
    public void A_fifo_at_the_output_path_stops_the_export_as_unopenable()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            ResumedAndAppending(handler, output, checkpoint);
            var cp = PaginationCheckpoint.Load(checkpoint)!;
            Assert.Null(cp.TempFile);

            File.Delete(output);
            Mkfifo(output);

            handler.Queue(HttpStatusCode.OK, UsersPage2);
            var checkpointBefore = File.ReadAllBytes(checkpoint);
            var servedBefore = handler.Served;
            var (warnings, errors) = ExportRun("/users", output, checkpoint);

            var stop = Assert.Single(errors);
            Assert.StartsWith("CheckpointOutputUnopenable", stop.FullyQualifiedErrorId,
                StringComparison.Ordinal);
            Assert.Equal(ErrorCategory.InvalidOperation, stop.CategoryInfo.Category);
            Assert.Contains($"'{output}' could not be opened for writing: the file has no "
                + "offset to write at.", stop.Exception.Message);
            Assert.Contains($"The resume checkpoint at '{checkpoint}' records",
                stop.Exception.Message);
            Assert.DoesNotContain("Grant write access", stop.Exception.Message);
            Assert.Contains(
                "Fix that and run again to resume from it, or remove it and the checkpoint to "
                + "export afresh.",
                stop.Exception.Message);
            Assert.IsNotType<NotSupportedException>(stop.Exception);
            Assert.Equal(servedBefore, handler.Served);
            Assert.DoesNotContain(warnings, w => w.Contains("no longer holds"));
            Assert.Equal(checkpointBefore, File.ReadAllBytes(checkpoint));

            // And the handle it opened to find that out is closed: a claim of this test's own
            // on the same FIFO succeeds, which it would not while the run still held one.
            using var claimed = new FileStream(output, FileMode.Open, FileAccess.ReadWrite,
                FileShare.None);
        }
        finally
        {
            try { File.Delete(output); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The promotion route's own claim on the file it replaces. A run whose checkpoint names a
    /// temp puts those rows over the output and appends onto what it put there, and it asked for
    /// the file only after the move - which a rename asks nothing about, on either side of it. So
    /// the move went through over an output another run was holding and appending to: that run's
    /// next write landed in an inode with no name left on it, and the claim taken a statement
    /// later answered Taken about a file one statement old. The output is asked for first now,
    /// and a held one is the whole answer.
    /// </summary>
    [Fact]
    public void A_promotion_over_a_held_output_stops_and_replaces_nothing()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            InterruptedIntoATemp(handler, output, checkpoint);
            var cp = PaginationCheckpoint.Load(checkpoint)!;
            var temp = Path.Combine(dir, cp.TempFile!);
            Assert.True(File.Exists(temp), "the interrupted run left no temp to promote");
            Assert.False(File.Exists(output));

            // A previous export's rows at -OutputFile, and the run that has them: appending,
            // sharing reads, the way a resumed run holds its own output.
            File.WriteAllLines(output, OldExport);
            handler.Queue(HttpStatusCode.OK, UsersPage2);
            using (var live = new StreamWriter(
                       new FileStream(output, FileMode.Append, FileAccess.Write, FileShare.Read))
                   { AutoFlush = true })
            {
                var before = ReadDisk(dir, checkpoint, temp, output);
                var servedBefore = handler.Served;
                var (warnings, errors) = ExportRun("/users", output, checkpoint);

                var stop = Assert.Single(errors);
                Assert.StartsWith("CheckpointOutputHeld", stop.FullyQualifiedErrorId,
                    StringComparison.Ordinal);
                Assert.Equal(ErrorCategory.ResourceBusy, stop.CategoryInfo.Category);
                Assert.Contains($"Another export is still writing '{output}'",
                    stop.Exception.Message);
                // The temp, not the output, is where these items are counted on the promotion
                // route, and going on replaces the output rather than cutting it back - the
                // held sentence for a run that had already resumed says neither.
                Assert.Contains($"The resume checkpoint at '{checkpoint}' records "
                    + $"{cp.ItemsCollected} items into the temp '{cp.TempFile}'",
                    stop.Exception.Message);
                Assert.Contains($"going on would replace '{output}' with them under the run "
                    + "that holds it", stop.Exception.Message);
                Assert.DoesNotContain("cut that file back", stop.Exception.Message);
                Assert.Contains("This run stops here; nothing was written.",
                    stop.Exception.Message);

                // No request, no recovery, and nothing moved: the temp still holds the rows the
                // checkpoint counts, the checkpoint still names it, and the staged file the move
                // would have come from was taken back.
                Assert.Equal(servedBefore, handler.Served);
                Assert.DoesNotContain(warnings, w => w.Contains("Recovered"));
                AssertUnmoved(before, ReadDisk(dir, checkpoint, temp, output));
                Assert.False(File.Exists($"{output}.adopt"));

                // And the same inode, not a new file wearing the name: the holder's next write
                // is read back through the path.
                live.WriteLine("{\"id\":\"old-0000004\"}");
            }

            Assert.Equal([.. OldExport, "{\"id\":\"old-0000004\"}"], File.ReadAllLines(output));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The same claim, refused the other way, on the route where the move would have done the
    /// damage itself: a FIFO at -OutputFile is replaced by a rename, so the caller's pipe and
    /// whatever reads it would simply be gone - and the run would then have appended to a regular
    /// file nobody was reading.
    /// </summary>
    [Fact]
    public void A_promotion_over_a_fifo_output_stops_and_leaves_the_pipe()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            InterruptedIntoATemp(handler, output, checkpoint);
            var cp = PaginationCheckpoint.Load(checkpoint)!;
            var temp = Path.Combine(dir, cp.TempFile!);
            Assert.False(File.Exists(output));
            Mkfifo(output);

            handler.Queue(HttpStatusCode.OK, UsersPage2);
            var checkpointBefore = File.ReadAllBytes(checkpoint);
            var tempBefore = File.ReadAllBytes(temp);
            var servedBefore = handler.Served;
            var (warnings, errors) = ExportRun("/users", output, checkpoint);

            var stop = Assert.Single(errors);
            Assert.StartsWith("CheckpointOutputUnopenable", stop.FullyQualifiedErrorId,
                StringComparison.Ordinal);
            Assert.Equal(ErrorCategory.InvalidOperation, stop.CategoryInfo.Category);
            Assert.Contains($"'{output}' could not be opened for writing: the file has no "
                + "offset to write at.", stop.Exception.Message);
            // The temp, not the output, is where these items are counted on the promotion
            // route - the fixed lead-in above no longer says so on its own.
            Assert.Contains($"The resume checkpoint at '{checkpoint}' records "
                + $"{cp.ItemsCollected} items into the temp '{cp.TempFile}'", stop.Exception.Message);
            Assert.Contains($"cannot promote them into '{output}'", stop.Exception.Message);
            Assert.DoesNotContain("Grant write access", stop.Exception.Message);
            Assert.Contains(
                "Fix that and run again to recover them, or remove it and the checkpoint to "
                + "export afresh.",
                stop.Exception.Message);
            Assert.Equal(servedBefore, handler.Served);
            Assert.DoesNotContain(warnings, w => w.Contains("Recovered"));
            Assert.Equal(checkpointBefore, File.ReadAllBytes(checkpoint));
            Assert.Equal(tempBefore, File.ReadAllBytes(temp));
            Assert.False(File.Exists($"{output}.adopt"));

            // Still a pipe, and still nobody's: a stream with no offset in it is what says the
            // first, and a claim of this test's own would be refused if the run had kept one.
            using var stillAFifo = new FileStream(output, FileMode.Open, FileAccess.ReadWrite,
                FileShare.None);
            Assert.False(stillAFifo.CanSeek, "the promotion replaced the caller's FIFO");
        }
        finally
        {
            try { File.Delete(output); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The disk a promotion leaves for the instant between the temp's unlink and the save that
    /// repoints the position: the rows are in the output at the length recorded for them, the
    /// temp is gone, and the checkpoint still names it. Written out here rather than raced for,
    /// so what the route does with each shape can be asked one shape at a time.
    /// </summary>
    private static long PromotedButNotYetRepointed(string dir, string output, string checkpoint)
    {
        var cp = PaginationCheckpoint.Load(checkpoint)!;
        var temp = Path.Combine(dir, cp.TempFile!);
        var recorded = cp.DataLength!.Value;
        File.WriteAllBytes(output, File.ReadAllBytes(temp)[..(int)recorded]);
        File.Delete(temp);
        return recorded;
    }

    /// <summary>
    /// The window the second run walked through. A checkpoint naming a temp that is not on disk
    /// was read as items in no file at all: the run said so, deleted the position counting them,
    /// exported from the beginning and moved its own temp over the output at the end - which was
    /// the promoting run's output, held and being appended to. The output is asked about before
    /// any of that now, and a held one is the same stop the other two routes reach.
    /// </summary>
    [Fact]
    public void A_vanished_temp_over_a_held_output_stops_the_export()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            InterruptedIntoATemp(handler, output, checkpoint);
            var recorded = PromotedButNotYetRepointed(dir, output, checkpoint);
            Assert.Equal(recorded, new FileInfo(output).Length);
            var cp = PaginationCheckpoint.Load(checkpoint)!;
            Assert.NotNull(cp.TempFile);

            handler.Queue(HttpStatusCode.OK, UsersPage2);
            using var live = new StreamWriter(
                new FileStream(output, FileMode.Append, FileAccess.Write, FileShare.Read));

            var before = ReadDisk(dir, checkpoint, output);
            var servedBefore = handler.Served;
            var (warnings, errors) = ExportRun("/users", output, checkpoint);

            var stop = Assert.Single(errors);
            Assert.StartsWith("CheckpointOutputHeld", stop.FullyQualifiedErrorId,
                StringComparison.Ordinal);
            Assert.Contains($"Another export is still writing '{output}'", stop.Exception.Message);
            // The temp this checkpoint named is gone, not held - so the items are recorded
            // (past tense) into a temp the sentence does not go on to name, and going on means
            // exporting fresh rather than cutting the output back.
            Assert.Contains($"The resume checkpoint at '{checkpoint}' recorded "
                + $"{cp.ItemsCollected} items into a temp that is gone", stop.Exception.Message);
            Assert.Contains($"going on would export from the beginning and replace '{output}' "
                + "under the run that holds it", stop.Exception.Message);
            Assert.DoesNotContain("cut that file back", stop.Exception.Message);
            Assert.DoesNotContain(warnings, w => w.Contains("missing or incomplete"));
            Assert.Equal(servedBefore, handler.Served);
            AssertUnmoved(before, ReadDisk(dir, checkpoint, output));
            Assert.True(File.Exists(checkpoint),
                "the position the promoting run is resuming from was deleted");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The other half of the vanished-temp claim, where there is no second export at all: the
    /// output the promoting run left behind cannot be opened for writing by this account. It used
    /// to be reported as a file that never held the recorded items - the same misreading an
    /// unopenable output got on every route - and the position counting them deleted on the
    /// strength of it. The advice is the only thing that recovers here, so the run giving it
    /// leaves the checkpoint standing over a temp that is, in fact, already gone.
    /// </summary>
    [Fact]
    public void A_vanished_temp_over_an_unopenable_output_stops_the_export()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            InterruptedIntoATemp(handler, output, checkpoint);
            var recorded = PromotedButNotYetRepointed(dir, output, checkpoint);
            Assert.Equal(recorded, new FileInfo(output).Length);
            var cp = PaginationCheckpoint.Load(checkpoint)!;
            Assert.NotNull(cp.TempFile);

            // 0444: the owner cannot open it read-write either, which is the claim failing on
            // permissions rather than on another handle.
            File.SetUnixFileMode(output, UnixFileMode.UserRead);

            // Nothing is queued for this attempt: the stop is decided before any request, and a
            // response queued here and never served would still be waiting for the fresh export
            // below, ahead of the two pages that export actually asks for.
            var before = ReadDisk(dir, checkpoint, output);
            var servedBefore = handler.Served;
            var (warnings, errors) = ExportRun("/users", output, checkpoint);

            var stop = Assert.Single(errors);
            Assert.StartsWith("CheckpointOutputUnopenable", stop.FullyQualifiedErrorId,
                StringComparison.Ordinal);
            Assert.Equal(ErrorCategory.PermissionDenied, stop.CategoryInfo.Category);
            Assert.Contains($"'{output}' could not be opened for writing:", stop.Exception.Message);
            Assert.Contains("is denied", stop.Exception.Message);
            // The reason's own trailing period is trimmed before it is quoted, so the sentence
            // this joins into is punctuated once, not twice.
            Assert.DoesNotContain(".. ", stop.Exception.Message);
            Assert.Contains("is denied. The resume checkpoint", stop.Exception.Message);
            Assert.Contains($"The resume checkpoint at '{checkpoint}' recorded "
                + $"{cp.ItemsCollected} items into a temp that is gone", stop.Exception.Message);
            Assert.Contains($"cannot export a fresh copy into '{output}' either",
                stop.Exception.Message);
            Assert.Contains("This run stops here; nothing was written.", stop.Exception.Message);
            Assert.Contains(
                "Grant write access to that file and run again, or remove it and the checkpoint "
                + "to export afresh.",
                stop.Exception.Message);

            Assert.DoesNotContain(warnings, w => w.Contains("missing or incomplete"));
            Assert.Equal(servedBefore, handler.Served);
            AssertUnmoved(before, ReadDisk(dir, checkpoint, output));
            Assert.True(File.Exists(checkpoint),
                "the position counting the vanished temp's items was deleted");

            // Follow it. Nothing was recoverable from the temp regardless, so the run that comes
            // back exports fresh once it can reach the output at all.
            File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            handler.Queue(HttpStatusCode.OK, UsersPage1);
            handler.Queue(HttpStatusCode.OK, UsersPage2);
            var resumed = ExportWarnings("/users", output, checkpoint);

            Assert.Contains(resumed, w => w.Contains("missing or incomplete"));
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(output));
            Assert.False(File.Exists(checkpoint));
        }
        finally
        {
            try
            {
                File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                Directory.Delete(dir, true);
            }
            catch { }
        }
    }

    /// <summary>
    /// And the shape that is no promotion at all. The temp is gone and the file at -OutputFile is
    /// a previous export's, which nothing holds - and its length says nothing either way: two
    /// rows of one shape are two rows of another shape's length, so a previous run's output
    /// measures exactly what a promotion would have left about as often as a promoted one does.
    /// This checkpoint counts its items into the temp, so resuming into that file would put rows
    /// the caller has already consumed in front of this run's under no warning at all. It takes
    /// the route it always took, which replaces the whole file and cannot leave a wrong one
    /// behind.
    /// </summary>
    [Fact]
    public void A_vanished_temp_over_an_unheld_output_still_exports_from_the_beginning()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            InterruptedIntoATemp(handler, output, checkpoint);
            var cp = PaginationCheckpoint.Load(checkpoint)!;
            File.Delete(Path.Combine(dir, cp.TempFile!));

            // A previous export's two rows, of the same shape and so of the same length as the
            // two this checkpoint counts into the temp that has gone.
            File.WriteAllLines(output, ["{\"id\":\"x1\"}", "{\"id\":\"x2\"}"]);
            Assert.Equal(cp.DataLength, new FileInfo(output).Length);

            handler.Queue(HttpStatusCode.OK, UsersPage1);
            handler.Queue(HttpStatusCode.OK, UsersPage2);
            var (warnings, errors) = ExportRun("/users", output, checkpoint);

            Assert.Empty(errors);
            Assert.Contains(warnings, w => w.Contains("missing or incomplete"));

            // The whole file, and not the previous export's first two rows with this
            // enumeration's remainder spliced onto them.
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(output));
            Assert.False(File.Exists(checkpoint));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The two runs the shapes above were taken from, released together on the promotion route.
    /// Both completed: whichever lost the promotion found the temp gone, warned that the items it
    /// counted were not on disk, deleted the position counting them, exported from the beginning
    /// and moved its own temp over the file the other one was holding and appending to. One of
    /// them ends the run instead now, and nothing it would have taken with it moves.
    /// </summary>
    [Fact]
    public void Two_exports_released_together_on_the_promotion_route_leave_one_with_the_file()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        var gate = new GatedUsersByUrlHandler();
        var results = new (string[] Warnings, ErrorRecord[] Errors)[2];
        var failures = new Exception?[2];
        var threads = new Thread[2];
        var ready = new[] { new ManualResetEventSlim(false), new ManualResetEventSlim(false) };
        using var go = new ManualResetEventSlim(false);
        try
        {
            var setup = new ScriptedHandler();
            using (MgxTransportScope.Inject(setup))
            {
                InterruptedIntoATemp(setup, output, checkpoint);
            }
            Assert.NotNull(PaginationCheckpoint.Load(checkpoint)!.TempFile);
            Assert.False(File.Exists(output));

            using var transport = MgxTransportScope.Inject(gate);
            for (var i = 0; i < threads.Length; i++)
            {
                var slot = i;
                threads[slot] = new Thread(() =>
                {
                    try
                    {
                        results[slot] = ExportReleasedWith("/users", output, checkpoint,
                            ready[slot], go);
                    }
                    catch (Exception ex) { failures[slot] = ex; }
                });
                threads[slot].Start();
            }

            foreach (var r in ready)
                Assert.True(r.Wait(TimeSpan.FromSeconds(60)), "an export never came up");
            go.Set();

            // One of them got past the reconcile, so it holds the output and is parked on its
            // first request. It stays there until the other has finished, which is what the run
            // that stops does with nothing on the wire at all.
            Assert.True(gate.Arrived.Wait(TimeSpan.FromSeconds(60)),
                "neither export reached the wire");
            Assert.True(
                SpinWait.SpinUntil(() => threads.Any(t => !t.IsAlive), TimeSpan.FromSeconds(60)),
                "both exports were still running with one of them holding the output");

            gate.Released.Set();
            foreach (var t in threads)
                Assert.True(t.Join(TimeSpan.FromSeconds(60)), "an export never finished");
            Assert.All(failures, Assert.Null);

            // Exactly one run stopped, on one of the two files this checkpoint stands for, and
            // it neither asked for a page nor said the items it was protecting were lost.
            var stop = Assert.Single(results.SelectMany(r => r.Errors));
            Assert.StartsWith("Checkpoint", stop.FullyQualifiedErrorId, StringComparison.Ordinal);
            Assert.Contains("This run stops here; nothing was written.", stop.Exception.Message);
            Assert.DoesNotContain(results.SelectMany(r => r.Warnings),
                w => w.Contains("missing or incomplete"));

            // One run had the file: every item once, in order, and the position spent by the run
            // that finished rather than by the run that gave up on it.
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(output));
            Assert.False(File.Exists(checkpoint));
            Assert.Empty(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
            Assert.Empty(Directory.GetFiles(dir, "*.adopt"));
        }
        finally
        {
            gate.Released.Set();
            foreach (var t in threads) t?.Join(TimeSpan.FromSeconds(30));
            foreach (var r in ready) r.Dispose();
            gate.Arrived.Dispose();
            gate.Released.Dispose();
            gate.Dispose();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The write a promotion makes before it replaces anything, refused. A directory standing
    /// where the staged copy goes is one of the ways it fails, and the answer used to come out of
    /// the catch around the whole promotion as "nothing was promoted" - which the caller reads as
    /// the temp having gone: it said the items it counted were not on disk, deleted the position
    /// counting them, exported from the beginning and swept the temp that was holding every one.
    /// The staging says so for itself now, and both files are where they were found.
    /// </summary>
    [Fact]
    public void A_directory_at_the_staging_name_stops_the_export_and_changes_nothing()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            InterruptedIntoATemp(handler, output, checkpoint);
            var cp = PaginationCheckpoint.Load(checkpoint)!;
            var temp = Path.Combine(dir, cp.TempFile!);
            Assert.True(File.Exists(temp), "the interrupted run left no temp to promote");

            var adopt = $"{output}.adopt";
            Directory.CreateDirectory(adopt);

            handler.Queue(HttpStatusCode.OK, UsersPage2);
            var before = ReadDisk(dir, checkpoint, temp, output);
            var servedBefore = handler.Served;
            var (warnings, errors) = ExportRun("/users", output, checkpoint);

            var stop = Assert.Single(errors);
            Assert.StartsWith("CheckpointStagingFailed", stop.FullyQualifiedErrorId,
                StringComparison.Ordinal);
            Assert.Equal(ErrorCategory.WriteError, stop.CategoryInfo.Category);
            Assert.Contains($"The {cp.ItemsCollected} items the resume checkpoint at "
                + $"'{checkpoint}' records are in '{cp.TempFile}', whole, but staging them into "
                + $"'{output}' failed at '{adopt}': ", stop.Exception.Message);
            Assert.Contains("Nothing was changed; fix that and run again to recover them, or "
                + "remove the temp and the checkpoint to export afresh.", stop.Exception.Message);

            // Not the sentence for a temp that has gone, and not the delete that goes with it.
            // The runtime's own words, punctuated once: the reason is quoted inside a sentence
            // that ends it, and a message already ending in a period put two there.
            Assert.DoesNotContain(".. Nothing was changed;", stop.Exception.Message);

            Assert.DoesNotContain(warnings, w => w.Contains("missing or incomplete"));
            Assert.DoesNotContain(warnings, w => w.Contains("Recovered"));
            Assert.Equal(servedBefore, handler.Served);
            AssertUnmoved(before, ReadDisk(dir, checkpoint, temp, output));
            Assert.True(Directory.Exists(adopt), "the run removed the caller's directory");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The same write refused the other way: another run holding the staging name, which is what
    /// two exports over one command line reach a page apart. Nothing this run may take, and
    /// nothing it may report as a temp that has gone.
    /// </summary>
    [Fact]
    public void A_held_staging_name_stops_the_export_and_leaves_the_holders_file()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            InterruptedIntoATemp(handler, output, checkpoint);
            var cp = PaginationCheckpoint.Load(checkpoint)!;
            var temp = Path.Combine(dir, cp.TempFile!);
            var adopt = $"{output}.adopt";

            // The run that has it, holding the staged copy the way a promotion holds its own.
            using var holder = new FileStream(adopt, FileMode.Create, FileAccess.ReadWrite,
                FileShare.None);
            holder.Write("{\"id\":\"theirs\"}\n"u8);
            holder.Flush();
            var holderBytes = holder.Length;

            handler.Queue(HttpStatusCode.OK, UsersPage2);
            var before = ReadDisk(dir, checkpoint, temp, output);
            var servedBefore = handler.Served;
            var (warnings, errors) = ExportRun("/users", output, checkpoint);

            var stop = Assert.Single(errors);
            Assert.StartsWith("CheckpointStagingFailed", stop.FullyQualifiedErrorId,
                StringComparison.Ordinal);
            Assert.Equal(ErrorCategory.WriteError, stop.CategoryInfo.Category);
            Assert.Contains($"staging them into '{output}' failed at '{adopt}': ",
                stop.Exception.Message);
            Assert.Contains("Nothing was changed;", stop.Exception.Message);
            Assert.DoesNotContain(warnings, w => w.Contains("missing or incomplete"));
            Assert.Equal(servedBefore, handler.Served);

            // The temp and the checkpoint as they were, and the holder's own bytes still theirs:
            // a promotion that could not stage does not truncate what it could not open.
            AssertUnmoved(before, ReadDisk(dir, checkpoint, temp, output));
            Assert.Equal(holderBytes, holder.Length);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The copy a promotion stages, left behind by a run killed between the write and the
    /// rename. No glob of the sweep's shape reaches that name - it is the output's own name with
    /// a suffix, not "{output}.{32-hex}.tmp" - so it stood beside the output until somebody
    /// noticed it, holding a partial copy of an earlier run's rows. The sweep takes it by name
    /// now, and asks for it first, the way it asks for a temp.
    /// </summary>
    [Fact]
    public void A_staged_copy_an_interrupted_promotion_left_is_swept()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var adopt = $"{output}.adopt";
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // What a killed promotion leaves: its staged copy, and no checkpoint counting it.
            File.WriteAllLines(adopt, OldExport);
            var orphanTemp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
            File.WriteAllLines(orphanTemp, OldExport);

            handler.Queue(HttpStatusCode.OK, UsersPage1);
            handler.Queue(HttpStatusCode.OK, UsersPage2);
            Export("/users", output, Path.Combine(dir, "run.checkpoint"));

            Assert.False(File.Exists(adopt), "the staged copy outlived the run that swept");
            Assert.False(File.Exists(orphanTemp));
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(output));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// And the one the sweep may not take: a promotion running right now holds the name it has
    /// just staged, and a file another run holds is not an orphan whichever name it wears.
    /// </summary>
    [Fact]
    public void A_staged_copy_another_run_holds_is_left_alone()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var adopt = $"{output}.adopt";
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            using var holder = new FileStream(adopt, FileMode.Create, FileAccess.ReadWrite,
                FileShare.None);
            holder.Write("{\"id\":\"theirs\"}\n"u8);
            holder.Flush();
            var holderBytes = holder.Length;

            handler.Queue(HttpStatusCode.OK, UsersPage1);
            handler.Queue(HttpStatusCode.OK, UsersPage2);
            Export("/users", output, Path.Combine(dir, "run.checkpoint"));

            Assert.True(File.Exists(adopt), "the sweep took a staged copy another run was holding");
            Assert.Equal(holderBytes, holder.Length);
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(output));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// Both streams of an export, for a scenario whose evidence is on the verbose one: whether
    /// the run swept the temps beside its output is not something a warning says.
    /// </summary>
    private static (string[] Warnings, string[] Verbose) ExportStreams(string uri,
        string outputPath, string checkpointPath)
    {
        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection).Assembly);
        ps.Invoke();
        ps.Commands.Clear();

        ps.AddCommand("Export-MgxCollection")
          .AddParameter("Uri", uri)
          .AddParameter("OutputFile", outputPath)
          .AddParameter("CheckpointPath", checkpointPath)
          .AddParameter("All")
          .AddParameter("Verbose");
        try { ps.Invoke(); }
        catch (CmdletInvocationException) { }
        return ([.. ps.Streams.Warning.Select(w => w.Message)],
                [.. ps.Streams.Verbose.Select(v => v.Message)]);
    }

    /// <summary>
    /// A refusal leaves the checkpoint standing because the export that wrote it resumes from
    /// exactly that, and the items it counted are in a temp beside this output. A checkpoint
    /// written before the temp's name was recorded names none - the shape every release before
    /// 2.1 wrote - so keyed on that name the sweep in the same run found nothing to spare and
    /// deleted the file the spared position was pointing at, under a warning that had promised
    /// to leave things alone. The refusal takes the whole sweep with it instead.
    /// </summary>
    [Fact]
    public void A_refusal_of_a_checkpoint_naming_no_temp_holds_the_whole_sweep_off()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        var orphan = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // A groups export killed partway: its two items in a temp beside this output, no
            // output of its own yet, and a position counting them that names neither file.
            var counted = "{\"id\":\"g1\"}\n{\"id\":\"g2\"}\n";
            File.WriteAllText(orphan, counted);
            File.WriteAllText(checkpoint, """
            {"resource":"https://graph.microsoft.com/v1.0/groups?$top=999",
             "nextLink":"https://graph.microsoft.com/v1.0/groups?$skiptoken=P2",
             "itemsCollected":2,"pageItemsAlreadyWritten":0}
            """);

            // A users export over the same -CheckpointPath. It refuses that position and dies
            // on its first request, so it leaves no output and no temp of its own.
            var (warnings, verbose) = ExportStreams("/users", output, checkpoint);

            Assert.Contains(warnings, w => w.Contains("belongs to a different export",
                StringComparison.Ordinal));
            Assert.Contains(verbose, v => v.Contains(
                $"Left the temp files beside '{output}' alone: one of them may hold the items "
                + "of the checkpoint this run refused.", StringComparison.Ordinal));
            Assert.True(File.Exists(orphan),
                "the sweep took the temp the refused checkpoint stands for");
            Assert.Equal(counted, File.ReadAllText(orphan));
            Assert.True(File.Exists(checkpoint), "the refused position was deleted");
            Assert.False(File.Exists(output), "the run that stopped left an output behind");

            // And the export that owns both comes back to find them where it left them.
            handler.Queue(HttpStatusCode.OK, GroupsPage2);
            var (recovered, _) = ExportStreams("/groups", output, checkpoint);

            Assert.Contains(recovered, w => w.Contains(
                "Recovered 2 items from an interrupted export's temp file.",
                StringComparison.Ordinal));
            Assert.Equal(["{\"id\":\"g1\"}", "{\"id\":\"g2\"}", "{\"id\":\"g3\"}"],
                File.ReadAllLines(output));
            Assert.Empty(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// An export run with a -WarningAction of the caller's choosing, both streams captured. A
    /// warning is a terminating error under Stop and nothing at all under SilentlyContinue, so
    /// where the run writes one decides what a caller is left holding.
    /// </summary>
    private static (string[] Warnings, string[] Verbose, ErrorRecord[] Errors) ExportWith(
        string uri, string outputPath, string checkpointPath, string warningAction)
    {
        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection).Assembly);
        ps.Invoke();
        ps.Commands.Clear();

        ps.AddCommand("Export-MgxCollection")
          .AddParameter("Uri", uri)
          .AddParameter("OutputFile", outputPath)
          .AddParameter("CheckpointPath", checkpointPath)
          .AddParameter("All")
          .AddParameter("Verbose")
          .AddParameter("WarningAction", warningAction);
        List<ErrorRecord> errors = [];
        try { ps.Invoke(); }
        catch (CmdletInvocationException ex) { errors.Add(ex.ErrorRecord); }
        // What -WarningAction Stop raises, and it is not a CmdletInvocationException: left
        // uncaught it comes off the thread this runs on and takes the test host with it.
        catch (ActionPreferenceStopException ex) { errors.Add(ex.ErrorRecord); }
        errors.AddRange(ps.Streams.Error);
        return ([.. ps.Streams.Warning.Select(w => w.Message)],
                [.. ps.Streams.Verbose.Select(v => v.Message)],
                [.. errors]);
    }

    /// <summary>
    /// The state a crash leaves partway through a checkpointed export, with a pipe standing at
    /// the staging name: the previous export's output, this run's items in the temp beside it,
    /// a checkpoint naming both, and an entry the recovery has to take off the name first.
    /// </summary>
    private static (string Temp, string Adopt) InterruptedWithAPipeAtTheStagingName(string dir,
        string output, string checkpoint)
    {
        File.WriteAllText(output, "{\"id\":\"old\"}\n");
        var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, "{\"id\":\"u1\"}\n{\"id\":\"u2\"}\n");
        new PaginationCheckpoint
        {
            Resource = "https://graph.microsoft.com/v1.0/users?$top=999",
            NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
            ItemsCollected = 2,
            PageItemsAlreadyWritten = 0,
            TempFile = Path.GetFileName(temp),
            OutputFile = output,
            DataLength = new FileInfo(temp).Length,
        }.Save(checkpoint);
        var adopt = $"{output}.adopt";
        Mkfifo(adopt);
        return (temp, adopt);
    }

    /// <summary>
    /// The sweep's removal warning was written where the deletion happens, which is above the
    /// recovery it clears the way for. A warning is a terminating error under -WarningAction
    /// Stop, so the run ended there: the pipe deleted, the items still in the temp, the output
    /// still the previous export's, and a caller told only that something had been removed. The
    /// warning is held until this run's own outcome for the recovery is known and written after
    /// it, so a run stopped by it stops with the promotion made and the position repointed at
    /// the file it landed in - resumable.
    /// </summary>
    [Fact]
    public void A_sweep_under_WarningAction_Stop_leaves_the_recovery_made_and_resumable()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        string? adopt = null;
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);
            var (temp, adoptPath) = InterruptedWithAPipeAtTheStagingName(dir, output, checkpoint);
            adopt = adoptPath;

            // On its own thread with a watchdog: an open of a pipe with no reader never comes
            // back, and a test that called the export inline would hang the suite.
            (string[] Warnings, string[] Verbose, ErrorRecord[] Errors) result = ([], [], []);
            var running = new Thread(() =>
                result = ExportWith("/users", output, checkpoint, "Stop")) { IsBackground = true };
            running.Start();
            Assert.True(running.Join(TimeSpan.FromSeconds(30)),
                "the run never returned: the staging open is waiting for a reader on the pipe");

            // The run ended on a warning, which is what Stop makes of one.
            var stop = Assert.Single(result.Errors);
            Assert.StartsWith("ActionPreferenceStop", stop.FullyQualifiedErrorId,
                StringComparison.Ordinal);

            // And what it ended with: the items promoted into the output, the temp spent, the
            // pipe off the name, and a position that records the file they are in.
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}"], File.ReadAllLines(output));
            Assert.False(File.Exists(temp), "the promoted temp was left on disk");
            Assert.False(File.Exists(adoptPath), "the pipe outlived the run");
            var cp = PaginationCheckpoint.Load(checkpoint)!;
            Assert.Null(cp.TempFile);
            Assert.Equal(Path.GetFullPath(output), cp.OutputFile);
            Assert.Equal(new FileInfo(output).Length, cp.DataLength);
            Assert.Equal(0, handler.Served);
        }
        finally
        {
            if (adopt != null) { try { File.Delete(adopt); } catch { } }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The other end of the same preference. -WarningAction SilentlyContinue takes the removal
    /// off every stream a caller sees, and an entry beside their output was deleted with nothing
    /// anywhere to say so. The verbose line stays where the deletion happens, as the record of
    /// when it happened, so a caller who asks for verbose is told either way.
    /// </summary>
    [Fact]
    public void A_sweep_under_WarningAction_SilentlyContinue_still_removes_and_says_so_in_verbose()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        string? adopt = null;
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);
            var (temp, adoptPath) = InterruptedWithAPipeAtTheStagingName(dir, output, checkpoint);
            adopt = adoptPath;
            handler.Queue(HttpStatusCode.OK, UsersPage2);

            (string[] Warnings, string[] Verbose, ErrorRecord[] Errors) result = ([], [], []);
            var running = new Thread(() =>
                    result = ExportWith("/users", output, checkpoint, "SilentlyContinue"))
                { IsBackground = true };
            running.Start();
            Assert.True(running.Join(TimeSpan.FromSeconds(30)),
                "the run never returned: the staging open is waiting for a reader on the pipe");

            Assert.Empty(result.Errors);
            Assert.Empty(result.Warnings);
            Assert.Contains(result.Verbose, v => v.Contains(
                $"Removed what stood at the staging name '{Path.GetFileName(adoptPath)}': "
                + "an entry no copy could be staged in, such as a pipe or a socket.",
                StringComparison.Ordinal));
            Assert.False(File.Exists(adoptPath), "the pipe outlived the run");
            Assert.False(File.Exists(temp), "the promoted temp was left on disk");
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(output));
            Assert.False(File.Exists(checkpoint), "the completed export left its position behind");
        }
        finally
        {
            if (adopt != null) { try { File.Delete(adopt); } catch { } }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A staging failure after the sweep took something off the name. The removal cannot be a
    /// warning of its own there - a warning in front of a terminating error is what
    /// -WarningAction Stop ends the run on instead of the stop - so the stop says it: a reader
    /// told only that the staging failed goes looking for a file that is no longer there. The
    /// two halves of one disk state, in one sentence, and the same phrase the warning uses.
    /// </summary>
    [Fact]
    public void The_staging_stop_names_what_the_sweep_took_off_the_name()
    {
        var message = typeof(Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection).GetMethod(
            "CheckpointStagingStopMessage",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var checkpoint = new PaginationCheckpoint { ItemsCollected = 7 };
        string Build(string? removedKind) => (string)message.Invoke(null, [
            Activator.CreateInstance(
                typeof(Mgx.Cmdlets.Base.MgxCmdletBase).GetNestedType("StagingFailure",
                    System.Reflection.BindingFlags.NonPublic)!,
                ["/d/out.jsonl.adopt", "the volume is full"])!,
            "/d/run.checkpoint", "/d/out.jsonl", checkpoint, "out.jsonl.abc.tmp", removedKind,
            "Nothing was changed;"])!;

        var swept = Build("a link, and not what it pointed at");
        Assert.Contains("failed at '/d/out.jsonl.adopt': the volume is full, after removing what "
            + "stood at the staging name: a link, and not what it pointed at. Nothing was "
            + "changed;", swept);

        // And where the name was clear, the sentence says nothing about a removal.
        var clear = Build(null);
        Assert.Contains("failed at '/d/out.jsonl.adopt': the volume is full. Nothing was changed;",
            clear);
        Assert.DoesNotContain("after removing", clear);
    }
}
