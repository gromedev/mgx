using System.Net.Sockets;
using System.Text.Json;
using Mgx.Engine.Errors;
using Mgx.Engine.Http;
using Polly.Timeout;

namespace Mgx.IntegrationTests;

/// <summary>
/// The fault classes an operation has to survive, named once. Each one says how the mock
/// produces it and what <see cref="MgxErrorClassifier"/> makes of it; what the operation is
/// then expected to do comes from <see cref="MgxErrorPolicy"/> at the point of injection,
/// never from a status list restated in a test.
/// </summary>
public enum FaultKind
{
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
    Throttled,
    ServerError,
    ConnectionReset,
    AttemptTimeout,
    BodyTimeout,
    MalformedJson,
    RepeatedNextLink,
    UnusableNextLink,
    ExpiredToken,
    ChangedAuthContext,
    ConsistencyDelay,
}

/// <summary>How the mock produces a fault, which is also what decides what the classifier sees.</summary>
public enum FaultShape
{
    /// <summary>An HTTP status, on the request or inside a $batch envelope.</summary>
    Status,

    /// <summary>An exception thrown instead of an answer.</summary>
    Transport,

    /// <summary>An answer held past the per-attempt timeout.</summary>
    HeldPastAttemptTimeout,

    /// <summary>Headers, then a body that delivers a prefix and stops.</summary>
    StalledBody,

    /// <summary>A 200 whose body stops mid-document.</summary>
    TruncatedJson,

    /// <summary>A page whose @odata.nextLink is its own URL.</summary>
    SelfNextLink,

    /// <summary>A page whose @odata.nextLink is not a link mgx will follow.</summary>
    UnusableNextLink,

    /// <summary>The session's AuthContext object replaced while the operation runs.</summary>
    SwappedAuthContext,
}

/// <summary>The four points a fault is injected at.</summary>
public enum InjectionPoint
{
    /// <summary>Page N of a pagination, after earlier pages were delivered.</summary>
    Page,

    /// <summary>Item N of a fan-out, after earlier items succeeded.</summary>
    FanOutItem,

    /// <summary>Subrequest N of a $batch, after earlier subrequests were answered.</summary>
    BatchSubrequest,

    /// <summary>Mid-body of a content download, after earlier bytes arrived.</summary>
    DownloadBody,
}

public enum FaultCoverage
{
    /// <summary>The case runs and asserts the outcome the policy's decision implies.</summary>
    Covered,

    /// <summary>The case runs and records today's behavior, which the policy does not decide.</summary>
    Pinned,

    /// <summary>The fault cannot land at this point. <see cref="FaultCell.Note"/> says why.</summary>
    NotApplicable,
}

/// <summary>What one fault does at one injection point, and the shape it takes there.</summary>
public sealed record FaultCell(FaultCoverage Coverage, string Note);

/// <summary>One row of the catalog: a fault, how it is produced, and where it can land.</summary>
public sealed record FaultEntry
{
    public required FaultKind Kind { get; init; }

    public required FaultShape Shape { get; init; }

    /// <summary>The status the fault carries, or 0 when it carries none.</summary>
    public int Status { get; init; }

    /// <summary>The exception the fault is thrown as, for <see cref="FaultShape.Transport"/>.</summary>
    public Func<Exception>? Transport { get; init; }

    /// <summary>
    /// The class <see cref="MgxErrorClassifier"/> assigns, or null when the classifier never
    /// sees this fault at all - a self-referencing nextLink and a swapped auth context are not
    /// failures of a request. <see cref="FaultInjectionCatalogTests"/> holds each non-null value
    /// to what the classifier actually answers, so this column cannot drift away from it.
    /// </summary>
    public MgxErrorClass? Class { get; init; }

    /// <summary>How the mock produces it.</summary>
    public required string Production { get; init; }

    public required IReadOnlyDictionary<InjectionPoint, FaultCell> Cells { get; init; }

    public FaultCell At(InjectionPoint point) => Cells[point];
}

/// <summary>
/// The one fault catalog. The four injection-point theories read it; each of them iterates
/// every kind, so a cell that cannot be reached shows up as an explicit "not applicable" row
/// with its reason rather than as a case nobody wrote.
/// </summary>
public static class FaultCatalog
{
    private static FaultCell Covered(string note) => new(FaultCoverage.Covered, note);

    private static FaultCell Pinned(string note) => new(FaultCoverage.Pinned, note);

    private static FaultCell NotApplicable(string why) => new(FaultCoverage.NotApplicable, why);

    private const string StatusNotInABody =
        "a status arrives on the response line, before the first byte of the body; once bytes "
        + "are flowing there is no status left to change. Hop 2's own 401/403 - the expired "
        + "pre-authenticated URL - is a request-start fault, held in GraphContentClientTests.";

    private const string NoLinksInABatch =
        "the batch client never reads @odata.nextLink out of a sub-response; nothing in a $batch "
        + "follows a link, so there is no link to poison.";

    private const string NoLinksInADownload = "a content download has no pages and follows no link.";

    private static IReadOnlyDictionary<InjectionPoint, FaultCell> Cells(
        FaultCell page, FaultCell fanOut, FaultCell batch, FaultCell download) =>
        new Dictionary<InjectionPoint, FaultCell>
        {
            [InjectionPoint.Page] = page,
            [InjectionPoint.FanOutItem] = fanOut,
            [InjectionPoint.BatchSubrequest] = batch,
            [InjectionPoint.DownloadBody] = download,
        };

    /// <summary>A plain status fault: the same shape at the first three points, impossible mid-body.</summary>
    private static FaultEntry StatusEntry(FaultKind kind, int status, MgxErrorClass cls) => new()
    {
        Kind = kind,
        Shape = FaultShape.Status,
        Status = status,
        Class = cls,
        Production = $"the request is answered {status}",
        Cells = Cells(
            Covered($"page 3 is answered {status} after pages 1 and 2 were delivered"),
            Covered($"the third url is answered {status} after the first two succeeded"),
            Covered($"sub-response 23 carries status {status} inside the chunk's 200 envelope"),
            NotApplicable(StatusNotInABody)),
    };

    public static IReadOnlyList<FaultEntry> Entries { get; } =
    [
        StatusEntry(FaultKind.Unauthorized, 401, MgxErrorClass.Authentication),
        StatusEntry(FaultKind.Forbidden, 403, MgxErrorClass.Authorization),
        StatusEntry(FaultKind.NotFound, 404, MgxErrorClass.NotFound),
        StatusEntry(FaultKind.Conflict, 409, MgxErrorClass.Conflict),
        StatusEntry(FaultKind.Throttled, 429, MgxErrorClass.Throttle),
        StatusEntry(FaultKind.ServerError, 503, MgxErrorClass.TransientServer),

        new FaultEntry
        {
            Kind = FaultKind.ConnectionReset,
            Shape = FaultShape.Transport,
            Transport = () => new HttpRequestException(
                "The connection was reset.", new SocketException((int)SocketError.ConnectionReset)),
            Class = MgxErrorClass.TransientTransport,
            Production = "the send throws HttpRequestException over a reset SocketException",
            Cells = Cells(
                Covered("the page-3 GET is thrown on instead of answered"),
                Covered("the third url's GET is thrown on instead of answered"),
                Covered("the chunk carrying subrequest 23 has its own POST thrown on - a "
                    + "sub-response cannot carry a transport failure, only a status"),
                Covered("the body throws IOException after delivering its first bytes; a reset "
                    + "mid-stream reaches the reader as an IOException, not as the "
                    + "HttpRequestException a failed send raises, and both classify the same")),
        },

        new FaultEntry
        {
            Kind = FaultKind.AttemptTimeout,
            Shape = FaultShape.HeldPastAttemptTimeout,
            Class = MgxErrorClass.TransientTransport,
            Production = "the answer is held past AttemptTimeoutSeconds, so the pipeline's "
                + "per-attempt timeout raises TimeoutRejectedException",
            Cells = Cells(
                Covered("page 3's answer is held past the attempt timeout"),
                Covered("the third url's answer is held past the attempt timeout"),
                Covered("the chunk carrying subrequest 23 has its POST held past the attempt timeout"),
                NotApplicable(
                    "the per-attempt timeout ends when the response headers arrive; past that "
                    + "only the per-read idle timeout bounds the body, which is the body-timeout row")),
        },

        new FaultEntry
        {
            Kind = FaultKind.BodyTimeout,
            Shape = FaultShape.StalledBody,
            Class = MgxErrorClass.TransientTransport,
            Production = "headers arrive, then the body delivers a prefix and stops without closing",
            Cells = Cells(
                Pinned("page 3's body stalls. The class is retryable for a GET, but the body is "
                    + "read after the resilience pipeline has already returned the response, so "
                    + "nothing retries it and the pagination ends at the stall"),
                Pinned("the third url's body stalls, and for the same reason nothing retries it"),
                Covered("the chunk's envelope stalls; the chunk POST is not idempotent, so the "
                    + "policy does not retry it either way"),
                Pinned("the download body stalls mid-stream. ShouldRetryDownload answers true for "
                    + "the HttpRequestException the idle timeout raises, but the copy runs "
                    + "outside the download pipeline, so nothing retries it")),
        },

        new FaultEntry
        {
            Kind = FaultKind.MalformedJson,
            Shape = FaultShape.TruncatedJson,
            Class = MgxErrorClass.Permanent,
            Production = "a 200 whose body stops in the middle of the value array",
            Cells = Cells(
                Covered("page 3 is answered 200 with a body that stops mid-array"),
                Covered("the third url is answered 200 with a body that stops mid-array"),
                Covered("the chunk's envelope stops mid-array"),
                NotApplicable("download bytes are opaque; nothing on the content path parses them")),
        },

        new FaultEntry
        {
            Kind = FaultKind.RepeatedNextLink,
            Shape = FaultShape.SelfNextLink,
            Class = null,
            Production = "a page whose @odata.nextLink is its own URL",
            Cells = Cells(
                Pinned("page 3 points back at page 3. Nothing stops a self-loop of non-empty "
                    + "pages today - only -Top / maxItems ends it; an empty self-loop ends at "
                    + "the consecutive-empty-page limit"),
                Pinned("the third url's page points back at itself, ended the same two ways"),
                NotApplicable(NoLinksInABatch),
                NotApplicable(NoLinksInADownload)),
        },

        new FaultEntry
        {
            Kind = FaultKind.UnusableNextLink,
            Shape = FaultShape.UnusableNextLink,
            Class = MgxErrorClass.Permanent,
            Production = "a page whose @odata.nextLink is not an absolute https URL, which "
                + "NextLinkValidator refuses with an InvalidOperationException",
            Cells = Cells(
                Covered("page 3 carries a nextLink that is not a URL"),
                Covered("the third url's page carries a nextLink that is not a URL"),
                NotApplicable(NoLinksInABatch),
                NotApplicable(NoLinksInADownload)),
        },

        new FaultEntry
        {
            Kind = FaultKind.ExpiredToken,
            Shape = FaultShape.Status,
            Status = 401,
            Class = MgxErrorClass.Authentication,
            Production = "a 401 that arrives only after earlier requests of the same operation "
                + "were answered - the token expired while the operation was running",
            Cells = Cells(
                Pinned("page 3 is answered 401 after two pages were delivered: no retry, no "
                    + "re-authentication, and the pages already delivered stand"),
                Pinned("the third url is answered 401 after the first two succeeded"),
                Pinned("sub-response 23 carries 401 after the earlier subrequests were answered"),
                NotApplicable(StatusNotInABody)),
        },

        new FaultEntry
        {
            Kind = FaultKind.ChangedAuthContext,
            Shape = FaultShape.SwappedAuthContext,
            Class = null,
            Production = "the Graph session's AuthContext object is replaced with one carrying a "
                + "different application while the operation is running",
            Cells = Cells(
                Pinned("the session's AuthContext is swapped while page 2 is being answered; the "
                    + "running invocation finishes on the client it resolved, and the next "
                    + "invocation is the one that notices"),
                Pinned("the session's AuthContext is swapped while the second id is being "
                    + "answered, with the same outcome"),
                NotApplicable(
                    "the auth context is read once per invocation, in GetClient, before the "
                    + "first request goes out; no chunk POST re-reads it, so a swap has no "
                    + "subrequest-level point to land at. What it does to a running invocation "
                    + "is pinned at the page and fan-out points."),
                NotApplicable(
                    "hop 2 is token-free by construction - no Authorization header, no auth "
                    + "context - so nothing about the download body depends on one")),
        },

        new FaultEntry
        {
            Kind = FaultKind.ConsistencyDelay,
            Shape = FaultShape.Status,
            Status = 404,
            Class = MgxErrorClass.NotFound,
            Production = "a 404 on the first attempt that would be a 200 on any later one - the "
                + "read-after-write window",
            Cells = Cells(
                Pinned("page 3 answers 404 first and 200 afterwards; the classifier assigns "
                    + "NotFound and the policy does not retry, so the 200 is never asked for"),
                Pinned("the third url answers 404 first and 200 afterwards, with the same outcome"),
                Pinned("sub-response 23 carries 404, which the batch item loop does not retry"),
                NotApplicable(StatusNotInABody)),
        },
    ];

    public static FaultEntry Entry(FaultKind kind) =>
        Entries.SingleOrDefault(e => e.Kind == kind)
        ?? throw new InvalidOperationException($"No catalog entry for {kind}.");

    /// <summary>Every kind, for a theory that has to show a row per kind at its own point.</summary>
    public static TheoryData<FaultKind> AllKinds
    {
        get
        {
            var data = new TheoryData<FaultKind>();
            foreach (var kind in Enum.GetValues<FaultKind>())
                data.Add(kind);
            return data;
        }
    }

    // ── The policy's decisions, asked rather than restated ──────────────────

    /// <summary>What the retry policy says about this fault on an idempotent request (a GET page,
    /// a fan-out read).</summary>
    public static bool RetriedOnAnIdempotentRequest(FaultEntry entry) =>
        entry.Class is { } cls && MgxErrorPolicy.ShouldRetry(cls, isIdempotent: true);

    /// <summary>What it says on a non-idempotent request - the $batch POST itself.</summary>
    public static bool RetriedOnANonIdempotentRequest(FaultEntry entry) =>
        entry.Class is { } cls && MgxErrorPolicy.ShouldRetry(cls, isIdempotent: false);

    /// <summary>What it says about a $batch sub-response carrying this fault's status.</summary>
    public static bool RetriedAsABatchItem(FaultEntry entry, string method) =>
        entry.Class is { } cls && MgxErrorPolicy.ShouldRetry(
            cls, isIdempotent: !string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase));

    /// <summary>What the download-host retry filter says about it.</summary>
    public static bool RetriedByTheDownloadPipeline(FaultEntry entry, Exception? exception) =>
        entry.Class is { } cls
        && MgxErrorPolicy.ShouldRetryDownload(new MgxErrorInfo(cls, entry.Status), exception);

    /// <summary>Whether this fault counts toward opening the circuit.</summary>
    public static bool CountsAgainstTheBreaker(FaultEntry entry) =>
        entry.Class is { } cls && MgxErrorPolicy.CountsAsCircuitFailure(new MgxErrorInfo(cls, entry.Status));

    /// <summary>
    /// What the classifier answers for the fault this row produces. The <see cref="FaultEntry.Class"/>
    /// column is held to this, so the catalog cannot claim a class the classifier does not assign.
    /// </summary>
    public static MgxErrorClass? ClassifierAnswer(FaultEntry entry) => entry.Shape switch
    {
        FaultShape.Status => MgxErrorClassifier.Classify(entry.Status).Class,
        FaultShape.Transport => MgxErrorClassifier.Classify(entry.Transport!(), cancellationRequested: false).Class,
        // The pipeline's per-attempt timeout, as Polly raises it.
        FaultShape.HeldPastAttemptTimeout =>
            MgxErrorClassifier.Classify(new TimeoutRejectedException(), cancellationRequested: false).Class,
        // What ResilientGraphClient turns a timed-out body read into.
        FaultShape.StalledBody => MgxErrorClassifier.Classify(
            new HttpRequestException(ResilientGraphClient.BodyReadTimedOutMessage),
            cancellationRequested: false).Class,
        FaultShape.TruncatedJson => MgxErrorClassifier.Classify(
            MalformedJsonException(), cancellationRequested: false).Class,
        // What NextLinkValidator.ValidateOrThrow raises.
        FaultShape.UnusableNextLink => MgxErrorClassifier.Classify(
            new InvalidOperationException("Pagination stopped"), cancellationRequested: false).Class,
        _ => null,
    };

    /// <summary>A real JsonException, from a real truncated document rather than a constructed one.</summary>
    public static JsonException MalformedJsonException()
    {
        try
        {
            JsonDocument.Parse(TruncatedPageBody).Dispose();
        }
        catch (JsonException ex)
        {
            return ex;
        }
        throw new InvalidOperationException("The truncated body in the catalog now parses.");
    }

    /// <summary>A 200 body that stops in the middle of the value array.</summary>
    public const string TruncatedPageBody = """{"value":[{"id":"u5""";
}

/// <summary>
/// The catalog's own guards: the table has a row per kind, every row's class is the class the
/// classifier assigns, and nothing is marked impossible everywhere.
/// </summary>
public class FaultInjectionCatalogTests
{
    [Theory]
    [MemberData(nameof(FaultCatalog.AllKinds), MemberType = typeof(FaultCatalog))]
    public void Every_kind_has_exactly_one_row(FaultKind kind)
    {
        var entry = FaultCatalog.Entry(kind);
        Assert.Equal(kind, entry.Kind);
        Assert.False(string.IsNullOrWhiteSpace(entry.Production));
        foreach (var point in Enum.GetValues<InjectionPoint>())
        {
            var cell = entry.At(point);
            Assert.False(string.IsNullOrWhiteSpace(cell.Note),
                $"{kind} at {point} has no note saying what shape it takes or why it cannot land there");
        }
    }

    [Theory]
    [MemberData(nameof(FaultCatalog.AllKinds), MemberType = typeof(FaultCatalog))]
    public void The_class_a_row_claims_is_the_one_the_classifier_assigns(FaultKind kind)
    {
        // The catalog names the class so the table can be read on its own; this holds that
        // column to MgxErrorClassifier rather than letting it become a second opinion.
        // ErrorPolicyParityTests owns status-to-decision for every status; nothing here
        // restates those rows.
        var entry = FaultCatalog.Entry(kind);
        Assert.Equal(entry.Class, FaultCatalog.ClassifierAnswer(entry));
    }

    [Theory]
    [MemberData(nameof(FaultCatalog.AllKinds), MemberType = typeof(FaultCatalog))]
    public void No_kind_is_impossible_at_every_point(FaultKind kind)
    {
        // "Where physically possible" has to be auditable from the table, which means a kind
        // that is not applicable anywhere would be a kind nobody covers at all.
        var entry = FaultCatalog.Entry(kind);
        Assert.Contains(Enum.GetValues<InjectionPoint>(),
            point => entry.At(point).Coverage != FaultCoverage.NotApplicable);
    }

    [Fact]
    public void The_consistency_class_still_has_no_producer()
    {
        // MgxErrorClass.Consistency is declared for the read-after-write window and nothing
        // assigns it yet, which is why the eventual-consistency rows below record a NotFound
        // that is not retried instead of asserting a retry. The producer is 2.3.0 work.
        for (var status = 100; status < 600; status++)
            Assert.NotEqual(MgxErrorClass.Consistency, MgxErrorClassifier.Classify(status).Class);

        Exception[] shapes =
        [
            new HttpRequestException(),
            new SocketException((int)SocketError.ConnectionReset),
            new IOException(),
            new TaskCanceledException(),
            new TimeoutRejectedException(),
            new InvalidOperationException(),
        ];
        foreach (var exception in shapes)
        {
            Assert.NotEqual(MgxErrorClass.Consistency,
                MgxErrorClassifier.Classify(exception, cancellationRequested: false).Class);
            Assert.NotEqual(MgxErrorClass.Consistency,
                MgxErrorClassifier.Classify(exception, cancellationRequested: true).Class);
        }
    }
}
