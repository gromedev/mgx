using System.Net;
using System.Text;

namespace Mgx.IntegrationTests.Fakes;

/// <summary>
/// Scripted HttpMessageHandler for testing the resilience pipeline without network access.
/// Responses are dequeued in order; the last response repeats once the queue is drained,
/// so a test only has to script the responses it actually cares about.
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
    private readonly MockRuleSet _rules = new();
    private Func<HttpRequestMessage, HttpResponseMessage>? _last;
    private readonly Lock _sync = new();

    /// <summary>Method and URI of every request the handler received, in order.</summary>
    public List<(HttpMethod Method, string Uri)> Requests { get; } = [];

    public int RequestCount
    {
        get { lock (_sync) return Requests.Count; }
    }

    /// <summary>
    /// Program a response for the requests a predicate picks out, rather than for a position in
    /// the script, with the same precedence <see cref="MockHttpHandler"/> uses: a keyed entry
    /// that matches this request wins and consumes no scripted response; an unmatched request
    /// takes the script, whose last entry repeats as it always has.
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
    public MockResponseRule<StubHttpMessageHandler> When(
        Func<MockRequest, bool> predicate, string? name = null)
        => _rules.When(this, predicate, name);

    /// <summary>Every keyed entry, with the requests it matched and the ones it answered.</summary>
    public IReadOnlyList<MockRuleReport> ProgrammedRules
    {
        get { lock (_sync) return _rules.ProgrammedRules; }
    }

    /// <summary>
    /// The keyed entries that never answered a request, by name. A fault an earlier entry
    /// shadowed leaves no other trace: the operation completes exactly as it would have with
    /// nothing programmed.
    /// </summary>
    public IReadOnlyList<string> UnansweredRules
    {
        get { lock (_sync) return _rules.UnansweredRules; }
    }

    public StubHttpMessageHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage> factory)
    {
        _responses.Enqueue(factory);
        return this;
    }

    /// <summary>Queue a response with a JSON body.</summary>
    public StubHttpMessageHandler EnqueueJson(HttpStatusCode status, string json) =>
        Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });

    /// <summary>Queue a status-only response, optionally with a Retry-After header in seconds.</summary>
    public StubHttpMessageHandler EnqueueStatus(HttpStatusCode status, int? retryAfterSeconds = null) =>
        Enqueue(_ =>
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            if (retryAfterSeconds.HasValue)
                response.Headers.Add("Retry-After", retryAfterSeconds.Value.ToString());
            return response;
        });

    /// <summary>Queue the same response factory <paramref name="count"/> times.</summary>
    public StubHttpMessageHandler EnqueueRepeated(int count, Func<HttpRequestMessage, HttpResponseMessage> factory)
    {
        for (var i = 0; i < count; i++)
            Enqueue(factory);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Only read when something is programmed to look: a predicate over a $batch envelope
        // needs the bytes, and a handler with no keyed entries touches the request exactly as
        // it did before there were any.
        byte[]? bodyBytes = null;
        if (!_rules.IsEmpty && request.Content != null)
            bodyBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var keyed = new MockRequest(request, bodyBytes);

        MockReply? reply;
        Func<HttpRequestMessage, HttpResponseMessage>? factory = null;
        lock (_sync)
        {
            // Record method/uri rather than the message: HttpClient disposes the
            // request once the call completes, so the object is not safe to keep.
            Requests.Add((request.Method, request.RequestUri?.ToString() ?? string.Empty));

            reply = _rules.Resolve(keyed);
            if (reply == null)
            {
                if (_responses.Count > 0)
                    _last = _responses.Dequeue();

                factory = _last ?? throw new InvalidOperationException(
                    "StubHttpMessageHandler received a request but no response was scripted.");
            }
        }

        if (reply == null)
            return factory!(request);

        // On the send's own token, so an answer held past the attempt timeout ends as a timeout.
        if (reply.Delay > TimeSpan.Zero)
            await Task.Delay(reply.Delay, cancellationToken);

        if (reply.Exception != null)
            throw reply.Exception;

        return reply.CreateResponse(keyed);
    }
}
