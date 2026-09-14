using System.Net;
using System.Text;

namespace Mgx.IntegrationTests;

/// <summary>
/// Serializes test classes that share ResiliencePipelineFactory static state.
/// Without this, xUnit runs classes in parallel and Reset() calls from one
/// class can corrupt circuit breaker/pipeline state in another.
/// </summary>
[CollectionDefinition("Pipeline")]
public class PipelineCollection;

/// <summary>
/// A request as it looked on the wire at send time. Captured eagerly because the
/// client may dispose the request content once the call completes, so reading
/// Requests[n].Content after the fact is unreliable.
/// </summary>
public sealed record CapturedRequest(
    HttpMethod Method,
    string Uri,
    IReadOnlyDictionary<string, string[]> Headers,
    IReadOnlyDictionary<string, string[]> ContentHeaders,
    byte[]? Body)
{
    public string? BodyText => Body == null ? null : Encoding.UTF8.GetString(Body);
}

/// <summary>
/// Mock HTTP handler that returns configurable responses.
/// Tracks request count for verifying retry behavior.
/// </summary>
public class MockHttpHandler : HttpMessageHandler
{
    private readonly Queue<MockReply> _responses = new();
    private readonly MockRuleSet _rules = new();
    private readonly List<HttpRequestMessage> _requests = [];
    private readonly List<CapturedRequest> _captured = [];
    private readonly List<HttpStatusCode> _served = [];
    private readonly object _lock = new();
    private MockReply? _defaultResponse;

    public int RequestCount
    {
        get { lock (_lock) { return _requests.Count; } }
    }

    public List<HttpRequestMessage> Requests
    {
        get { lock (_lock) { return [.. _requests]; } }
    }

    /// <summary>
    /// Requests buffered at send time: method, URI, headers, and body bytes.
    /// Survives the client disposing the originals.
    /// </summary>
    public List<CapturedRequest> CapturedRequests
    {
        get { lock (_lock) { return [.. _captured]; } }
    }

    /// <summary>
    /// The status codes handed back, in order. A test whose assertion is that nothing was
    /// reported needs some way to know the wire reported something to begin with. An answer
    /// that never reached the caller is not here: one thrown instead of answered, and one held
    /// past the attempt timeout that canceled the send.
    /// </summary>
    public List<HttpStatusCode> ServedStatusCodes
    {
        get { lock (_lock) { return [.. _served]; } }
    }

    /// <summary>
    /// Program a response for the requests a predicate picks out, rather than for a position in
    /// the queue. A fault can then be aimed at page 3, at one fan-out item, or at the second
    /// attempt of a request, and it lands there however the requests interleave.
    /// Precedence: a keyed entry that matches this request wins and consumes no queue entry; an
    /// unmatched request takes the queue; an empty queue takes the default response.
    /// <para>
    /// Among the keyed entries the first one that matches, in programming order, answers - so a
    /// narrower entry has to be programmed before a broader one, and a fault registered after a
    /// generator that also matches its request never answers at all. An entry that never
    /// answered is named in <see cref="UnansweredRules"/>, and a test that aims a fault should
    /// assert that list is empty: nothing else about the run says the fault was not injected.
    /// </para>
    /// <paramref name="name"/> is what those assertions call this entry, and defaults to its
    /// position in programming order.
    /// </summary>
    public MockResponseRule<MockHttpHandler> When(Func<MockRequest, bool> predicate, string? name = null)
        => _rules.When(this, predicate, name);

    /// <summary>Every keyed entry, with the requests it matched and the ones it answered.</summary>
    public IReadOnlyList<MockRuleReport> ProgrammedRules
    {
        get { lock (_lock) { return _rules.ProgrammedRules; } }
    }

    /// <summary>
    /// The keyed entries that never answered a request, by name. A fault an earlier entry
    /// shadowed leaves no other trace: the operation completes exactly as it would have with
    /// nothing programmed.
    /// </summary>
    public IReadOnlyList<string> UnansweredRules
    {
        get { lock (_lock) { return _rules.UnansweredRules; } }
    }

    public void QueueResponse(HttpStatusCode statusCode, string? body = null, Dictionary<string, string>? headers = null, string contentType = "application/json")
    {
        _responses.Enqueue(new MockReply { StatusCode = statusCode, Body = body, Headers = headers, ContentType = contentType });
    }

    /// <summary>
    /// Queue a response with a raw byte body, for binary and non-UTF8 cases.
    /// </summary>
    public void QueueBytes(HttpStatusCode statusCode, byte[] body, string contentType, Dictionary<string, string>? headers = null)
    {
        _responses.Enqueue(new MockReply { StatusCode = statusCode, BodyBytes = body, Headers = headers, ContentType = contentType });
    }

    /// <summary>
    /// Queue a response with no content at all - a 204, or a 200 with a zero-length body.
    /// </summary>
    public void QueueEmpty(HttpStatusCode statusCode, Dictionary<string, string>? headers = null)
    {
        _responses.Enqueue(new MockReply { StatusCode = statusCode, Headers = headers });
    }

    public void SetDefaultResponse(HttpStatusCode statusCode, string? body = null, Dictionary<string, string>? headers = null)
    {
        _defaultResponse = new MockReply { StatusCode = statusCode, Body = body, Headers = headers, ContentType = "application/json" };
    }

    /// <summary>
    /// Queue N failures followed by a success.
    /// </summary>
    public void QueueFailuresThenSuccess(int failCount, HttpStatusCode failStatus, string successBody, Dictionary<string, string>? failHeaders = null)
    {
        for (int i = 0; i < failCount; i++)
            QueueResponse(failStatus, null, failHeaders);
        QueueResponse(HttpStatusCode.OK, successBody);
    }

    /// <summary>
    /// Queue an exception to be thrown on the next request.
    /// Used to test retry behavior for TaskCanceledException, HttpRequestException, etc.
    /// </summary>
    public void QueueException(Exception exception)
    {
        _responses.Enqueue(new MockReply { Exception = exception });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        byte[]? bodyBytes = null;
        if (request.Content != null)
            bodyBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);

        var captured = new CapturedRequest(
            request.Method,
            request.RequestUri?.OriginalString ?? string.Empty,
            request.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray()),
            request.Content?.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray())
                ?? new Dictionary<string, string[]>(),
            bodyBytes);

        var keyed = new MockRequest(request, bodyBytes);

        MockReply mock;
        lock (_lock)
        {
            _requests.Add(request);
            _captured.Add(captured);
            // Keyed first, then the queue, then the default. A keyed answer leaves the queue
            // where it stands, so a test can aim one fault and script the rest in order.
            mock = _rules.Resolve(keyed)
                ?? (_responses.Count > 0 ? _responses.Dequeue() : _defaultResponse ?? new MockReply());
        }

        // On the send's own token: an answer held past the attempt timeout has to end as a
        // timeout, not as a mock that never returns.
        if (mock.Delay > TimeSpan.Zero)
            await Task.Delay(mock.Delay, cancellationToken);

        if (mock.Exception != null)
            throw mock.Exception;

        var response = mock.CreateResponse(keyed);
        // Recorded here rather than where the answer was chosen, so the list says what the
        // handler handed back: an answer held past an attempt timeout is never handed back at
        // all, a thrown one never has a status, and two held answers come back in an order the
        // one they resolved in does not predict.
        lock (_lock) _served.Add(response.StatusCode);

        return response;
    }
}

public static class TestData
{
    public static string UsersPage1 => """
    {
        "value": [
            {"id": "user1", "displayName": "User One"},
            {"id": "user2", "displayName": "User Two"}
        ],
        "@odata.nextLink": "https://graph.microsoft.com/v1.0/users?$skiptoken=page2"
    }
    """;

    public static string UsersPage2 => """
    {
        "value": [
            {"id": "user3", "displayName": "User Three"}
        ]
    }
    """;

    public static string SingleUser => """
    {
        "id": "user1",
        "displayName": "User One",
        "userPrincipalName": "user1@test.com"
    }
    """;

    public static string EmptyCollection => """
    {
        "value": []
    }
    """;
}
