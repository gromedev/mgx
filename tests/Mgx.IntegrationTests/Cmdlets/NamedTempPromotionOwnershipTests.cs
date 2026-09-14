using System.Management.Automation;
using System.Net;
using System.Text;
using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests;

/// <summary>
/// Recovery from a checkpoint that NAMES its temp - every checkpoint this release writes.
/// The temp is taken by name rather than by the glob adoption searches with, which says which
/// file holds the counted items but nothing at all about whether anyone is still writing it:
/// a run whose export dies partway leaves that state on disk, and so does one that is still
/// going. Promotion copied the rows into its own output and unlinked the file underneath the
/// run that was filling it, which then wrote into an unlinked inode and failed its own
/// promotion naming the OUTPUT. A scheduled export overlapping itself reaches this with
/// nothing misconfigured.
/// </summary>
[Collection("Pipeline")]
public class NamedTempPromotionOwnershipTests
{
    private const string Page1 = """
    {"value":[{"id":"u1"},{"id":"u2"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=P2"}
    """;
    private const string Page2 = """
    {"value":[{"id":"u3"}]}
    """;
    private const string Baseline = """
    {"value":[{"id":"a1"},{"id":"a2"}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/users/delta?$deltatoken=D1"}
    """;
    private const string ChangesPage1 = """
    {"value":[{"id":"b1"},{"id":"b2"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users/delta?$skiptoken=B2"}
    """;
    private const string ChangesPage2 = """
    {"value":[{"id":"b3"}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/users/delta?$deltatoken=D2"}
    """;
    private const string ServerError = """{"error":{"code":"InternalServerError","message":"boom"}}""";

    /// <summary>
    /// Answers what it was queued with, and 500s once the queue is empty - so a run dying on
    /// the wire needs no assumption about how many times the client retries first.
    /// </summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _steps = new();
        private readonly object _lock = new();

        public void Queue(HttpStatusCode status, string body)
        {
            lock (_lock) _steps.Enqueue((status, body));
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            (HttpStatusCode Status, string Body) step;
            lock (_lock)
                step = _steps.Count > 0 ? _steps.Dequeue() : (HttpStatusCode.InternalServerError, ServerError);
            return Task.FromResult(new HttpResponseMessage(step.Status)
            {
                RequestMessage = request,
                Content = new StringContent(step.Body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static string NewDir() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-promote-owner-{Guid.NewGuid():N}")).FullName;

    /// <summary>
    /// What a run leaves on the streams. Errors carries the terminating record a run that will
    /// not touch a checkpoint's held or unopenable temp ends on, read from both places
    /// PowerShell can put it.
    /// </summary>
    private sealed record RunResult(List<string> Warnings, List<PSObject> Output,
        List<ErrorRecord> Errors);

    private static PowerShell Shell(bool withContext = false)
    {
        var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        if (withContext)
        {
            ps.AddScript("function Get-MgContext { [PSCustomObject]@{ TenantId = 'test-tenant-00000000-0000-0000-0000-000000000000' } }");
            ps.Invoke();
            ps.Commands.Clear();
        }
        return ps;
    }

    private static RunResult Export(string output, string checkpoint)
    {
        using var ps = Shell();
        ps.AddCommand("Export-MgxCollection")
          .AddParameter("Uri", "/users")
          .AddParameter("OutputFile", output)
          .AddParameter("CheckpointPath", checkpoint)
          .AddParameter("All");
        // A run that dies is the subject here, so a terminating error is an expected outcome.
        List<PSObject> results = [];
        List<ErrorRecord> errors = [];
        try { results = [.. ps.Invoke()]; }
        catch (CmdletInvocationException ex) { errors.Add(ex.ErrorRecord); }
        errors.AddRange(ps.Streams.Error);
        return new RunResult([.. ps.Streams.Warning.Select(w => w.Message)], results, errors);
    }

    /// <summary>
    /// The same export with the verbose stream captured. What the stale-temp sweep left alone,
    /// and why, is said there and nowhere else.
    /// </summary>
    private static List<string> ExportVerbose(string output, string checkpoint)
    {
        using var ps = Shell();
        ps.AddCommand("Export-MgxCollection")
          .AddParameter("Uri", "/users")
          .AddParameter("OutputFile", output)
          .AddParameter("CheckpointPath", checkpoint)
          .AddParameter("All")
          .AddParameter("Verbose", true);
        try { ps.Invoke(); }
        catch (CmdletInvocationException) { }
        return [.. ps.Streams.Verbose.Select(v => v.Message)];
    }

    private static RunResult Sync(string deltaPath, string checkpoint, string output)
    {
        using var ps = Shell(withContext: true);
        ps.AddCommand("Sync-MgxDelta")
          .AddParameter("Uri", "/users/delta")
          .AddParameter("DeltaPath", deltaPath)
          .AddParameter("CheckpointPath", checkpoint)
          .AddParameter("OutputFile", output);
        List<PSObject> results = [];
        List<ErrorRecord> errors = [];
        try { results = [.. ps.Invoke()]; }
        catch (CmdletInvocationException ex) { errors.Add(ex.ErrorRecord); }
        errors.AddRange(ps.Streams.Error);
        return new RunResult([.. ps.Streams.Warning.Select(w => w.Message)], results, errors);
    }

    /// <summary>
    /// Reopens a temp the way the cmdlet opens its own - new StreamWriter(path, append: false),
    /// which asks for FileShare.Read - with the bytes the checkpoint counted already in it.
    /// This is the run that has not died: it is between pages, and its next flush is still to
    /// come.
    /// </summary>
    private static StreamWriter HoldLikeALiveRun(string tempPath)
    {
        var counted = File.ReadAllText(tempPath);
        var writer = new StreamWriter(tempPath, append: false);
        writer.Write(counted);
        writer.Flush();
        return writer;
    }

    /// <summary>
    /// The reproduction. A run dies after its first page, leaving a temp and a checkpoint
    /// naming it; the same export is running again against that -OutputFile before the first
    /// one has finished. The second run must not take the file the first is writing: it copied
    /// those rows into its own output, unlinked the temp, and the first run's closing move then
    /// failed with a FileNotFoundException reported against the output it never got to write.
    ///
    /// Nor may it export around them, which is what taking them looks like a page later: the
    /// output it would replace is the one the checkpoint records, and the position it would save
    /// goes to the same path. It stops, and says which of the two files to deal with.
    /// </summary>
    [Fact]
    public void An_export_does_not_promote_a_temp_a_running_export_still_holds()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // The first run: one page collected, then the wire dies. A real temp, and a real
            // checkpoint naming it with the flushed length.
            handler.Queue(HttpStatusCode.OK, Page1);
            Export(output, checkpoint);
            var temp = Assert.Single(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
            Assert.False(File.Exists(output));
            Assert.Equal(2, PaginationCheckpoint.Load(checkpoint)!.ItemsCollected);

            // It is not dead. It still holds that temp, with its two rows in it.
            using var live = HoldLikeALiveRun(temp);

            // The second run, same -OutputFile and same -CheckpointPath, with the whole
            // collection waiting for it on the wire.
            handler.Queue(HttpStatusCode.OK, Page1);
            handler.Queue(HttpStatusCode.OK, Page2);
            var second = Export(output, checkpoint);

            Assert.True(File.Exists(temp), "a running export's temp was unlinked under it");
            Assert.DoesNotContain(second.Warnings, w => w.Contains("Recovered"));
            var stop = Assert.Single(second.Errors);
            Assert.StartsWith("CheckpointTempHeld", stop.FullyQualifiedErrorId,
                StringComparison.Ordinal);
            Assert.Contains("Another export is still writing the temp file", stop.Exception.Message);
            Assert.Contains(checkpoint, stop.Exception.Message);
            Assert.Contains("This run stops here; nothing was written.", stop.Exception.Message);
            Assert.DoesNotContain("cannot be opened for writing by this account",
                stop.Exception.Message);

            // And it published no output of its own: the file at -OutputFile is still the one
            // the checkpoint counts bytes of, which here is no file at all.
            Assert.False(File.Exists(output), "the stopped run published an output");

            // And the first run goes on, undisturbed: the page it fetched since its last save
            // lands, and the move it ends with finds the file it has been writing all along.
            live.WriteLine("{\"id\":\"u3-live\"}");
            live.Flush();
            Assert.True(File.Exists(temp));
            live.Dispose();
            File.Move(temp, output, overwrite: true);
            Assert.Equal(
                ["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3-live\"}"],
                File.ReadAllLines(output));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// What the refusal leaves behind. A temp that is missing or short means the counted items
    /// are in no file, and the checkpoint describing them is deleted on the way to exporting
    /// again - but a temp another run is writing is the position that run comes back to, and
    /// deleting it is the same mistake as unlinking the temp, one file over. Both files are
    /// still there, and still say exactly what they said.
    /// </summary>
    [Fact]
    public void A_refused_promotion_leaves_the_running_exports_checkpoint_and_temp_alone()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            handler.Queue(HttpStatusCode.OK, Page1);
            Export(output, checkpoint);
            var temp = Assert.Single(Directory.GetFiles(dir, "out.jsonl.*.tmp"));

            var checkpointBefore = File.ReadAllBytes(checkpoint);
            var tempBefore = File.ReadAllBytes(temp);

            using (HoldLikeALiveRun(temp))
            {
                // The second run gets nothing off the wire, so it never saves a checkpoint of
                // its own: whatever is on disk afterwards is what the refusal left.
                Export(output, checkpoint);

                Assert.Equal(checkpointBefore, File.ReadAllBytes(checkpoint));
                Assert.Equal(tempBefore, ReadShared(temp));
                Assert.False(File.Exists(output), "the other run's rows were published as an output");
                Assert.False(File.Exists(output + ".adopt"));
                Assert.Equal([temp], Directory.GetFiles(dir, "out.jsonl.*.tmp"));
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// The ordinary resume, which is the same state on disk minus the holder: the run that
    /// wrote the temp is gone, so its items are recovered into the output and the enumeration
    /// carries on from the page the checkpoint records. A claim that cannot tell the two apart
    /// would cost every resume this release writes.
    /// </summary>
    [Fact]
    public void An_export_promotes_the_named_temp_of_a_run_that_is_gone()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            handler.Queue(HttpStatusCode.OK, Page1);
            Export(output, checkpoint);
            Assert.Single(Directory.GetFiles(dir, "out.jsonl.*.tmp"));

            handler.Queue(HttpStatusCode.OK, Page2);
            var resumed = Export(output, checkpoint);

            Assert.Contains(resumed.Warnings, w =>
                w.Contains("Recovered 2 items from an interrupted export's temp file"));
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(output));
            Assert.Equal(3L, Assert.IsType<Mgx.Cmdlets.Models.MgxExportResult>(
                Assert.Single(resumed.Output).BaseObject).ItemCount);
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
            Assert.False(File.Exists(checkpoint), "a completed export deletes its checkpoint");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// Where the claim must not go. A checkpoint written before the output file was recorded is
    /// diagnosed as this export's by the temp it names corroborating it - the same resolution
    /// the promotion below uses - so a claim taken during that reading would answer "this names
    /// no file of mine" for a temp that is merely open at the moment, and a settled ownership
    /// diagnosis would swing on which second the export started in. The checkpoint is still
    /// this export's; it is the promotion that refuses.
    /// </summary>
    [Fact]
    public void A_held_temp_does_not_change_who_a_checkpoint_without_an_output_belongs_to()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        var temp = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
        try
        {
            using var live = new StreamWriter(temp, append: false);
            live.WriteLine("{\"id\":\"u1\"}");
            live.WriteLine("{\"id\":\"u2\"}");
            live.Flush();

            // No "outputFile" field: the shape a release that did not record one leaves.
            File.WriteAllText(checkpoint, $$"""
            {"resource":"https://graph.microsoft.com/v1.0/users?$top=999",
             "nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=P2",
             "itemsCollected":2,"pageItemsAlreadyWritten":0,
             "tempFile":"{{Path.GetFileName(temp)}}",
             "dataLength":{{new FileInfo(temp).Length}}}
            """);

            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);
            handler.Queue(HttpStatusCode.OK, Page1);
            handler.Queue(HttpStatusCode.OK, Page2);
            var run = Export(output, checkpoint);

            Assert.StartsWith("CheckpointTempHeld", Assert.Single(run.Errors).FullyQualifiedErrorId,
                StringComparison.Ordinal);
            Assert.DoesNotContain(run.Warnings, w => w.Contains("no longer corroborate it"));
            Assert.True(File.Exists(temp), "a running export's temp was unlinked under it");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// The same helper, reached the same way, from the other cmdlet. A sync has one thing more
    /// to lose: taking the live run's temp and then resuming past its items advances the delta
    /// token over changes that are in no file this run can produce, and a delta token that has
    /// moved cannot be asked for them again. Re-enumerating instead moves it just the same, one
    /// completed page later, so the run ends before either.
    /// </summary>
    [Fact]
    public void A_sync_does_not_promote_a_temp_a_running_sync_still_holds()
    {
        var dir = NewDir();
        var delta = Path.Combine(dir, "state.json");
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // A baseline sync, then one that dies after its first page of changes.
            handler.Queue(HttpStatusCode.OK, Baseline);
            Sync(delta, checkpoint, output);
            handler.Queue(HttpStatusCode.OK, ChangesPage1);
            Sync(delta, checkpoint, output);
            var temp = Assert.Single(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}"], File.ReadAllLines(temp));

            using var live = HoldLikeALiveRun(temp);

            // The overlapping sync, which re-enumerates from the delta token instead.
            handler.Queue(HttpStatusCode.OK, ChangesPage1);
            handler.Queue(HttpStatusCode.OK, ChangesPage2);
            var second = Sync(delta, checkpoint, output);

            Assert.True(File.Exists(temp), "a running sync's temp was unlinked under it");
            Assert.DoesNotContain(second.Warnings, w => w.Contains("Recovered"));
            var stop = Assert.Single(second.Errors);
            Assert.StartsWith("CheckpointTempHeld", stop.FullyQualifiedErrorId,
                StringComparison.Ordinal);
            Assert.Contains("Another sync is still writing the temp file", stop.Exception.Message);
            Assert.Contains("advance the delta token past them", stop.Exception.Message);
            Assert.DoesNotContain("cannot be opened for writing by this account",
                stop.Exception.Message);

            // Nothing of the second run's is on disk: the output is still the baseline's, and
            // the token is still the one the interrupted run was working from.
            Assert.Equal(["{\"id\":\"a1\"}", "{\"id\":\"a2\"}"], File.ReadAllLines(output));

            live.WriteLine("{\"id\":\"b3-live\"}");
            live.Flush();
            live.Dispose();
            Assert.Equal(
                ["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3-live\"}"],
                File.ReadAllLines(temp));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// Takes write access away from a file the way each platform expresses it. Unix mode bits
    /// and a Windows deny entry are different mechanisms and neither exists on the other host,
    /// so the choice is made per OS and each branch executes on the host it is for.
    /// <para>
    /// The read-only attribute refuses the open the same way and is not the form used here,
    /// because it refuses the unlink as well: one of these cases is a sweep that takes the name
    /// back, and under the attribute the copy outlived it. A deny entry for the account the
    /// tests run as refuses reading and writing the file and says nothing about deleting it,
    /// which is the directory's to grant - so the entry the run meets is a file it cannot open
    /// and can still take off the name, which is what mode 000 is on the other host.
    /// </para>
    /// </summary>
    private static void MakeUnwritable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // The premise itself, so a run that then opened the file would be measuring nothing.
            Assert.Equal(0, Icacls(path, "/deny", $"{Environment.UserName}:(R,W)"));
        }
        else
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    /// <summary>Puts it back, so the directory can be removed on either platform.</summary>
    private static void MakeWritable(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            if (OperatingSystem.IsWindows())
                Icacls(path, "/remove:d", Environment.UserName);
            else
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>
    /// One icacls call over the file, waited for, with its exit code for the caller to read.
    /// .NET's own access-control types are Windows-only and this assembly is built for a target
    /// that has none of them, so the platform's own command is what edits an entry here.
    /// </summary>
    private static int Icacls(string path, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("icacls")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(path);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var icacls = System.Diagnostics.Process.Start(start);
        Assert.NotNull(icacls);
        icacls.WaitForExit();
        return icacls.ExitCode;
    }

    /// <summary>
    /// Whether this account can still open the file read-write after that. CI runs as an
    /// ordinary user and cannot, which is the case these tests are about; a root shell opens a
    /// 0444 file and reaches the ordinary recovery instead. Both answers are asserted, so the
    /// test executes and counts whichever host it lands on rather than being skipped on one.
    /// </summary>
    private static bool StillOpenableReadWrite(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// The other way the exclusive claim fails, which the refusal used to report as the first.
    /// A temp this account cannot open read-write - mode bits, an ACL - stops the run for the
    /// same reason and leaves the same two files behind, but there is no second export anywhere:
    /// the sentence saying another run was still writing it sent the caller looking for
    /// something that does not exist instead of at the file's permissions.
    /// </summary>
    [Fact]
    public void An_export_names_a_temp_it_cannot_open_as_a_permissions_problem()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        string? temp = null;
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            handler.Queue(HttpStatusCode.OK, Page1);
            Export(output, checkpoint);
            temp = Assert.Single(Directory.GetFiles(dir, "out.jsonl.*.tmp"));

            MakeUnwritable(temp);
            var openable = StillOpenableReadWrite(temp);

            handler.Queue(HttpStatusCode.OK, Page1);
            handler.Queue(HttpStatusCode.OK, Page2);
            var second = Export(output, checkpoint);

            if (openable)
            {
                // Running as root: the claim succeeds and this is the ordinary resume.
                Assert.Contains(second.Warnings, w =>
                    w.Contains("Recovered 2 items from an interrupted export's temp file"));
            }
            else
            {
                var stop = Assert.Single(second.Errors);
                Assert.StartsWith("CheckpointTempUnopenable", stop.FullyQualifiedErrorId,
                    StringComparison.Ordinal);
                Assert.Contains("cannot be opened for writing by this account",
                    stop.Exception.Message);
                Assert.Contains(Path.GetFileName(temp), stop.Exception.Message);
                Assert.Contains("Grant write access to that file and run again",
                    stop.Exception.Message);
                Assert.DoesNotContain("Another export is still writing the temp file",
                    stop.Exception.Message);
                Assert.True(File.Exists(temp), "the refused temp was swept by the run that stopped");
                Assert.False(File.Exists(output), "the stopped run published an output");
            }
        }
        finally
        {
            if (temp != null) MakeWritable(temp);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The same distinction from the other cmdlet, which reaches the same claim. A sync stops
    /// either way; what it can honestly say about the cause is what differs.
    /// </summary>
    [Fact]
    public void A_sync_names_a_temp_it_cannot_open_as_a_permissions_problem()
    {
        var dir = NewDir();
        var delta = Path.Combine(dir, "state.json");
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        string? temp = null;
        try
        {
            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);

            handler.Queue(HttpStatusCode.OK, Baseline);
            Sync(delta, checkpoint, output);
            handler.Queue(HttpStatusCode.OK, ChangesPage1);
            Sync(delta, checkpoint, output);
            temp = Assert.Single(Directory.GetFiles(dir, "out.jsonl.*.tmp"));

            MakeUnwritable(temp);
            var openable = StillOpenableReadWrite(temp);

            handler.Queue(HttpStatusCode.OK, ChangesPage1);
            handler.Queue(HttpStatusCode.OK, ChangesPage2);
            var second = Sync(delta, checkpoint, output);

            if (openable)
            {
                Assert.Contains(second.Warnings, w =>
                    w.Contains("Recovered 2 items from an interrupted sync's temp file"));
            }
            else
            {
                var stop = Assert.Single(second.Errors);
                Assert.StartsWith("CheckpointTempUnopenable", stop.FullyQualifiedErrorId,
                    StringComparison.Ordinal);
                Assert.Contains("cannot be opened for writing by this account",
                    stop.Exception.Message);
                Assert.Contains(Path.GetFileName(temp), stop.Exception.Message);
                Assert.Contains("Grant write access to that file and run again",
                    stop.Exception.Message);
                Assert.DoesNotContain("Another sync is still writing the temp file",
                    stop.Exception.Message);
                Assert.True(File.Exists(temp), "the refused temp was swept by the run that stopped");
            }
        }
        finally
        {
            if (temp != null) MakeWritable(temp);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The sweep's own account of a temp it passed over. It asks for the file exclusively and
    /// leaves what it cannot claim, which is right for both ways the claim fails and honest
    /// about only one of them: an orphan this account cannot open read-write has no second run
    /// behind it, and calling it one sent the caller looking for an export that is not there.
    ///
    /// Which of the second kind it is is the open's own answer and not the line's to guess. Said
    /// as "the open was refused on permissions, not on sharing", the same sentence went out for
    /// a directory, for a pipe with no length to write at and for a path too long for the
    /// volume - and a caller who went and granted write access to the file found nothing had
    /// changed. The reason the open gave is quoted instead.
    ///
    /// No checkpoint here, so no resume is pending and the sweep runs over every temp beside
    /// the output. The file is left where it is either way; only the sentence differs.
    /// </summary>
    [Fact]
    public void The_sweep_names_a_temp_it_cannot_open_by_the_reason_the_open_gave()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        var orphan = Path.Combine(dir, $"out.jsonl.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllLines(orphan, ["{\"id\":\"ORPHAN-1\"}"]);
            MakeUnwritable(orphan);
            var openable = StillOpenableReadWrite(orphan);

            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);
            handler.Queue(HttpStatusCode.OK, Page1);
            handler.Queue(HttpStatusCode.OK, Page2);

            var verbose = ExportVerbose(output, checkpoint);

            if (openable)
            {
                // Running as root: the claim succeeds and the file is an ordinary orphan.
                Assert.Contains(verbose, v =>
                    v.Contains("Deleted an orphaned temp file")
                    && v.Contains(Path.GetFileName(orphan)));
            }
            else
            {
                var line = Assert.Single(verbose, v => v.Contains(Path.GetFileName(orphan)));
                Assert.Contains("this run cannot open it for writing - ", line);
                Assert.DoesNotContain("not on sharing", line);
                Assert.DoesNotContain("another run is writing to it", line);
                // The reason the open gave, quoted, and it names the file it was refused on.
                Assert.Contains(orphan, line[line.IndexOf(" - ", StringComparison.Ordinal)..]);
                Assert.True(File.Exists(orphan), "the sweep deleted a temp it could not claim");
            }

            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(output));
        }
        finally
        {
            MakeWritable(orphan);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // Reads a file the test's own live writer still holds open: on Windows the reader
    // must offer ReadWrite sharing or the holder's write access denies the open.
    private static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// A FIFO at <paramref name="path"/>, which .NET has no call for.
    /// </summary>
    private static void Mkfifo(string path)
    {
        using var mkfifo = System.Diagnostics.Process.Start("/usr/bin/mkfifo", [path]);
        Assert.NotNull(mkfifo);
        mkfifo.WaitForExit();
        Assert.Equal(0, mkfifo.ExitCode);
    }

    /// <summary>
    /// The staging name in the stale-temp sweep, which reached it with the claim-then-delete a
    /// temp takes. That claim answers for a regular file and for nothing else: a pipe and a
    /// socket came back unopenable and were left standing - under a sentence calling them a file
    /// this account could not open - and a dangling link was left as well, so the next
    /// promotion met exactly the entry its own sweep exists to clear. Both go now, through that
    /// sweep, and the run says which kind it took.
    /// </summary>
    [Theory]
    [InlineData("pipe")]
    [InlineData("dangling link")]
    public void The_sweep_clears_what_stands_at_the_staging_name(string kind)
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var adopt = $"{output}.adopt";
        try
        {
            if (kind == "pipe") Mkfifo(adopt);
            else File.CreateSymbolicLink(adopt, Path.Combine(dir, "nothing-is-here"));

            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);
            handler.Queue(HttpStatusCode.OK, Page1);
            handler.Queue(HttpStatusCode.OK, Page2);

            var verbose = ExportVerbose(output, Path.Combine(dir, "run.checkpoint"));

            Assert.Contains(verbose, v => v.Contains(
                $"Removed what stood at the staging name '{Path.GetFileName(adopt)}': "
                + (kind == "pipe"
                    ? "an entry no copy could be staged in, such as a pipe or a socket."
                    : "a link, and not what it pointed at."), StringComparison.Ordinal));
            // File.Exists answers for a pipe and for a dangling link alike on this platform, so
            // it is what says the entry itself is gone rather than what it pointed at.
            Assert.False(File.Exists(adopt), "the entry outlived the sweep");
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(output));
        }
        finally
        {
            try { File.Delete(adopt); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// What the sweep of the staging name calls a regular file it cannot open. The only kinds it
    /// told apart were a link and a copy nothing held, so a 0444 or a mode-000 file - the claim
    /// refused, the unlink allowed by the directory it sits in - was reported as "an entry no
    /// copy could be staged in, such as a pipe or a socket": a kind of entry it is not, and one
    /// the caller cannot have made by the means they actually used.
    /// </summary>
    [Fact]
    public void The_sweep_calls_a_staged_copy_it_cannot_open_what_it_is()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var adopt = $"{output}.adopt";
        try
        {
            File.WriteAllText(adopt, "{\"id\":\"half-staged\"}\n");
            MakeUnwritable(adopt);
            var openable = StillOpenableReadWrite(adopt);

            var handler = new ScriptedHandler();
            using var transport = MgxTransportScope.Inject(handler);
            handler.Queue(HttpStatusCode.OK, Page1);
            handler.Queue(HttpStatusCode.OK, Page2);

            var verbose = ExportVerbose(output, Path.Combine(dir, "run.checkpoint"));

            // Running as root the claim succeeds and it is an ordinary leftover copy.
            Assert.Contains(verbose, v => v.Contains(
                $"Removed what stood at the staging name '{Path.GetFileName(adopt)}': "
                + (openable
                    ? "a copy left by an interrupted promotion."
                    : "a copy this account cannot open."), StringComparison.Ordinal));
            Assert.DoesNotContain(verbose, v => v.Contains("such as a pipe or a socket"));
            Assert.False(File.Exists(adopt), "the copy outlived the sweep");
        }
        finally
        {
            MakeWritable(adopt);
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
