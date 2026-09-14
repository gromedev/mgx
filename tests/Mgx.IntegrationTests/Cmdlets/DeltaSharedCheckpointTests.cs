using System.Reflection;
using System.Management.Automation;
using System.Net;
using System.Text;
using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests;

/// <summary>
/// Two syncs of the same -Uri build the same resource string, so the resource comparison that
/// guards recovery cannot tell them apart. What separates them is the file they collect into,
/// and a checkpoint that records a byte length used to record nothing about which file that
/// length was measured in - so a shared -CheckpointPath let one sync cut the other's output
/// back to its own offset, mid-line, and append the resumed page onto the torn byte.
/// </summary>
[Collection("Pipeline")]
public class DeltaSharedCheckpointTests
{
    private const string ChangesPage1 = """
    {"value":[{"id":"b1"},{"id":"b2"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users/delta?$skiptoken=B2"}
    """;
    private const string ChangesPage2 = """
    {"value":[{"id":"b3"}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/users/delta?$deltatoken=D2"}
    """;
    // One page and no delta token, so a run that reaches it finishes without saving any state -
    // and the run after it builds exactly the request URL this one did.
    private const string ChangesPageNoToken = """
    {"value":[{"id":"b9"}]}
    """;
    // What "$deltatoken=latest" answers: nothing to return now, and a token to return from next
    // time.
    private const string LatestBaseline = """
    {"value":[],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/users/delta?$deltatoken=D9"}
    """;
    private const string ServerError = """{"error":{"code":"InternalServerError","message":"boom"}}""";

    private const string GroupsPage = """
    {"value":[{"id":"g1"}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/groups/delta?$deltatoken=G1"}
    """;
    // A first page with a continuation, so an attempt can reach a page boundary - and save a
    // checkpoint of its own naming its own temp - before the request after it answers 410.
    private const string GroupsPageOne = """
    {"value":[{"id":"g1"},{"id":"g2"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/groups/delta?$skiptoken=G2"}
    """;
    private const string GroupsResync = """
    {"value":[{"id":"g9"}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/groups/delta?$deltatoken=G1"}
    """;
    private const string TokenExpired =
        """{"error":{"code":"deltaTokenExpired","message":"Delta token has expired"}}""";

    private static readonly string[] OldSync =
        ["{\"id\":\"old-0000001\"}", "{\"id\":\"old-0000002\"}", "{\"id\":\"old-0000003\"}"];

    /// <summary>A groups sync holding a token older than Graph keeps them.</summary>
    private static void StaleState(string deltaPath) =>
        new DeltaState
        {
            DeltaLink = "https://graph.microsoft.com/v1.0/groups/delta?$deltatoken=stale",
            Resource = "/groups/delta",
            GraphEndpoint = "https://graph.microsoft.com",
            Select = "",
            ApiVersion = "v1.0",
        }.Save(deltaPath);

    /// <summary>A users sync partway through, holding the token its last run was issued.</summary>
    private static void UsersState(string deltaPath) =>
        new DeltaState
        {
            DeltaLink = "https://graph.microsoft.com/v1.0/users/delta?$deltatoken=D0",
            Resource = "/users/delta",
            GraphEndpoint = "https://graph.microsoft.com",
            Select = "",
            ApiVersion = "v1.0",
        }.Save(deltaPath);

    /// <summary>
    /// Runs a sync on this thread and parks it inside the window the hold exists to close: past
    /// the reconcile, which has claimed the output and cut it back, and short of the writer that
    /// used to open a handle of its own a page fetch later. The verbose the resume announces
    /// itself with is written from exactly there, and a subscriber to that stream blocks the
    /// pipeline where it is raised.
    /// </summary>
    private static (string[] Warnings, ErrorRecord[] Errors) SyncParkedAtResume(
        string deltaPath, string checkpointPath, string outputPath,
        ManualResetEventSlim arrived, ManualResetEventSlim released)
    {
        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Delta.SyncMgxDelta).Assembly);
        ps.Invoke();
        ps.Commands.Clear();

        ps.Streams.Verbose.DataAdding += (_, e) =>
        {
            if (e.ItemAdded is VerboseRecord v
                && v.Message.Contains("Resuming delta enumeration", StringComparison.Ordinal)
                && !arrived.IsSet)
            {
                arrived.Set();
                released.Wait();
            }
        };

        ps.AddCommand("Sync-MgxDelta")
          .AddParameter("Uri", "/users/delta")
          .AddParameter("DeltaPath", deltaPath)
          .AddParameter("CheckpointPath", checkpointPath)
          .AddParameter("OutputFile", outputPath)
          .AddParameter("Verbose");
        List<ErrorRecord> errors = [];
        try { ps.Invoke(); }
        catch (CmdletInvocationException ex) { errors.Add(ex.ErrorRecord); }
        errors.AddRange(ps.Streams.Error);
        return ([.. ps.Streams.Warning.Select(w => w.Message)], [.. errors]);
    }

    /// <summary>
    /// A sync built and waiting to be let go: the runspace is up and the module imported before
    /// <paramref name="ready"/> is set, so what <paramref name="go"/> releases is two runs
    /// arriving at the reconcile together rather than two runspaces starting up.
    /// </summary>
    private static (string[] Warnings, ErrorRecord[] Errors) SyncReleasedWith(string deltaPath,
        string checkpointPath, string outputPath,
        ManualResetEventSlim ready, ManualResetEventSlim go)
    {
        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Delta.SyncMgxDelta).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        ps.AddCommand("Sync-MgxDelta")
          .AddParameter("Uri", "/users/delta")
          .AddParameter("DeltaPath", deltaPath)
          .AddParameter("CheckpointPath", checkpointPath)
          .AddParameter("OutputFile", outputPath);

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
    private sealed class GatedChangesByUrlHandler : HttpMessageHandler
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
                .Contains("skiptoken=B2", StringComparison.Ordinal) ? ChangesPage2 : ChangesPage1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static void Sync(string deltaPath, string checkpointPath, string outputPath,
        string uri = "/users/delta")
    {
        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Delta.SyncMgxDelta).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        ps.AddCommand("Sync-MgxDelta")
          .AddParameter("Uri", uri)
          .AddParameter("DeltaPath", deltaPath)
          .AddParameter("CheckpointPath", checkpointPath)
          .AddParameter("OutputFile", outputPath);
        // A run that dies is part of the scenario, so a terminating error is expected.
        try { ps.Invoke(); }
        catch (CmdletInvocationException) { }
    }

    /// <summary>
    /// The same sync with the error stream captured too. A run that will not touch the two files
    /// the checkpoint stands for ends on a terminating error, and the id and the sentence are
    /// the whole of what the caller has to act on. Both places PowerShell can put that record
    /// are read, since which one it uses depends on how the command was added.
    /// </summary>
    private static (string[] Warnings, ErrorRecord[] Errors) SyncRun(string deltaPath,
        string checkpointPath, string outputPath, bool latest = false)
    {
        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Delta.SyncMgxDelta).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        ps.AddCommand("Sync-MgxDelta")
          .AddParameter("Uri", "/users/delta")
          .AddParameter("DeltaPath", deltaPath)
          .AddParameter("CheckpointPath", checkpointPath)
          .AddParameter("OutputFile", outputPath);
        if (latest) ps.AddParameter("Latest");
        List<ErrorRecord> errors = [];
        try { ps.Invoke(); }
        catch (CmdletInvocationException ex) { errors.Add(ex.ErrorRecord); }
        errors.AddRange(ps.Streams.Error);
        return ([.. ps.Streams.Warning.Select(w => w.Message)], [.. errors]);
    }

    /// <summary>
    /// Everything a run that stops may not have moved: both state files, the output if there is
    /// one, the temp the checkpoint names, and the directory's own entry list - which is what
    /// says no temp of the stopped run's, and no probe of its own, outlived it.
    /// </summary>
    private sealed class DiskState
    {
        public byte[] Checkpoint = [];
        public byte[] Temp = [];
        public byte[]? Output;
        public byte[]? Delta;
        public string[] Entries = [];
    }

    private static DiskState ReadDisk(string dir, string checkpointPath, string temp,
        string outputPath, string deltaPath) => new()
    {
        Checkpoint = File.ReadAllBytes(checkpointPath),
        Temp = ReadShared(temp),
        Output = File.Exists(outputPath) ? ReadShared(outputPath) : null,
        Delta = File.Exists(deltaPath) ? File.ReadAllBytes(deltaPath) : null,
        Entries = [.. Directory.GetFiles(dir).Select(f => Path.GetFileName(f))
                            .OrderBy(n => n, StringComparer.Ordinal)],
    };

    /// <summary>
    /// The same, for the shape a resumed sync leaves: its changes are in the output itself and
    /// the checkpoint names no temp, so there is no third file to read.
    /// </summary>
    private static DiskState ReadDisk(string dir, string checkpointPath, string outputPath,
        string deltaPath) => new()
    {
        Checkpoint = File.ReadAllBytes(checkpointPath),
        Output = ReadShared(outputPath),
        Delta = File.Exists(deltaPath) ? File.ReadAllBytes(deltaPath) : null,
        Entries = [.. Directory.GetFiles(dir).Select(f => Path.GetFileName(f))
                            .OrderBy(n => n, StringComparer.Ordinal)],
    };

    private static void AssertUnmoved(DiskState before, DiskState after)
    {
        Assert.Equal(before.Entries, after.Entries);
        Assert.Equal(before.Checkpoint, after.Checkpoint);
        Assert.Equal(before.Temp, after.Temp);
        Assert.Equal(before.Output, after.Output);
        Assert.Equal(before.Delta, after.Delta);
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

    private static string[] SyncWarnings(string deltaPath, string checkpointPath, string outputPath,
        string uri = "/users/delta", bool latest = false,
        string[]? property = null, string[]? prefer = null, string? filter = null)
    {
        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Delta.SyncMgxDelta).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        ps.AddCommand("Sync-MgxDelta")
          .AddParameter("Uri", uri)
          .AddParameter("DeltaPath", deltaPath)
          .AddParameter("CheckpointPath", checkpointPath)
          .AddParameter("OutputFile", outputPath);
        if (latest) ps.AddParameter("Latest");
        if (property != null) ps.AddParameter("Property", property);
        if (prefer != null) ps.AddParameter("Prefer", prefer);
        if (filter != null) ps.AddParameter("Filter", filter);
        try { ps.Invoke(); }
        catch (CmdletInvocationException) { }
        return [.. ps.Streams.Warning.Select(w => w.Message)];
    }

    /// <summary>
    /// Sync A is interrupted twice, so the checkpoint it leaves records a length with no temp
    /// to explain it. Sync B, its own delta state and its own output, must not have that
    /// length applied to its file.
    /// </summary>
    [Fact]
    public void A_checkpoint_from_another_sync_does_not_cut_this_ones_output()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-shared-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaA = Path.Combine(dir, "stateA.json");
        var deltaB = Path.Combine(dir, "stateB.json");
        var outA = Path.Combine(dir, "outA.jsonl");
        var outB = Path.Combine(dir, "outB.jsonl");

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);                 // A run 1, page 1
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError); // A run 1, page 2
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError); // A run 2 promotes, then dies
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);                 // B, page 1
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);                 // B, page 2
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            Sync(deltaA, checkpointPath, outA);
            Sync(deltaA, checkpointPath, outA);

            var cp = PaginationCheckpoint.Load(checkpointPath)!;
            Assert.Null(cp.TempFile);
            Assert.Equal(new FileInfo(outA).Length, cp.DataLength);

            // B has its own delta state and its own output, which already holds an earlier sync.
            File.WriteAllLines(outB, OldSync);
            Sync(deltaB, checkpointPath, outB);

            var lines = File.ReadAllLines(outB);
            Assert.All(lines, l => Assert.StartsWith("{\"id\":\"", l));
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"], lines);

            // A's own output is untouched by B.
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}"], File.ReadAllLines(outA));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The same collision between two directories rather than two names. What distinguishes the
    /// syncs is which file they collect into, and a leaf comparison cannot see that: sync B
    /// adopted A's position and cut B's own output to A's byte count.
    /// </summary>
    [Fact]
    public void A_checkpoint_for_the_same_name_in_another_directory_does_not_cut_this_ones_output()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-shared-dir-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaA = Path.Combine(dir, "stateA.json");
        var deltaB = Path.Combine(dir, "stateB.json");
        var outA = Path.Combine(Directory.CreateDirectory(Path.Combine(dir, "a")).FullName, "out.jsonl");
        var outB = Path.Combine(Directory.CreateDirectory(Path.Combine(dir, "b")).FullName, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);                 // A run 1, page 1
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError); // A run 1, page 2
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError); // A run 2 promotes, then dies
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);                 // B, page 1
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);                 // B, page 2
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            Sync(deltaA, checkpointPath, outA);
            Sync(deltaA, checkpointPath, outA);

            var cp = PaginationCheckpoint.Load(checkpointPath)!;
            Assert.Null(cp.TempFile);
            Assert.Equal(new FileInfo(outA).Length, cp.DataLength);

            File.WriteAllLines(outB, OldSync);
            Sync(deltaB, checkpointPath, outB);

            var lines = File.ReadAllLines(outB);
            Assert.All(lines, l => Assert.StartsWith("{\"id\":\"", l));
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"], lines);
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}"], File.ReadAllLines(outA));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The same sync, resumed with -Uri typed differently. Graph answers "/users/delta" and
    /// "/Users/delta" from one collection, so both runs enumerate the same thing - but the
    /// recorded resource was compared ordinally, which made the second run a different sync:
    /// its own position was refused as another's, the temp holding the changes the first run
    /// had already collected was swept, and the enumeration started over.
    /// </summary>
    [Fact]
    public void A_checkpoint_written_under_another_spelling_of_the_resource_still_resumes()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-spelling-{Guid.NewGuid():N}")).FullName;
        var deltaPath = Path.Combine(dir, "state.json");
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            // Page one reaches the temp; page two fails, so the position survives.
            Sync(deltaPath, checkpointPath, output, "/users/delta");
            Assert.NotNull(PaginationCheckpoint.Load(checkpointPath));
            Assert.Single(Directory.GetFiles(dir, "out.jsonl.*.tmp"));

            // The resume, typed the other way, with only the page the dead run never got.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            var first = handler.CapturedRequests.Count;
            Sync(deltaPath, checkpointPath, output, "/Users/delta");

            Assert.Contains("skiptoken=B2", handler.CapturedRequests[first].Uri);
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                File.ReadAllLines(output));
            var state = DeltaState.Load(deltaPath);
            Assert.NotNull(state);
            Assert.Contains("$deltatoken=D2", state!.DeltaLink);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A sync writing to the pipeline was outside the ownership check entirely, so a checkpoint
    /// left by a sync collecting into a file resumed it at that sync's nextLink. Every page
    /// before that one went to the file and never to the pipeline, and the delta token was
    /// saved over them on success - which is the one failure a delta sync cannot recover from,
    /// since there is no re-fetch once the token moves.
    /// </summary>
    [Fact]
    public void A_pipeline_sync_does_not_resume_from_a_checkpoint_that_collected_into_a_file()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-mode-{Guid.NewGuid():N}")).FullName;
        var deltaPath = Path.Combine(dir, "state.json");
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        try
        {
            // The other sync's position: page 1 is already in its file, page 2 is what remains.
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users/delta?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users/delta?$skiptoken=B2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = null,
                OutputFile = Path.Combine(dir, "elsewhere.jsonl"),
                DataLength = 24,
            }.Save(checkpointPath);

            using var transport = MgxTransportScope.Inject(new ByUrlHandler());

            using var ps = PowerShell.Create();
            ps.AddCommand("Import-Module")
              .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Delta.SyncMgxDelta).Assembly);
            ps.Invoke();
            ps.Commands.Clear();
            ps.AddCommand("Sync-MgxDelta")
              .AddParameter("Uri", "/users/delta")
              .AddParameter("DeltaPath", deltaPath)
              .AddParameter("CheckpointPath", checkpointPath);
            var emitted = ps.Invoke()
                .Select(r => ((System.Collections.Hashtable)r.BaseObject)["id"]!.ToString()!)
                .ToArray();

            // Every change, not just the ones after the other sync's position.
            Assert.Equal(["b1", "b2", "b3"], emitted);
            // Only now may a token stand for this enumeration.
            var state = DeltaState.Load(deltaPath);
            Assert.NotNull(state);
            Assert.Contains("$deltatoken=D2", state!.DeltaLink);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The same checkpoint from a release that did not record the output file yet. A file-mode
    /// run names no temp while it is appending, and none once a cancellation has promoted the
    /// one it had, so all such a checkpoint says about where its items went is the length it
    /// counted. A pipeline sync adopted every one of those: it emitted the tail of the
    /// enumeration, saved the delta token over everything before it, and there is no re-fetch
    /// once the token has moved. A pipeline run measures no file, so a length is the file mode.
    /// </summary>
    [Fact]
    public void A_pipeline_sync_does_not_resume_from_a_checkpoint_that_records_only_a_length()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-legacy-{Guid.NewGuid():N}")).FullName;
        var deltaPath = Path.Combine(dir, "state.json");
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        try
        {
            // The other sync's position, in the shape 2.1.3 and before wrote it.
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users/delta?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users/delta?$skiptoken=B2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = null,
                OutputFile = null,
                DataLength = 24,
            }.Save(checkpointPath);

            using var transport = MgxTransportScope.Inject(new ByUrlHandler());

            using var ps = PowerShell.Create();
            ps.AddCommand("Import-Module")
              .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Delta.SyncMgxDelta).Assembly);
            ps.Invoke();
            ps.Commands.Clear();
            ps.AddCommand("Sync-MgxDelta")
              .AddParameter("Uri", "/users/delta")
              .AddParameter("DeltaPath", deltaPath)
              .AddParameter("CheckpointPath", checkpointPath);
            var emitted = ps.Invoke()
                .Select(r => ((System.Collections.Hashtable)r.BaseObject)["id"]!.ToString()!)
                .ToArray();

            // Every change, not just the ones after the other sync's position.
            Assert.Equal(["b1", "b2", "b3"], emitted);
            // Only now may a token stand for this enumeration.
            var state = DeltaState.Load(deltaPath);
            Assert.NotNull(state);
            Assert.Contains("$deltatoken=D2", state!.DeltaLink);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// How far "it is left as it is" has to reach. The refused checkpoint's items are in the
    /// temp it names, and the stale-temp sweep a few lines later deleted every temp beside the
    /// output - including that one. The sync it belongs to then came back to a position
    /// pointing at a file that is gone and re-enumerated from the delta token, which is the
    /// whole cost the refusal was written to avoid.
    /// </summary>
    [Fact]
    public void A_refused_checkpoints_temp_survives_the_run_that_refused_it()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-refused-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaA = Path.Combine(dir, "stateA.json");
        var deltaB = Path.Combine(dir, "stateB.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            // Sync A dies on page 2: its two items are in its temp, the output was never
            // promoted, and the checkpoint names both.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaA, checkpointPath, output);

            var cp = PaginationCheckpoint.Load(checkpointPath)!;
            Assert.NotNull(cp.TempFile);
            var temp = Path.Combine(dir, cp.TempFile!);
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}"], File.ReadAllLines(temp));
            var before = File.ReadAllBytes(checkpointPath);

            // Sync B: another collection, its own delta state, the same -OutputFile and
            // -CheckpointPath, failing before its first page boundary.
            Sync(deltaB, checkpointPath, output, "/groups/delta");

            Assert.Equal(before, File.ReadAllBytes(checkpointPath));
            Assert.True(File.Exists(temp),
                "the refused checkpoint's items were swept away by the run that refused it");

            // A, back where it left off, rather than at the delta token.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            Sync(deltaA, checkpointPath, output);
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                File.ReadAllLines(output));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The refusal's promise where the run that made it goes on to finish. A sync that refused
    /// the checkpoint it found and then completed without ever saving one of its own - one page
    /// was all it had - deleted the file it had warned a moment earlier it would leave alone,
    /// so the temp it had spared the sweep was orphaned and the sync that owns both came back
    /// to a position that was no longer there and re-enumerated from the beginning.
    /// </summary>
    [Fact]
    public void A_completed_sync_that_saved_no_checkpoint_leaves_the_refused_one_standing()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-donerefused-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaA = Path.Combine(dir, "stateA.json");
        var deltaB = Path.Combine(dir, "stateB.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            // Sync A dies on page 2: its two items are in its temp, and the checkpoint names it.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaA, checkpointPath, output);

            var cp = PaginationCheckpoint.Load(checkpointPath)!;
            var temp = Path.Combine(dir, cp.TempFile!);
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}"], File.ReadAllLines(temp));
            var before = File.ReadAllBytes(checkpointPath);

            // Sync B: another collection over the same -CheckpointPath and -OutputFile. It
            // refuses what it finds and then finishes in one page, so no boundary of its own
            // is ever reached and nothing of this run's is written over that path.
            handler.QueueResponse(HttpStatusCode.OK, GroupsPage);
            Sync(deltaB, checkpointPath, output, "/groups/delta");

            Assert.Equal(["{\"id\":\"g1\"}"], File.ReadAllLines(output));
            Assert.True(File.Exists(checkpointPath),
                "the run that refused the checkpoint deleted it on the way out");
            Assert.Equal(before, File.ReadAllBytes(checkpointPath));
            Assert.True(File.Exists(temp),
                "the refused checkpoint's items outlived the checkpoint that counted them");

            // A, back where it left off, rather than at page one.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            Sync(deltaA, checkpointPath, output);
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                File.ReadAllLines(output));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// And where the refusal has already been voided. This run reached a page boundary, which
    /// saved its own position over the same path - so the file the success delete reaches is
    /// this run's own, describing an enumeration that has just finished, and leaving it costs
    /// the next -Latest run the baseline it asks for. Taking the path over is what a second
    /// sync sharing it gets; the delete is not what makes that so.
    /// </summary>
    [Fact]
    public void A_completed_sync_that_wrote_over_the_refused_position_still_deletes_it()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-donetookover-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaA = Path.Combine(dir, "stateA.json");
        var deltaB = Path.Combine(dir, "stateB.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaA, checkpointPath, output);
            var theirs = PaginationCheckpoint.Load(checkpointPath)!.TempFile!;

            // Two pages, so the first boundary saves this run's position over the refused one.
            handler.QueueResponse(HttpStatusCode.OK, GroupsPageOne);
            handler.QueueResponse(HttpStatusCode.OK, GroupsResync);
            Sync(deltaB, checkpointPath, output, "/groups/delta");

            Assert.Equal(["{\"id\":\"g1\"}", "{\"id\":\"g2\"}", "{\"id\":\"g9\"}"],
                File.ReadAllLines(output));
            Assert.False(File.Exists(checkpointPath),
                "a completed sync left its own position into a finished enumeration behind");
            Assert.True(File.Exists(Path.Combine(dir, theirs)),
                "the refused checkpoint's staging file was swept by the run that refused it");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The refusal that is not an ownership verdict at all, and what a sync can do about it:
    /// nothing. A checkpoint refused because the temp it names could not be claimed counts rows
    /// that are in that temp and in no other file, and it is the only thing on disk that counts
    /// them. Every way of going on takes them: the sweep at the top of the attempt reclaims the
    /// temp the moment its holder is gone, the first page boundary saves this run's position
    /// over the checkpoint, the completion door then deletes what it finds there as its own, and
    /// a page carrying a deltaLink moves the token past changes held in that temp alone. So the
    /// run ends where it finds out, with one page's worth of work in front of it and none of it
    /// done.
    /// </summary>
    [Fact]
    public void A_held_temp_stops_the_sync_before_anything_is_written()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-heldtemp-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaPath = Path.Combine(dir, "state.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            // A sync dies on page 2: its two items are in its temp, and the checkpoint names
            // both files. No token was saved, so every run below builds the same request URL
            // and the checkpoint is this command line's own by every test there is.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaPath, checkpointPath, output);

            var temp = Path.Combine(dir, PaginationCheckpoint.Load(checkpointPath)!.TempFile!);
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}"], File.ReadAllLines(temp));

            // The next run finds that temp held open - by another process, not necessarily
            // another sync - with one page of changes waiting for it on the wire.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPageNoToken);
            string[] warnings;
            ErrorRecord[] errors;
            DiskState before, after;
            int requestsBefore;
            using (new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                before = ReadDisk(dir, checkpointPath, temp, output, deltaPath);
                requestsBefore = handler.RequestCount;
                (warnings, errors) = SyncRun(deltaPath, checkpointPath, output);
                after = ReadDisk(dir, checkpointPath, temp, output, deltaPath);
            }

            var stop = Assert.Single(errors);
            Assert.StartsWith("CheckpointTempHeld", stop.FullyQualifiedErrorId,
                StringComparison.Ordinal);
            Assert.Contains("Another sync is still writing the temp file",
                stop.Exception.Message);
            Assert.Contains("the 2 items it records are that run's", stop.Exception.Message);
            Assert.Contains("This run stops here; nothing was written.", stop.Exception.Message);
            Assert.Contains(
                "Wait for that sync to finish, or give this one its own -OutputFile and "
                + "-CheckpointPath.",
                stop.Exception.Message);

            // Nothing that promises to leave the files alone and then goes on to replace them.
            Assert.DoesNotContain(warnings, w => w.Contains("re-enumerates"));
            Assert.DoesNotContain(warnings, w => w.Contains("Both files are left as they are"));

            // And nothing ran: no request, no output, no token, and both files exactly as the
            // holder left them - down to the directory's own entry list, so no temp of this
            // run's and no probe survives it either.
            Assert.Equal(requestsBefore, handler.RequestCount);
            AssertUnmoved(before, after);
            Assert.False(File.Exists(output), "the stopped run wrote an output");
            Assert.Null(DeltaState.Load(deltaPath));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The same disk with two pages on the wire instead of one, which is the shape that made
    /// sparing the file pointless: a refusing run that reaches one page boundary saves its own
    /// position over the very path it has just promised to leave, and the door it walks out of
    /// deletes that as its own. A run that stops has no page boundary to reach.
    /// </summary>
    [Fact]
    public void A_held_temp_stops_the_sync_that_would_have_paged_twice()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-heldtemp2p-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaPath = Path.Combine(dir, "state.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaPath, checkpointPath, output);

            var temp = Path.Combine(dir, PaginationCheckpoint.Load(checkpointPath)!.TempFile!);
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}"], File.ReadAllLines(temp));

            // Two pages: a first with a continuation, so the run would save a boundary
            // checkpoint of its own, and a second that completes it.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPageNoToken);
            ErrorRecord[] errors;
            DiskState before, after;
            int requestsBefore;
            using (new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                before = ReadDisk(dir, checkpointPath, temp, output, deltaPath);
                requestsBefore = handler.RequestCount;
                (_, errors) = SyncRun(deltaPath, checkpointPath, output);
                after = ReadDisk(dir, checkpointPath, temp, output, deltaPath);
            }

            Assert.StartsWith("CheckpointTempHeld", Assert.Single(errors).FullyQualifiedErrorId,
                StringComparison.Ordinal);
            Assert.Equal(requestsBefore, handler.RequestCount);
            AssertUnmoved(before, after);
            Assert.Equal(2, PaginationCheckpoint.Load(checkpointPath)!.ItemsCollected);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The shape the delta token turns into a permanent loss. A page carrying a deltaLink is a
    /// completed enumeration, so a run that reaches one saves a token over the position the
    /// held checkpoint records - and every later run of the same command line then builds a
    /// request that checkpoint no longer matches and refuses it as another sync's, for good. A
    /// run that stops saves no token, because it fetches no page.
    /// </summary>
    [Fact]
    public void A_stopped_sync_saves_no_token_from_the_page_it_never_fetched()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-heldtemptoken-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaPath = Path.Combine(dir, "state.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaPath, checkpointPath, output);
            var temp = Path.Combine(dir, PaginationCheckpoint.Load(checkpointPath)!.TempFile!);

            // One page, and it finishes the enumeration: had the run taken it, the token would
            // have moved past the changes in the temp beside it.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            ErrorRecord[] errors;
            DiskState before, after;
            using (new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                before = ReadDisk(dir, checkpointPath, temp, output, deltaPath);
                (_, errors) = SyncRun(deltaPath, checkpointPath, output);
                after = ReadDisk(dir, checkpointPath, temp, output, deltaPath);
            }

            Assert.StartsWith("CheckpointTempHeld", Assert.Single(errors).FullyQualifiedErrorId,
                StringComparison.Ordinal);
            AssertUnmoved(before, after);
            Assert.False(File.Exists(deltaPath), "a token was saved for a page nothing fetched");

            // Which leaves the pair reachable. The holder dies, and the next run of the same
            // command line is the one it was kept for.
            var recovered = SyncWarnings(deltaPath, checkpointPath, output);

            Assert.Contains(recovered, w => w.Contains("Recovered 2 items"));
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                File.ReadAllLines(output));
            Assert.Contains("$deltatoken=D2", DeltaState.Load(deltaPath)!.DeltaLink);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The whole cost of getting that wrong, on one disk. Sync A holds its temp with two rows
    /// in it; sync B over the same command line stops; A then dies, leaving the two files B
    /// left alone. A's re-run is what recovers those rows - it promotes the temp over the
    /// output and appends the rest of the enumeration - and it can only do that while the
    /// checkpoint counting them is there.
    /// </summary>
    [Fact]
    public void The_rows_a_held_temp_holds_come_back_after_the_sync_that_stopped()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-heldtempback-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaPath = Path.Combine(dir, "state.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            // A collects page one into its temp and is still going.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaPath, checkpointPath, output);
            var temp = Path.Combine(dir, PaginationCheckpoint.Load(checkpointPath)!.TempFile!);
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}"], File.ReadAllLines(temp));

            // B stops. Nothing is queued for it, and it asks for nothing.
            ErrorRecord[] errors;
            using (new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                (_, errors) = SyncRun(deltaPath, checkpointPath, output);
                Assert.StartsWith("CheckpointTempHeld",
                    Assert.Single(errors).FullyQualifiedErrorId, StringComparison.Ordinal);
            }

            // A dies, leaving exactly the two files B left alone.
            Assert.True(File.Exists(checkpointPath), "B took A's position with it");
            Assert.True(File.Exists(temp), "B took A's temp with it");

            // A's re-run. It promotes the two rows and resumes from the page its own
            // checkpoint recorded, so the row below is appended to them rather than replacing
            // them - which is what tells a recovery from a fresh enumeration here.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            var warnings = SyncWarnings(deltaPath, checkpointPath, output);

            Assert.Contains(warnings, w => w.Contains("Recovered 2 items"));
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                File.ReadAllLines(output));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// What -Latest is told. A from-now baseline over these two files is a run like any other:
    /// the checkpoint is a position an enumeration is still in progress at, so the baseline is
    /// refused first, and then the temp it names stops the enumeration that was going to happen
    /// instead. Nothing is written either way, and the token the caller asked for is not saved
    /// over the changes the temp holds.
    /// </summary>
    [Fact]
    public void A_held_temp_stops_a_Latest_baseline_too()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-heldtemplatest-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaPath = Path.Combine(dir, "state.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaPath, checkpointPath, output);
            var temp = Path.Combine(dir, PaginationCheckpoint.Load(checkpointPath)!.TempFile!);

            handler.QueueResponse(HttpStatusCode.OK, LatestBaseline);
            string[] warnings;
            ErrorRecord[] errors;
            DiskState before, after;
            using (new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                before = ReadDisk(dir, checkpointPath, temp, output, deltaPath);
                (warnings, errors) = SyncRun(deltaPath, checkpointPath, output, latest: true);
                after = ReadDisk(dir, checkpointPath, temp, output, deltaPath);
            }

            var refused = Assert.Single(warnings, w => w.Contains("-Latest ignored"));
            Assert.Contains("a resume checkpoint exists", refused);
            Assert.StartsWith("CheckpointTempHeld", Assert.Single(errors).FullyQualifiedErrorId,
                StringComparison.Ordinal);
            AssertUnmoved(before, after);
            Assert.False(File.Exists(deltaPath), "a from-now token was saved over the temp's rows");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The other half of the same refusal, where there is no second sync at all: a temp this
    /// account cannot open read-write. The advice - grant write access, run again - is the only
    /// thing that recovers those rows, so the run giving it must still leave the position they
    /// are counted by. It stops rather than enumerate over it. This follows the advice and
    /// measures what comes back.
    /// </summary>
    [Fact]
    public void An_unopenable_temp_stops_the_sync_and_says_how_to_recover_it()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-unopenabletemp-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaPath = Path.Combine(dir, "state.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaPath, checkpointPath, output);
            var temp = Path.Combine(dir, PaginationCheckpoint.Load(checkpointPath)!.TempFile!);
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}"], File.ReadAllLines(temp));

            // 0444: the owner cannot open it read-write either, which is the claim failing on
            // permissions rather than on another handle.
            File.SetUnixFileMode(temp, UnixFileMode.UserRead);

            // Nothing queued: the run under test asks for nothing, and the page below belongs
            // to the run that follows its advice.
            var before = ReadDisk(dir, checkpointPath, temp, output, deltaPath);
            var requestsBefore = handler.RequestCount;
            var (_, errors) = SyncRun(deltaPath, checkpointPath, output);
            var after = ReadDisk(dir, checkpointPath, temp, output, deltaPath);

            var stop = Assert.Single(errors);
            Assert.StartsWith("CheckpointTempUnopenable", stop.FullyQualifiedErrorId,
                StringComparison.Ordinal);
            Assert.Contains("cannot be opened for writing by this account", stop.Exception.Message);
            Assert.Contains("not on sharing", stop.Exception.Message);
            Assert.Contains("This run stops here; nothing was written.", stop.Exception.Message);
            Assert.Contains(
                "Grant write access to that file and run again to recover them, or remove it "
                + "and the checkpoint to sync afresh.",
                stop.Exception.Message);
            Assert.Equal(requestsBefore, handler.RequestCount);
            AssertUnmoved(before, after);

            // Follow it.
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            var recovered = SyncWarnings(deltaPath, checkpointPath, output);

            Assert.Contains(recovered, w => w.Contains("Recovered 2 items"));
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
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
    /// And the 410 door, which used to have a reading of its own to make here. It no longer
    /// sees this case at all: a run that stops at the reconcile has not sent a request, so an
    /// expired token is not something it can have been told. The door's own sentence said the
    /// checkpoint was left alone and the same run then deleted it, which is the second way the
    /// spared pair went.
    /// </summary>
    [Fact]
    public void A_stopped_sync_never_reaches_the_410_door()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-heldtemp410-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaPath = Path.Combine(dir, "state.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            // No delta token is ever saved, so every run below builds the same request URL and
            // the checkpoint this one leaves is that command line's own by every test there is.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaPath, checkpointPath, output);
            var temp = Path.Combine(dir, PaginationCheckpoint.Load(checkpointPath)!.TempFile!);

            // An expired token and the full re-sync behind it, both waiting on the wire.
            handler.QueueResponse(HttpStatusCode.Gone, TokenExpired);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPageNoToken);
            string[] warnings;
            ErrorRecord[] errors;
            DiskState before, after;
            int requestsBefore;
            using (new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                before = ReadDisk(dir, checkpointPath, temp, output, deltaPath);
                requestsBefore = handler.RequestCount;
                (warnings, errors) = SyncRun(deltaPath, checkpointPath, output);
                after = ReadDisk(dir, checkpointPath, temp, output, deltaPath);
            }

            Assert.StartsWith("CheckpointTempHeld", Assert.Single(errors).FullyQualifiedErrorId,
                StringComparison.Ordinal);
            Assert.DoesNotContain(warnings, w => w.Contains("410 Gone"));
            Assert.Equal(requestsBefore, handler.RequestCount);
            AssertUnmoved(before, after);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// How far "spare the temps a refused checkpoint names" reaches. The sweep only ever
    /// deletes names a run gives its own temp - the output's name, a dot, 32 hex digits,
    /// ".tmp" - so a refused checkpoint naming "out.jsonl.{guid}.tmp" beside an output called
    /// "out" names a file this sweep could not touch either way. Skipping on it bought that
    /// file nothing and cost this output its own orphans, which the pre-length adoption path
    /// then picks up on a line count alone.
    /// </summary>
    [Fact]
    public void A_refused_temp_this_sweep_could_never_reach_does_not_spare_this_outputs_orphans()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-sweep-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaA = Path.Combine(dir, "stateA.json");
        var deltaB = Path.Combine(dir, "stateB.json");
        var mine = Path.Combine(dir, "out");
        var theirs = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            // The sync into "out" collects page one into its temp and dies on page two.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaA, checkpointPath, mine);
            var orphan = Assert.Single(Directory.GetFiles(dir, "out.*.tmp"));

            // The sync next door takes the shared checkpoint over: it refuses what it finds,
            // saves its own position at its first page boundary, and dies on page two. The
            // first sync's temp is now an orphan - nothing on disk describes it.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaB, checkpointPath, theirs);
            var theirTemp = Assert.Single(Directory.GetFiles(dir, "out.jsonl.*.tmp"));

            // "out" again. It refuses a checkpoint naming a temp beside a different output,
            // and that name must not stand in the way of its own sweep.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            Sync(deltaA, checkpointPath, mine);

            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                File.ReadAllLines(mine));
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
    /// What each refusal says. A checkpoint recording another sync's output is a second sync
    /// over one -CheckpointPath, and naming that is what the caller acts on. One recording no
    /// output at all is not: it was believed while the files beside it corroborated it, and a
    /// single sync whose temp has since gone, or whose output has been replaced, reaches the
    /// same refusal. Every release before this one recorded no output, so that is the ordinary
    /// upgrade path - and it was told there was a second sync somewhere and to give each its
    /// own -CheckpointPath, which fixes nothing it has.
    /// </summary>
    [Fact]
    public void A_refusal_reports_the_cause_it_can_show()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-refusal-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaPath = Path.Combine(dir, "state.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            File.WriteAllLines(output, OldSync);

            // The shape an interrupted fresh sync left before outputs were recorded, whose temp
            // is no longer beside it.
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users/delta?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users/delta?$skiptoken=B2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = $"out.jsonl.{Guid.NewGuid():N}.tmp",
                OutputFile = null,
                DataLength = 24,
            }.Save(checkpointPath);

            var uncorroborated = SyncWarnings(deltaPath, checkpointPath, output);
            Assert.Contains(uncorroborated, w => w.Contains("no longer corroborate"));
            Assert.DoesNotContain(uncorroborated,
                w => w.Contains("records an enumeration this run cannot resume from"));

            // The same position, recording an output this run is not collecting into.
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users/delta?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users/delta?$skiptoken=B2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = null,
                OutputFile = Path.Combine(dir, "elsewhere.jsonl"),
                DataLength = 24,
            }.Save(checkpointPath);

            Assert.Contains(SyncWarnings(deltaPath, checkpointPath, output),
                w => w.Contains("records an enumeration this run cannot resume from"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// And the cause it cannot show. A run interrupted partway through an incremental sync
    /// leaves a checkpoint recording the delta URL it was enumerating. Lose the state under it -
    /// corrupt, deleted by hand, a -DeltaPath moved - and the same command line comes back
    /// building a full-sync URL, which is not that one, so it refuses its own earlier position.
    /// The refusal is right; the reading was not. It named a different sync, over a
    /// -CheckpointPath no second sync has ever touched, and told the caller to give each of them
    /// its own - which is the arrangement they were already running.
    /// </summary>
    [Fact]
    public void A_refusal_does_not_name_a_second_sync_it_cannot_see()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-selfrefusal-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaPath = Path.Combine(dir, "state.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            // One sync, one -CheckpointPath: interrupted on page 2 of its incremental pass, so
            // what it leaves records the delta URL it was reading.
            UsersState(deltaPath);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaPath, checkpointPath, output);
            Assert.Contains("$deltatoken=D0", PaginationCheckpoint.Load(checkpointPath)!.Resource);

            // The state goes, and the same command line enumerates in full.
            File.Delete(deltaPath);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            var refusal = Assert.Single(
                SyncWarnings(deltaPath, checkpointPath, output),
                w => w.Contains(checkpointPath));

            Assert.Contains("records an enumeration this run cannot resume from", refusal);
            Assert.Contains("either another sync's, sharing this -CheckpointPath", refusal);
            Assert.Contains("this sync's own from a pass it no longer makes", refusal);
            Assert.DoesNotContain("belongs to a different sync", refusal);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A 410 says the token this run holds is dead. It says nothing about a checkpoint this
    /// same run has already decided belongs to another sync, and deleting that one took the
    /// other sync's position away seconds after warning that it would be left alone - while the
    /// retry, running the stale-temp sweep a second time with the refusal forgotten, took the
    /// temp holding its items too. Both halves of the other sync's progress, on the one path
    /// that promised to touch neither.
    /// </summary>
    [Fact]
    public void An_expired_token_does_not_delete_the_checkpoint_this_run_refused()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-410refused-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaA = Path.Combine(dir, "stateA.json");
        var deltaB = Path.Combine(dir, "stateB.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            // Sync A dies on page 2: its two items are in its temp, and the checkpoint names
            // both files.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaA, checkpointPath, output);

            var cp = PaginationCheckpoint.Load(checkpointPath)!;
            var temp = Path.Combine(dir, cp.TempFile!);
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}"], File.ReadAllLines(temp));
            var before = File.ReadAllBytes(checkpointPath);

            // Sync B: another collection over the same -CheckpointPath and -OutputFile, holding
            // a token Graph answers 410 for. It refuses A's checkpoint, re-syncs in full, and
            // dies before a page boundary of its own.
            StaleState(deltaB);
            handler.QueueResponse(HttpStatusCode.Gone, TokenExpired);
            Sync(deltaB, checkpointPath, output, "/groups/delta");

            Assert.True(File.Exists(checkpointPath),
                "the expired token deleted a checkpoint this run had refused as another sync's");
            Assert.Equal(before, File.ReadAllBytes(checkpointPath));
            Assert.True(File.Exists(temp),
                "the retry swept away the temp the refusal a moment earlier had spared");

            // A, back where it left off, rather than at the delta token.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            Sync(deltaA, checkpointPath, output);
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                File.ReadAllLines(output));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// And the run that refused still gets its own sync done: the 410 retry enumerates in full,
    /// collects into the output and saves the token it was issued, with the other sync's temp
    /// still where the refusal left it.
    /// </summary>
    [Fact]
    public void A_full_resync_after_a_refusal_still_completes_its_own_sync()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-410done-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaA = Path.Combine(dir, "stateA.json");
        var deltaB = Path.Combine(dir, "stateB.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaA, checkpointPath, output);
            var temp = Path.Combine(dir, PaginationCheckpoint.Load(checkpointPath)!.TempFile!);

            StaleState(deltaB);
            handler.QueueResponse(HttpStatusCode.Gone, TokenExpired);
            handler.QueueResponse(HttpStatusCode.OK, GroupsPage);
            Sync(deltaB, checkpointPath, output, "/groups/delta");

            Assert.Equal(["{\"id\":\"g1\"}"], File.ReadAllLines(output));
            Assert.Contains("$deltatoken=G1", DeltaState.Load(deltaB)!.DeltaLink);
            Assert.True(File.Exists(temp),
                "the retry swept away the temp the refusal a moment earlier had spared");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// What the run that refused leaves beside its own output. The attempt a 410 sends round the
    /// loop again keeps its temp - a checkpoint counting those items is on disk - and the retry
    /// spares it, because the sweep is held off over every temp beside this output for as long
    /// as the refusal stands. What ends the holding off was never written: the checkpoint is
    /// replaced by the retry's, or deleted by the run that completed, without a word about the
    /// file it used to name. So a sync that promoted its output and reported success left a
    /// partial copy of the changes it had already collected beside the finished file, for some
    /// later sync's sweep to reclaim.
    /// </summary>
    [Fact]
    public void A_full_resync_after_a_refusal_leaves_no_temp_of_its_own()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-410kept-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaA = Path.Combine(dir, "stateA.json");
        var deltaB = Path.Combine(dir, "stateB.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            // Sync A dies on page 2: the checkpoint names the temp holding its two items.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaA, checkpointPath, output);
            var theirs = PaginationCheckpoint.Load(checkpointPath)!.TempFile!;

            // Sync B refuses that checkpoint, collects a page of its own into a temp, and loses
            // its token to a 410 on the request after it. The retry re-syncs in full.
            StaleState(deltaB);
            handler.QueueResponse(HttpStatusCode.OK, GroupsPageOne);
            handler.QueueResponse(HttpStatusCode.Gone, TokenExpired);
            handler.QueueResponse(HttpStatusCode.OK, GroupsResync);
            Sync(deltaB, checkpointPath, output, "/groups/delta");

            Assert.Equal(["{\"id\":\"g9\"}"], File.ReadAllLines(output));
            Assert.Contains("$deltatoken=G1", DeltaState.Load(deltaB)!.DeltaLink);

            // The one temp beside the output is the refused checkpoint's, which this run had no
            // business touching either way.
            var left = Assert.Single(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
            Assert.Equal(theirs, Path.GetFileName(left));
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}"], File.ReadAllLines(left));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The same compound with the 410 on the first request of all, so the attempt that dies has
    /// saved no checkpoint and its temp is empty. The keep asked only whether a checkpoint was
    /// on disk, and the one there is the refused sync's: it counts nothing this run wrote and
    /// names a temp of its own, so an empty file was kept on a foreign file's account and left
    /// beside the output the retry went on to finish.
    /// </summary>
    [Fact]
    public void An_attempt_that_saved_no_checkpoint_keeps_no_temp_on_a_refused_ones_account()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-410empty-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaA = Path.Combine(dir, "stateA.json");
        var deltaB = Path.Combine(dir, "stateB.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaA, checkpointPath, output);
            var theirs = PaginationCheckpoint.Load(checkpointPath)!.TempFile!;

            StaleState(deltaB);
            handler.QueueResponse(HttpStatusCode.Gone, TokenExpired);
            handler.QueueResponse(HttpStatusCode.OK, GroupsResync);
            Sync(deltaB, checkpointPath, output, "/groups/delta");

            Assert.Equal(["{\"id\":\"g9\"}"], File.ReadAllLines(output));
            var left = Assert.Single(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
            Assert.Equal(theirs, Path.GetFileName(left));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// And the temp goes at the moment it stops being anything, not at the end of the run. The
    /// retry saves its own first page-boundary checkpoint, which names its own temp - so the
    /// one the attempt before it left is from then on counted by nothing - and then dies. What
    /// is beside the output is the temp the checkpoint names, and the refused sync's.
    /// </summary>
    [Fact]
    public void A_retry_deletes_the_temp_its_own_checkpoint_has_stopped_naming()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-410release-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaA = Path.Combine(dir, "stateA.json");
        var deltaB = Path.Combine(dir, "stateB.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaA, checkpointPath, output);
            var theirs = PaginationCheckpoint.Load(checkpointPath)!.TempFile!;

            // The retry reaches a page boundary of its own and dies on the page after it.
            StaleState(deltaB);
            handler.QueueResponse(HttpStatusCode.OK, GroupsPageOne);
            handler.QueueResponse(HttpStatusCode.Gone, TokenExpired);
            handler.QueueResponse(HttpStatusCode.OK, GroupsPageOne);
            Sync(deltaB, checkpointPath, output, "/groups/delta");

            var cp = PaginationCheckpoint.Load(checkpointPath);
            Assert.NotNull(cp);
            Assert.NotNull(cp!.TempFile);
            Assert.NotEqual(theirs, cp.TempFile);

            var temps = Directory.GetFiles(dir, "out.jsonl.*.tmp")
                .Select(Path.GetFileName).Order().ToArray();
            Assert.Equal(new[] { theirs, cp.TempFile }.Order().ToArray(), temps);
            Assert.Equal(["{\"id\":\"g1\"}", "{\"id\":\"g2\"}"],
                File.ReadAllLines(Path.Combine(dir, cp.TempFile!)));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The other end of it: the retry dies before a page boundary of its own. The temp the
    /// attempt before it filled is what an interrupted run leaves and stays where it is, while
    /// the retry's own, which nothing counts, is the one that goes. The position that counted
    /// the first goes with the token it belongs to - the 410 door deletes it, because the
    /// attempt that took the path over wrote it, and it enumerates what Graph has just declared
    /// gone.
    /// </summary>
    [Fact]
    public void A_retry_that_dies_leaves_the_temp_its_own_attempt_filled()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-410dead-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaA = Path.Combine(dir, "stateA.json");
        var deltaB = Path.Combine(dir, "stateB.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaA, checkpointPath, output);
            var theirs = PaginationCheckpoint.Load(checkpointPath)!.TempFile!;

            StaleState(deltaB);
            handler.QueueResponse(HttpStatusCode.OK, GroupsPageOne);
            handler.QueueResponse(HttpStatusCode.Gone, TokenExpired);
            Sync(deltaB, checkpointPath, output, "/groups/delta");

            Assert.False(File.Exists(checkpointPath),
                "the expired token spared a position of this run's own into the dead enumeration");

            var temps = Directory.GetFiles(dir, "out.jsonl.*.tmp")
                .Select(Path.GetFileName).Order().ToArray();
            Assert.Equal(2, temps.Length);
            var mine = temps.Single(t => t != theirs);
            Assert.Equal(["{\"id\":\"g1\"}", "{\"id\":\"g2\"}"],
                File.ReadAllLines(Path.Combine(dir, mine!)));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// And what the run after it finds. The refusal is decided against the file that was on
    /// disk when the run started, and the very next page boundary saves this run's own position
    /// over that path - so by the time the 410 door reads the refusal, the file it spares is
    /// this run's, recording the enumeration Graph has just declared gone. The retry then dies
    /// before saving a position of its own, and the door has already deleted the delta state,
    /// so the next invocation of the same command line started with nothing to resume from and
    /// found that checkpoint waiting. It refused it - correctly; it enumerates something this
    /// command line can no longer build - and warned about a sync that was never there, while
    /// holding off its stale-temp sweep for a second run over files nothing counted any more.
    /// </summary>
    [Fact]
    public void A_refused_position_this_run_wrote_over_goes_with_the_expired_token()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-410tookover-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaA = Path.Combine(dir, "stateA.json");
        var deltaB = Path.Combine(dir, "stateB.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            // Sync A dies on page 2: the checkpoint names the temp holding its two items.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaA, checkpointPath, output);
            var theirs = PaginationCheckpoint.Load(checkpointPath)!.TempFile!;

            // Sync B refuses that checkpoint, reaches a page boundary of its own - which saves
            // over the same path - loses its token to a 410, and dies again in the re-sync
            // before it can record a position of the enumeration it is now making.
            StaleState(deltaB);
            handler.QueueResponse(HttpStatusCode.OK, GroupsPageOne);
            handler.QueueResponse(HttpStatusCode.Gone, TokenExpired);
            Sync(deltaB, checkpointPath, output, "/groups/delta");

            Assert.False(File.Exists(checkpointPath),
                "the 410 door spared a position this run had written over the refused one");
            Assert.True(File.Exists(Path.Combine(dir, theirs)),
                "the retry swept away the temp the refusal a moment earlier had spared");
            Assert.False(File.Exists(deltaB), "the expired delta state outlived the 410");

            // The next invocation of B's command line, with nothing of its own left to resume
            // from: no checkpoint to refuse, so no warning about a sync that was never there,
            // and the sweep it no longer holds off reclaims what the interrupted run left.
            handler.QueueResponse(HttpStatusCode.OK, GroupsResync);
            var warnings = SyncWarnings(deltaB, checkpointPath, output, "/groups/delta");

            Assert.DoesNotContain(warnings, w => w.Contains("resume checkpoint"));
            Assert.Equal(["{\"id\":\"g9\"}"], File.ReadAllLines(output));
            Assert.Contains("$deltatoken=G1", DeltaState.Load(deltaB)!.DeltaLink);
            Assert.Empty(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The other branch of the same door, which is the refusal's promise where it still means
    /// something: the 410 arrives on the first request of all, so no attempt of this run has
    /// saved anything over the path and what is there is still the position that was refused.
    /// It is left exactly as it was found, byte for byte, and it is still the other sync's
    /// enumeration rather than this one's.
    /// </summary>
    [Fact]
    public void An_expired_token_spares_the_refused_position_no_attempt_wrote_over()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-410intact-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaA = Path.Combine(dir, "stateA.json");
        var deltaB = Path.Combine(dir, "stateB.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaA, checkpointPath, output);
            var theirs = PaginationCheckpoint.Load(checkpointPath)!.TempFile!;
            var before = File.ReadAllBytes(checkpointPath);

            // The same compound as above, differing only in reaching no page boundary before
            // the 410 - so the door decides over a path this run has not written to.
            StaleState(deltaB);
            handler.QueueResponse(HttpStatusCode.Gone, TokenExpired);
            Sync(deltaB, checkpointPath, output, "/groups/delta");

            Assert.Equal(before, File.ReadAllBytes(checkpointPath));
            var cp = PaginationCheckpoint.Load(checkpointPath);
            Assert.NotNull(cp);
            Assert.Contains("/users/delta", cp!.Resource);
            Assert.Equal(theirs, cp.TempFile);
            Assert.True(File.Exists(Path.Combine(dir, theirs)),
                "the retry swept away the temp the refusal a moment earlier had spared");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A state file that cannot be read is a door out of the delta position like -FullSync and
    /// the query-shape changes, and every one of those takes the resume checkpoint with the
    /// position it discards. This one did not, and what it left describes the delta-link
    /// spelling the fresh sync no longer builds - so the run refused its own earlier position,
    /// warned about a second sync that was never there, and held its stale-temp sweep off over
    /// a temp nothing counted any more.
    /// </summary>
    [Fact]
    public void A_corrupt_delta_state_takes_the_checkpoint_of_the_position_it_abandons()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-corrupt-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaPath = Path.Combine(dir, "state.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            // A sync resuming from a token, interrupted on page two: the checkpoint records the
            // delta link it was enumerating, and its temp holds the two items it collected.
            UsersState(deltaPath);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaPath, checkpointPath, output);
            var temp = Path.Combine(dir, PaginationCheckpoint.Load(checkpointPath)!.TempFile!);
            Assert.True(File.Exists(temp));

            // And the state file is unreadable by the time the next run starts.
            File.WriteAllText(deltaPath, "{ this is not a delta state");

            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            var warnings = SyncWarnings(deltaPath, checkpointPath, output);

            Assert.Contains(warnings, w => w.Contains("is corrupt. Starting full sync."));
            Assert.DoesNotContain(warnings,
                w => w.Contains("records an enumeration this run cannot resume from"));
            Assert.False(File.Exists(checkpointPath),
                "the abandoned position outlived the state it described");
            Assert.Empty(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
            Assert.Equal(["{\"id\":\"b3\"}"], File.ReadAllLines(output));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// And what the caller is told on the way past. -Latest is suppressed by a resume checkpoint
    /// merely existing, and the sentence saying so tells the caller to delete that file - which
    /// this door has just done. The advice has to describe the disk as it is.
    /// </summary>
    [Fact]
    public void A_corrupt_state_does_not_send_the_caller_after_a_checkpoint_it_deleted()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-corruptlatest-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaPath = Path.Combine(dir, "state.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            UsersState(deltaPath);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaPath, checkpointPath, output);
            Assert.True(File.Exists(checkpointPath));

            File.WriteAllText(deltaPath, "{ this is not a delta state");

            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            var warnings = SyncWarnings(deltaPath, checkpointPath, output, latest: true);

            Assert.Contains(warnings, w => w.Contains("the previous delta state was discarded"));
            Assert.DoesNotContain(warnings, w => w.Contains("a resume checkpoint exists"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The same sentence, at the three doors a changed query shape opens. A $select, a Prefer
    /// header or a filter that differs from the state's discards that state and the resume
    /// checkpoint counted into it - and the -Latest guard downstream still read a checkpoint as
    /// standing, so the advice it wrote told the caller to go and delete a file the door had
    /// just deleted, and said nothing about the state that had actually been discarded.
    /// </summary>
    [Theory]
    [InlineData("select")]
    [InlineData("prefer")]
    [InlineData("filter")]
    public void A_query_shape_door_does_not_send_the_caller_after_a_checkpoint_it_deleted(string door)
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-shapelatest-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaPath = Path.Combine(dir, "state.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            // A sync resuming from a token, interrupted on page two: the checkpoint it leaves
            // records the delta link it was enumerating.
            UsersState(deltaPath);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaPath, checkpointPath, output);
            Assert.True(File.Exists(checkpointPath));

            // The next run asks for a different shape of the same query, with -Latest on.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            var warnings = SyncWarnings(deltaPath, checkpointPath, output, latest: true,
                property: door == "select" ? ["id", "displayName"] : null,
                prefer: door == "prefer" ? ["return=minimal"] : null,
                filter: door == "filter" ? "startswith(displayName,'a')" : null);

            Assert.Contains(warnings, w => w.Contains(door switch
            {
                "select" => "Property selection changed since last sync",
                "prefer" => "Prefer headers changed since last sync",
                _ => "Filter changed since last sync",
            }));
            Assert.Contains(warnings, w => w.Contains("the previous delta state was discarded"));
            Assert.DoesNotContain(warnings, w => w.Contains("a resume checkpoint exists"));
            Assert.False(File.Exists(checkpointPath),
                "the position counted into the discarded state outlived it");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// Answers by URL rather than in order, so what the sync asks for first is what decides
    /// which items it receives.
    /// </summary>

    /// <summary>
    /// The disk a resumed sync leaves at a page boundary. One run dies with its changes in a
    /// temp; the next promotes that temp over the output, appends the page it resumed onto,
    /// saves its position against the output itself - there is no temp left to name - and dies
    /// too. That last position is the one shape a shared -CheckpointPath offers no temp to
    /// refuse a second run by. No page carried a deltaLink, so the token is still the one the
    /// state file held when this started.
    /// </summary>
    private static void ResumedAndAppending(MockHttpHandler handler, string deltaPath,
        string checkpointPath, string output)
    {
        UsersState(deltaPath);
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
        Sync(deltaPath, checkpointPath, output);
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
        Sync(deltaPath, checkpointPath, output);
    }

    /// <summary>
    /// The refusal one file over from the temp claim, and the one it could not reach. A sync that
    /// has resumed once writes straight into -OutputFile and saves a position naming no temp at
    /// all, so a second run over the same command line found nothing in the checkpoint to refuse
    /// it by: it judged the file its own, cut it back to the recorded offset under a live writer,
    /// appended, and moved the delta token past changes the output no longer held. The holder's
    /// next write landed past the hole the cut left. There is no pass this run can make that
    /// leaves any of that standing, so it makes none.
    /// </summary>
    [Fact]
    public void A_held_output_stops_the_sync_before_anything_is_written()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-heldout-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaPath = Path.Combine(dir, "state.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            ResumedAndAppending(handler, deltaPath, checkpointPath, output);

            // The premise, off the file: this position counts its changes into the output and
            // names no temp, which is the shape the claim on the temp has nothing to say about.
            var cp = PaginationCheckpoint.Load(checkpointPath)!;
            Assert.Null(cp.TempFile);
            Assert.Equal(Path.GetFullPath(output), cp.OutputFile);
            Assert.Equal(new FileInfo(output).Length, cp.DataLength);
            Assert.Contains("$deltatoken=D0", DeltaState.Load(deltaPath)!.DeltaLink);

            // Sync A is not dead. It holds the output the way a resumed run does - appending,
            // sharing reads - and has written one more change since its last save, so the file
            // is longer than the checkpoint records and cutting it back leaves a hole under it.
            // And the page waiting on the wire carries a deltaLink, so going on would move the
            // token as well.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            DiskState before, after;
            int requestsBefore;
            string[] warnings;
            ErrorRecord[] errors;
            using (var live = new StreamWriter(
                       new FileStream(output, FileMode.Append, FileAccess.Write, FileShare.Read)))
            {
                live.WriteLine("{\"id\":\"b9-live\"}");
                live.Flush();
                Assert.True(new FileInfo(output).Length > cp.DataLength,
                    "the holder wrote nothing past the recorded length, so no cut would show");

                before = ReadDisk(dir, checkpointPath, output, deltaPath);
                requestsBefore = handler.RequestCount;
                (warnings, errors) = SyncRun(deltaPath, checkpointPath, output);

                var stop = Assert.Single(errors);
                Assert.StartsWith("CheckpointOutputHeld", stop.FullyQualifiedErrorId,
                    StringComparison.Ordinal);
                Assert.Equal(ErrorCategory.ResourceBusy, stop.CategoryInfo.Category);
                Assert.Contains($"Another sync is still writing '{output}'",
                    stop.Exception.Message);
                Assert.Contains(checkpointPath, stop.Exception.Message);
                Assert.Contains($"records {cp.ItemsCollected} items into", stop.Exception.Message);
                Assert.Contains("would cut that file back under it", stop.Exception.Message);
                Assert.Contains("advance the delta token past them", stop.Exception.Message);
                Assert.Contains("This run stops here; nothing was written.", stop.Exception.Message);
                Assert.Contains(
                    "Wait for that sync to finish, or give this one its own -OutputFile and "
                    + "-CheckpointPath.",
                    stop.Exception.Message);

                // And not the sentence for a file whose contents this run never looked at.
                Assert.DoesNotContain(warnings, w => w.Contains("no longer holds"));
                Assert.DoesNotContain(warnings, w => w.Contains("Re-enumerating"));

                // Nothing ran: no request, and the output, the position, the delta state and the
                // directory's entry list exactly as the holder left them. Read after the record
                // and not before it, so a run that took the checkpoint with it fails on the
                // answer it did not give.
                after = ReadDisk(dir, checkpointPath, output, deltaPath);
                Assert.Equal(requestsBefore, handler.RequestCount);
                AssertUnmoved(before, after);
                Assert.Contains("$deltatoken=D0", DeltaState.Load(deltaPath)!.DeltaLink);

                // A goes on, and the change it writes next lands where its last one ended.
                live.WriteLine("{\"id\":\"b10-live\"}");
                live.Flush();
            }

            // Ordinal, both of them: the culture-aware comparisons treat a NUL as ignorable,
            // which is the one byte this is looking for.
            var published = File.ReadAllText(output);
            Assert.False(published.Contains('\0'),
                "the cut left a hole for the holder's next write to land past");
            Assert.EndsWith("{\"id\":\"b9-live\"}" + Environment.NewLine
                + "{\"id\":\"b10-live\"}" + Environment.NewLine, published,
                StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The other half of the same claim, where there is no second sync at all: the output the
    /// checkpoint counts its changes into cannot be opened for writing by this account. It was
    /// reported as a file that no longer holds those changes - a reading of the file's contents,
    /// given about a file this run had not been able to open - and the position counting them was
    /// deleted on the strength of it, with the token then advancing past them. The advice is the
    /// only thing that recovers them, so the run giving it leaves all of that standing. This
    /// follows the advice and measures what comes back.
    /// </summary>
    [Fact]
    public void An_unopenable_output_stops_the_sync_and_says_how_to_recover_it()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-unopenout-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaPath = Path.Combine(dir, "state.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            ResumedAndAppending(handler, deltaPath, checkpointPath, output);
            var cp = PaginationCheckpoint.Load(checkpointPath)!;
            Assert.Null(cp.TempFile);
            var recorded = cp.DataLength!.Value;

            // The changes the checkpoint counts are there, and one written after its last save
            // with them - so a run that can open the file cuts back to the recorded length.
            File.AppendAllText(output, "{\"id\":\"b9-past-the-save\"}" + Environment.NewLine);
            Assert.True(new FileInfo(output).Length > recorded);

            // 0444: the owner cannot open it read-write either, which is the claim failing on
            // permissions rather than on another handle.
            File.SetUnixFileMode(output, UnixFileMode.UserRead);

            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            var before = ReadDisk(dir, checkpointPath, output, deltaPath);
            var requestsBefore = handler.RequestCount;
            var (warnings, errors) = SyncRun(deltaPath, checkpointPath, output);

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
            Assert.Contains($"The resume checkpoint at '{checkpointPath}' records",
                stop.Exception.Message);
            Assert.Contains("advance the delta token past them", stop.Exception.Message);
            Assert.Contains("This run stops here; nothing was written.", stop.Exception.Message);
            Assert.Contains(
                "Grant write access to that file and run again to resume from it, or remove it "
                + "and the checkpoint to sync afresh.",
                stop.Exception.Message);

            // Not said about a file whose contents this run could not read, and not acted on.
            // The disk is read after the record and not before it, so a run that deleted the
            // position fails on the answer it did not give.
            Assert.DoesNotContain(warnings, w => w.Contains("no longer holds"));
            Assert.Equal(requestsBefore, handler.RequestCount);
            AssertUnmoved(before, ReadDisk(dir, checkpointPath, output, deltaPath));
            Assert.True(File.Exists(checkpointPath),
                "the position counting the changes the advice recovers was deleted");
            Assert.Contains("$deltatoken=D0", DeltaState.Load(deltaPath)!.DeltaLink);

            // Follow it. The run that comes back cuts the output to the recorded length and
            // appends the page it resumes onto, so the change written past the last save goes,
            // nothing is written twice, and the token moves only now.
            File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            var resumed = SyncWarnings(deltaPath, checkpointPath, output);

            Assert.DoesNotContain(resumed, w => w.Contains("no longer holds"));
            Assert.Equal(
                ["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b1\"}", "{\"id\":\"b2\"}",
                 "{\"id\":\"b3\"}"],
                File.ReadAllLines(output));
            Assert.Contains("$deltatoken=D2", DeltaState.Load(deltaPath)!.DeltaLink);
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
    /// records is not the file that checkpoint describes - the changes it counts are in no file
    /// at all - and nothing holds it and nothing refuses to open it. That still takes today's
    /// route: say so, delete the position, and re-enumerate from the last saved token.
    /// </summary>
    [Fact]
    public void A_stale_length_output_still_reenumerates_from_the_token()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-staleout-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaPath = Path.Combine(dir, "state.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            ResumedAndAppending(handler, deltaPath, checkpointPath, output);
            var cp = PaginationCheckpoint.Load(checkpointPath)!;
            Assert.Null(cp.TempFile);

            // The output is replaced by one that does not hold those changes.
            File.WriteAllText(output, "{\"id\":\"short\"}" + Environment.NewLine);
            Assert.True(new FileInfo(output).Length < cp.DataLength);

            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            var (warnings, errors) = SyncRun(deltaPath, checkpointPath, output);

            Assert.Empty(errors);
            Assert.Contains(warnings,
                w => w.Contains($"'{output}' no longer holds the {cp.ItemsCollected} items"));
            Assert.False(File.Exists(checkpointPath), "the stale position was left on disk");
            Assert.Equal(["{\"id\":\"b3\"}"], File.ReadAllLines(output));
            Assert.Contains("$deltatoken=D2", DeltaState.Load(deltaPath)!.DeltaLink);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private sealed class ByUrlHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.ToString().Contains("skiptoken=B2", StringComparison.Ordinal)
                ? ChangesPage2
                : ChangesPage1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>
    /// The disk a resumed sync leaves at a page boundary, with every change in the output
    /// exactly once. One run dies with its changes in a temp; the next promotes that temp over
    /// the output, saves its position against the output itself - there is no temp left to name
    /// - and dies at its first request. No page carried a deltaLink, so the token is still the
    /// one the state file held when this started.
    /// </summary>
    private static void PromotedAndAppending(MockHttpHandler handler, string deltaPath,
        string checkpointPath, string output)
    {
        UsersState(deltaPath);
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
        Sync(deltaPath, checkpointPath, output);
        Sync(deltaPath, checkpointPath, output);
    }

    /// <summary>
    /// Two syncs over one command line, both past the stat the reconcile makes. The claim on the
    /// output was asked for and let go inside the reconcile and the writer opened its own handle
    /// a page fetch later, so for that window neither run held the file: both passed the claim,
    /// both cut it back to the recorded offset, both appended, and the token moved past changes
    /// the file no longer held. The claim is the handle now, so the run that takes it keeps it
    /// across that window, and the second run meets a file the first one has.
    ///
    /// A is parked in the window itself. The verbose announcing the resume is written after the
    /// reconcile - the claim taken, the change past the last save cut away - and before the
    /// writer is opened, which is exactly where the two runs used to overlap.
    /// </summary>
    [Fact]
    public void Two_syncs_released_together_leave_one_with_the_output_and_the_token()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-interleave-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaPath = Path.Combine(dir, "state.json");
        var output = Path.Combine(dir, "out.jsonl");
        // A real second thread, not a scheduled task: the two runs have to be able to be inside
        // the cmdlet at the same time, and joining one is not a blocking wait on work the test
        // framework might have scheduled behind it.
        (string[] Warnings, ErrorRecord[] Errors) aResult = default;
        Exception? aFailed = null;
        Thread? a = null;
        using var arrived = new ManualResetEventSlim(false);
        using var released = new ManualResetEventSlim(false);

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            PromotedAndAppending(handler, deltaPath, checkpointPath, output);

            var cp = PaginationCheckpoint.Load(checkpointPath)!;
            Assert.Null(cp.TempFile);
            Assert.Equal(Path.GetFullPath(output), cp.OutputFile);
            var recorded = cp.DataLength!.Value;
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}"], File.ReadAllLines(output));
            Assert.Equal(new FileInfo(output).Length, recorded);
            Assert.Contains("$deltatoken=D0", DeltaState.Load(deltaPath)!.DeltaLink);

            // A change written after the last save, so the cut A makes is visible from outside.
            File.AppendAllText(output, "{\"id\":\"b9-past-the-save\"}" + Environment.NewLine);
            Assert.True(new FileInfo(output).Length > recorded);

            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            var requestsBefore = handler.RequestCount;

            a = new Thread(() =>
            {
                try
                {
                    aResult = SyncParkedAtResume(deltaPath, checkpointPath, output,
                        arrived, released);
                }
                catch (Exception ex) { aFailed = ex; }
            });
            a.Start();
            Assert.True(arrived.Wait(TimeSpan.FromSeconds(60)),
                "sync A never reached the window between its reconcile and its writer");

            // Past the reconcile: the change written after the last save has been cut away. And
            // short of the writer: no page has been asked for, so nothing has been appended.
            Assert.Equal(recorded, new FileInfo(output).Length);
            Assert.Equal(requestsBefore, handler.RequestCount);

            var (warnings, errors) = SyncRun(deltaPath, checkpointPath, output);

            var stop = Assert.Single(errors);
            Assert.StartsWith("CheckpointOutputHeld", stop.FullyQualifiedErrorId,
                StringComparison.Ordinal);
            Assert.Equal(ErrorCategory.ResourceBusy, stop.CategoryInfo.Category);
            Assert.Contains($"Another sync is still writing '{output}'", stop.Exception.Message);
            Assert.Contains($"records {cp.ItemsCollected} items into", stop.Exception.Message);
            Assert.Contains("advance the delta token past them", stop.Exception.Message);
            Assert.Contains("This run stops here; nothing was written.", stop.Exception.Message);
            Assert.DoesNotContain(warnings, w => w.Contains("no longer holds"));

            // B sent no request and took nothing with it: the position A resumes from is still
            // on disk, the file is what A cut it back to, and the token has not moved.
            Assert.Equal(requestsBefore, handler.RequestCount);
            Assert.True(File.Exists(checkpointPath), "B deleted the position A is resuming from");
            Assert.Equal(recorded, new FileInfo(output).Length);
            Assert.Contains("$deltatoken=D0", DeltaState.Load(deltaPath)!.DeltaLink);

            released.Set();
            Assert.True(a.Join(TimeSpan.FromSeconds(60)), "sync A never finished");
            Assert.Null(aFailed);
            Assert.Empty(aResult.Errors);

            // One run had the file. Every change once, in order, the position spent, and the
            // token advanced exactly once - to the link the page A fetched carried.
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                File.ReadAllLines(output));
            Assert.False(File.Exists(checkpointPath),
                "the completed sync left its position behind");
            var state = DeltaState.Load(deltaPath)!;
            Assert.Contains("$deltatoken=D2", state.DeltaLink);
            Assert.Equal(3, state.ItemCount);
        }
        finally
        {
            released.Set();
            a?.Join(TimeSpan.FromSeconds(30));
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A directory standing where the output should be. It is not a file, so File.Exists said no
    /// and the run took the route it takes for an output that is not there at all - warning that
    /// the file no longer held the changes, deleting the position, and re-enumerating from a
    /// token that is about to advance past them. Opening a directory read-write is refused on
    /// both platforms, and that is an answer. The write probe above this passes: it writes
    /// beside the path, not to it.
    /// </summary>
    [Fact]
    public void A_directory_standing_at_the_output_path_stops_the_sync()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-outdir-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaPath = Path.Combine(dir, "state.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            PromotedAndAppending(handler, deltaPath, checkpointPath, output);
            var cp = PaginationCheckpoint.Load(checkpointPath)!;
            Assert.Null(cp.TempFile);
            var position = File.ReadAllBytes(checkpointPath);
            var state = File.ReadAllBytes(deltaPath);

            File.Delete(output);
            Directory.CreateDirectory(output);

            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            var requestsBefore = handler.RequestCount;
            var (warnings, errors) = SyncRun(deltaPath, checkpointPath, output);

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
                + "remove the checkpoint to sync afresh.",
                stop.Exception.Message);
            Assert.DoesNotContain("Grant write access", stop.Exception.Message);

            Assert.DoesNotContain(warnings, w => w.Contains("no longer holds"));
            Assert.DoesNotContain(warnings, w => w.Contains("Re-enumerating"));
            Assert.Equal(requestsBefore, handler.RequestCount);
            Assert.True(File.Exists(checkpointPath), "the position was deleted over a directory");
            Assert.Equal(position, File.ReadAllBytes(checkpointPath));
            Assert.Equal(state, File.ReadAllBytes(deltaPath));
            Assert.Contains("$deltatoken=D0", DeltaState.Load(deltaPath)!.DeltaLink);
            Assert.True(Directory.Exists(output));
            Assert.Empty(Directory.GetFileSystemEntries(output));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The two write probes the sync makes before it fetches anything, and what each of them
    /// calls the file it could not write. Both are the same call, and one noun for both told a
    /// caller whose -OutputFile directory was not writable that they could not write to the
    /// delta state path - naming a file that had just passed its own probe.
    /// </summary>
    [Fact]
    public void The_write_probe_names_the_file_it_could_not_write()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-probe-{Guid.NewGuid():N}")).FullName;
        var outDir = Directory.CreateDirectory(Path.Combine(dir, "out")).FullName;
        var stateDir = Directory.CreateDirectory(Path.Combine(dir, "state")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            // r-x: the directory can be searched and not written, which is what the probe
            // fails on. Where the write below succeeds the account is root, a mode says
            // nothing, and there is nothing here to measure.
            File.SetUnixFileMode(outDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var output = Path.Combine(outDir, "out.jsonl");
            var writable = true;
            try { File.WriteAllText(output, ""); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                writable = false;
            }
            if (writable) return;

            var deltaPath = Path.Combine(dir, "state.json");
            var (_, errors) = SyncRun(deltaPath, checkpointPath, output);
            var stop = Assert.Single(errors);
            Assert.Contains($"Cannot write to output path '{output}'", stop.Exception.Message);
            Assert.DoesNotContain("delta state path", stop.Exception.Message);

            // And the probe on -DeltaPath keeps its own noun.
            File.SetUnixFileMode(stateDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var unwritableState = Path.Combine(stateDir, "state.json");
            var (_, stateErrors) = SyncRun(unwritableState, checkpointPath,
                Path.Combine(dir, "fine.jsonl"));
            var stateStop = Assert.Single(stateErrors);
            Assert.Contains($"Cannot write to delta state path '{unwritableState}'",
                stateStop.Exception.Message);
        }
        finally
        {
            try
            {
                foreach (var d in new[] { outDir, stateDir })
                    File.SetUnixFileMode(d,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.Delete(dir, true);
            }
            catch { }
        }
    }

    /// <summary>
    /// The disk an interrupted fresh sync leaves: its changes are in a temp beside the output,
    /// the checkpoint names that temp, and there is no output at all yet. The run after it
    /// promotes the temp over the output and appends onto what it promoted.
    /// </summary>
    private static void InterruptedIntoATemp(MockHttpHandler handler, string deltaPath,
        string checkpointPath, string output)
    {
        UsersState(deltaPath);
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
        Sync(deltaPath, checkpointPath, output);
    }

    /// <summary>
    /// The other route that ends in an append, one file over. A sync whose checkpoint names a
    /// temp promotes it over the output and then appends onto it - and used to hold nothing
    /// between the move that put the changes there and the writer's own open a page fetch later.
    /// The checkpoint it repoints in that gap names no temp, which is the shape a second run
    /// over the same command line reads as an output it may cut back, append to, and move the
    /// token past. So the promotion takes the file it promoted into.
    /// </summary>
    [Fact]
    public void A_promoting_sync_holds_the_output_it_promoted_into()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-promotehold-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaPath = Path.Combine(dir, "state.json");
        var output = Path.Combine(dir, "out.jsonl");
        (string[] Warnings, ErrorRecord[] Errors) aResult = default;
        Exception? aFailed = null;
        Thread? a = null;
        using var arrived = new ManualResetEventSlim(false);
        using var released = new ManualResetEventSlim(false);

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            InterruptedIntoATemp(handler, deltaPath, checkpointPath, output);

            // The premise: the changes are in a temp, the checkpoint names it, and nothing is at
            // -OutputFile for anyone to be holding.
            var before = PaginationCheckpoint.Load(checkpointPath)!;
            Assert.NotNull(before.TempFile);
            Assert.False(File.Exists(output));
            Assert.Contains("$deltatoken=D0", DeltaState.Load(deltaPath)!.DeltaLink);

            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            var requestsBefore = handler.RequestCount;

            a = new Thread(() =>
            {
                try
                {
                    aResult = SyncParkedAtResume(deltaPath, checkpointPath, output,
                        arrived, released);
                }
                catch (Exception ex) { aFailed = ex; }
            });
            a.Start();
            Assert.True(arrived.Wait(TimeSpan.FromSeconds(60)),
                "sync A never reached the window between its promotion and its writer");

            // Past the promotion: the changes are in the output, the temp is gone, and the
            // position on disk names no temp - which is exactly what a second run reads as an
            // output it may take. And short of the writer: no page has been asked for.
            //
            // Measured off the directory entry, since the run holds the file and a reader of it
            // is refused until the run ends. What it holds is read at the end, when it does not.
            var promotedBytes = ("{\"id\":\"b1\"}" + Environment.NewLine
                + "{\"id\":\"b2\"}" + Environment.NewLine).Length;
            Assert.Equal(promotedBytes, new FileInfo(output).Length);
            var repointed = PaginationCheckpoint.Load(checkpointPath)!;
            Assert.Null(repointed.TempFile);
            Assert.Equal(promotedBytes, repointed.DataLength);
            Assert.Equal(["out.jsonl", "run.checkpoint", "state.json"],
                Directory.GetFiles(dir).Select(Path.GetFileName)
                         .OrderBy(n => n, StringComparer.Ordinal));
            Assert.Equal(requestsBefore, handler.RequestCount);

            var (warnings, errors) = SyncRun(deltaPath, checkpointPath, output);

            var stop = Assert.Single(errors);
            Assert.StartsWith("CheckpointOutputHeld", stop.FullyQualifiedErrorId,
                StringComparison.Ordinal);
            Assert.Equal(ErrorCategory.ResourceBusy, stop.CategoryInfo.Category);
            Assert.Contains($"Another sync is still writing '{output}'", stop.Exception.Message);
            Assert.Contains("advance the delta token past them", stop.Exception.Message);
            Assert.DoesNotContain(warnings, w => w.Contains("no longer holds"));

            // B sent no request, took nothing with it, and moved no token.
            Assert.Equal(requestsBefore, handler.RequestCount);
            Assert.True(File.Exists(checkpointPath), "B deleted the position A is resuming from");
            Assert.Contains("$deltatoken=D0", DeltaState.Load(deltaPath)!.DeltaLink);

            released.Set();
            Assert.True(a.Join(TimeSpan.FromSeconds(60)), "sync A never finished");
            Assert.Null(aFailed);
            Assert.Empty(aResult.Errors);

            // One run had the file. Every change once, in order, the position spent, and the
            // token advanced exactly once.
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                File.ReadAllLines(output));
            Assert.False(File.Exists(checkpointPath),
                "the completed sync left its position behind");
            Assert.Contains("$deltatoken=D2", DeltaState.Load(deltaPath)!.DeltaLink);
        }
        finally
        {
            released.Set();
            a?.Join(TimeSpan.FromSeconds(30));
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The promotion route's own claim on the file it replaces. A sync whose checkpoint names a
    /// temp puts those changes over the output and appends onto what it put there, and it asked
    /// for the file only after the move - which a rename asks nothing about, on either side of
    /// it. So the move went through over an output another run was holding and appending to, that
    /// run's next write landed in an inode with no name left on it, and this one would have gone
    /// on to move the delta token past changes the file at the path no longer held.
    /// </summary>
    [Fact]
    public void A_promotion_over_a_held_output_stops_the_sync_and_replaces_nothing()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-promoteheld-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaPath = Path.Combine(dir, "state.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            InterruptedIntoATemp(handler, deltaPath, checkpointPath, output);
            var cp = PaginationCheckpoint.Load(checkpointPath)!;
            var temp = Path.Combine(dir, cp.TempFile!);
            Assert.True(File.Exists(temp), "the interrupted run left no temp to promote");
            Assert.False(File.Exists(output));

            // A previous sync's changes at -OutputFile, and the run that has them: appending,
            // sharing reads, the way a resumed run holds its own output.
            File.WriteAllLines(output, OldSync);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            using (var live = new StreamWriter(
                       new FileStream(output, FileMode.Append, FileAccess.Write, FileShare.Read))
                   { AutoFlush = true })
            {
                var before = ReadDisk(dir, checkpointPath, temp, output, deltaPath);
                var requestsBefore = handler.RequestCount;
                var (warnings, errors) = SyncRun(deltaPath, checkpointPath, output);

                var stop = Assert.Single(errors);
                Assert.StartsWith("CheckpointOutputHeld", stop.FullyQualifiedErrorId,
                    StringComparison.Ordinal);
                Assert.Equal(ErrorCategory.ResourceBusy, stop.CategoryInfo.Category);
                Assert.Contains($"Another sync is still writing '{output}'",
                    stop.Exception.Message);
                // The temp, not the output, is where these items are counted on the promotion
                // route, and going on replaces the output rather than cutting it back.
                Assert.Contains($"The resume checkpoint at '{checkpointPath}' records "
                    + $"{cp.ItemsCollected} items into the temp '{cp.TempFile}'",
                    stop.Exception.Message);
                Assert.Contains($"going on would replace '{output}' with them under the run "
                    + "that holds it", stop.Exception.Message);
                Assert.DoesNotContain("cut that file back", stop.Exception.Message);
                Assert.Contains("advance the delta token past them", stop.Exception.Message);

                // No request, no recovery, no token: the temp still holds the changes the
                // checkpoint counts, the checkpoint still names it, and the staged file the move
                // would have come from was taken back.
                Assert.Equal(requestsBefore, handler.RequestCount);
                Assert.DoesNotContain(warnings, w => w.Contains("Recovered"));
                AssertUnmoved(before, ReadDisk(dir, checkpointPath, temp, output, deltaPath));
                Assert.False(File.Exists($"{output}.adopt"));
                Assert.Contains("$deltatoken=D0", DeltaState.Load(deltaPath)!.DeltaLink);

                // And the same inode, not a new file wearing the name: the holder's next write
                // is read back through the path.
                live.WriteLine("{\"id\":\"old-0000004\"}");
            }

            Assert.Equal([.. OldSync, "{\"id\":\"old-0000004\"}"], File.ReadAllLines(output));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The window the second run walked through, one cmdlet over. A checkpoint naming a temp that
    /// is not on disk was read as changes in no file at all: the sync said so, deleted the
    /// position counting them, re-enumerated from the last saved token and moved its own temp over
    /// the output - which was the promoting run's output, held and being appended to - and then
    /// advanced the token past both runs' work. The output is asked about before any of that now.
    /// </summary>
    [Fact]
    public void A_vanished_temp_over_a_held_output_stops_the_sync()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-vanished-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaPath = Path.Combine(dir, "state.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            InterruptedIntoATemp(handler, deltaPath, checkpointPath, output);

            // The instant a promoting run leaves between its temp's unlink and the save that
            // repoints the position: the changes are in the output at the length recorded for
            // them, the temp is gone, and the checkpoint still names it.
            var cp = PaginationCheckpoint.Load(checkpointPath)!;
            var temp = Path.Combine(dir, cp.TempFile!);
            var recorded = cp.DataLength!.Value;
            File.WriteAllBytes(output, File.ReadAllBytes(temp)[..(int)recorded]);
            File.Delete(temp);

            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            using var live = new StreamWriter(
                new FileStream(output, FileMode.Append, FileAccess.Write, FileShare.Read));

            var before = ReadDisk(dir, checkpointPath, output, deltaPath);
            var requestsBefore = handler.RequestCount;
            var (warnings, errors) = SyncRun(deltaPath, checkpointPath, output);

            var stop = Assert.Single(errors);
            Assert.StartsWith("CheckpointOutputHeld", stop.FullyQualifiedErrorId,
                StringComparison.Ordinal);
            Assert.Contains($"Another sync is still writing '{output}'", stop.Exception.Message);
            // The temp this checkpoint named is gone, not held - so the items are recorded
            // (past tense) into a temp the sentence does not go on to name, and going on means
            // re-enumerating fresh rather than cutting the output back.
            Assert.Contains($"The resume checkpoint at '{checkpointPath}' recorded "
                + $"{cp.ItemsCollected} items into a temp that is gone", stop.Exception.Message);
            Assert.Contains("going on would re-enumerate from the last saved delta token and "
                + $"replace '{output}' under the run that holds it", stop.Exception.Message);
            Assert.DoesNotContain("cut that file back", stop.Exception.Message);
            Assert.Contains("advance the delta token past them", stop.Exception.Message);
            Assert.DoesNotContain(warnings, w => w.Contains("missing or incomplete"));
            Assert.Equal(requestsBefore, handler.RequestCount);
            AssertUnmoved(before, ReadDisk(dir, checkpointPath, output, deltaPath));
            Assert.True(File.Exists(checkpointPath),
                "the position the promoting run is resuming from was deleted");
            Assert.Contains("$deltatoken=D0", DeltaState.Load(deltaPath)!.DeltaLink);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// Two syncs released together on the promotion route. Both completed: whichever lost the
    /// promotion found the temp gone, warned that the changes it counted were not on disk, deleted
    /// the position counting them, re-enumerated over the output the other one was holding, and
    /// moved the delta token past work that was no longer in any file. One of them ends the run
    /// instead now, and the token moves once.
    /// </summary>
    [Fact]
    public void Two_syncs_released_together_on_the_promotion_route_advance_the_token_once()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-together-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaPath = Path.Combine(dir, "state.json");
        var output = Path.Combine(dir, "out.jsonl");
        var gate = new GatedChangesByUrlHandler();
        var results = new (string[] Warnings, ErrorRecord[] Errors)[2];
        var failures = new Exception?[2];
        var threads = new Thread[2];
        var ready = new[] { new ManualResetEventSlim(false), new ManualResetEventSlim(false) };
        using var go = new ManualResetEventSlim(false);
        try
        {
            var setup = new MockHttpHandler();
            setup.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
            using (MgxTransportScope.Inject(setup))
            {
                InterruptedIntoATemp(setup, deltaPath, checkpointPath, output);
            }
            Assert.NotNull(PaginationCheckpoint.Load(checkpointPath)!.TempFile);
            Assert.False(File.Exists(output));
            Assert.Contains("$deltatoken=D0", DeltaState.Load(deltaPath)!.DeltaLink);

            using var transport = MgxTransportScope.Inject(gate);
            for (var i = 0; i < threads.Length; i++)
            {
                var slot = i;
                threads[slot] = new Thread(() =>
                {
                    try
                    {
                        results[slot] = SyncReleasedWith(deltaPath, checkpointPath, output,
                            ready[slot], go);
                    }
                    catch (Exception ex) { failures[slot] = ex; }
                });
                threads[slot].Start();
            }

            foreach (var r in ready)
                Assert.True(r.Wait(TimeSpan.FromSeconds(60)), "a sync never came up");
            go.Set();

            // One of them got past the reconcile, so it holds the output and is parked on its
            // first request. It stays there until the other has finished, which is what the run
            // that stops does with nothing on the wire at all.
            Assert.True(gate.Arrived.Wait(TimeSpan.FromSeconds(60)),
                "neither sync reached the wire");
            Assert.True(
                SpinWait.SpinUntil(() => threads.Any(t => !t.IsAlive), TimeSpan.FromSeconds(60)),
                "both syncs were still running with one of them holding the output");

            gate.Released.Set();
            foreach (var t in threads)
                Assert.True(t.Join(TimeSpan.FromSeconds(60)), "a sync never finished");
            Assert.All(failures, Assert.Null);

            // Exactly one run stopped, on one of the files this checkpoint stands for, and it
            // neither asked for a page nor said the changes it was protecting were lost.
            //
            // Which file it stopped on is the race's to decide, and both answers are the same
            // run: the stop that loses the temp, or the output the promoter is holding, and the
            // stop that reaches the staging name first and finds the promoter's copy standing at
            // it. The second is the narrow one - it needs this run to arrive between the other's
            // create and its rename - and it is not one platform's: the staging name is swept
            // before either claim on both. So the sentence asserted is the one that belongs to
            // whichever route the run took, and what is asserted of the run itself is the same
            // either way, below: one winner, every change once, and nothing of this one's left
            // on the disk.
            var stop = Assert.Single(results.SelectMany(r => r.Errors));
            Assert.StartsWith("Checkpoint", stop.FullyQualifiedErrorId, StringComparison.Ordinal);
            if (stop.FullyQualifiedErrorId.StartsWith("CheckpointStagingFailed",
                    StringComparison.Ordinal))
            {
                // The promoter's own staged copy, held from the create through the rename. Named
                // as that and not as an entry nothing could be made of, and the sentence says
                // the changes are still whole in the temp the checkpoint names.
                Assert.Contains("another run has the staged copy open", stop.Exception.Message);
                Assert.Contains("whole, but staging them into", stop.Exception.Message);
                Assert.Contains("Nothing was changed;", stop.Exception.Message);
            }
            else
            {
                Assert.Contains("This run stops here; nothing was written.",
                    stop.Exception.Message);
            }
            Assert.DoesNotContain(results.SelectMany(r => r.Warnings),
                w => w.Contains("missing or incomplete"));

            // One run had the file: every change once, in order, the position spent by the run
            // that finished, and the token moved exactly once.
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                File.ReadAllLines(output));
            Assert.False(File.Exists(checkpointPath));
            Assert.Contains("$deltatoken=D2", DeltaState.Load(deltaPath)!.DeltaLink);
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
    /// the temp having gone: it said the changes it counted were not on disk, deleted the position
    /// counting them, re-enumerated over the output and moved the delta token past every one of
    /// them. The staging says so for itself now, and both files and the token are where they were.
    /// </summary>
    [Fact]
    public void A_directory_at_the_staging_name_stops_the_sync_and_changes_nothing()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-staging-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaPath = Path.Combine(dir, "state.json");
        var output = Path.Combine(dir, "out.jsonl");
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            InterruptedIntoATemp(handler, deltaPath, checkpointPath, output);
            var cp = PaginationCheckpoint.Load(checkpointPath)!;
            var temp = Path.Combine(dir, cp.TempFile!);
            Assert.True(File.Exists(temp), "the interrupted run left no temp to promote");

            var adopt = $"{output}.adopt";
            Directory.CreateDirectory(adopt);

            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            var before = ReadDisk(dir, checkpointPath, temp, output, deltaPath);
            var requestsBefore = handler.RequestCount;
            var (warnings, errors) = SyncRun(deltaPath, checkpointPath, output);

            var stop = Assert.Single(errors);
            Assert.StartsWith("CheckpointStagingFailed", stop.FullyQualifiedErrorId,
                StringComparison.Ordinal);
            Assert.Equal(ErrorCategory.WriteError, stop.CategoryInfo.Category);
            Assert.Contains($"The {cp.ItemsCollected} items the resume checkpoint at "
                + $"'{checkpointPath}' records are in '{cp.TempFile}', whole, but staging them "
                + $"into '{output}' failed at '{adopt}': ", stop.Exception.Message);
            Assert.Contains("Nothing was changed; fix that and run again to recover them, or "
                + "remove the temp and the checkpoint to re-enumerate from the last saved delta "
                + "token.", stop.Exception.Message);

            // The runtime's own words, punctuated once: the reason is quoted inside a sentence
            // that ends it, and a message already ending in a period put two there.
            Assert.DoesNotContain(".. Nothing was changed;", stop.Exception.Message);

            Assert.DoesNotContain(warnings, w => w.Contains("missing or incomplete"));
            Assert.DoesNotContain(warnings, w => w.Contains("Recovered"));
            Assert.Equal(requestsBefore, handler.RequestCount);
            AssertUnmoved(before, ReadDisk(dir, checkpointPath, temp, output, deltaPath));
            Assert.True(Directory.Exists(adopt), "the run removed the caller's directory");
            Assert.Contains("$deltatoken=D0", DeltaState.Load(deltaPath)!.DeltaLink);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The same write refused the other way: another run holding the staging name, which is what
    /// two syncs over one command line reach a page apart. Nothing this run may take, and nothing
    /// it may report as a temp that has gone.
    /// </summary>
    [Fact]
    public void A_held_staging_name_stops_the_sync_and_leaves_the_holders_file()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-staging-held-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaPath = Path.Combine(dir, "state.json");
        var output = Path.Combine(dir, "out.jsonl");
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            InterruptedIntoATemp(handler, deltaPath, checkpointPath, output);
            var cp = PaginationCheckpoint.Load(checkpointPath)!;
            var temp = Path.Combine(dir, cp.TempFile!);
            var adopt = $"{output}.adopt";

            using var holder = new FileStream(adopt, FileMode.Create, FileAccess.ReadWrite,
                FileShare.None);
            holder.Write("{\"id\":\"theirs\"}\n"u8);
            holder.Flush();
            var holderBytes = holder.Length;

            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            var before = ReadDisk(dir, checkpointPath, temp, output, deltaPath);
            var requestsBefore = handler.RequestCount;
            var (warnings, errors) = SyncRun(deltaPath, checkpointPath, output);

            var stop = Assert.Single(errors);
            Assert.StartsWith("CheckpointStagingFailed", stop.FullyQualifiedErrorId,
                StringComparison.Ordinal);
            Assert.Equal(ErrorCategory.WriteError, stop.CategoryInfo.Category);
            Assert.Contains($"staging them into '{output}' failed at '{adopt}': ",
                stop.Exception.Message);
            Assert.Contains("Nothing was changed;", stop.Exception.Message);
            Assert.DoesNotContain(warnings, w => w.Contains("missing or incomplete"));
            Assert.Equal(requestsBefore, handler.RequestCount);

            AssertUnmoved(before, ReadDisk(dir, checkpointPath, temp, output, deltaPath));
            Assert.Equal(holderBytes, holder.Length);
            Assert.Contains("$deltatoken=D0", DeltaState.Load(deltaPath)!.DeltaLink);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A pipe at the staging name, which is one of the kinds the sweep removes. What came off
    /// the name went to the verbose stream, so the deletion reached nobody who had not asked for
    /// verbose - and a sync that removes an entry beside the caller's output has to say it did.
    /// It is a warning now, in the one sentence both cmdlets say it in.
    /// </summary>
    [Fact]
    public void A_pipe_at_the_staging_name_is_swept_and_the_sync_warns_what_it_removed()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-staging-pipe-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaPath = Path.Combine(dir, "state.json");
        var output = Path.Combine(dir, "out.jsonl");
        var adopt = $"{output}.adopt";
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            InterruptedIntoATemp(handler, deltaPath, checkpointPath, output);
            Mkfifo(adopt);

            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);

            // On its own thread with a watchdog: an open of a pipe with no reader never comes
            // back, and a test that called the sync inline would hang the suite.
            (string[] Warnings, ErrorRecord[] Errors) result = ([], []);
            var running = new Thread(() => result = SyncRun(deltaPath, checkpointPath, output))
                { IsBackground = true };
            running.Start();
            Assert.True(running.Join(TimeSpan.FromSeconds(30)),
                "the sync never returned: the staging open is waiting for a reader on the pipe");

            Assert.Empty(result.Errors);
            Assert.Contains(result.Warnings, w => w.Contains(
                $"Removed what stood at the staging name '{Path.GetFileName(adopt)}': "
                + "an entry no copy could be staged in, such as a pipe or a socket."));
            Assert.Contains(result.Warnings, w => w.Contains("Recovered 2 items"));

            // The pipe is gone, the promotion landed in a regular file at the output path, and
            // the resumed page is behind the promoted changes.
            Assert.False(File.Exists(adopt));
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                File.ReadAllLines(output));
        }
        finally
        {
            try { File.Delete(adopt); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static void Mkfifo(string path)
    {
        using var mkfifo = System.Diagnostics.Process.Start("/usr/bin/mkfifo", [path]);
        Assert.NotNull(mkfifo);
        mkfifo.WaitForExit();
        Assert.Equal(0, mkfifo.ExitCode);
    }

    /// <summary>
    /// The two output paths a grant does nothing for. A symlink loop and a FIFO both refuse the
    /// open, and both used to end under ErrorCategory.PermissionDenied - the category a caller
    /// filtering on it reads as a mode to change, over paths where there is no mode to change
    /// and the sentence beside it already said as much. The open's own reason decides the
    /// category now, and a 0444 output above still ends under PermissionDenied.
    /// </summary>
    [Fact]
    public void An_output_no_grant_would_open_stops_the_sync_under_the_right_category()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-outcategory-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaPath = Path.Combine(dir, "state.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            PromotedAndAppending(handler, deltaPath, checkpointPath, output);
            Assert.Null(PaginationCheckpoint.Load(checkpointPath)!.TempFile);
            var position = File.ReadAllBytes(checkpointPath);

            // A link that points at itself: the open comes back ELOOP, which is neither a hold
            // nor a mode.
            File.Delete(output);
            using (var ln = System.Diagnostics.Process.Start("/bin/ln", ["-s", output, output]))
            {
                ln!.WaitForExit();
                Assert.Equal(0, ln.ExitCode);
            }

            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            var requestsBefore = handler.RequestCount;
            var loop = Assert.Single(SyncRun(deltaPath, checkpointPath, output).Errors);

            Assert.StartsWith("CheckpointOutputUnopenable", loop.FullyQualifiedErrorId,
                StringComparison.Ordinal);
            Assert.Equal(ErrorCategory.InvalidOperation, loop.CategoryInfo.Category);
            Assert.Contains("Too many levels of symbolic links", loop.Exception.Message);
            // No trailing period on this reason, so trimming one off it is a no-op: the single
            // period the sentence joins on is the one it always had.
            Assert.Contains($"Too many levels of symbolic links : '{output}'. The resume "
                + "checkpoint", loop.Exception.Message);
            Assert.DoesNotContain("Grant write access", loop.Exception.Message);

            // A pipe: it opens, and then has no length and no offset to write at.
            File.Delete(output);
            using (var mkfifo = System.Diagnostics.Process.Start("/usr/bin/mkfifo", [output]))
            {
                mkfifo!.WaitForExit();
                Assert.Equal(0, mkfifo.ExitCode);
            }

            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            var fifo = Assert.Single(SyncRun(deltaPath, checkpointPath, output).Errors);

            Assert.StartsWith("CheckpointOutputUnopenable", fifo.FullyQualifiedErrorId,
                StringComparison.Ordinal);
            Assert.Equal(ErrorCategory.InvalidOperation, fifo.CategoryInfo.Category);
            Assert.Contains($"'{output}' could not be opened for writing: the file has no offset "
                + "to write at.", fifo.Exception.Message);
            Assert.DoesNotContain("Grant write access", fifo.Exception.Message);

            // Neither run asked for a page, and neither took the position counting the changes
            // the advice tells the caller to come back for.
            Assert.Equal(requestsBefore, handler.RequestCount);
            Assert.Equal(position, File.ReadAllBytes(checkpointPath));
            Assert.Contains("$deltatoken=D0", DeltaState.Load(deltaPath)!.DeltaLink);
        }
        finally
        {
            try { File.Delete(output); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The 410 door sends the attempt loop round again with whatever the attempt before it was
    /// holding, and a run that resumed from a checkpoint holds -OutputFile. The door deletes the
    /// checkpoint, so the retry normally finds nothing to reconcile - but that delete can fail,
    /// on a checkpoint directory this account may read and not unlink from, and then the retry
    /// reads the same position again with the output still this run's. The claim answered held,
    /// about this run's own handle, and the run ended telling the caller to wait for a second
    /// sync and to give it its own -OutputFile: there was no second sync, and both were already
    /// this one's. The hold goes with the position it was taken for, which is the position the
    /// 410 has just declared dead.
    /// </summary>
    [Fact]
    public void An_expired_token_does_not_leave_the_retry_refused_by_this_runs_own_hold()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-410hold-{Guid.NewGuid():N}")).FullName;
        var cpDir = Directory.CreateDirectory(Path.Combine(dir, "cp")).FullName;
        var checkpointPath = Path.Combine(cpDir, "run.checkpoint");
        var deltaPath = Path.Combine(dir, "state.json");
        var output = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            // An interrupted first pass, resumed: its changes are in the output, the checkpoint
            // records that file and no temp, and there is no delta state yet - a token comes
            // with the deltaLink at the end. So the URL this run builds is the URL it rebuilds
            // for the retry, and one position is this run's on both attempts.
            File.WriteAllText(output, "{\"id\":\"a1\"}\n{\"id\":\"a2\"}\n");
            var recorded = new FileInfo(output).Length;
            new PaginationCheckpoint
            {
                Resource = "https://graph.microsoft.com/v1.0/users/delta?$top=999",
                NextLink = "https://graph.microsoft.com/v1.0/users/delta?$skiptoken=B2",
                ItemsCollected = 2,
                PageItemsAlreadyWritten = 0,
                TempFile = null,
                OutputFile = output,
                DataLength = recorded,
            }.Save(checkpointPath);

            // The delete the 410 door makes, refused: 0555 on the checkpoint's own directory.
            using (var chmod = System.Diagnostics.Process.Start("/bin/chmod", ["555", cpDir]))
            {
                chmod!.WaitForExit();
                Assert.Equal(0, chmod.ExitCode);
            }

            handler.QueueResponse(HttpStatusCode.Gone, TokenExpired);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            var (warnings, errors) = SyncRun(deltaPath, checkpointPath, output);

            // The premise: the door could not take the position, so the retry read it again.
            Assert.Contains(warnings, w => w.Contains("Could not delete resume checkpoint"));
            Assert.Contains(warnings, w => w.Contains("Delta token expired (HTTP 410 Gone)"));

            // And the retry was not refused by the handle the attempt before it left standing.
            Assert.DoesNotContain(errors,
                e => e.FullyQualifiedErrorId.StartsWith("CheckpointOutputHeld",
                    StringComparison.Ordinal));
            Assert.DoesNotContain(errors,
                e => e.Exception.Message.Contains("Another sync is still writing"));
            Assert.Empty(errors);
        }
        finally
        {
            using (var chmod = System.Diagnostics.Process.Start("/bin/chmod", ["755", cpDir]))
                chmod!.WaitForExit();
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The sync's twin of the export's unsearchable-parent advice. The open comes back as an
    /// access failure naming the file, and the sentence read it as a mode on the file and told
    /// the caller to grant write access to it - a file they cannot stat, let alone chmod,
    /// through a parent that refuses to be looked into. The grant is on the directory, and the
    /// sentence says which directory.
    ///
    /// Built from the refusal rather than run into: this sync asks whether it can write beside
    /// -OutputFile before it reconciles anything, and a directory that refuses to be searched
    /// refuses that probe first - so the sentence is reached here the way the run would reach
    /// it, and the words it produces are what this pins.
    /// </summary>
    [Fact]
    public void The_unsearchable_parent_stop_says_where_the_grant_belongs()
    {
        var baseType = typeof(Mgx.Cmdlets.Base.MgxCmdletBase);
        var reasonType = baseType.GetNestedType("UnopenableReason",
            BindingFlags.NonPublic | BindingFlags.Public)!;
        var refusalType = baseType.GetNestedType("CheckpointFileRefusal",
            BindingFlags.NonPublic | BindingFlags.Public)!;
        var fileType = baseType.GetNestedType("CheckpointFile",
            BindingFlags.NonPublic | BindingFlags.Public)!;
        var claimType = baseType.GetNestedType("ClaimRefusal",
            BindingFlags.NonPublic | BindingFlags.Public)!;
        var routeType = baseType.GetNestedType("CheckpointStopRoute",
            BindingFlags.NonPublic | BindingFlags.Public)!;
        var message = typeof(Mgx.Cmdlets.Cmdlets.Delta.SyncMgxDelta).GetMethod(
            "CheckpointFileStopMessage", BindingFlags.Static | BindingFlags.NonPublic)!;

        const string vault = "/somewhere/vault";
        const string output = $"{vault}/out.jsonl";
        var reason = Activator.CreateInstance(reasonType,
            [$"the directory '{vault}' cannot be searched", true, false, true]);
        var refusal = Activator.CreateInstance(refusalType,
        [
            Enum.Parse(fileType, "Output"),
            Enum.Parse(claimType, "CannotBeOpened"),
            Enum.Parse(routeType, "Appending"),
            reason,
        ]);
        var checkpoint = new PaginationCheckpoint { ItemsCollected = 7, DataLength = 42 };

        var sentence = (string)message.Invoke(null,
            [refusal, "/somewhere/run.checkpoint", output, checkpoint])!;

        Assert.Contains($"'{output}' could not be opened for writing: the directory "
            + $"'{vault}' cannot be searched.", sentence);
        Assert.Contains(
            "Grant access to that directory and run again to resume from it, or remove the "
            + "checkpoint to sync afresh.",
            sentence);
        Assert.DoesNotContain("Grant write access to that file", sentence);
    }

    /// <summary>
    /// Both streams of a sync, for the scenarios whose evidence is on the verbose one: whether
    /// the run swept the temps beside its output is not something a warning says.
    /// </summary>
    private static (string[] Warnings, string[] Verbose) SyncStreams(string deltaPath,
        string checkpointPath, string outputPath, string uri = "/users/delta",
        bool latest = false)
    {
        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Delta.SyncMgxDelta).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        ps.AddCommand("Sync-MgxDelta")
          .AddParameter("Uri", uri)
          .AddParameter("DeltaPath", deltaPath)
          .AddParameter("CheckpointPath", checkpointPath)
          .AddParameter("OutputFile", outputPath)
          .AddParameter("Verbose");
        if (latest) ps.AddParameter("Latest");
        try { ps.Invoke(); }
        catch (CmdletInvocationException) { }
        return ([.. ps.Streams.Warning.Select(w => w.Message)],
                [.. ps.Streams.Verbose.Select(v => v.Message)]);
    }

    /// <summary>
    /// The shape every release before 2.1 wrote: the position, the count, and nothing about
    /// which files the changes are in. Written by rewriting a real checkpoint, so the resource
    /// and the nextLink are the ones this run built rather than ones a test guessed at.
    /// </summary>
    private static void StripToPre21Shape(string checkpointPath)
    {
        var cp = PaginationCheckpoint.Load(checkpointPath)!;
        Assert.NotNull(cp.TempFile);
        Assert.NotNull(cp.DataLength);
        cp.TempFile = null;
        cp.OutputFile = null;
        cp.DataLength = null;
        cp.Save(checkpointPath);
    }

    /// <summary>
    /// A refusal leaves the checkpoint standing because the sync that wrote it resumes from
    /// exactly that, and the changes it counted are in a temp beside this output. A checkpoint
    /// written before the temp's name was recorded names none, so keyed on that name the sweep
    /// in the same run found nothing to spare and deleted the file the spared position was
    /// pointing at - seconds after a warning promising both files would be left as they are.
    /// The refusal takes the whole sweep with it instead.
    /// </summary>
    [Fact]
    public void A_refusal_of_a_checkpoint_naming_no_temp_holds_the_whole_sweep_off()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-sweepoff-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var groupsState = Path.Combine(dir, "groups-state.json");
        var usersState = Path.Combine(dir, "users-state.json");
        var output = Path.Combine(dir, "out.jsonl");
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            // A groups sync killed after its first page: two changes in a temp beside this
            // output, no output yet, and a position counting them.
            handler.QueueResponse(HttpStatusCode.OK, GroupsPageOne);
            Sync(groupsState, checkpointPath, output, "/groups/delta");
            var orphan = Assert.Single(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
            var counted = File.ReadAllBytes(orphan);
            StripToPre21Shape(checkpointPath);
            Assert.False(File.Exists(output));

            // A users sync over the same -CheckpointPath and -OutputFile. It refuses that
            // position, and dies on its own first request without an output or a temp of its
            // own to leave behind.
            var (warnings, verbose) = SyncStreams(usersState, checkpointPath, output);

            Assert.Contains(warnings, w => w.Contains(
                "records an enumeration this run cannot resume from", StringComparison.Ordinal));
            Assert.Contains(verbose, v => v.Contains(
                $"Left the temp files beside '{output}' alone: one of them may hold the items "
                + "of the checkpoint this run refused.", StringComparison.Ordinal));
            Assert.True(File.Exists(orphan),
                "the sweep took the temp the refused checkpoint stands for");
            Assert.Equal(counted, File.ReadAllBytes(orphan));
            Assert.True(File.Exists(checkpointPath), "the refused position was deleted");
            Assert.False(File.Exists(output), "the run that stopped left an output behind");

            // And the sync that owns both comes back to find them where it left them.
            handler.QueueResponse(HttpStatusCode.OK, GroupsResync);
            var (recovered, _) = SyncStreams(groupsState, checkpointPath, output, "/groups/delta");

            Assert.Contains(recovered, w => w.Contains(
                "Recovered 2 items from an interrupted sync's temp file.",
                StringComparison.Ordinal));
            Assert.Equal(["{\"id\":\"g1\"}", "{\"id\":\"g2\"}", "{\"id\":\"g9\"}"],
                File.ReadAllLines(output));
            Assert.Empty(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The corroboration a checkpoint recording no output is believed on is asked for only
    /// where there is something to corroborate. Applied to a checkpoint recording neither an
    /// output nor a length, the predicate answered no for want of a measurement - so the whole
    /// pre-2.1 file-mode shape was refused here while the export recovered it, this cmdlet's
    /// own adoption branch was reachable from no shape any release ever wrote, and the position
    /// left standing refused every -Latest that followed for as long as it was there.
    /// </summary>
    [Fact]
    public void A_checkpoint_recording_neither_field_recovers_and_latest_follows_it()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-delta-neither-{Guid.NewGuid():N}")).FullName;
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var deltaPath = Path.Combine(dir, "state.json");
        var output = Path.Combine(dir, "out.jsonl");
        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            // A first sync, with no state to resume from, killed after its first page: two
            // changes in a temp, no output, and no delta token yet.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaPath, checkpointPath, output);
            var orphan = Assert.Single(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
            StripToPre21Shape(checkpointPath);
            Assert.False(File.Exists(output));
            Assert.False(File.Exists(deltaPath));

            // The same command line comes back to its own position. The changes it counts are
            // in the temp beside the output it names, which is the evidence that shape is
            // decided on - and the page that finishes it carries no token, so nothing but the
            // checkpoint can have a say in the -Latest below.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPageNoToken);
            var (warnings, _) = SyncStreams(deltaPath, checkpointPath, output);

            Assert.Contains(warnings, w => w.Contains(
                "Recovered 2 items from an interrupted sync's temp file.",
                StringComparison.Ordinal));
            Assert.DoesNotContain(warnings, w => w.Contains("no longer corroborate it",
                StringComparison.Ordinal));
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b9\"}"],
                File.ReadAllLines(output));
            Assert.False(File.Exists(orphan), "the adopted temp outlived the recovery");
            Assert.False(File.Exists(checkpointPath),
                "the completed sync left the position it recovered from behind");

            // And -Latest is answerable again: the file that would have refused it for good
            // went with the sync that finished.
            handler.QueueResponse(HttpStatusCode.OK, LatestBaseline);
            var (latest, _) = SyncStreams(deltaPath, checkpointPath, output, latest: true);

            Assert.DoesNotContain(latest, w => w.Contains("-Latest ignored",
                StringComparison.Ordinal));
            Assert.Contains("$deltatoken=D9", DeltaState.Load(deltaPath)!.DeltaLink);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
