using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Mgx.IntegrationTests;

/// <summary>
/// One request as a rule predicate sees it: the message itself, plus the body bytes the
/// handler has already read. A predicate over a $batch envelope needs those bytes, and it
/// cannot await them - the handler reads the content once, so a predicate that read it again
/// would be reading a stream the client is entitled to have disposed.
/// </summary>
public sealed class MockRequest(HttpRequestMessage message, byte[]? body)
{
    /// <summary>The request as sent. Everything below is shorthand onto it.</summary>
    public HttpRequestMessage Message => message;

    public HttpMethod Method => message.Method;

    public Uri? RequestUri => message.RequestUri;

    /// <summary>The URI as the caller wrote it, absolute or relative.</summary>
    public string Uri => message.RequestUri?.OriginalString ?? string.Empty;

    public HttpRequestHeaders Headers => message.Headers;

    /// <summary>The request body, or null when the request carried no content.</summary>
    public byte[]? Body => body;

    public string? BodyText => body == null ? null : Encoding.UTF8.GetString(body);
}

/// <summary>
/// What a handler answers one request with, whether the entry came from the queue or from a
/// request-keyed rule. Both fakes render it the same way, so a queued response and a keyed
/// response are indistinguishable on the wire.
/// </summary>
public sealed record MockReply
{
    public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;

    public string? Body { get; init; }

    public byte[]? BodyBytes { get; init; }

    public string? ContentType { get; init; }

    public Dictionary<string, string>? Headers { get; init; }

    /// <summary>Thrown instead of answering. Nothing is recorded as served.</summary>
    public Exception? Exception { get; init; }

    /// <summary>
    /// Held before the answer is handed back, so an attempt timeout has something to fire on.
    /// The wait takes the send's own token, which is what makes it a timeout rather than a hang.
    /// </summary>
    public TimeSpan Delay { get; init; }

    /// <summary>
    /// The body delivers <see cref="Body"/> or <see cref="BodyBytes"/> and then stops without
    /// closing, leaving the body-read timeout as the only thing that ends the read.
    /// </summary>
    public bool StallBody { get; init; }

    /// <summary>
    /// Computes the whole response from the request, for answers too numerous to enumerate in
    /// advance. Takes precedence over every field here except <see cref="Delay"/>.
    /// </summary>
    public Func<MockRequest, HttpResponseMessage>? Factory { get; init; }

    internal HttpResponseMessage CreateResponse(MockRequest request)
    {
        if (Factory != null)
        {
            var computed = Factory(request);
            computed.RequestMessage ??= request.Message;
            return computed;
        }

        // Real transports (SocketsHttpHandler) set RequestMessage on the response; consumers
        // like the pacer's OnRetry hook read the request URI off it. Mirror that here.
        var response = new HttpResponseMessage(StatusCode) { RequestMessage = request.Message };
        if (StallBody)
        {
            var stalling = new StallingContent(BodyBytes ?? Encoding.UTF8.GetBytes(Body ?? "{"));
            stalling.Headers.TryAddWithoutValidation("Content-Type", ContentType ?? "application/json");
            response.Content = stalling;
        }
        else if (BodyBytes != null)
        {
            response.Content = new ByteArrayContent(BodyBytes);
            if (ContentType != null)
                response.Content.Headers.TryAddWithoutValidation("Content-Type", ContentType);
        }
        else if (Body != null)
        {
            response.Content = new StringContent(Body, Encoding.UTF8, ContentType ?? "application/json");
        }

        if (Headers != null)
        {
            foreach (var (key, value) in Headers)
                response.Headers.TryAddWithoutValidation(key, value);
        }

        return response;
    }
}

/// <summary>One request-keyed entry: which requests it answers, and with what.</summary>
internal sealed class MockRule
{
    public required Func<MockRequest, bool> Predicate { get; init; }

    /// <summary>
    /// What an assertion calls this entry. A predicate cannot be printed, so the name is the
    /// only thing a failure message can carry; null until the rule set fills in its position.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>Null answers every matching request; N answers only the Nth, 1-based.</summary>
    public int? Attempt { get; set; }

    public MockReply Reply { get; set; } = new();

    /// <summary>How many requests have matched this entry's predicate so far.</summary>
    public int Matches { get; set; }

    /// <summary>How many of those this entry was the one to answer.</summary>
    public int Answers { get; set; }
}

/// <summary>
/// One programmed entry as an assertion sees it: what it was called, how many requests matched
/// its predicate, and how many of those it answered. A matched entry that answered nothing was
/// shadowed by an earlier one, or scoped to an attempt that never came.
/// </summary>
public sealed record MockRuleReport(string Name, int Matches, int Answers)
{
    public override string ToString() => $"{Name} (matched {Matches}, answered {Answers})";
}

/// <summary>
/// One request-keyed entry under construction. <c>OnAttempt</c> and <c>AfterDelay</c> narrow
/// it and must come before the answer; a <c>Respond</c> or <c>Throw</c> call arms it and hands
/// back the handler, so entries chain.
/// </summary>
public sealed class MockResponseRule<TOwner>
{
    private readonly MockRuleSet _rules;
    private readonly MockRule _rule;
    private readonly TOwner _owner;
    private TimeSpan _delay;

    internal MockResponseRule(MockRuleSet rules, MockRule rule, TOwner owner)
    {
        _rules = rules;
        _rule = rule;
        _owner = owner;
    }

    /// <summary>
    /// Answer only the <paramref name="attempt"/>th request matching this entry's predicate,
    /// 1-based. The counting is per entry, so the requests another entry answered still count
    /// here and nowhere else; every other attempt falls through to whatever answers next - a
    /// later entry, the queue, or the default.
    /// </summary>
    public MockResponseRule<TOwner> OnAttempt(int attempt)
    {
        _rule.Attempt = attempt;
        return this;
    }

    /// <summary>Hold the answer this long before handing it back.</summary>
    public MockResponseRule<TOwner> AfterDelay(TimeSpan delay)
    {
        _delay = delay;
        return this;
    }

    /// <summary>Answer with a status and an optional body.</summary>
    public TOwner Respond(
        HttpStatusCode statusCode,
        string? body = null,
        Dictionary<string, string>? headers = null,
        string contentType = "application/json")
        => Arm(new MockReply
        {
            StatusCode = statusCode,
            Body = body,
            Headers = headers,
            ContentType = contentType
        });

    /// <summary>Answer with a raw byte body, for binary and non-UTF8 cases.</summary>
    public TOwner RespondBytes(
        HttpStatusCode statusCode,
        byte[] body,
        string contentType,
        Dictionary<string, string>? headers = null)
        => Arm(new MockReply
        {
            StatusCode = statusCode,
            BodyBytes = body,
            ContentType = contentType,
            Headers = headers
        });

    /// <summary>Answer with no content at all - a 204, or a 200 with a zero-length body.</summary>
    public TOwner RespondEmpty(HttpStatusCode statusCode, Dictionary<string, string>? headers = null)
        => Arm(new MockReply { StatusCode = statusCode, Headers = headers });

    /// <summary>
    /// Compute the answer from the request. What a page-generating fake is built on: 100,000
    /// items are a function of the skip token, not a hundred thousand queued responses.
    /// </summary>
    public TOwner Respond(Func<MockRequest, HttpResponseMessage> factory)
        => Arm(new MockReply { Factory = factory });

    /// <summary>
    /// Answer with headers and a body that delivers <paramref name="prefix"/> and then stops
    /// without closing, so the body-read timeout is the only thing that ends the read.
    /// </summary>
    public TOwner RespondStalled(
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string prefix = "{",
        string contentType = "application/json",
        Dictionary<string, string>? headers = null)
        => Arm(new MockReply
        {
            StatusCode = statusCode,
            Body = prefix,
            ContentType = contentType,
            Headers = headers,
            StallBody = true
        });

    /// <summary>Throw instead of answering, for the transport failures that never reach a status.</summary>
    public TOwner Throw(Exception exception) => Arm(new MockReply { Exception = exception });

    private TOwner Arm(MockReply reply)
    {
        _rule.Reply = reply with { Delay = _delay };
        _rules.Add(_rule);
        return _owner;
    }
}

/// <summary>
/// The request-keyed entries a handler answers from, in the order they were programmed. Not
/// synchronized on its own: a handler resolves under the same lock it already holds for its
/// queue, so one request takes exactly one answer.
/// </summary>
public sealed class MockRuleSet
{
    private readonly List<MockRule> _rules = [];

    /// <summary>Nothing is programmed, so a handler can skip the work of preparing a match.</summary>
    public bool IsEmpty => _rules.Count == 0;

    internal void Add(MockRule rule)
    {
        rule.Name ??= $"#{_rules.Count + 1}";
        _rules.Add(rule);
    }

    /// <summary>
    /// Start an entry keyed on <paramref name="predicate"/>. <paramref name="name"/> is what
    /// assertions and failure messages call it, and defaults to its position in programming
    /// order, 1-based, as <c>#1</c>.
    /// </summary>
    public MockResponseRule<TOwner> When<TOwner>(
        TOwner owner, Func<MockRequest, bool> predicate, string? name = null)
        => new(this, new MockRule { Predicate = predicate, Name = name }, owner);

    /// <summary>
    /// Every entry in programming order, with the requests that matched it and the ones it
    /// answered. Read after the operation under test has finished: the counts are live.
    /// </summary>
    public IReadOnlyList<MockRuleReport> ProgrammedRules =>
        [.. _rules.Select(rule => new MockRuleReport(rule.Name!, rule.Matches, rule.Answers))];

    /// <summary>
    /// The entries that never answered a request, by name. An entry a broader one programmed
    /// before it shadows is here and nowhere else: the operation it was aimed at completes,
    /// every item arrives, and no other observation says the fault was never injected.
    /// </summary>
    public IReadOnlyList<string> UnansweredRules =>
        [.. _rules.Where(rule => rule.Answers == 0).Select(rule => rule.Name!)];

    /// <summary>
    /// The first entry whose predicate matches and whose attempt scope selects this request, or
    /// null when none does. Every matching entry's counter advances whether or not it is the one
    /// that answers, so an entry's attempt index means the Nth request matching that entry and
    /// nothing else - one entry's faults never renumber another's.
    /// </summary>
    internal MockReply? Resolve(MockRequest request)
    {
        MockReply? chosen = null;
        foreach (var rule in _rules)
        {
            if (!rule.Predicate(request)) continue;
            var matched = ++rule.Matches;
            if (chosen != null) continue;
            if (rule.Attempt is int scoped && scoped != matched) continue;
            rule.Answers++;
            chosen = rule.Reply;
        }
        return chosen;
    }
}
