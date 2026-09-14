using System.Management.Automation;
using System.Net;
using System.Text;
using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests;

/// <summary>
/// An endpoint that refuses $top, or the auto-added $count, sends the export round its attempt
/// loop again without that option - and the checkpoint it saves afterwards records the URL it
/// actually fetched. The recovery that runs before the loop compared only the URL the first
/// attempt builds, so an export's own checkpoint read as another export's: nothing was
/// recovered, the sweep deleted the temp holding its items, and the retry - which does build
/// that URL - appended the rest of the enumeration onto whatever file was at -OutputFile.
/// </summary>
[Collection("Pipeline")]
public class ExportDegradedQueryCheckpointTests
{
    private const string Page1 = """
    {"value":[{"id":"u1"},{"id":"u2"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=P2"}
    """;
    private const string Page2 = """
    {"value":[{"id":"u3"}]}
    """;
    private const string TopUnsupported = """
    {"error":{"code":"Request_UnsupportedQuery","message":"$top is not supported on this endpoint"}}
    """;
    private const string CountUnsupported = """
    {"error":{"code":"BadRequest","message":"$count is not supported on this endpoint"}}
    """;
    private const string ServerError = """{"error":{"code":"InternalServerError","message":"boom"}}""";
    // A bare 400 off the resumed nextLink, with nothing to do with $count - which is all
    // IsCountRejection needs to drop the auto-added one and send the loop round again.
    private const string ExpiredSkipToken = """{"error":{"code":"BadRequest","message":"skip token expired"}}""";

    private static readonly string[] OldExport =
        ["{\"id\":\"old1\"}", "{\"id\":\"old2\"}", "{\"id\":\"old3\"}"];

    /// <summary>
    /// Answers by URL rather than in order: the option this endpoint refuses is refused every
    /// time it appears, and page two fails until the second run asks for it.
    /// </summary>
    private sealed class RefusingHandler : HttpMessageHandler
    {
        private readonly object _lock = new();
        private readonly string _refused;
        private readonly string _refusal;

        public RefusingHandler(string refused, string refusal)
        {
            _refused = refused;
            _refusal = refusal;
        }

        public List<string> Requests { get; } = [];
        public bool PageTwoFails { get; set; } = true;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            HttpStatusCode status;
            string body;
            lock (_lock)
            {
                Requests.Add(url);
                if (url.Contains(_refused, StringComparison.Ordinal))
                    (status, body) = (HttpStatusCode.BadRequest, _refusal);
                else if (!url.Contains("skiptoken=P2", StringComparison.Ordinal))
                    (status, body) = (HttpStatusCode.OK, Page1);
                else if (PageTwoFails)
                    (status, body) = (HttpStatusCode.InternalServerError, ServerError);
                else
                    (status, body) = (HttpStatusCode.OK, Page2);
            }
            return Task.FromResult(new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }


    /// <summary>
    /// Refuses the option only once the first page has been collected: page one answers with
    /// $top on it, and the nextLink that follows comes back Request_UnsupportedQuery. That is
    /// what sends the attempt loop round again with a page-boundary checkpoint already on disk.
    /// </summary>
    private sealed class RefusingOnPageTwoHandler : HttpMessageHandler
    {
        private readonly object _lock = new();

        public List<string> Requests { get; } = [];
        public bool PageTwoAnswers { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            HttpStatusCode status;
            string body;
            lock (_lock)
            {
                Requests.Add(url);
                if (url.Contains("skiptoken=P2", StringComparison.Ordinal))
                    (status, body) = PageTwoAnswers
                        ? (HttpStatusCode.OK, Page2)
                        : (HttpStatusCode.BadRequest, TopUnsupported);
                else if (url.Contains("$top=", StringComparison.Ordinal))
                    (status, body) = (HttpStatusCode.OK, Page1);
                else
                    // The retry, without the option the endpoint refused. It dies before its
                    // own first page boundary, so the checkpoint on disk is still the one the
                    // attempt before it saved.
                    (status, body) = (HttpStatusCode.InternalServerError, ServerError);
            }
            return Task.FromResult(new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static long? Export(string outputPath, string checkpointPath, string? filter = null)
    {
        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection).Assembly);
        ps.Invoke();
        ps.Commands.Clear();

        var cmd = ps.AddCommand("Export-MgxCollection")
                    .AddParameter("Uri", "/users")
                    .AddParameter("OutputFile", outputPath)
                    .AddParameter("CheckpointPath", checkpointPath)
                    .AddParameter("All");
        if (filter != null) cmd.AddParameter("Filter", filter);
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
    /// Answers the resumed nextLink with one bare 400 before it will serve page two. Page one
    /// is served whatever form the URL is in, so the run that follows the refusal enumerates
    /// the collection from the beginning.
    /// </summary>
    private sealed class RefusingTheResumedLinkHandler : HttpMessageHandler
    {
        private readonly object _lock = new();

        public List<string> Requests { get; } = [];
        public bool PageTwoFails { get; set; } = true;
        public bool RefuseTheResumedLinkOnce { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            HttpStatusCode status;
            string body;
            lock (_lock)
            {
                Requests.Add(url);
                if (!url.Contains("skiptoken=P2", StringComparison.Ordinal))
                    (status, body) = (HttpStatusCode.OK, Page1);
                else if (PageTwoFails)
                    (status, body) = (HttpStatusCode.InternalServerError, ServerError);
                else if (RefuseTheResumedLinkOnce)
                {
                    RefuseTheResumedLinkOnce = false;
                    (status, body) = (HttpStatusCode.BadRequest, ExpiredSkipToken);
                }
                else
                    (status, body) = (HttpStatusCode.OK, Page2);
            }
            return Task.FromResult(new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static (long? Count, List<string> Warnings) ExportReporting(
        string outputPath, string checkpointPath, string? filter)
    {
        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.Export.ExportMgxCollection).Assembly);
        ps.Invoke();
        ps.Commands.Clear();

        var cmd = ps.AddCommand("Export-MgxCollection")
                    .AddParameter("Uri", "/users")
                    .AddParameter("OutputFile", outputPath)
                    .AddParameter("CheckpointPath", checkpointPath)
                    .AddParameter("All");
        if (filter != null) cmd.AddParameter("Filter", filter);
        long? count = null;
        try
        {
            foreach (var r in ps.Invoke())
                if (r?.BaseObject is Mgx.Cmdlets.Models.MgxExportResult summary)
                    count = summary.ItemCount;
        }
        catch (CmdletInvocationException) { }
        return (count, [.. ps.Streams.Warning.Select(w => w.Message)]);
    }

    private static string NewDir() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"mgx-export-degraded-{Guid.NewGuid():N}")).FullName;

    // Page one with no nextLink at all: an endpoint that pages the request carrying the option
    // and answers the one without it in a single page.
    private const string WholeCollection = """
    {"value":[{"id":"u1"},{"id":"u2"}]}
    """;

    /// <summary>
    /// Serves page one whatever form the URL is in, and hands back a nextLink carrying the same
    /// options - so the refusal can land on the CONTINUATION, which is the shape that leaves a
    /// page-boundary checkpoint and the temp it names on disk before the attempt loop goes
    /// round again. The refusal on $count says nothing about $count: a bare 400 is all a run
    /// that auto-added it asks for, and an expired skiptoken is the same 400.
    /// </summary>
    private sealed class RefusingTheContinuationHandler : HttpMessageHandler
    {
        private readonly object _lock = new();

        public List<string> Requests { get; } = [];
        public bool RefuseCount { get; set; } = true;
        public bool RefuseTop { get; set; }
        /// <summary>A continuation with nothing left to refuse dies instead of answering.</summary>
        public bool ContinuationFails { get; set; }
        /// <summary>The retry's page one is the whole collection, so it saves no checkpoint.</summary>
        public bool RetryTakesOnePage { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            var hasCount = url.Contains("$count=true", StringComparison.Ordinal);
            var hasTop = url.Contains("$top=", StringComparison.Ordinal);
            HttpStatusCode status;
            string body;
            lock (_lock)
            {
                Requests.Add(url);
                if (url.Contains("skiptoken=P2", StringComparison.Ordinal))
                {
                    if (RefuseCount && hasCount)
                        (status, body) = (HttpStatusCode.BadRequest, CountUnsupported);
                    else if (RefuseTop && hasTop)
                        (status, body) = (HttpStatusCode.BadRequest, TopUnsupported);
                    else if (ContinuationFails)
                        (status, body) = (HttpStatusCode.InternalServerError, ServerError);
                    else
                        (status, body) = (HttpStatusCode.OK, Page2);
                }
                else if (RetryTakesOnePage && !hasCount)
                    (status, body) = (HttpStatusCode.OK, WholeCollection);
                else
                {
                    var link = "https://graph.microsoft.com/v1.0/users?"
                        + (hasCount ? "$count=true&" : "")
                        + (hasTop ? "$top=100&" : "")
                        + "$skiptoken=P2";
                    body = "{\"value\":[{\"id\":\"u1\"},{\"id\":\"u2\"}],\"@odata.nextLink\":\"" + link + "\"}";
                    status = HttpStatusCode.OK;
                }
            }
            return Task.FromResult(new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>
    /// The endpoint refuses $top, and the -OutputFile already holds a completed export. The
    /// interrupted run's items are in a temp; the run that follows has to recover them and
    /// resume, and what it must never do is treat the earlier export's rows as its own first
    /// pages.
    /// </summary>
    [Fact]
    public void An_export_resumes_its_own_checkpoint_after_the_endpoint_refused_top()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new RefusingHandler("$top=", TopUnsupported);
            using var transport = MgxTransportScope.Inject(handler);

            // A completed export is already sitting at -OutputFile.
            File.WriteAllLines(output, OldExport);

            // Run one: refused $top, retried without it, collected page one into its temp and
            // died on page two.
            Export(output, checkpoint);
            var cp = PaginationCheckpoint.Load(checkpoint);
            Assert.NotNull(cp);
            Assert.DoesNotContain("$top=", cp!.Resource);
            Assert.NotNull(cp.TempFile);
            Assert.Equal(OldExport, File.ReadAllLines(output));

            // Run two: the same export again, with page two answering this time.
            handler.PageTwoFails = false;
            var reported = Export(output, checkpoint);

            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(output));
            Assert.Equal(3, reported);
            Assert.Empty(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The same defect on the other option, in the shape where it costs the resume rather than
    /// the file: the endpoint refuses the auto-added $count, and the run that died left no
    /// output - which is what the accommodation this replaces needed to see before it looked at
    /// the checkpoint's URL at all.
    /// </summary>
    [Fact]
    public void An_export_resumes_its_own_checkpoint_after_the_endpoint_refused_count()
    {
        const string filter = "startsWith(displayName,'a')";
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new RefusingHandler("$count=true", CountUnsupported);
            using var transport = MgxTransportScope.Inject(handler);

            Export(output, checkpoint, filter);
            Assert.False(File.Exists(output), "a fresh run promotes nothing when it dies");
            var temp = Assert.Single(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
            var cp = PaginationCheckpoint.Load(checkpoint);
            Assert.NotNull(cp);
            Assert.DoesNotContain("$count=true", cp!.Resource);

            handler.PageTwoFails = false;
            var first = handler.Requests.Count;
            var reported = Export(output, checkpoint, filter);

            // Resumed: the first thing the second run asks for is the page the first never got.
            Assert.Contains("skiptoken=P2", handler.Requests[first]);
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(output));
            Assert.Equal(3, reported);
            Assert.False(File.Exists(temp), "the temp its items came from was left behind");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The retry's own sweep, against a checkpoint that is this run's. Dropping $count or $top
    /// sends the attempt loop round again, and the attempt that died kept its temp precisely
    /// because the checkpoint counting those items survived it. The sweep at the top of the
    /// next attempt then deleted the file that checkpoint names, so the next invocation found a
    /// position pointing at nothing and enumerated from the first page - unbounded work thrown
    /// away, since a checkpoint is saved at every page boundary.
    /// </summary>
    [Fact]
    public void A_retry_does_not_sweep_the_temp_this_runs_own_checkpoint_names()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new RefusingOnPageTwoHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // Page one lands in a temp and is checkpointed; the nextLink is refused, the loop
            // retries without $top, and that attempt dies before a boundary of its own.
            Assert.Null(Export(output, checkpoint));

            var cp = PaginationCheckpoint.Load(checkpoint);
            Assert.NotNull(cp);
            Assert.NotNull(cp!.TempFile);
            Assert.True(File.Exists(Path.Combine(dir, cp.TempFile!)),
                "the retry's sweep deleted the temp this run's own checkpoint names");

            // The next invocation resumes where the first left off rather than starting over.
            handler.PageTwoAnswers = true;
            var first = handler.Requests.Count;
            var reported = Export(output, checkpoint);

            Assert.Contains("skiptoken=P2", handler.Requests[first]);
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(output));
            Assert.Equal(3, reported);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The other end of the same rebuild. The run recovers the interrupted export's temp,
    /// reports that it is resuming, and then the resumed nextLink comes back 400 - any 400 on a
    /// run that auto-added $count is read as the endpoint refusing it, so the loop goes round
    /// with the URL rebuilt without it. The checkpoint was written under the previous form and
    /// is no longer recognized, and the resume just announced is withdrawn. Every sibling
    /// refusal on this path says so; this one restarted the enumeration with nothing on any
    /// stream but the recovery message, so the run looked from the outside like one that had
    /// resumed.
    /// </summary>
    [Fact]
    public void An_export_says_so_when_a_rebuilt_request_costs_it_the_resume_it_announced()
    {
        const string filter = "id ne null";
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new RefusingTheResumedLinkHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // Run one: page one collected under the auto-added $count, page two dies.
            Export(output, checkpoint, filter);
            var cp = PaginationCheckpoint.Load(checkpoint);
            Assert.NotNull(cp);
            Assert.Contains("$count=true", cp!.Resource);
            Assert.False(File.Exists(output), "a fresh run promotes nothing when it dies");

            // Run two: the temp is recovered and the resume announced, then the nextLink is
            // refused and the URL is rebuilt into a form the checkpoint was not written under.
            handler.PageTwoFails = false;
            handler.RefuseTheResumedLinkOnce = true;
            var (reported, warnings) = ExportReporting(output, checkpoint, filter);

            Assert.Contains(warnings, w => w.Contains("Resuming from checkpoint"));
            Assert.Contains(warnings,
                w => w.Contains("under a form of the request the endpoint has since refused"));

            // And the run really did start over, which is what the warning is about.
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(output));
            Assert.Equal(3, reported);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// What a retry leaves beside the output once it succeeds. The attempt that died kept its
    /// temp because a checkpoint counting those items survived it; the attempt that followed
    /// saved a checkpoint of its own over that one, naming its own temp, and from that moment
    /// nothing on disk referred to the first. It was spared anyway - the sweep is held off for
    /// as long as this run remembers keeping it - so a reported-successful export left a
    /// partial copy of the caller's data sitting beside the finished file, for some later
    /// export's sweep to reclaim.
    /// </summary>
    [Fact]
    public void A_successful_retry_leaves_no_temp_from_the_attempt_it_abandoned()
    {
        const string filter = "startsWith(displayName,'a')";
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new RefusingTheContinuationHandler();
            using var transport = MgxTransportScope.Inject(handler);

            var reported = Export(output, checkpoint, filter);

            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(output));
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
    /// The same, chained: the endpoint refuses the auto-added $count on the continuation and
    /// then $top on the next one, so two attempts are abandoned with a temp each before the
    /// third finishes.
    /// </summary>
    [Fact]
    public void A_second_retry_leaves_no_temp_from_either_attempt_before_it()
    {
        const string filter = "startsWith(displayName,'a')";
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new RefusingTheContinuationHandler { RefuseTop = true };
            using var transport = MgxTransportScope.Inject(handler);

            var reported = Export(output, checkpoint, filter);

            // Page one and a refused continuation, twice, and then the pair that answered.
            Assert.Equal(6, handler.Requests.Count);
            Assert.DoesNotContain("$count=true", handler.Requests[5]);
            Assert.DoesNotContain("$top=", handler.Requests[5]);
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(output));
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
    /// And it goes at the moment it stops being anything, not at the end of the run. The retry
    /// saves its first page-boundary checkpoint - which names its own temp, so the one before
    /// it is now counted by nothing - and then dies, promoting nothing. What is left is the one
    /// temp the checkpoint on disk names, and the run after it resumes from exactly that, so
    /// what went was never something a resume needed.
    /// </summary>
    [Fact]
    public void A_retry_deletes_the_temp_its_own_checkpoint_has_stopped_naming()
    {
        const string filter = "startsWith(displayName,'a')";
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new RefusingTheContinuationHandler { ContinuationFails = true };
            using var transport = MgxTransportScope.Inject(handler);

            Assert.Null(Export(output, checkpoint, filter));

            var cp = PaginationCheckpoint.Load(checkpoint);
            Assert.NotNull(cp);
            Assert.NotNull(cp!.TempFile);
            var temp = Assert.Single(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
            Assert.Equal(cp.TempFile, Path.GetFileName(temp));
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}"], File.ReadAllLines(temp));

            handler.ContinuationFails = false;
            var first = handler.Requests.Count;
            var reported = Export(output, checkpoint, filter);

            Assert.Contains("skiptoken=P2", handler.Requests[first]);
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(output));
            Assert.Equal(3, reported);
            Assert.Empty(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The retry that never saves a checkpoint at all: the endpoint pages the request carrying
    /// the option it refuses and answers the one without it in a single page, so the attempt
    /// that finishes reaches no page boundary and writes over nothing. The temp the attempt
    /// before it left is released by the run finishing instead.
    /// </summary>
    [Fact]
    public void A_retry_that_saves_no_checkpoint_of_its_own_leaves_no_temp_behind()
    {
        const string filter = "startsWith(displayName,'a')";
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new RefusingTheContinuationHandler { RetryTakesOnePage = true };
            using var transport = MgxTransportScope.Inject(handler);

            var reported = Export(output, checkpoint, filter);

            // Page one, the continuation it refused, and the retry's single page: nothing the
            // attempt that finished saved can have been what tidied up after the one before it.
            Assert.Equal(3, handler.Requests.Count);
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}"], File.ReadAllLines(output));
            Assert.Equal(2, reported);
            Assert.False(File.Exists(checkpoint));
            Assert.Empty(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The other end of it: a retry that fetched every page and then could not move its temp
    /// over the output. Nothing is promoted and nothing is finished, so the checkpoint and the
    /// temp it names have to be left exactly as an interruption leaves them - the abandoned
    /// attempt's temp is the only thing that goes - and the next run resumes from them.
    /// </summary>
    [Fact]
    public void A_failed_promotion_after_a_retry_keeps_what_the_next_run_resumes_from()
    {
        const string filter = "startsWith(displayName,'a')";
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new RefusingTheContinuationHandler();
            using var transport = MgxTransportScope.Inject(handler);

            // Something at the output path the promotion cannot replace, so every page is
            // fetched and written and the move at the end of the retry is what fails.
            Directory.CreateDirectory(output);

            Assert.Null(Export(output, checkpoint, filter));

            Assert.True(File.Exists(checkpoint),
                "the failed promotion deleted the position the next run resumes from");
            var cp = PaginationCheckpoint.Load(checkpoint);
            Assert.NotNull(cp);
            Assert.NotNull(cp!.TempFile);
            var temp = Assert.Single(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
            Assert.Equal(cp.TempFile, Path.GetFileName(temp));
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(temp));

            // With the occupier gone: the temp is promoted, the page the checkpoint recorded is
            // the one page fetched, and the collection is not enumerated again from the start.
            Directory.Delete(output);
            var first = handler.Requests.Count;
            var reported = Export(output, checkpoint, filter);

            Assert.Equal(1, handler.Requests.Count - first);
            Assert.Contains("skiptoken=P2", handler.Requests[first]);
            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(output));
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
    /// The scenario above, carried through to the invocation that finishes. The retry that dies
    /// before a boundary of its own has a temp too, and the checkpoint on disk - the attempt
    /// before it saved that - counts nothing in it: keeping it left a file no recovery can
    /// reach beside the one that is resumed from, and the invocation that did resume appended
    /// to the output, so no sweep ever came for it. A finished export leaves its output.
    /// </summary>
    [Fact]
    public void A_retry_that_dies_before_a_boundary_leaves_only_the_temp_its_checkpoint_names()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var handler = new RefusingOnPageTwoHandler();
            using var transport = MgxTransportScope.Inject(handler);

            Assert.Null(Export(output, checkpoint));

            var cp = PaginationCheckpoint.Load(checkpoint);
            Assert.NotNull(cp);
            Assert.NotNull(cp!.TempFile);
            var temp = Assert.Single(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
            Assert.Equal(cp.TempFile, Path.GetFileName(temp));

            handler.PageTwoAnswers = true;
            var reported = Export(output, checkpoint);

            Assert.Equal(["{\"id\":\"u1\"}", "{\"id\":\"u2\"}", "{\"id\":\"u3\"}"],
                File.ReadAllLines(output));
            Assert.Equal(3, reported);
            Assert.False(File.Exists(checkpoint));
            Assert.Empty(Directory.GetFiles(dir, "out.jsonl.*.tmp"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
    // Ids of unequal width, so a byte count taken from one enumeration and applied to the file
    // another left lands inside a line rather than on a boundary.
    //
    // The collection as the first run below reads it, paged two items and then one. An export
    // is a snapshot of a collection that goes on changing, and the runs here are minutes apart:
    // what the skip token names is a position in THIS enumeration, and the byte count beside it
    // describes the bytes THIS enumeration wrote.
    private const string GrowingPage1 = """
    {"value":[{"id":"u1"},{"id":"uu22"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=P2"}
    """;
    private const string GrowingPage2 = """
    {"value":[{"id":"uuu333"}]}
    """;

    // The same collection with one item created at the front, which is what the runs after the
    // first enumerate - paged two and two, under a skip token of their own.
    private const string GrownPage1 = """
    {"value":[{"id":"a0"},{"id":"u1"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=Q2"}
    """;
    private const string GrownPage2 = """
    {"value":[{"id":"uu22"},{"id":"uuu333"}]}
    """;

    // And the whole of it in a single page: how the endpoint answers the form of the request it
    // does not refuse, so the attempt that finishes reaches no page boundary and saves nothing.
    private const string GrownWholeCollection = """
    {"value":[{"id":"a0"},{"id":"u1"},{"id":"uu22"},{"id":"uuu333"}]}
    """;

    private static readonly string[] GrownCollection =
        ["{\"id\":\"a0\"}", "{\"id\":\"u1\"}", "{\"id\":\"uu22\"}", "{\"id\":\"uuu333\"}"];

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>What the resumed-link entry <see cref="ProgramGrowingEndpoint"/> arms is called,
    /// so a run can assert by name that <see cref="MockHttpHandler.UnansweredRules"/> does not
    /// carry it - it is the one entry that ever answers the refusal these tests inject.</summary>
    private const string FaultName = "fault-resumed-link";

    /// <summary>
    /// An endpoint that pages the form of the request carrying <paramref name="refusedOption"/>
    /// and answers the form without it whole, over a collection that grows once the first run
    /// is done with it. The refusal lands on the continuation, which is the shape that leaves a
    /// page-boundary checkpoint on disk before the attempt loop goes round again;
    /// <paramref name="run"/> says which of the three invocations is talking to it.
    /// </summary>
    private static void ProgramGrowingEndpoint(
        MockHttpHandler handler, string refusedOption, string refusal, Func<int> run)
    {
        handler
            // The position the first run's enumeration handed out. It dies on it; the run that
            // resumes is refused the option its URL carries; the run after that is answered
            // from the snapshot the first run was reading.
            .When(r => r.Uri.Contains("$skiptoken=P2", StringComparison.Ordinal), FaultName)
                .Respond(_ => run() switch
                {
                    1 => Json(HttpStatusCode.InternalServerError, ServerError),
                    2 => Json(HttpStatusCode.BadRequest, refusal),
                    _ => Json(HttpStatusCode.OK, GrowingPage2)
                })
            // The position the enumeration over the grown collection hands out.
            .When(r => r.Uri.Contains("$skiptoken=Q2", StringComparison.Ordinal))
                .Respond(_ => Json(HttpStatusCode.OK, GrownPage2))
            // Page one of the form the endpoint pages, against the collection as it stands.
            .When(r => r.Uri.Contains(refusedOption, StringComparison.Ordinal))
                .Respond(_ => Json(HttpStatusCode.OK, run() == 1 ? GrowingPage1 : GrownPage1))
            // And the form it does not refuse.
            .When(_ => true)
                .Respond(_ => Json(HttpStatusCode.OK, GrownWholeCollection));
    }

    /// <summary>
    /// What a rebuilt request leaves at -CheckpointPath, and what the run after it does with
    /// that. The endpoint refuses $top on the continuation, so the attempt loop goes round with
    /// a position of this run's already on disk, and the attempt that follows compares it
    /// against a URL the run has stopped building and refuses it. That refusal says nothing
    /// about whose the file is: it is this export's own position, into an enumeration this run
    /// goes on to make again and replace the output with. Kept beside the finished output, it
    /// is a position nothing comes back to, and the next run over the same command line reads
    /// it as its own - the completed file cut back to the byte count of an enumeration two runs
    /// old, and the rest appended from a skip token that counts items it no longer holds.
    /// </summary>
    [Fact]
    public void A_checkpoint_refused_for_a_rebuilt_request_goes_with_the_completed_export()
    {
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var run = 1;
            var handler = new MockHttpHandler();
            ProgramGrowingEndpoint(handler, "$top=", TopUnsupported, () => run);
            using var transport = MgxTransportScope.Inject(handler);

            // Run one: page one collected into a temp under the automatic $top, and the
            // continuation dies. The checkpoint records that form of the URL and names the temp.
            var firstResult = Export(output, checkpoint);
            Assert.DoesNotContain(FaultName, handler.UnansweredRules);
            Assert.Null(firstResult);
            var cp = PaginationCheckpoint.Load(checkpoint);
            Assert.NotNull(cp);
            Assert.Contains("$top=", cp!.Resource);
            Assert.NotNull(cp.TempFile);

            // Run two: the temp is promoted and the resume announced, then the continuation
            // comes back Request_UnsupportedQuery, the URL is rebuilt without $top, and the
            // form the endpoint answers hands over the whole collection in one page - so the
            // attempt that finishes never reaches a boundary and saves no checkpoint of its own.
            run = 2;
            var (secondCount, secondWarnings) = ExportReporting(output, checkpoint, null);

            Assert.Contains(secondWarnings, w => w.Contains("Resuming from checkpoint"));
            Assert.Equal(GrownCollection, File.ReadAllLines(output));
            Assert.Equal(4, secondCount);

            // Taken here and weighed below, because what the file costs is what run three does.
            var checkpointSurvivedRunTwo = File.Exists(checkpoint);

            // Run three, the same command line, with $top answered again.
            run = 3;
            var (thirdCount, thirdWarnings) = ExportReporting(output, checkpoint, null);

            Assert.Equal(GrownCollection, File.ReadAllLines(output));
            Assert.Equal(4, thirdCount);
            Assert.DoesNotContain(thirdWarnings,
                w => w.Contains("resum", StringComparison.OrdinalIgnoreCase));
            Assert.False(checkpointSurvivedRunTwo,
                "a completed export left a position into the enumeration it had just replaced");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// The same on the other option: a -Filter run auto-adds $count, the continuation comes
    /// back a bare 400, and the URL is rebuilt without it. The checkpoint the attempt before
    /// saved is this export's own under the form that carried $count, and the run that refuses
    /// it goes on to enumerate the collection again and replace the output.
    /// </summary>
    [Fact]
    public void A_checkpoint_refused_for_a_dropped_count_goes_with_the_completed_export()
    {
        const string filter = "startsWith(displayName,'a')";
        var dir = NewDir();
        var output = Path.Combine(dir, "out.jsonl");
        var checkpoint = Path.Combine(dir, "run.checkpoint");
        try
        {
            var run = 1;
            var handler = new MockHttpHandler();
            ProgramGrowingEndpoint(handler, "$count=true", CountUnsupported, () => run);
            using var transport = MgxTransportScope.Inject(handler);

            var firstResult = Export(output, checkpoint, filter);
            Assert.DoesNotContain(FaultName, handler.UnansweredRules);
            Assert.Null(firstResult);
            var cp = PaginationCheckpoint.Load(checkpoint);
            Assert.NotNull(cp);
            Assert.Contains("$count=true", cp!.Resource);
            Assert.NotNull(cp.TempFile);

            run = 2;
            var (secondCount, secondWarnings) = ExportReporting(output, checkpoint, filter);

            Assert.Contains(secondWarnings, w => w.Contains("Resuming from checkpoint"));
            Assert.Equal(GrownCollection, File.ReadAllLines(output));
            Assert.Equal(4, secondCount);

            var checkpointSurvivedRunTwo = File.Exists(checkpoint);

            run = 3;
            var (thirdCount, thirdWarnings) = ExportReporting(output, checkpoint, filter);

            Assert.Equal(GrownCollection, File.ReadAllLines(output));
            Assert.Equal(4, thirdCount);
            Assert.DoesNotContain(thirdWarnings,
                w => w.Contains("resum", StringComparison.OrdinalIgnoreCase));
            Assert.False(checkpointSurvivedRunTwo,
                "a completed export left a position into the enumeration it had just replaced");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
