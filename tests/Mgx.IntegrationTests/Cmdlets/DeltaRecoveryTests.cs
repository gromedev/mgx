using System.Management.Automation;
using System.Net;
using Mgx.Cmdlets.Base;
using Mgx.Engine.Http;
using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests;

/// <summary>
/// End-to-end recovery for a JSONL delta sync, driven through the cmdlet against a mock
/// transport. OrphanAdoptionTests and CheckpointRetentionTests call TryAdoptOrphanedTemp
/// directly; both shipped bugs in this area lived in the CALLER, in the interaction between
/// adoption, the checkpoint-deletion branch and the temp-vs-direct write decision, so the
/// state machine needs coverage that actually runs it.
///
/// The invariant under test: a checkpoint that survives a run must never describe items that
/// exist nowhere. Once the delta token advances there is no re-fetch, so an item skipped here
/// is gone permanently.
/// </summary>
[Collection("Pipeline")]
public class DeltaRecoveryTests
{
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

    private static PowerShell Shell()
    {
        var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Delta.SyncMgxDelta).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        ps.AddScript("function Get-MgContext { [PSCustomObject]@{ TenantId = 'test-tenant-00000000-0000-0000-0000-000000000000' } }");
        ps.Invoke();
        ps.Commands.Clear();
        return ps;
    }

    private static void Sync(string deltaPath, string checkpointPath, string outputPath)
    {
        using var ps = Shell();
        ps.AddCommand("Sync-MgxDelta")
          .AddParameter("Uri", "/users/delta")
          .AddParameter("DeltaPath", deltaPath)
          .AddParameter("CheckpointPath", checkpointPath)
          .AddParameter("OutputFile", outputPath);
        // A run that dies is the subject here, so a terminating error is an expected outcome.
        try { ps.Invoke(); }
        catch (System.Management.Automation.CmdletInvocationException) { }
    }

    /// <summary>
    /// The steady-state failure: a completed run, then a run that dies on a transient 500
    /// partway through. The dead run's temp holds the pages the checkpoint counts. If that
    /// temp is discarded, the next run sees checkpoint + output + no temp - which is the
    /// ROUTINE "nothing to promote" state - resumes in append mode, and the token advances
    /// past pages that were never written anywhere. And recovery must land on what a clean
    /// run 2 would have produced: promotion replaces the completed run's rows, which the
    /// caller has already consumed, rather than appending behind them.
    /// </summary>
    [Fact]
    public void A_transient_error_midrun_does_not_strand_the_pages_the_checkpoint_counts()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-recovery-{Guid.NewGuid():N}")).FullName;
        var deltaPath = Path.Combine(dir, "state.json");
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var outputPath = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, Baseline);                        // run 1
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);                    // run 2, page 1
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);    // run 2, page 2
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);    // ... and its retry
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);                    // run 3 resumes

        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            Sync(deltaPath, checkpointPath, outputPath);
            Assert.Equal(["{\"id\":\"a1\"}", "{\"id\":\"a2\"}"], File.ReadAllLines(outputPath));
            Assert.False(File.Exists(checkpointPath), "a completed run deletes its checkpoint");

            Sync(deltaPath, checkpointPath, outputPath);
            var checkpoint = PaginationCheckpoint.Load(checkpointPath);
            Assert.NotNull(checkpoint);
            Assert.Equal(2, checkpoint.ItemsCollected);
            Assert.Contains("skiptoken=B2", checkpoint.NextLink);
            // b1 and b2 are counted by the checkpoint but are not in the output, so they must
            // still be somewhere: the temp is the only place they can be.
            Assert.Single(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
            Assert.Equal(
                ["{\"id\":\"b1\"}", "{\"id\":\"b2\"}"],
                File.ReadAllLines(Directory.GetFiles(dir, "out.jsonl.*.tmp")[0]));

            Sync(deltaPath, checkpointPath, outputPath);
            Assert.Equal(
                ["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                File.ReadAllLines(outputPath));
            Assert.Contains("$deltatoken=D2", DeltaState.Load(deltaPath)!.DeltaLink);
            Assert.False(File.Exists(checkpointPath));
            Assert.Empty(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// The semantics every recovery path has to agree with: a completed sync's output is that
    /// sync's changes alone. A fresh run writes to a temp and moves it over the output, so the
    /// previous sync's rows - already consumed by whatever reads the file - do not accumulate.
    /// </summary>
    [Fact]
    public void A_completed_sync_replaces_the_previous_syncs_output()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-recovery-{Guid.NewGuid():N}")).FullName;
        var deltaPath = Path.Combine(dir, "state.json");
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var outputPath = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, Baseline);        // run 1: a1,a2
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);    // run 2: b1,b2
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);    // run 2: b3

        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            Sync(deltaPath, checkpointPath, outputPath);
            Assert.Equal(["{\"id\":\"a1\"}", "{\"id\":\"a2\"}"], File.ReadAllLines(outputPath));

            Sync(deltaPath, checkpointPath, outputPath);
            Assert.Equal(
                ["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                File.ReadAllLines(outputPath));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// A checkpoint that predates the recorded temp name and length cannot say which file its
    /// items are in, so against an existing output it must not guess: an appending run's items
    /// are in the output, a fresh run's are in a temp, and merging the wrong one either repeats
    /// rows or hands a stale file to the resume. Re-enumerating from the token loses nothing.
    /// </summary>
    [Fact]
    public void A_checkpoint_that_does_not_name_its_file_reenumerates_rather_than_guessing()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-recovery-{Guid.NewGuid():N}")).FullName;
        var deltaPath = Path.Combine(dir, "state.json");
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var outputPath = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, Baseline);                        // run 1
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);                    // run 2, page 1
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);    // run 2 dies
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);                    // run 3 re-enumerates
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);

        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            Sync(deltaPath, checkpointPath, outputPath);
            Sync(deltaPath, checkpointPath, outputPath);

            // The same position as recorded, minus the fields that locate the items.
            var checkpoint = PaginationCheckpoint.Load(checkpointPath)!;
            checkpoint.TempFile = null;
            checkpoint.DataLength = null;
            checkpoint.Save(checkpointPath);
            var before = handler.Requests.Count;

            Sync(deltaPath, checkpointPath, outputPath);

            Assert.Contains("$deltatoken=D1", handler.Requests[before].RequestUri!.ToString());
            Assert.Equal(
                ["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                File.ReadAllLines(outputPath));
            Assert.Contains("$deltatoken=D2", DeltaState.Load(deltaPath)!.DeltaLink);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// The same failure with no output from an earlier run. Here the checkpoint IS stale in the
    /// sense its warning means - the output is genuinely missing - but the temp still holds the
    /// items, so adoption creates the output from it rather than the checkpoint being dropped.
    /// </summary>
    [Fact]
    public void A_first_run_that_dies_midway_still_recovers_its_pages()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-recovery-{Guid.NewGuid():N}")).FullName;
        var deltaPath = Path.Combine(dir, "state.json");
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var outputPath = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);

        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            Sync(deltaPath, checkpointPath, outputPath);
            Assert.False(File.Exists(outputPath), "a fresh run promotes its temp only on success");
            Assert.True(File.Exists(checkpointPath));

            Sync(deltaPath, checkpointPath, outputPath);
            Assert.Equal(
                ["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                File.ReadAllLines(outputPath));
            Assert.Contains("$deltatoken=D2", DeltaState.Load(deltaPath)!.DeltaLink);
            Assert.Empty(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// The same guard through the sync's own adoption call. A checkpoint too old to record a
    /// length leaves adoption a line count and nothing else, and a temp cut inside the row that
    /// count reaches hands the fragment back as a line like any other - so it went into the
    /// JSONL output as a change, was reported as a recovery, and the rest of the enumeration
    /// was appended behind it. Nothing is lost by refusing: the delta token has not moved.
    /// </summary>
    [Fact]
    public void A_sync_does_not_adopt_a_temp_cut_inside_the_row_the_checkpoint_counts()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-recovery-torn-{Guid.NewGuid():N}")).FullName;
        var deltaPath = Path.Combine(dir, "state.json");
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var outputPath = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            // A first sync collects page one into its temp and dies on page two.
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            Sync(deltaPath, checkpointPath, outputPath);
            var checkpoint = PaginationCheckpoint.Load(checkpointPath)!;
            var temp = Path.Combine(dir, checkpoint.TempFile!);

            // The same position as a release that recorded no length left it, and a temp that
            // lost its tail after the two rows the checkpoint counts had been written.
            File.WriteAllText(temp, "{\"id\":\"b1\"}\n{\"id\":\"b");
            checkpoint.TempFile = null;
            checkpoint.DataLength = null;
            checkpoint.Save(checkpointPath);

            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            Sync(deltaPath, checkpointPath, outputPath);

            var lines = File.ReadAllLines(outputPath);
            foreach (var line in lines)
                System.Text.Json.JsonDocument.Parse(line).Dispose();
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"], lines);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// -Latest means "baseline from now". After an interrupted enumeration that is the same
    /// catastrophe the code already guards against after a state discard: the collected items
    /// are abandoned and the token jumps past every change that preceded it, with no way back.
    /// Without delta state on disk none of the existing guards fire, so the checkpoint is the
    /// only signal that this is not a fresh run.
    /// </summary>
    [Fact]
    public void Latest_is_ignored_while_a_resume_checkpoint_exists()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-recovery-{Guid.NewGuid():N}")).FullName;
        var deltaPath = Path.Combine(dir, "state.json");
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var outputPath = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);                    // run 1, page 1
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);    // run 1, page 2
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);    // ... and its retry
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);                    // run 2 resumes

        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            Sync(deltaPath, checkpointPath, outputPath);
            Assert.True(File.Exists(checkpointPath));
            Assert.False(File.Exists(deltaPath), "run 1 never completed, so no token was saved");

            using (var ps = Shell())
            {
                ps.AddCommand("Sync-MgxDelta").AddParameter("Uri", "/users/delta")
                  .AddParameter("DeltaPath", deltaPath).AddParameter("CheckpointPath", checkpointPath)
                  .AddParameter("OutputFile", outputPath).AddParameter("Latest", true);
                ps.Invoke();
                Assert.Contains(ps.Streams.Warning, w => w.Message.Contains("-Latest ignored"));
            }

            // The interrupted enumeration finished instead of being baselined away.
            Assert.Equal(
                ["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                File.ReadAllLines(outputPath));
            var token = DeltaState.Load(deltaPath)!.DeltaLink;
            Assert.Contains("$deltatoken=D2", token);
            Assert.DoesNotContain("token=latest", token);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// A resume appends into the output itself, so a denying ACL or read-only bit there is this
    /// run's problem before any request is made: the claim on the file the checkpoint counts its
    /// items into fails on permissions, and the run ends on an error record naming that file and
    /// the two ways out of it. What it may never be is an unhandled failure -
    /// UnauthorizedAccessException does not derive from IOException, and a handler catching only
    /// the latter let an ordinary permission problem out of the cmdlet as one.
    /// </summary>
    [Fact]
    public void A_denied_output_ends_the_resume_as_an_error_record_not_an_unhandled_failure()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-recovery-{Guid.NewGuid():N}")).FullName;
        var deltaPath = Path.Combine(dir, "state.json");
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var outputPath = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, Baseline);                        // run 1
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);                    // run 2, page 1
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);    // run 2 dies
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);                    // run 3 resumes

        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            Sync(deltaPath, checkpointPath, outputPath);
            Sync(deltaPath, checkpointPath, outputPath);

            // The shape a promoted cancellation leaves: the checkpoint points at the output
            // itself, so the resume opens that file directly rather than merging a temp over
            // it - a merge's rename would replace the file, permissions and all.
            var checkpoint = PaginationCheckpoint.Load(checkpointPath)!;
            checkpoint.TempFile = null;
            checkpoint.DataLength = new FileInfo(outputPath).Length;
            checkpoint.Save(checkpointPath);

            if (OperatingSystem.IsWindows())
                File.SetAttributes(outputPath, File.GetAttributes(outputPath) | FileAttributes.ReadOnly);
            else
                File.SetUnixFileMode(outputPath, UnixFileMode.UserRead);

            using var ps = Shell();
            ps.AddCommand("Sync-MgxDelta")
              .AddParameter("Uri", "/users/delta")
              .AddParameter("DeltaPath", deltaPath)
              .AddParameter("CheckpointPath", checkpointPath)
              .AddParameter("OutputFile", outputPath);
            // Only the wrapper a terminating error arrives in is caught here. Any other type
            // escaping this call is the unhandled failure this is about, and fails the test.
            List<ErrorRecord> errors = [];
            try { ps.Invoke(); }
            catch (CmdletInvocationException ex) { errors.Add(ex.ErrorRecord); }
            errors.AddRange(ps.Streams.Error);

            var stop = Assert.Single(errors);
            Assert.StartsWith("CheckpointOutputUnopenable", stop.FullyQualifiedErrorId,
                StringComparison.Ordinal);
            Assert.Equal(ErrorCategory.PermissionDenied, stop.CategoryInfo.Category);
            Assert.Contains($"'{outputPath}' could not be opened for writing:",
                stop.Exception.Message);
            Assert.Contains(
                "Grant write access to that file and run again to resume from it, or remove it "
                + "and the checkpoint to sync afresh.",
                stop.Exception.Message);
        }
        finally
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    File.SetAttributes(outputPath, File.GetAttributes(outputPath) & ~FileAttributes.ReadOnly);
                else
                    File.SetUnixFileMode(outputPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A run that dies before any page boundary leaves no checkpoint, so there is nothing to
    /// resume and nothing the temp is needed for. It must not be left behind: a stale temp is
    /// adoptable by a later run whose checkpoint happens to match the output name.
    /// </summary>
    [Fact]
    public void A_run_that_dies_with_no_checkpoint_leaves_no_temp()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-recovery-{Guid.NewGuid():N}")).FullName;
        var deltaPath = Path.Combine(dir, "state.json");
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var outputPath = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);

        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            Sync(deltaPath, checkpointPath, outputPath);
            Assert.False(File.Exists(checkpointPath), "no page completed, so no position to save");
            Assert.Empty(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
            Assert.False(File.Exists(outputPath));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// The escape hatch the ignored--Latest warning names has to work: -FullSync discards the
    /// enumeration outright, checkpoint included, so there is nothing left to protect and
    /// re-baselining from now is exactly what was asked for.
    /// </summary>
    [Fact]
    public void FullSync_with_Latest_still_baselines_from_now_despite_a_checkpoint()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-recovery-{Guid.NewGuid():N}")).FullName;
        var deltaPath = Path.Combine(dir, "state.json");
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var outputPath = Path.Combine(dir, "out.jsonl");

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
        handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
        handler.QueueResponse(HttpStatusCode.OK,
            """{"value":[],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/users/delta?$deltatoken=FROMNOW"}""");

        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            Sync(deltaPath, checkpointPath, outputPath);
            Assert.True(File.Exists(checkpointPath));

            using (var ps = Shell())
            {
                ps.AddCommand("Sync-MgxDelta").AddParameter("Uri", "/users/delta")
                  .AddParameter("DeltaPath", deltaPath).AddParameter("CheckpointPath", checkpointPath)
                  .AddParameter("OutputFile", outputPath)
                  .AddParameter("FullSync", true).AddParameter("Latest", true);
                ps.Invoke();
                Assert.DoesNotContain(ps.Streams.Warning, w => w.Message.Contains("-Latest ignored"));
            }

            Assert.Contains("$deltatoken=FROMNOW", DeltaState.Load(deltaPath)!.DeltaLink);
            Assert.Contains("deltatoken=latest", handler.Requests[^1].RequestUri!.ToString());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// A temp file carries no identity: TryAdoptOrphanedTemp globs "{output}.*.tmp", takes the
    /// newest, and checks only that it has enough lines. The Resource check added in 48ffe87
    /// validates the CHECKPOINT, so a CURRENT checkpoint plus a temp orphaned by an unrelated
    /// run still merges that run's rows into a healthy output - and one success makes them
    /// permanent. -FullSync and the -Property/-Filter/-Prefer discards all leave such orphans.
    /// A fresh run has nothing to resume, so every leftover temp is an orphan and must go.
    /// </summary>
    [Fact]
    public void A_fresh_run_clears_temps_orphaned_by_an_earlier_enumeration()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-recovery-{Guid.NewGuid():N}")).FullName;
        var deltaPath = Path.Combine(dir, "state.json");
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var outputPath = Path.Combine(dir, "out.jsonl");

        // An orphan from an enumeration that no longer exists, named the way a run names its
        // own temp, and long enough to satisfy any later checkpoint's line count.
        var foreign = $"{outputPath}.deadbeefdeadbeefdeadbeefdeadbeef.tmp";
        File.WriteAllLines(foreign, Enumerable.Range(0, 50).Select(i => $"{{\"id\":\"foreign{i}\"}}"));

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, Baseline);

        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            Sync(deltaPath, checkpointPath, outputPath);

            Assert.False(File.Exists(foreign), "the orphan must not survive a fresh run");
            Assert.Empty(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
            var lines = File.ReadAllLines(outputPath);
            Assert.Equal(["{\"id\":\"a1\"}", "{\"id\":\"a2\"}"], lines);
            Assert.DoesNotContain(lines, l => l.Contains("foreign"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// Resuming from a mid-page checkpoint must land exactly on the seam. PageItemsAlreadyWritten
    /// counts every item of the in-flight page that is already in the output, including the ones
    /// an earlier resume skipped rather than wrote, so the count and the file agree.
    /// </summary>
    [Fact]
    public void Resuming_from_a_midpage_checkpoint_repeats_no_items()
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-recovery-{Guid.NewGuid():N}")).FullName;
        var deltaPath = Path.Combine(dir, "state.json");
        var checkpointPath = Path.Combine(dir, "run.checkpoint");
        var outputPath = Path.Combine(dir, "out.jsonl");
        var page2Url = "https://graph.microsoft.com/v1.0/users/delta?$skiptoken=P2";

        // Stopped at totalProcessed = 1000: all 999 of page 1, plus item 0 of page 2.
        var written = Enumerable.Range(0, 999).Select(i => $"{{\"id\":\"p{i}\"}}")
            .Concat(["{\"id\":\"q0\"}"]).ToArray();
        File.WriteAllLines(outputPath, written);
        new PaginationCheckpoint
        {
            Resource = "https://graph.microsoft.com/v1.0/users/delta?$top=999",
            NextLink = page2Url,
            ItemsCollected = 1000,
            PageItemsAlreadyWritten = 1,
            TempFile = null,
            DataLength = new FileInfo(outputPath).Length,
        }.Save(checkpointPath);

        var items = Enumerable.Range(0, 999).Select(i => $"{{\"id\":\"q{i}\"}}");
        var page2 = "{\"value\":[" + string.Join(",", items)
            + "],\"@odata.deltaLink\":\"https://graph.microsoft.com/v1.0/users/delta?$deltatoken=D9\"}";

        var handler = new MockHttpHandler();
        handler.QueueResponse(HttpStatusCode.OK, page2);

        using var transport = MgxTransportScope.Inject(handler);
        try
        {
            Sync(deltaPath, checkpointPath, outputPath);

            var lines = File.ReadAllLines(outputPath);
            Assert.Equal(999 + 999, lines.Length);
            Assert.Equal(lines.Length, lines.Distinct().Count());
            Assert.Equal("{\"id\":\"q0\"}", lines[999]);
            Assert.Equal("{\"id\":\"q1\"}", lines[1000]);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
