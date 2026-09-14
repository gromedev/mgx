using System.Management.Automation;
using System.Net;
using Mgx.Cmdlets.Base;
using Mgx.Engine.Http;
using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests;

/// <summary>
/// A checkpoint is a promise that ItemsCollected items are durably somewhere. Every test here
/// breaks that promise a different way and asserts the sync notices. Once the delta token
/// advances there is no re-fetch, so an item skipped here is gone permanently.
/// </summary>
[Collection("Pipeline")]
public class DeltaCheckpointIntegrityTests
{
    private const string DeltaLink1 = "https://graph.microsoft.com/v1.0/users/delta?$deltatoken=D1";
    private const string Page2Url = "https://graph.microsoft.com/v1.0/users/delta?$skiptoken=B2";

    private const string ChangesPage1 = """
    {"value":[{"id":"b1"},{"id":"b2"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users/delta?$skiptoken=B2"}
    """;
    private const string ChangesPage2 = """
    {"value":[{"id":"b3"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users/delta?$skiptoken=B3"}
    """;
    private const string ChangesPage3 = """
    {"value":[{"id":"b4"}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/users/delta?$deltatoken=D2"}
    """;
    private const string ChangesPage2Final = """
    {"value":[{"id":"b3"}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/users/delta?$deltatoken=D2"}
    """;
    private const string ServerError = """{"error":{"code":"InternalServerError","message":"boom"}}""";

    private const string EmptyChangesPage = """
    {"value":[],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users/delta?$skiptoken=B3"}
    """;
    private const string ChangesPage3Linked = """
    {"value":[{"id":"b4"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users/delta?$skiptoken=B4"}
    """;
    private const string EmptyFinalPage = """
    {"value":[],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/users/delta?$deltatoken=D2"}
    """;

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

    /// <summary>
    /// The shape a delta enumeration is full of: pages behind nextLinks with nothing in them,
    /// including the last one before the deltaLink. PageIterator raises its empty-page limit to
    /// 1000 for exactly this, so the sync walks them rather than giving up. Every answer takes
    /// a real asynchronous hop - a socket read always yields and Task.FromResult never does,
    /// and the thread the iterator resumes on is what these pages are here to settle.
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

            var body = url.Contains("skiptoken=B4", StringComparison.Ordinal) ? EmptyFinalPage
                : url.Contains("skiptoken=B3", StringComparison.Ordinal) ? ChangesPage3Linked
                : url.Contains("skiptoken=B2", StringComparison.Ordinal) ? EmptyChangesPage
                : ChangesPage1;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed record Env(string Dir, string DeltaPath, string CheckpointPath, string OutputPath);

    private static Env NewEnv(string checkpointName = "run.checkpoint")
    {
        var dir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-integrity-{Guid.NewGuid():N}")).FullName;
        var env = new Env(dir,
            Path.Combine(dir, "state.json"),
            Path.Combine(dir, checkpointName),
            Path.Combine(dir, "out.jsonl"));
        // Delta state pins requestUrl to DeltaLink1 for every run below.
        new DeltaState
        {
            DeltaLink = DeltaLink1,
            Resource = "/users/delta",
            GraphEndpoint = "https://graph.microsoft.com",
            Select = "",
            ApiVersion = "v1.0",
        }.Save(env.DeltaPath);
        return env;
    }

    private static void Sync(Env env)
    {
        using var ps = Shell();
        ps.AddCommand("Sync-MgxDelta")
          .AddParameter("Uri", "/users/delta")
          .AddParameter("DeltaPath", env.DeltaPath)
          .AddParameter("CheckpointPath", env.CheckpointPath)
          .AddParameter("OutputFile", env.OutputPath);
        try { ps.Invoke(); }
        catch (CmdletInvocationException) { }
    }

    private static string[] Ids(string outputPath) =>
        File.Exists(outputPath) ? File.ReadAllLines(outputPath) : [];

    private static void SyncToPipeline(Env env)
    {
        using var ps = Shell();
        ps.AddCommand("Sync-MgxDelta")
          .AddParameter("Uri", "/users/delta")
          .AddParameter("DeltaPath", env.DeltaPath)
          .AddParameter("CheckpointPath", env.CheckpointPath);
        try { ps.Invoke(); }
        catch (CmdletInvocationException) { }
    }

    /// <summary>
    /// A sync whose warnings and error records the caller reads. Nothing is swallowed here: a
    /// run that dies off the pipeline thread has to fail the test that says it does not.
    /// </summary>
    private static (List<string> Warnings, int Errors) SyncReporting(Env env)
    {
        using var ps = Shell();
        ps.AddCommand("Sync-MgxDelta")
          .AddParameter("Uri", "/users/delta")
          .AddParameter("DeltaPath", env.DeltaPath)
          .AddParameter("CheckpointPath", env.CheckpointPath)
          .AddParameter("OutputFile", env.OutputPath);
        ps.Invoke();
        return ([.. ps.Streams.Warning.Select(w => w.Message)], ps.Streams.Error.Count);
    }

    /// <summary>A checkpoint torn between the write and the rename: no position in it at all.</summary>
    private const string TornCheckpoint =
        """{"resource":"https://graph.microsoft.com/v1.0/users/delta?$deltatoken=D1","nextLi""";

    // ---------------------------------------------------------------- D5

    /// <summary>
    /// A temp shorter than the checkpoint promises. Adoption declines, and because the output
    /// exists the checkpoint is kept and the run resumes at NextLink - past items that are in
    /// neither file. The token then advances over them.
    /// </summary>
    [Fact]
    public void A_torn_temp_does_not_let_the_token_advance_past_what_it_lost()
    {
        var env = NewEnv();
        try
        {
            var handler = new MockHttpHandler();
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);                  // run 1: b1,b2
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);                  // run 2 re-enumerates
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2Final);
            using var transport = MgxTransportScope.Inject(handler);

            Sync(env);
            var temp = Directory.GetFiles(env.Dir, "out.jsonl.*.tmp").Single();
            Assert.Equal(2, PaginationCheckpoint.Load(env.CheckpointPath)!.ItemsCollected);

            // The writer flushed two items; the machine lost power and only one reached the
            // platter. The checkpoint still promises two.
            File.WriteAllLines(temp, ["{\"id\":\"b1\"}"]);
            var before = handler.Requests.Count;

            Sync(env);

            // Resuming at NextLink would step over b2, which is now in no file at all.
            Assert.Contains("$deltatoken=D1", handler.Requests[before].RequestUri!.ToString());
            Assert.Equal(
                ["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                Ids(env.OutputPath));
        }
        finally { try { Directory.Delete(env.Dir, true); } catch { } }
    }

    // ---------------------------------------------------------------- D6

    /// <summary>
    /// PaginationCheckpoint.Save writes "{checkpointPath}.tmp" and renames. With a checkpoint
    /// named after the output - which the collision check permits, it only forbids equal paths -
    /// that staging file matches the output's own "{output}.*.tmp" glob, so a crash mid-save
    /// leaves a file that adoption will happily copy into the data.
    /// </summary>
    [Fact]
    public void The_checkpoints_own_staging_file_is_never_adopted_as_data()
    {
        // -CheckpointPath named after -OutputFile is permitted: the collision check only forbids
        // the two being the same file. PaginationCheckpoint.Save then stages through
        // "out.jsonl.ckpt.tmp", which matches the output's own "out.jsonl.*.tmp" glob.
        var env = NewEnv("out.jsonl.ckpt");
        try
        {
            var handler = new MockHttpHandler();
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2Final);
            using var transport = MgxTransportScope.Inject(handler);

            Sync(env);
            var temp = Directory.GetFiles(env.Dir, "out.jsonl.*.tmp")
                .Single(f => !f.EndsWith(".ckpt.tmp", StringComparison.Ordinal));

            // A save that died between writing the staging file and renaming it. It is newer
            // than the real temp, so newest-wins selection prefers it.
            var staged = $"{env.CheckpointPath}.tmp";
            File.WriteAllText(staged, File.ReadAllText(env.CheckpointPath));
            File.SetLastWriteTimeUtc(staged, File.GetLastWriteTimeUtc(temp).AddMinutes(5));

            Sync(env);

            Assert.Equal(
                ["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                Ids(env.OutputPath));
        }
        finally { try { Directory.Delete(env.Dir, true); } catch { } }
    }

    // ---------------------------------------------------------------- D7

    /// <summary>
    /// File.Exists is presence, not content. An output truncated to nothing between runs still
    /// reads as a valid baseline, so the run appends the tail to an empty file and the token
    /// advances over everything the checkpoint said was already there.
    /// </summary>
    [Fact]
    public void A_truncated_output_is_not_mistaken_for_the_baseline_it_describes()
    {
        var env = NewEnv();
        try
        {
            var handler = new MockHttpHandler();
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);                  // run 1: b1,b2
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);                  // run 2: adopts, adds b3
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);                  // run 3 re-enumerates
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage3);
            using var transport = MgxTransportScope.Inject(handler);

            Sync(env);
            Sync(env);
            // Run 2 resumed, so it wrote straight to the output and left no temp behind.
            Assert.Empty(Directory.GetFiles(env.Dir, "out.jsonl.*.tmp"));
            Assert.Equal(3, PaginationCheckpoint.Load(env.CheckpointPath)!.ItemsCollected);

            File.WriteAllText(env.OutputPath, "");   // emptied underneath the checkpoint
            var before = handler.Requests.Count;

            Sync(env);

            // Appending the tail to an empty file would advance the token over b1..b3.
            Assert.Contains("$deltatoken=D1", handler.Requests[before].RequestUri!.ToString());
            Assert.Equal(
                ["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}", "{\"id\":\"b4\"}"],
                Ids(env.OutputPath));
        }
        finally { try { Directory.Delete(env.Dir, true); } catch { } }
    }

    // ---------------------------------------------------------------- D8

    /// <summary>
    /// An interrupted run leaves the output ahead of its checkpoint: everything it wrote after
    /// the last save is on disk but uncounted. Those items are re-fetched on resume, so unless
    /// the output is cut back to the length the checkpoint recorded they are written twice.
    /// </summary>
    [Fact]
    public void Items_written_after_the_last_checkpoint_are_not_duplicated_on_resume()
    {
        var env = NewEnv();
        try
        {
            var handler = new MockHttpHandler();
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);                  // run 1: b1,b2
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2);                  // run 2: adopts, adds b3
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage3);                  // run 3: b4
            using var transport = MgxTransportScope.Inject(handler);

            Sync(env);
            Sync(env);
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"], Ids(env.OutputPath));

            // Run 2 got partway into page 3 before it died: b4 reached the file, the checkpoint
            // still ends at b3, and page 3 will be fetched again.
            File.AppendAllLines(env.OutputPath, ["{\"id\":\"b4\"}"]);

            Sync(env);

            var ids = Ids(env.OutputPath);
            Assert.Equal(
                ["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}", "{\"id\":\"b4\"}"],
                ids);
            Assert.Equal(ids.Length, ids.Distinct().Count());
        }
        finally { try { Directory.Delete(env.Dir, true); } catch { } }
    }

    // ---------------------------------------------------------------- D9

    /// <summary>
    /// Adoption ends in a move followed by a delete. A temp that outlives its own adoption -
    /// the delete failed, or the process died in between - must not be merged a second time.
    /// The run below fails before completing a page, so the only checkpoint on disk afterwards
    /// is the one adoption itself left; if that still names the temp, the next run adopts it again.
    /// </summary>
    [Fact]
    public void A_temp_that_survives_its_own_adoption_is_not_merged_twice()
    {
        var env = NewEnv();
        try
        {
            var handler = new MockHttpHandler();
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);                  // run 1: b1,b2
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);  // run 2: adopts, then dies
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2Final);             // run 3: b3
            using var transport = MgxTransportScope.Inject(handler);

            Sync(env);
            var temp = Directory.GetFiles(env.Dir, "out.jsonl.*.tmp").Single();
            var tempBytes = File.ReadAllBytes(temp);

            Sync(env);
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}"], Ids(env.OutputPath));
            Assert.True(File.Exists(env.CheckpointPath), "the run failed, so its position survives");

            // The delete never happened.
            File.WriteAllBytes(temp, tempBytes);

            Sync(env);

            Assert.Equal(
                ["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                Ids(env.OutputPath));
        }
        finally { try { Directory.Delete(env.Dir, true); } catch { } }
    }

    /// <summary>
    /// A checkpoint is read back from disk, so its contents are input, not a fact. One naming
    /// the output as the temp to adopt would have the file appended to itself and then removed.
    /// </summary>
    [Fact]
    public void A_checkpoint_naming_the_output_as_its_own_temp_does_not_consume_it()
    {
        var env = NewEnv();
        try
        {
            File.WriteAllLines(env.OutputPath, ["{\"id\":\"a1\"}", "{\"id\":\"a2\"}"]);
            new PaginationCheckpoint
            {
                Resource = DeltaLink1,
                NextLink = Page2Url,
                ItemsCollected = 2,
                TempFile = "out.jsonl",
                DataLength = new FileInfo(env.OutputPath).Length,
            }.Save(env.CheckpointPath);

            var handler = new MockHttpHandler();
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2Final);
            using var transport = MgxTransportScope.Inject(handler);

            Sync(env);

            Assert.True(File.Exists(env.OutputPath), "the output must survive");
            Assert.Equal(
                ["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                Ids(env.OutputPath));
        }
        finally { try { Directory.Delete(env.Dir, true); } catch { } }
    }

    /// <summary>
    /// The recorded temp name accepts only what a run could have written: the output's name,
    /// 32 hex digits, ".tmp". A checkpoint naming any other file - here, the checkpoint
    /// itself - would have that file's bytes copied into the output as data and the file
    /// deleted as the spent temp.
    /// </summary>
    [Fact]
    public void A_checkpoint_naming_a_file_that_is_not_a_temp_does_not_consume_it()
    {
        var env = NewEnv();
        try
        {
            File.WriteAllLines(env.OutputPath, ["{\"id\":\"a1\"}", "{\"id\":\"a2\"}"]);
            new PaginationCheckpoint
            {
                Resource = DeltaLink1,
                NextLink = Page2Url,
                ItemsCollected = 2,
                TempFile = Path.GetFileName(env.CheckpointPath),
                DataLength = 10, // shorter than the checkpoint file, so the length guard passes
            }.Save(env.CheckpointPath);

            var handler = new MockHttpHandler();
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2Final);
            using var transport = MgxTransportScope.Inject(handler);

            Sync(env);

            // The unusable name reads as "items are in no file": re-enumerate, output replaced.
            Assert.Equal(
                ["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                Ids(env.OutputPath));
        }
        finally { try { Directory.Delete(env.Dir, true); } catch { } }
    }

    // ---------------------------------------------------------------- D10

    /// <summary>
    /// Temp selection is newest-by-write-time with nothing tying a temp to the run that wrote it.
    /// A newer file from an unrelated run wins over the one the checkpoint is actually about.
    /// </summary>
    [Fact]
    public void Adoption_takes_the_temp_its_checkpoint_describes_not_the_newest_one()
    {
        var env = NewEnv();
        try
        {
            var handler = new MockHttpHandler();
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.InternalServerError, ServerError);
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2Final);
            using var transport = MgxTransportScope.Inject(handler);

            Sync(env);                                   // leaves its own temp with b1,b2
            var own = Directory.GetFiles(env.Dir, "out.jsonl.*.tmp").Single();

            // Something else drops a newer file matching the same glob.
            var decoy = $"{env.OutputPath}.zzzzzzzz.tmp";
            File.WriteAllLines(decoy, ["{\"id\":\"decoy1\"}", "{\"id\":\"decoy2\"}", "{\"id\":\"decoy3\"}"]);
            File.SetLastWriteTimeUtc(decoy, File.GetLastWriteTimeUtc(own).AddMinutes(5));

            Sync(env);

            var ids = Ids(env.OutputPath);
            Assert.DoesNotContain(ids, l => l.Contains("decoy"));
            Assert.Contains("{\"id\":\"b1\"}", ids);
            Assert.Contains("{\"id\":\"b2\"}", ids);
        }
        finally { try { Directory.Delete(env.Dir, true); } catch { } }
    }

    // ---------------------------------------------------------------- D11

    /// <summary>
    /// The item loop calls TryGetProperty("@removed") on every element. That throws
    /// InvalidOperationException on anything that is not a JSON object, which escapes as a
    /// terminating error naming neither the endpoint nor the item.
    /// </summary>
    [Fact]
    public void A_non_object_item_in_a_page_does_not_terminate_the_run()
    {
        var env = NewEnv();
        try
        {
            var handler = new MockHttpHandler();
            handler.QueueResponse(HttpStatusCode.OK,
                """{"value":[{"id":"b1"},"not-an-object",{"id":"b2"}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/users/delta?$deltatoken=D2"}""");
            using var transport = MgxTransportScope.Inject(handler);

            Exception? escaped = null;
            using (var ps = Shell())
            {
                ps.AddCommand("Sync-MgxDelta")
                  .AddParameter("Uri", "/users/delta")
                  .AddParameter("DeltaPath", env.DeltaPath)
                  .AddParameter("CheckpointPath", env.CheckpointPath)
                  .AddParameter("OutputFile", env.OutputPath);
                try { ps.Invoke(); }
                catch (Exception ex) { escaped = ex; }
            }

            Assert.Null(escaped);
            var ids = Ids(env.OutputPath);
            Assert.Contains("{\"id\":\"b1\"}", ids);
            Assert.Contains("{\"id\":\"b2\"}", ids);
        }
        finally { try { Directory.Delete(env.Dir, true); } catch { } }
    }

    // ---------------------------------------------------------------- D12

    /// <summary>
    /// A resumed run starts partway into a page, and PageIterator drops the skipped items before
    /// the consumer ever sees them. A mid-page checkpoint saved while still on that first page
    /// therefore has to count the skipped items as well as the new ones - it records a position
    /// in the page, not how much this run wrote. Counting only the new ones understates the
    /// output, and the next resume skips too few and writes the difference a second time.
    /// </summary>
    [Fact]
    public void A_second_interruption_on_a_resumed_page_does_not_duplicate_its_items()
    {
        const string RefusedLink = "https://not-graph.example.com/v1.0/users/delta?$skiptoken=P2";
        const string Page2Link = "https://graph.microsoft.com/v1.0/users/delta?$skiptoken=P2";

        static string Item(int i) => $"{{\"id\":\"p{i}\"}}";
        static string Page(string nextLink) =>
            $"{{\"value\":[{string.Join(",", Enumerable.Range(0, 1000).Select(Item))}],"
            + $"\"@odata.nextLink\":\"{nextLink}\"}}";

        var env = NewEnv();
        try
        {
            // Ctrl-C 500 items into the first page: the run promoted what it had written and
            // recorded the output, 500 items in, all 500 of them from the page in flight.
            File.WriteAllLines(env.OutputPath, Enumerable.Range(0, 500).Select(Item));
            new PaginationCheckpoint
            {
                Resource = DeltaLink1,
                NextLink = DeltaLink1,
                ItemsCollected = 500,
                PageItemsAlreadyWritten = 500,
                TempFile = null,
                DataLength = new FileInfo(env.OutputPath).Length,
            }.Save(env.CheckpointPath);

            var handler = new MockHttpHandler();
            // The resumed run writes the other 500 items of the page, saves the mid-page
            // checkpoint that item 1000 triggers, and is then refused the page's nextLink -
            // so it dies before the page boundary and that mid-page save is what survives.
            handler.QueueResponse(HttpStatusCode.OK, Page(RefusedLink));
            // The run after it re-fetches the same page, this time paginating to the end.
            handler.QueueResponse(HttpStatusCode.OK, Page(Page2Link));
            handler.QueueResponse(HttpStatusCode.OK,
                """{"value":[{"id":"tail"}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/users/delta?$deltatoken=D2"}""");
            using var transport = MgxTransportScope.Inject(handler);

            Sync(env);

            Assert.Equal(1000, Ids(env.OutputPath).Length);
            var checkpoint = PaginationCheckpoint.Load(env.CheckpointPath)!;
            Assert.Equal(1000, checkpoint.ItemsCollected);
            // 1000 items of this page are in the output, not the 500 this run put there.
            Assert.Equal(1000, checkpoint.PageItemsAlreadyWritten);

            Sync(env);

            var ids = Ids(env.OutputPath);
            Assert.Equal(ids.Length, ids.Distinct().Count());
            Assert.Equal(1001, ids.Length);
            Assert.Equal("{\"id\":\"tail\"}", ids[^1]);
        }
        finally { try { Directory.Delete(env.Dir, true); } catch { } }
    }

    /// <summary>
    /// PaginationCheckpoint.Load answers null for a file torn by a crash and for one that is
    /// locked or that this account cannot open, and the sync does the same thing either way:
    /// resume stays null, so it re-enumerates from the delta token. Deleting the file changed
    /// nothing about the run and destroyed a position the next run, or another account, could
    /// still have resumed from. In file mode the delete came out of the branch that reads
    /// "output file is missing" - a reading of contents, off a checkpoint with none to read.
    /// </summary>
    [Fact]
    public void A_checkpoint_this_sync_cannot_read_is_left_for_a_sync_that_can()
    {
        var env = NewEnv();
        try
        {
            File.WriteAllText(env.CheckpointPath, TornCheckpoint);
            var before = File.ReadAllBytes(env.CheckpointPath);

            var handler = new MockHttpHandler();
            handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
            using var transport = MgxTransportScope.Inject(handler);

            // The interrupted run never promoted its output, so this is the branch that used to
            // call the checkpoint stale for it. The sync then fails on its first page, so
            // nothing on the success path can be what removes the file.
            Assert.False(File.Exists(env.OutputPath));
            Sync(env);

            Assert.True(File.Exists(env.CheckpointPath),
                "a position this sync could not read was deleted anyway");
            Assert.Equal(before, File.ReadAllBytes(env.CheckpointPath));
        }
        finally { try { Directory.Delete(env.Dir, true); } catch { } }
    }

    /// <summary>
    /// The same checkpoint against a sync writing to the pipeline, which reaches the second of
    /// the two deletes and took the file with no warning on any stream.
    /// </summary>
    [Fact]
    public void A_checkpoint_a_pipeline_sync_cannot_read_is_left_for_a_sync_that_can()
    {
        var env = NewEnv();
        try
        {
            File.WriteAllText(env.CheckpointPath, TornCheckpoint);
            var before = File.ReadAllBytes(env.CheckpointPath);

            var handler = new MockHttpHandler();
            handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
            using var transport = MgxTransportScope.Inject(handler);

            SyncToPipeline(env);

            Assert.True(File.Exists(env.CheckpointPath),
                "a position this sync could not read was deleted anyway");
            Assert.Equal(before, File.ReadAllBytes(env.CheckpointPath));
        }
        finally { try { Directory.Delete(env.Dir, true); } catch { } }
    }

    /// <summary>
    /// A checkpoint recording no nextLink says something an unreadable one cannot: the sync
    /// that wrote it got to the end. That one is still deleted by a run that gets nowhere,
    /// because -Latest is suppressed by the checkpoint file merely existing - a marker left
    /// lying there costs the next -Latest run the baseline it asked for.
    /// </summary>
    [Fact]
    public void A_checkpoint_that_says_the_previous_sync_finished_is_deleted()
    {
        var env = NewEnv();
        try
        {
            new PaginationCheckpoint
            {
                Resource = DeltaLink1,
                NextLink = null,
                ItemsCollected = 3,
                PageItemsAlreadyWritten = 0,
                TempFile = null,
                DataLength = null,
            }.Save(env.CheckpointPath);

            var handler = new MockHttpHandler();
            handler.SetDefaultResponse(HttpStatusCode.InternalServerError, ServerError);
            using var transport = MgxTransportScope.Inject(handler);

            SyncToPipeline(env);

            Assert.False(File.Exists(env.CheckpointPath));
        }
        finally { try { Directory.Delete(env.Dir, true); } catch { } }
    }

    /// <summary>
    /// The last step of a completed sync is the move that puts its temp over the output, and it
    /// can fail with every page already fetched: a destination another process holds open, a
    /// read-only file, a share that dropped. The checkpoint goes only once that move has
    /// landed, so a failure leaves both files exactly as an interruption leaves them and the
    /// next run resumes from them instead of re-enumerating from the delta token.
    /// </summary>
    [Fact]
    public void A_failed_promotion_keeps_the_checkpoint_and_the_temp_holding_the_sync()
    {
        var env = NewEnv();
        try
        {
            var handler = new MockHttpHandler();
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage1);       // run 1: b1,b2
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2Final);  // run 1: b3, then the deltaLink
            handler.QueueResponse(HttpStatusCode.OK, ChangesPage2Final);  // run 2, resumed
            using var transport = MgxTransportScope.Inject(handler);

            // Something at the output path the promotion cannot replace, so both pages are
            // fetched and written and the move at the end of the run is what fails.
            Directory.CreateDirectory(env.OutputPath);

            Sync(env);

            Assert.True(File.Exists(env.CheckpointPath),
                "the failed promotion deleted the position the next run resumes from");
            var cp = PaginationCheckpoint.Load(env.CheckpointPath);
            Assert.NotNull(cp);
            Assert.NotNull(cp!.TempFile);
            var temp = Path.Combine(env.Dir, cp.TempFile!);
            Assert.True(File.Exists(temp),
                "the failed promotion deleted the temp holding every item the sync fetched");
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"],
                File.ReadAllLines(temp));

            // With the occupier gone: the temp is promoted, one page is fetched - the one the
            // checkpoint recorded - and the enumeration does not start over.
            Directory.Delete(env.OutputPath);
            var before = handler.RequestCount;
            Sync(env);

            Assert.Equal(1, handler.RequestCount - before);
            Assert.Equal(["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b3\"}"], Ids(env.OutputPath));
        }
        finally { try { Directory.Delete(env.Dir, true); } catch { } }
    }

    /// <summary>
    /// A checkpoint the sync cannot write, over pages that are empty behind nextLinks. The
    /// page-boundary callback runs on whichever thread the iterator resumed on, and a page that
    /// writes nothing resumes on the thread pool - so warning about the disk from there ended
    /// the sync at that page: the items behind it were never fetched, and the delta token never
    /// advanced, leaving the caller to re-enumerate from the old one. The message is buffered
    /// on the client's warning channel and written by a drain on the pipeline thread: one per
    /// failed save, and the enumeration runs to the deltaLink.
    /// <para>
    /// The last save fails at the boundary before the empty final page, so no item follows it
    /// and the per-item drain never comes round again. The drain on the way out is what carries
    /// that one.
    /// </para>
    /// </summary>
    [Fact]
    public void A_failed_boundary_save_warns_and_the_sync_goes_on()
    {
        var env = NewEnv();
        try
        {
            // Every save into this path fails: Save stages its bytes under this name first, and
            // no account writes a file over a directory. The directory holding it is not sealed
            // instead, the way a read-only disk would seal it, because the sync writes its
            // output and its delta state there too and neither write is what is under test.
            Directory.CreateDirectory(env.CheckpointPath + ".tmp");

            var handler = new EmptyMiddlePageHandler();
            using var transport = MgxTransportScope.Inject(handler);
            var (warnings, errors) = SyncReporting(env);

            Assert.Equal(0, errors);

            // Each of the three boundaries carrying a nextLink tries to save, and each fails.
            Assert.Equal(3, warnings.Count(
                w => w.StartsWith("Checkpoint save failed (page boundary)", StringComparison.Ordinal)));

            // The empty page sits behind the second request, so a sync that died reporting its
            // failed save never asks for the rest - where the items and the token both are.
            Assert.Equal(4, handler.Urls.Count);
            Assert.Contains("skiptoken=B4", handler.Urls[3]);
            Assert.Equal(
                ["{\"id\":\"b1\"}", "{\"id\":\"b2\"}", "{\"id\":\"b4\"}"],
                Ids(env.OutputPath));
            Assert.Contains("$deltatoken=D2", DeltaState.Load(env.DeltaPath)!.DeltaLink);
        }
        finally { try { Directory.Delete(env.Dir, true); } catch { } }
    }
}
