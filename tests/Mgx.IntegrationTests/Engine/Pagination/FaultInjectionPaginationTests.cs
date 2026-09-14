using System.Management.Automation;
using System.Net;
using System.Text;
using System.Text.Json;
using Mgx.Cmdlets.Base;
using Mgx.Engine.Errors;
using Mgx.Engine.Http;
using Mgx.Engine.Models;
using Mgx.Engine.Pagination;
using Polly.CircuitBreaker;

namespace Mgx.IntegrationTests;

/// <summary>
/// Every fault in the catalog, injected at page 3 of a pagination that has already delivered
/// pages 1 and 2. What each case asserts comes from <see cref="MgxErrorPolicy"/>'s decision for
/// the class the classifier assigns: a retryable class has to show the mock serving the retry
/// and the pagination finishing, a non-retryable one exactly one attempt and the pages before
/// the fault still delivered. Nothing here restates a status table - the parity suite owns that.
/// </summary>
[Collection("Pipeline")]
public class FaultInjectionPaginationTests
{
    private const string CollectionUrl = "https://graph.microsoft.com/v1.0/users";
    private const string Page2Url = CollectionUrl + "?$skiptoken=page2";
    private const string Page3Url = CollectionUrl + "?$skiptoken=page3";

    /// <summary>What every armed fault in this file is called, so a theory can assert by name
    /// that <see cref="MockHttpHandler.UnansweredRules"/> does not carry it.</summary>
    private const string FaultName = "fault-page-3";

    /// <summary>
    /// One retry is all any of these needs, and a one-second attempt timeout is what makes the
    /// held-answer case fire without costing the suite anything.
    /// </summary>
    private static readonly ResilientGraphClientOptions Options = new()
    {
        NoRateLimit = true,
        NoAdaptivePacing = true,
        MaxRetryAttempts = 1,
        AttemptTimeoutSeconds = 1,
        TotalTimeoutSeconds = 30,
    };

    private static readonly TimeSpan ShortBodyRead = TimeSpan.FromMilliseconds(250);

    private static string PageBody(string[] ids, string? nextLink)
    {
        var value = string.Join(",", ids.Select(id => "{\"id\":\"" + id + "\"}"));
        var link = nextLink == null ? "" : ",\"@odata.nextLink\":\"" + nextLink + "\"";
        return "{\"value\":[" + value + "]" + link + "}";
    }

    private static Dictionary<string, string> NoBackoff() => new() { ["Retry-After"] = "0" };

    private static string ErrorBody(int status) =>
        "{\"error\":{\"code\":\"Injected_" + status + "\",\"message\":\"injected at page 3\"}}";

    /// <summary>
    /// Three pages of two items each, with the fault armed on page 3's first attempt only. The
    /// same programming serves every kind: a later attempt at page 3 is answered normally, so
    /// whether the retry happens at all is the policy's decision and not the mock's.
    /// </summary>
    private static MockHttpHandler ThreePages(Action<MockResponseRule<MockHttpHandler>> armPage3)
    {
        var handler = new MockHttpHandler();
        handler.When(r => r.Uri.Contains("skiptoken=page2"))
               .Respond(HttpStatusCode.OK, PageBody(["u3", "u4"], Page3Url));
        armPage3(handler.When(r => r.Uri.Contains("skiptoken=page3"), FaultName).OnAttempt(1));
        handler.When(r => r.Uri.Contains("skiptoken=page3"))
               .Respond(HttpStatusCode.OK, PageBody(["u5", "u6"], null));
        handler.SetDefaultResponse(HttpStatusCode.OK, PageBody(["u1", "u2"], Page2Url));
        return handler;
    }

    private static void ArmAtPageThree(MockResponseRule<MockHttpHandler> rule, FaultEntry entry)
    {
        switch (entry.Shape)
        {
            case FaultShape.Status:
                rule.Respond((HttpStatusCode)entry.Status, ErrorBody(entry.Status), NoBackoff());
                break;
            case FaultShape.Transport:
                rule.Throw(entry.Transport!());
                break;
            case FaultShape.HeldPastAttemptTimeout:
                rule.AfterDelay(TimeSpan.FromSeconds(5))
                    .Respond(HttpStatusCode.OK, PageBody(["u5", "u6"], null));
                break;
            case FaultShape.StalledBody:
                rule.RespondStalled(HttpStatusCode.OK, prefix: """{"value":[""");
                break;
            case FaultShape.TruncatedJson:
                rule.Respond(HttpStatusCode.OK, FaultCatalog.TruncatedPageBody);
                break;
            case FaultShape.UnusableNextLink:
                // Not a URL at all, so the validator refuses it for a reason that has nothing to
                // do with the host - SsrfValidationTests owns the cross-host refusal.
                rule.Respond(HttpStatusCode.OK, PageBody(["u5", "u6"], "pages/4"));
                break;
            default:
                throw new InvalidOperationException(
                    $"{entry.Kind} does not take a wire shape at a page; it needs its own case.");
        }
    }

    private static async Task<(List<JsonElement> Items, Exception? Thrown)> PaginateAsync(
        MockHttpHandler handler, int maxItems = 0, ResilientGraphClientOptions? options = null)
    {
        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, options ?? Options)
        {
            BodyReadTimeout = ShortBodyRead
        };
        var iterator = new PageIterator(client);
        var items = new List<JsonElement>();
        var thrown = await Record.ExceptionAsync(async () =>
        {
            await foreach (var item in iterator.StreamAllWithCountAsync(CollectionUrl, maxItems, null))
                items.Add(item);
        });
        return (items, thrown);
    }

    [Theory]
    [MemberData(nameof(FaultCatalog.AllKinds), MemberType = typeof(FaultCatalog))]
    public async Task Each_fault_at_page_three(FaultKind kind)
    {
        var entry = FaultCatalog.Entry(kind);
        var cell = entry.At(InjectionPoint.Page);
        if (cell.Coverage == FaultCoverage.NotApplicable)
        {
            FaultInjectionRows.AssertNotApplicable(entry, InjectionPoint.Page);
            return;
        }

        ResiliencePipelineFactory.Reset();
        try
        {
            if (cell.Coverage == FaultCoverage.Pinned)
            {
                await PinAtPageThree(entry);
                return;
            }

            var handler = ThreePages(rule => ArmAtPageThree(rule, entry));
            var (items, thrown) = await PaginateAsync(handler);
            Assert.DoesNotContain(FaultName, handler.UnansweredRules);

            if (FaultCatalog.RetriedOnAnIdempotentRequest(entry))
            {
                Assert.Null(thrown);
                Assert.Equal(6, items.Count);
                // page 1, page 2, page 3 refused, page 3 served: the retry is on the wire.
                Assert.Equal(4, handler.RequestCount);
            }
            else
            {
                Assert.NotNull(thrown);
                // Everything delivered before the fault is still delivered - including page 3's
                // own items when the fault is in the link that page carried rather than in the
                // page: the items were handed over before the link was read.
                Assert.Equal(entry.Shape == FaultShape.UnusableNextLink ? 6 : 4, items.Count);
                Assert.Equal(3, handler.RequestCount);
                AssertTheFaultSurfaced(entry, thrown!);
            }
        }
        finally
        {
            ResiliencePipelineFactory.Reset();
        }
    }

    private static void AssertTheFaultSurfaced(FaultEntry entry, Exception thrown)
    {
        switch (entry.Shape)
        {
            case FaultShape.Status:
                var graph = Assert.IsType<GraphServiceException>(thrown);
                Assert.Equal(entry.Status, (int)graph.StatusCode);
                Assert.Equal(entry.Class, MgxErrorClassifier.Classify((int)graph.StatusCode).Class);
                break;
            case FaultShape.TruncatedJson:
                Assert.IsAssignableFrom<JsonException>(thrown);
                break;
            case FaultShape.UnusableNextLink:
                var refused = Assert.IsType<InvalidOperationException>(thrown);
                Assert.Contains("nextLink", refused.Message, StringComparison.OrdinalIgnoreCase);
                break;
            case FaultShape.StalledBody:
                var stalled = Assert.IsType<HttpRequestException>(thrown);
                Assert.Equal(ResilientGraphClient.BodyReadTimedOutMessage, stalled.Message);
                break;
            default:
                Assert.Fail($"{entry.Kind} surfaced as {thrown.GetType().Name} with no case to check it.");
                break;
        }
    }

    // ── The rows that record today's behavior rather than a policy decision ──

    private static async Task PinAtPageThree(FaultEntry entry)
    {
        switch (entry.Kind)
        {
            case FaultKind.BodyTimeout: await PinAStalledPageBody(entry); return;
            case FaultKind.RepeatedNextLink: await PinASelfReferencingNextLink(); return;
            case FaultKind.ExpiredToken: await PinAnExpiredTokenAtPageThree(entry); return;
            case FaultKind.ConsistencyDelay: await PinAConsistencyDelay(entry); return;
            case FaultKind.ChangedAuthContext: PinAnAuthContextSwapBetweenPages(); return;
            default:
                Assert.Fail($"{entry.Kind} is pinned at a page with nothing to run.");
                return;
        }
    }

    private static async Task PinAStalledPageBody(FaultEntry entry)
    {
        var handler = ThreePages(rule => ArmAtPageThree(rule, entry));
        var (items, thrown) = await PaginateAsync(handler);
        Assert.DoesNotContain(FaultName, handler.UnansweredRules);

        // The policy would retry this class on a GET.
        Assert.True(FaultCatalog.RetriedOnAnIdempotentRequest(entry));

        // Nothing does. The page body is read after the resilience pipeline has already handed
        // the response back, so the retry predicate never sees the failure: one attempt at page
        // 3, and the pagination ends there with the pages before it delivered.
        var stalled = Assert.IsType<HttpRequestException>(thrown);
        Assert.Equal(ResilientGraphClient.BodyReadTimedOutMessage, stalled.Message);
        Assert.Equal(4, items.Count);
        Assert.Equal(3, handler.RequestCount);
    }

    private static async Task PinASelfReferencingNextLink()
    {
        // Non-empty pages: the link points back at the page that produced it, every time, and
        // nothing in the iterator notices. Only the item cap ends the walk - which is why this
        // case has to pass one.
        var looping = new MockHttpHandler();
        looping.When(r => r.Uri.Contains("skiptoken=page2"))
               .Respond(HttpStatusCode.OK, PageBody(["u3", "u4"], Page3Url));
        looping.When(r => r.Uri.Contains("skiptoken=page3"))
               .Respond(HttpStatusCode.OK, PageBody(["u5", "u6"], Page3Url));
        looping.SetDefaultResponse(HttpStatusCode.OK, PageBody(["u1", "u2"], Page2Url));

        var (items, thrown) = await PaginateAsync(looping, maxItems: 10);

        Assert.Null(thrown);
        Assert.Equal(10, items.Count);
        Assert.Equal(5, looping.RequestCount);
        Assert.Equal(3, looping.CapturedRequests.Count(r => r.Uri.Contains("skiptoken=page3")));

        // Empty pages: the consecutive-empty-page limit is what does stop a self-loop today.
        var empty = new MockHttpHandler();
        empty.When(r => r.Uri.Contains("skiptoken=page2"))
             .Respond(HttpStatusCode.OK, PageBody(["u3", "u4"], Page3Url));
        empty.When(r => r.Uri.Contains("skiptoken=page3"))
             .Respond(HttpStatusCode.OK, PageBody([], Page3Url));
        empty.SetDefaultResponse(HttpStatusCode.OK, PageBody(["u1", "u2"], Page2Url));

        var (emptyItems, emptyThrown) = await PaginateAsync(empty);

        Assert.Null(emptyThrown);
        Assert.Equal(4, emptyItems.Count);
        Assert.Equal(5, empty.RequestCount);
    }

    private static async Task PinAnExpiredTokenAtPageThree(FaultEntry entry)
    {
        var handler = ThreePages(rule => ArmAtPageThree(rule, entry));
        var (items, thrown) = await PaginateAsync(handler);
        Assert.DoesNotContain(FaultName, handler.UnansweredRules);

        Assert.Equal(MgxErrorClass.Authentication, entry.Class);
        Assert.False(FaultCatalog.RetriedOnAnIdempotentRequest(entry));

        var graph = Assert.IsType<GraphServiceException>(thrown);
        Assert.Equal(HttpStatusCode.Unauthorized, graph.StatusCode);
        Assert.Equal(4, items.Count);

        // One attempt, and no fresh token asked for: the mock is holding a good page 3 that the
        // run never comes back for. Mid-operation re-authentication is not something mgx does.
        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.Unauthorized],
            handler.ServedStatusCodes);
    }

    private static async Task PinAConsistencyDelay(FaultEntry entry)
    {
        var handler = ThreePages(rule => ArmAtPageThree(rule, entry));
        var (items, thrown) = await PaginateAsync(handler);
        Assert.DoesNotContain(FaultName, handler.UnansweredRules);

        // A read-after-write 404 classifies as NotFound, which the policy does not retry, so the
        // 200 the mock would serve on a second attempt is never asked for. MgxErrorClass
        // .Consistency is declared for this window and has no producer yet.
        Assert.Equal(MgxErrorClass.NotFound, entry.Class);
        Assert.False(FaultCatalog.RetriedOnAnIdempotentRequest(entry));

        var graph = Assert.IsType<GraphServiceException>(thrown);
        Assert.Equal(HttpStatusCode.NotFound, graph.StatusCode);
        Assert.Equal(4, items.Count);
        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.NotFound],
            handler.ServedStatusCodes);
    }

    private static void PinAnAuthContextSwapBetweenPages()
    {
        const string Tenant = "11111111-1111-1111-1111-111111111111";
        const string AppA = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        const string AppB = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

        var handler = new MockHttpHandler();
        handler.When(r => r.Uri.Contains("skiptoken=page3"))
               .Respond(HttpStatusCode.OK, PageBody(["u5", "u6"], null));
        handler.SetDefaultResponse(HttpStatusCode.OK, PageBody(["u1", "u2"], Page2Url));

        using var wire = new HttpClient(handler);
        // No transport seam here: the seam sits above the auth probe, and the probe is the thing
        // under test. GetClient falls back to the session's own client, so the mock is reached
        // through the identity check rather than around it.
        using var scope = GraphSessionScope.Arm(wire, GraphSessionScope.AuthContextFor(Tenant, AppA));

        // The swap lands while page 2 is being answered - after page 2's request went out on the
        // client the invocation resolved, and before page 3's does.
        handler.When(r => r.Uri.Contains("skiptoken=page2")).OnAttempt(1).Respond(_ =>
        {
            scope.Session.AuthContext = GraphSessionScope.AuthContextFor(Tenant, AppB);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    PageBody(["u3", "u4"], Page3Url), Encoding.UTF8, "application/json")
            };
        });
        handler.When(r => r.Uri.Contains("skiptoken=page2"))
               .Respond(HttpStatusCode.OK, PageBody(["u3", "u4"], Page3Url));

        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.InvokeMgxRequest).Assembly);
        ps.Invoke();

        var (firstCount, firstVerbose) = RunAll(ps);
        Assert.Equal(6, firstCount);
        Assert.DoesNotContain(firstVerbose, v => v.Contains("Graph identity changed"));

        var (secondCount, secondVerbose) = RunAll(ps);
        Assert.Equal(6, secondCount);
        Assert.Contains(secondVerbose, v => v.Contains("Graph identity changed"));

        static (int Output, List<string> Verbose) RunAll(PowerShell ps)
        {
            ps.Commands.Clear();
            ps.Streams.ClearStreams();
            ps.AddCommand("Invoke-MgxRequest")
              .AddParameter("Uri", "/users")
              .AddParameter("All", true)
              .AddParameter("Verbose", true)
              .AddParameter("WarningAction", ActionPreference.SilentlyContinue);
            var output = ps.Invoke();
            Assert.Empty(ps.Streams.Error.Select(e => e.FullyQualifiedErrorId));
            return (output.Count, [.. ps.Streams.Verbose.Select(v => v.Message)]);
        }
    }

    // ── The breaker dimension of the same faults ─────────────────────────────

    /// <summary>
    /// The breaker's minimum throughput and sampling window at the floors the options allow, so
    /// four attempts are enough to judge, and a long open window so nothing half-opens while the
    /// runs behind the first one are being made.
    /// </summary>
    private static readonly ResilientGraphClientOptions BreakerOptions = new()
    {
        NoRateLimit = true,
        NoAdaptivePacing = true,
        MaxRetryAttempts = 1,
        AttemptTimeoutSeconds = 1,
        TotalTimeoutSeconds = 30,
        CircuitBreakerMinThroughput = 2,
        CircuitBreakerFailureRatio = 0.4,
        CircuitBreakerSamplingDurationSeconds = 5,
        CircuitBreakerDurationSeconds = 60,
    };

    /// <summary>How many runs the drive below makes before it gives up on the circuit opening.</summary>
    private const int BreakerRuns = 3;

    /// <summary>Pages 1 and 2, then page 3 twice - its own attempt and the one retry.</summary>
    private const int RequestsPerRun = 4;

    /// <summary>
    /// The dimension the per-fault theory above does not ask about: whether repeating a fault
    /// opens the circuit. <see cref="FaultCatalog.CountsAgainstTheBreaker"/> answers it per class,
    /// and the two classes that answer differently are what makes the answer worth anything - a
    /// 503 is the service failing and a 429 is the service pacing us, and a circuit that opened
    /// on the second would stop a run exactly when the right response is to slow down and keep
    /// going.
    /// </summary>
    [Fact]
    public async Task Repeating_a_page_three_fault_opens_the_circuit_only_for_a_counted_class()
    {
        var counted = FaultCatalog.Entry(FaultKind.ServerError);
        var excluded = FaultCatalog.Entry(FaultKind.Throttled);

        var server = await DriveAgainstTheBreakerAsync(counted);
        var throttle = await DriveAgainstTheBreakerAsync(excluded);

        // Each side is what the policy says of its own class.
        Assert.Equal(FaultCatalog.CountsAgainstTheBreaker(counted), server.Opened);
        Assert.Equal(FaultCatalog.CountsAgainstTheBreaker(excluded), throttle.Opened);

        // And the two answers differ, which neither assertion above can say on its own: a policy
        // that counted throttles as well would agree with both of them and open the circuit on
        // the service's own pacing.
        Assert.NotEqual(server.Opened, throttle.Opened);

        // What the difference costs on the wire. Once the circuit is open the run behind it sends
        // nothing at all - the refusal is the client's, before the request. The class the breaker
        // ignores keeps sending every attempt, run after run, and what surfaces is still the
        // status the server gave.
        Assert.Equal(0, server.RequestsOnTheLastRun);
        Assert.IsType<BrokenCircuitException>(server.LastFailure);
        Assert.Equal(RequestsPerRun, throttle.RequestsOnTheLastRun);
        Assert.Equal(HttpStatusCode.TooManyRequests,
            Assert.IsType<GraphServiceException>(throttle.LastFailure).StatusCode);
    }

    /// <summary>
    /// Page 3 answered with this fault's status on every attempt, run and re-run against one
    /// pipeline so the outcomes accumulate in a single sampling window. Returns whether any run
    /// was refused by the open circuit, what the last run cost on the wire, and what it surfaced.
    /// </summary>
    private static async Task<(bool Opened, int RequestsOnTheLastRun, Exception LastFailure)>
        DriveAgainstTheBreakerAsync(FaultEntry entry)
    {
        ResiliencePipelineFactory.Reset();
        try
        {
            var opened = false;
            var requests = 0;
            Exception? last = null;
            for (var run = 0; run < BreakerRuns; run++)
            {
                var handler = new MockHttpHandler();
                // Not named and not asserted: once the circuit opens, a later run's client-side
                // refusal never sends a request at all, so this entry is legitimately unanswered
                // on that run - a blanket assertion here would fail the very outcome the test
                // means to observe.
                handler.When(r => r.Uri.Contains("skiptoken=page3"))
                       .Respond((HttpStatusCode)entry.Status, ErrorBody(entry.Status), NoBackoff());
                handler.When(r => r.Uri.Contains("skiptoken=page2"))
                       .Respond(HttpStatusCode.OK, PageBody(["u3", "u4"], Page3Url));
                handler.SetDefaultResponse(HttpStatusCode.OK, PageBody(["u1", "u2"], Page2Url));

                var (_, thrown) = await PaginateAsync(handler, options: BreakerOptions);

                Assert.NotNull(thrown);
                last = thrown;
                opened |= thrown is BrokenCircuitException;
                requests = handler.RequestCount;
            }
            return (opened, requests, last!);
        }
        finally
        {
            ResiliencePipelineFactory.Reset();
        }
    }

    // ── The same faults through the cmdlet, over the transport seam ──────────

    [Theory]
    [InlineData(FaultKind.Throttled)]
    [InlineData(FaultKind.Forbidden)]
    public void A_fault_at_page_three_of_Invoke_MgxRequest_All(FaultKind kind)
    {
        var entry = FaultCatalog.Entry(kind);
        var handler = ThreePages(rule => ArmAtPageThree(rule, entry));

        using (MgxTransportScope.Inject(handler, options: Options))
        {
            using var ps = PowerShell.Create();
            ps.AddCommand("Import-Module")
              .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.InvokeMgxRequest).Assembly);
            ps.Invoke();
            ps.Commands.Clear();
            ps.AddCommand("Invoke-MgxRequest")
              .AddParameter("Uri", "/users")
              .AddParameter("All", true)
              .AddParameter("WarningAction", ActionPreference.SilentlyContinue);
            var output = ps.Invoke();
            var errors = ps.Streams.Error.ToList();
            Assert.DoesNotContain(FaultName, handler.UnansweredRules);

            if (FaultCatalog.RetriedOnAnIdempotentRequest(entry))
            {
                Assert.Empty(errors);
                Assert.Equal(6, output.Count);
            }
            else
            {
                // The contract: only a failure that makes the whole invocation meaningless
                // terminates, so the pages already delivered are delivered and the page that
                // failed is one non-terminating record.
                Assert.Equal(4, output.Count);
                var record = Assert.Single(errors);
                Assert.Equal(
                    MgxErrorPresentation.Category(entry.Class!.Value),
                    record.CategoryInfo.Category);
            }
        }
    }
}

/// <summary>
/// What a theory says about a cell it cannot run. The acceptance is "every fault at each point
/// where it is physically possible", so a point that cannot host a fault has to say which fault
/// and why, in the same run as the ones it does host.
/// </summary>
internal static class FaultInjectionRows
{
    internal static void AssertNotApplicable(FaultEntry entry, InjectionPoint point)
    {
        var cell = entry.At(point);
        Assert.Equal(FaultCoverage.NotApplicable, cell.Coverage);
        Assert.False(string.IsNullOrWhiteSpace(cell.Note),
            $"{entry.Kind} is marked impossible at {point} without saying why");
        Assert.Contains(Enum.GetValues<InjectionPoint>(),
            other => entry.At(other).Coverage != FaultCoverage.NotApplicable);
    }
}
