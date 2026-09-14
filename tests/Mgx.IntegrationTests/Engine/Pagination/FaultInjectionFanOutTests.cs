using System.Management.Automation;
using System.Net;
using System.Text;
using System.Text.Json;
using Mgx.Cmdlets.Base;
using Mgx.Engine.Errors;
using Mgx.Engine.Http;
using Mgx.Engine.Models;
using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests;

/// <summary>
/// Every fault in the catalog, injected at the third item of a fan-out whose first two items
/// have already succeeded. The rule the fan-out exists to keep is that one item failing does not
/// discard the others' data, so each case reads both: what happened to the third item, measured
/// against <see cref="MgxErrorPolicy"/>'s decision for its class, and that the first two are
/// still there.
/// </summary>
[Collection("Pipeline")]
public class FaultInjectionFanOutTests
{
    private const string Url1 = "https://graph.microsoft.com/v1.0/groups/g1/members";
    private const string Url2 = "https://graph.microsoft.com/v1.0/groups/g2/members";
    private const string Url3 = "https://graph.microsoft.com/v1.0/groups/g3/members";

    /// <summary>What every armed fault in this file is called, so a theory can assert by name
    /// that <see cref="MockHttpHandler.UnansweredRules"/> does not carry it.</summary>
    private const string FaultName = "fault-item-3";

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

    private static string EntityBody(string id) => "{\"id\":\"" + id + "\"}";

    private static Dictionary<string, string> NoBackoff() => new() { ["Retry-After"] = "0" };

    private static string ErrorBody(int status) =>
        "{\"error\":{\"code\":\"Injected_" + status + "\",\"message\":\"injected at item 3\"}}";

    /// <summary>
    /// Three urls of two items each, with the fault armed on the third url's first attempt only.
    /// A later attempt at it is answered normally, so whether a retry happens is the policy's
    /// decision rather than the mock's.
    /// </summary>
    private static MockHttpHandler ThreeUrls(Action<MockResponseRule<MockHttpHandler>> armThird)
    {
        var handler = new MockHttpHandler();
        handler.When(r => r.Uri.Contains("/g1/")).Respond(HttpStatusCode.OK, PageBody(["m1", "m2"], null));
        handler.When(r => r.Uri.Contains("/g2/")).Respond(HttpStatusCode.OK, PageBody(["m3", "m4"], null));
        armThird(handler.When(r => r.Uri.Contains("/g3/"), FaultName).OnAttempt(1));
        handler.When(r => r.Uri.Contains("/g3/")).Respond(HttpStatusCode.OK, PageBody(["m5", "m6"], null));
        return handler;
    }

    private static void ArmAtTheThirdItem(MockResponseRule<MockHttpHandler> rule, FaultEntry entry)
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
                    .Respond(HttpStatusCode.OK, PageBody(["m5", "m6"], null));
                break;
            case FaultShape.StalledBody:
                rule.RespondStalled(HttpStatusCode.OK, prefix: """{"value":[""");
                break;
            case FaultShape.TruncatedJson:
                rule.Respond(HttpStatusCode.OK, FaultCatalog.TruncatedPageBody);
                break;
            case FaultShape.UnusableNextLink:
                rule.Respond(HttpStatusCode.OK, PageBody(["m5", "m6"], "members?page=2"));
                break;
            default:
                throw new InvalidOperationException(
                    $"{entry.Kind} does not take a wire shape at a fan-out item; it needs its own case.");
        }
    }

    private static async Task<FanOutResult> FanOutAsync(MockHttpHandler handler, int maxItemsPerUrl = 0)
    {
        using var httpClient = new HttpClient(handler);
        using var client = new ResilientGraphClient(httpClient, Options)
        {
            BodyReadTimeout = ShortBodyRead
        };
        // One at a time, so "the third item" is the third request on the wire and not a race.
        var fanOut = new ConcurrentFanOut(client, maxConcurrency: 1);
        return await fanOut.FetchAllAsync([Url1, Url2, Url3], maxItemsPerUrl);
    }

    [Theory]
    [MemberData(nameof(FaultCatalog.AllKinds), MemberType = typeof(FaultCatalog))]
    public async Task Each_fault_at_the_third_fan_out_item(FaultKind kind)
    {
        var entry = FaultCatalog.Entry(kind);
        var cell = entry.At(InjectionPoint.FanOutItem);
        if (cell.Coverage == FaultCoverage.NotApplicable)
        {
            FaultInjectionRows.AssertNotApplicable(entry, InjectionPoint.FanOutItem);
            return;
        }

        ResiliencePipelineFactory.Reset();
        try
        {
            if (cell.Coverage == FaultCoverage.Pinned)
            {
                await PinAtTheThirdItem(entry);
                return;
            }

            var handler = ThreeUrls(rule => ArmAtTheThirdItem(rule, entry));
            var result = await FanOutAsync(handler);
            Assert.DoesNotContain(FaultName, handler.UnansweredRules);

            if (FaultCatalog.RetriedOnAnIdempotentRequest(entry))
            {
                Assert.False(result.HasErrors);
                Assert.Equal(3, result.Results.Count);
                Assert.Equal(2, result.Results[Url3].Length);
                Assert.Equal(4, handler.RequestCount);
            }
            else
            {
                AssertTheFirstTwoSurvived(handler, result);
                Assert.Equal(3, handler.RequestCount);
                var failed = Assert.Single(result.Errors);
                Assert.Equal(Url3, failed.Key);
                AssertTheFaultSurfaced(entry, failed.Value);
            }
        }
        finally
        {
            ResiliencePipelineFactory.Reset();
        }
    }

    /// <summary>
    /// The whole point of the fan-out's per-url error bag: the two urls that worked keep their
    /// data whatever happened to the third, and the third was the third request on the wire.
    /// </summary>
    private static void AssertTheFirstTwoSurvived(MockHttpHandler handler, FanOutResult result)
    {
        Assert.True(result.HasErrors);
        Assert.Equal(2, result.Results.Count);
        Assert.Equal(2, result.Results[Url1].Length);
        Assert.Equal(2, result.Results[Url2].Length);
        Assert.DoesNotContain(Url3, result.Results.Keys);
        Assert.Contains("/g3/", handler.CapturedRequests[2].Uri);
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

    private static async Task PinAtTheThirdItem(FaultEntry entry)
    {
        switch (entry.Kind)
        {
            case FaultKind.BodyTimeout: await PinAStalledItemBody(entry); return;
            case FaultKind.RepeatedNextLink: await PinASelfReferencingNextLink(); return;
            case FaultKind.ExpiredToken: await PinAnExpiredTokenAtTheThirdItem(entry); return;
            case FaultKind.ConsistencyDelay: await PinAConsistencyDelay(entry); return;
            case FaultKind.ChangedAuthContext: PinAnAuthContextSwapBetweenItems(); return;
            default:
                Assert.Fail($"{entry.Kind} is pinned at a fan-out item with nothing to run.");
                return;
        }
    }

    private static async Task PinAStalledItemBody(FaultEntry entry)
    {
        var handler = ThreeUrls(rule => ArmAtTheThirdItem(rule, entry));
        var result = await FanOutAsync(handler);
        Assert.DoesNotContain(FaultName, handler.UnansweredRules);

        // The policy would retry this class on a GET.
        Assert.True(FaultCatalog.RetriedOnAnIdempotentRequest(entry));

        // Nothing does: the body is read after the pipeline handed the response back, so the
        // third item gets one attempt and its stall lands in the error bag.
        AssertTheFirstTwoSurvived(handler, result);
        Assert.Equal(3, handler.RequestCount);
        var stalled = Assert.IsType<HttpRequestException>(Assert.Single(result.Errors).Value);
        Assert.Equal(ResilientGraphClient.BodyReadTimedOutMessage, stalled.Message);
    }

    private static async Task PinASelfReferencingNextLink()
    {
        // Non-empty pages: the third url's page points back at itself, and only the per-url item
        // cap ends the walk.
        var looping = new MockHttpHandler();
        looping.When(r => r.Uri.Contains("/g1/")).Respond(HttpStatusCode.OK, PageBody(["m1", "m2"], null));
        looping.When(r => r.Uri.Contains("/g2/")).Respond(HttpStatusCode.OK, PageBody(["m3", "m4"], null));
        looping.When(r => r.Uri.Contains("/g3/")).Respond(HttpStatusCode.OK, PageBody(["m5", "m6"], Url3));

        var looped = await FanOutAsync(looping, maxItemsPerUrl: 6);

        Assert.False(looped.HasErrors);
        Assert.Equal(6, looped.Results[Url3].Length);
        Assert.Equal(5, looping.RequestCount);
        Assert.Equal(3, looping.CapturedRequests.Count(r => r.Uri.Contains("/g3/")));

        // Empty pages: the consecutive-empty-page limit is what does stop a self-loop today.
        var empty = new MockHttpHandler();
        empty.When(r => r.Uri.Contains("/g1/")).Respond(HttpStatusCode.OK, PageBody(["m1", "m2"], null));
        empty.When(r => r.Uri.Contains("/g2/")).Respond(HttpStatusCode.OK, PageBody(["m3", "m4"], null));
        empty.When(r => r.Uri.Contains("/g3/")).Respond(HttpStatusCode.OK, PageBody([], Url3));

        var stopped = await FanOutAsync(empty);

        Assert.False(stopped.HasErrors);
        Assert.Empty(stopped.Results[Url3]);
        Assert.Equal(5, empty.RequestCount);
    }

    private static async Task PinAnExpiredTokenAtTheThirdItem(FaultEntry entry)
    {
        var handler = ThreeUrls(rule => ArmAtTheThirdItem(rule, entry));
        var result = await FanOutAsync(handler);
        Assert.DoesNotContain(FaultName, handler.UnansweredRules);

        Assert.Equal(MgxErrorClass.Authentication, entry.Class);
        Assert.False(FaultCatalog.RetriedOnAnIdempotentRequest(entry));

        AssertTheFirstTwoSurvived(handler, result);
        var graph = Assert.IsType<GraphServiceException>(Assert.Single(result.Errors).Value);
        Assert.Equal(HttpStatusCode.Unauthorized, graph.StatusCode);

        // One attempt at the third item, and no fresh token asked for: the good page the mock is
        // holding for a second attempt is never fetched.
        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.Unauthorized],
            handler.ServedStatusCodes);
    }

    private static async Task PinAConsistencyDelay(FaultEntry entry)
    {
        var handler = ThreeUrls(rule => ArmAtTheThirdItem(rule, entry));
        var result = await FanOutAsync(handler);
        Assert.DoesNotContain(FaultName, handler.UnansweredRules);

        // A read-after-write 404 classifies as NotFound, which the policy does not retry, so the
        // 200 waiting behind it is never asked for. MgxErrorClass.Consistency, declared for this
        // window, still has no producer.
        Assert.Equal(MgxErrorClass.NotFound, entry.Class);
        Assert.False(FaultCatalog.RetriedOnAnIdempotentRequest(entry));

        AssertTheFirstTwoSurvived(handler, result);
        var graph = Assert.IsType<GraphServiceException>(Assert.Single(result.Errors).Value);
        Assert.Equal(HttpStatusCode.NotFound, graph.StatusCode);
        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.NotFound],
            handler.ServedStatusCodes);
    }

    private static void PinAnAuthContextSwapBetweenItems()
    {
        const string Tenant = "11111111-1111-1111-1111-111111111111";
        const string AppA = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        const string AppB = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

        var handler = new MockHttpHandler();
        handler.When(r => r.Uri.Contains("/users/u1")).Respond(HttpStatusCode.OK, EntityBody("u1"));
        handler.When(r => r.Uri.Contains("/users/u3")).Respond(HttpStatusCode.OK, EntityBody("u3"));

        using var wire = new HttpClient(handler);
        // The transport seam sits above the auth probe, and the probe is what is under test, so
        // this drives the real GetClient path against the session's own client.
        using var scope = GraphSessionScope.Arm(wire, GraphSessionScope.AuthContextFor(Tenant, AppA));

        // Swapped while the second id is being answered - one item into the fan-out.
        handler.When(r => r.Uri.Contains("/users/u2")).OnAttempt(1).Respond(_ =>
        {
            scope.Session.AuthContext = GraphSessionScope.AuthContextFor(Tenant, AppB);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(EntityBody("u2"), Encoding.UTF8, "application/json")
            };
        });
        handler.When(r => r.Uri.Contains("/users/u2")).Respond(HttpStatusCode.OK, EntityBody("u2"));

        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.InvokeMgxRequest).Assembly);
        ps.Invoke();

        var (firstCount, firstVerbose) = RunFanOut(ps);
        Assert.Equal(3, firstCount);
        Assert.DoesNotContain(firstVerbose, v => v.Contains("Graph identity changed"));

        var (secondCount, secondVerbose) = RunFanOut(ps);
        Assert.Equal(3, secondCount);
        Assert.Contains(secondVerbose, v => v.Contains("Graph identity changed"));

        static (int Output, List<string> Verbose) RunFanOut(PowerShell ps)
        {
            ps.Commands.Clear();
            ps.Streams.ClearStreams();
            ps.AddScript(
                "'u1','u2','u3' | Invoke-MgxRequest -Uri '/users/{id}' -Concurrency 1 "
                + "-Verbose -WarningAction SilentlyContinue");
            var output = ps.Invoke();
            Assert.Empty(ps.Streams.Error.Select(e => e.FullyQualifiedErrorId));
            return (output.Count, [.. ps.Streams.Verbose.Select(v => v.Message)]);
        }
    }

    // ── The same faults through the {id} fan-out, over the transport seam ────

    [Theory]
    [InlineData(FaultKind.Throttled)]
    [InlineData(FaultKind.Forbidden)]
    public void A_fault_at_the_third_id_of_Invoke_MgxRequest(FaultKind kind)
    {
        var entry = FaultCatalog.Entry(kind);
        var handler = new MockHttpHandler();
        handler.When(r => r.Uri.Contains("/users/u1")).Respond(HttpStatusCode.OK, EntityBody("u1"));
        handler.When(r => r.Uri.Contains("/users/u2")).Respond(HttpStatusCode.OK, EntityBody("u2"));
        handler.When(r => r.Uri.Contains("/users/u3"), FaultName).OnAttempt(1)
               .Respond((HttpStatusCode)entry.Status, ErrorBody(entry.Status), NoBackoff());
        handler.When(r => r.Uri.Contains("/users/u3")).Respond(HttpStatusCode.OK, EntityBody("u3"));

        using (MgxTransportScope.Inject(handler, options: Options))
        {
            using var ps = PowerShell.Create();
            ps.AddCommand("Import-Module")
              .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.InvokeMgxRequest).Assembly);
            ps.Invoke();
            ps.Commands.Clear();
            ps.AddScript(
                "'u1','u2','u3' | Invoke-MgxRequest -Uri '/users/{id}' -Concurrency 1 "
                + "-WarningAction SilentlyContinue");
            var output = ps.Invoke();
            var errors = ps.Streams.Error.ToList();
            Assert.DoesNotContain(FaultName, handler.UnansweredRules);

            if (FaultCatalog.RetriedOnAnIdempotentRequest(entry))
            {
                Assert.Empty(errors);
                Assert.Equal(3, output.Count);
            }
            else
            {
                // One failed id is one non-terminating record; the other two are still emitted.
                Assert.Equal(2, output.Count);
                var record = Assert.Single(errors);
                Assert.Equal("u3", record.TargetObject);
                Assert.Equal(
                    MgxErrorPresentation.Category(entry.Class!.Value),
                    record.CategoryInfo.Category);
            }
        }
    }
}
