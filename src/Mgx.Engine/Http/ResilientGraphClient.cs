using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.RateLimiting;
using Mgx.Engine.Models;
using Polly;

namespace Mgx.Engine.Http;

/// <summary>
/// HTTP client for Microsoft Graph with Polly 8.x retry, circuit breaker, and rate limiting.
/// Wraps an existing HttpClient (from GraphSession) with retry, circuit breaker, and rate limiting.
/// Pipeline and rate limiter are shared across invocations via ResiliencePipelineFactory
/// so circuit breaker accumulates failure history and rate limiter tracks token consumption.
/// </summary>
public sealed class ResilientGraphClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;
    private readonly TokenBucketRateLimiter? _rateLimiter;
    private readonly ConcurrentQueue<string> _pendingVerbose = new();
    private readonly ConcurrentQueue<string> _pendingWarnings = new();
    private readonly ConcurrentQueue<string> _pendingDebug = new();

    /// <summary>
    /// Maximum request body size (4MB). Bodies on this path are JSON to Graph endpoints,
    /// where 4MB is the service's own cap, so refusing early is a favor. The resilience
    /// wrap deliberately differs: it carries SDK requests (content uploads run to 250MB)
    /// and passes an over-cap body to the SDK untouched instead of refusing it.
    /// </summary>
    internal const int MaxRequestBodyBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Timeout for reading response bodies after headers have been received.
    /// ResponseHeadersRead means SendAsync returns immediately after headers arrive;
    /// the body is read lazily. Without this timeout, a stalled body stream hangs forever
    /// because HttpClient.Timeout and Polly's TotalTimeout only cover the SendAsync call.
    /// </summary>
    public TimeSpan BodyReadTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Where one request is, as a single word. Entering an attempt and the caller's stop each
    /// take it with one exchange, so the two orderings are the only ones that exist: the stop
    /// arrives while the request is between attempts and ends the wait, or it finds the attempt
    /// already on the wire and leaves it - and its answer - alone.
    /// </summary>
    private static class SendState
    {
        /// <summary>Nothing has gone out. The first attempt is the caller's own to guard.</summary>
        public const int BeforeFirstAttempt = 0;

        /// <summary>An attempt is on the wire, or its answer is still being handed back.</summary>
        public const int InAttempt = 1;

        /// <summary>An attempt is behind it and another may be waiting out a backoff.</summary>
        public const int BetweenAttempts = 2;

        /// <summary>The stop took the request between attempts. Nothing further goes out.</summary>
        public const int Stopped = 3;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Create a client with a shared (externally-managed) pipeline and rate limiter.
    /// Used by MgxCmdletBase for cross-invocation circuit breaker and rate limiting.
    /// Caller is responsible for pipeline/rate limiter lifecycle.
    /// </summary>
    public ResilientGraphClient(
        HttpClient httpClient,
        ResiliencePipeline<HttpResponseMessage> pipeline,
        TokenBucketRateLimiter? rateLimiter)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _rateLimiter = rateLimiter;
    }

    /// <summary>
    /// Create a client using the shared factory pipeline.
    /// Kept for backward compatibility and testing.
    /// </summary>
    public ResilientGraphClient(HttpClient httpClient, ResilientGraphClientOptions? options = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        options ??= ResilientGraphClientOptions.Default;
        var (pipeline, rateLimiter) = ResiliencePipelineFactory.GetOrCreate(options);
        _pipeline = pipeline;
        _rateLimiter = rateLimiter;
    }

    /// <summary>Optional callback for verbose/diagnostic messages. Buffered on thread pool, drained on pipeline thread.</summary>
    public Action<string>? VerboseWriter { get; set; }

    // Same buffering contract as VerboseWriter.
    public Action<string>? WarningWriter { get; set; }

    // Same buffering contract as VerboseWriter. Receives the -Debug request/response trace.
    public Action<string>? DebugWriter { get; set; }

    /// <summary>
    /// Emit a request/response trace for every call. Off by default: tracing buffers the whole
    /// response body in memory, which defeats the streaming reads the rest of the client relies on.
    /// </summary>
    public bool DebugEnabled { get; set; }

    /// <summary>
    /// Runs at the head of each attempt, before it claims the request, and is given the attempt
    /// number. Null on every shipping path: the suite holds an attempt here to sit inside the
    /// stretch between the retry decision and the claim, where a stop and the attempt about to
    /// go out contend for the same word - a window too narrow to enter any other way. Per client
    /// rather than static, so a test arms only the transport it built.
    /// </summary>
    internal Func<int, Task>? AttemptEntryGate { get; set; }

    /// <summary>Drain buffered verbose messages. Must be called on the pipeline thread.</summary>
    public void DrainVerboseMessages()
    {
        if (VerboseWriter == null)
        {
            // Discard if no writer configured
            while (_pendingVerbose.TryDequeue(out _)) { }
            return;
        }
        while (_pendingVerbose.TryDequeue(out var msg))
            VerboseWriter(msg);
    }

    // For anything that reports from the enumeration thread, where a cmdlet cannot
    // WriteWarning: PageIterator itself, and the page-boundary callbacks the iterator invokes
    // on whichever thread it resumed on. Public for the latter - they live in the cmdlets
    // assembly - and paired with DrainWarningMessages, which is where the message is written.
    public void EnqueueWarning(string message) => _pendingWarnings.Enqueue(message);

    // Same threading contract as DrainVerboseMessages.
    public void DrainWarningMessages()
    {
        if (WarningWriter == null)
        {
            while (_pendingWarnings.TryDequeue(out _)) { }
            return;
        }
        while (_pendingWarnings.TryDequeue(out var msg))
            WarningWriter(msg);
    }

    // Same threading contract as DrainVerboseMessages.
    public void DrainDebugMessages()
    {
        if (DebugWriter == null)
        {
            while (_pendingDebug.TryDequeue(out _)) { }
            return;
        }
        while (_pendingDebug.TryDequeue(out var msg))
            DebugWriter(msg);
    }

    /// <summary>
    /// POST is the only non-idempotent method in Graph API.
    /// GET/PUT/DELETE are idempotent by HTTP spec.
    /// PATCH in Graph is always absolute property assignment (not incremental), so it's idempotent.
    /// </summary>
    private static bool IsIdempotent(HttpMethod method) =>
        method != HttpMethod.Post;

    /// <summary>
    /// Send an HTTP request through the resilience pipeline.
    /// Content (if any) is buffered before the pipeline so retries get a fresh body.
    /// Rate limiter lease is held for the duration of the HTTP call.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string requestUri,
        HttpContent? content = null,
        Dictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default,
        int permitCount = 1,
        bool paceGate = true,
        bool traceResponseBody = true,
        bool redirectIsSuccess = false,
        CancellationToken stopRetries = default)
    {
        // Buffer content bytes before pipeline so retries reconstruct fresh HttpContent.
        // Snapshot ALL content headers (not just ContentType) to preserve
        // Content-Encoding, Content-Disposition, etc. on retry.
        byte[]? contentBytes = null;
        List<KeyValuePair<string, IEnumerable<string>>>? contentHeaders = null;
        if (content != null)
        {
            contentBytes = await content.ReadAsByteArrayAsync(cancellationToken);
            if (contentBytes.Length > MaxRequestBodyBytes)
                throw new InvalidOperationException(
                    $"Request body size ({contentBytes.Length:N0} bytes) exceeds the {MaxRequestBodyBytes / (1024 * 1024)}MB limit. " +
                    "Graph API rejects bodies larger than 4MB on most endpoints.");
            contentHeaders = content.Headers.ToList();
        }

        RateLimitLease? lease = null;

        // A caller that has stopped sending - a batch chunk refused while its siblings are still
        // going. It ends the retry loop without touching the attempt on the wire: the pipeline
        // asks the stop before deciding on another attempt, and the token below ends a backoff
        // that is already running, so the call comes back instead of waiting out a Retry-After
        // it will not act on. Canceling the request itself would take the answer to the attempt
        // in flight with it, and that answer is the record of what the server applied - the very
        // thing the caller stops in order to keep. So the link is canceled only between
        // attempts: never before the first, which has not gone out and is the caller's own to
        // guard, and never during one.
        //
        // Where the request is lives in one word, and the stop and the attempt about to go out
        // contend for it with an exchange each. Two words read one after the other cannot say
        // where the request is: the pair can be read in a state neither of them was ever in.
        var sendState = SendState.BeforeFirstAttempt;
        CancellationTokenSource? stopLink = null;
        var stopReg = default(CancellationTokenRegistration);
        var pipelineToken = cancellationToken;
        if (stopRetries.CanBeCanceled)
        {
            stopLink = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            pipelineToken = stopLink.Token;
            stopReg = stopRetries.Register(() =>
            {
                // One exchange decides it. Between attempts the stop takes the request and ends
                // the backoff; from any other state the exchange fails and the stop is left to
                // the retry predicate, which refuses the next attempt without reaching this one.
                if (Interlocked.CompareExchange(
                        ref sendState, SendState.Stopped, SendState.BetweenAttempts)
                    == SendState.BetweenAttempts)
                {
                    stopLink.Cancel();
                }
            });
        }

        // The waits ahead of the first attempt: the pacer's gate and a permit from the token
        // bucket. Either can park a request for seconds, and a caller that stopped sending
        // during one of them cannot make the check again - the one it made before calling is
        // stale by the time the wait ends, and the request would go out on the strength of it.
        // The stop reaches these two waits and no others: past them the request is going out,
        // and its answer is the thing the caller stopped in order to keep.
        CancellationTokenSource? preSendLink = null;
        var preSendToken = cancellationToken;
        if (stopRetries.CanBeCanceled)
        {
            preSendLink = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopRetries);
            preSendToken = preSendLink.Token;
        }

        var context = ResilienceContextPool.Shared.Get(pipelineToken);
        context.Properties.Set(ResiliencePipelineFactory.StopRetriesKey, stopRetries);
        context.Properties.Set(ResiliencePipelineFactory.IsIdempotentKey, IsIdempotent(method));
        // Always set VerboseWriterKey to buffer messages (prevents stale writers from pooled contexts)
        context.Properties.Set(ResiliencePipelineFactory.VerboseWriterKey,
            (Action<string>)(msg => _pendingVerbose.Enqueue(msg)));

        // One GUID per logical request, shared across retry attempts for correlation
        var clientRequestId = Guid.NewGuid().ToString();

        var totalSw = Stopwatch.StartNew();
        bool succeeded = false;
        // The last status an attempt got, for a stop that ends the request between attempts:
        // the answer is gone by then, but what it said is not.
        HttpStatusCode? lastStatus = null;
        var bucket = AdaptivePacing.Classify(requestUri);
        try
        {
            // Proactive gate ahead of the bucket lease: the pacer spaces requests, the token
            // bucket stays the hard backstop. Batch outer POSTs pass paceGate: false -
            // GraphBatchClient owns batch throughput - but their responses still feed signals.
            if (paceGate)
            {
                var pacedMs = await AdaptiveRequestPacer.WaitAsync(bucket, preSendToken);
                if (pacedMs > 0)
                {
                    MgxTelemetryCollector.Current.RecordPacingWait(pacedMs);
                    _pendingVerbose.Enqueue($"Adaptive pacing: waited {pacedMs}ms before sending ({bucket} workload)");
                }
            }

            if (_rateLimiter != null)
            {
                var limiterSw = Stopwatch.StartNew();
                lease = await _rateLimiter.AcquireAsync(permitCount, preSendToken);
                MgxTelemetryCollector.Current.RecordRateLimiterWait(limiterSw.ElapsedMilliseconds);
                if (!lease.IsAcquired)
                    throw new InvalidOperationException("Rate limit exceeded. Too many concurrent requests. Reduce -Concurrency on fan-out cmdlets, increase the queue with Set-MgxOption -RateLimitQueueLimit, or disable with Set-MgxOption -NoRateLimit.");
            }

            var attempt = 0;
            var result = await _pipeline.ExecuteAsync(
                async ctx =>
                {
                    attempt++;
                    if (AttemptEntryGate is { } gate) await gate(attempt);
                    var request = new HttpRequestMessage(method, requestUri);
                    if (contentBytes != null)
                    {
                        var freshContent = new ByteArrayContent(contentBytes);
                        // Copy ALL content headers (ContentType, ContentEncoding, etc.)
                        foreach (var header in contentHeaders!)
                            freshContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        request.Content = freshContent;
                    }
                    if (headers != null)
                    {
                        foreach (var (key, value) in headers)
                        {
                            // Content-Length is computed from the actual body; a caller value
                            // can only disagree with it, and the transport fails the request
                            // on either direction of the mismatch.
                            if (string.Equals(key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                            {
                                if (attempt == 1)
                                    _pendingWarnings.Enqueue(
                                        "Header 'Content-Length' is computed from the request body and was not overridden.");
                                continue;
                            }
                            if (request.Headers.TryAddWithoutValidation(key, value)) continue;
                            // The request collection refuses content headers (Content-Type,
                            // Content-Disposition, ...); they belong on the content, replacing
                            // any default the buffered content carried. Remove throws on a
                            // malformed name where TryAdd merely returns false - a name both
                            // collections refuse must warn, not crash the request.
                            if (request.Content != null)
                            {
                                try
                                {
                                    request.Content.Headers.Remove(key);
                                    if (request.Content.Headers.TryAddWithoutValidation(key, value)) continue;
                                }
                                catch (Exception ex) when (ex is FormatException or ArgumentException)
                                {
                                }
                            }
                            if (attempt == 1)
                                _pendingWarnings.Enqueue($"Header '{key}' is not valid on this request and was not sent.");
                        }
                    }
                    // A caller-supplied value wins: appending a second one would put two
                    // values on the wire and defeat the correlation id the caller is logging.
                    if (headers == null || !headers.ContainsKey("SdkVersion"))
                        request.Headers.TryAddWithoutValidation("SdkVersion", MgxSdkVersion.Value);
                    if (headers == null || !headers.ContainsKey("client-request-id"))
                        request.Headers.TryAddWithoutValidation("client-request-id", clientRequestId);

                    if (DebugEnabled)
                        _pendingDebug.Enqueue(GraphRequestTracer.FormatRequest(request, contentBytes, attempt));

                    var httpSw = Stopwatch.StartNew();
                    // From here to the answer is the window a stop must not touch: the request
                    // is going out, and canceling it would lose what the server did with it.
                    // The window is claimed rather than announced - a stop that took the request
                    // first keeps it, and this attempt ends here instead of going out on the
                    // strength of having overwritten the stop.
                    int seen;
                    do
                    {
                        seen = Volatile.Read(ref sendState);
                        // The stop reached the request before this attempt claimed it, so this
                        // one does not go out. Ended here rather than left to meet the canceled
                        // token below: the stop takes the word and cancels the link as two
                        // steps, and between them lies every instruction from here to the send.
                        // An attempt that read the word and went anyway is on the transport
                        // before the token arrives - the transport has the request and the
                        // cancel lands on a send already made - and the caller is then told
                        // that nothing further was sent over a request that was.
                        if (seen == SendState.Stopped)
                            throw new OperationCanceledException(stopRetries);
                    }
                    while (Interlocked.CompareExchange(ref sendState, SendState.InAttempt, seen) != seen);
                    try
                    {
                        // Stream response headers immediately instead of buffering entire body
                        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ctx.CancellationToken);
                        lastStatus = response.StatusCode;
                        MgxTelemetryCollector.Current.RecordHttpTime(httpSw.ElapsedMilliseconds);
                        // Per-attempt network time feeds the latency baseline (telemetry-only:
                        // makes the SPO soft-clamp visible; not a pacing input in 2.1).
                        AdaptiveRequestPacer.RecordLatency(bucket, httpSw.ElapsedMilliseconds);

                        if (DebugEnabled)
                        {
                            if (traceResponseBody)
                            {
                                await TraceResponseAsync(response, httpSw.ElapsedMilliseconds, ctx.CancellationToken);
                            }
                            else
                            {
                                // Content path: buffering a multi-megabyte download to trace it
                                // would defeat the streaming read. Headers-only line instead.
                                _pendingDebug.Enqueue(GraphRequestTracer.FormatResponse(response, httpSw.ElapsedMilliseconds, null));
                            }
                        }

                        return response;
                    }
                    finally
                    {
                        // One transition out, so there is no instant at which the state says the
                        // attempt is over while its response is still being handed back unread.
                        // A stop that landed during the attempt is not resumed here: the retry it
                        // would have stopped is already refused by the retry predicate, and
                        // canceling now would take the answer the attempt was kept alive for.
                        Interlocked.CompareExchange(
                            ref sendState, SendState.BetweenAttempts, SendState.InAttempt);
                    }
                },
                context);
            // A redirect counts as success only for the caller that expects one. Graph answers
            // /content with a 302 to a pre-authenticated download host and GraphContentClient
            // follows it, so booking that as a failure made every successful two-hop download
            // register as failed. Every OTHER caller treats a 3xx as an error and throws -
            // AllowAutoRedirect is off - so a blanket 3xx-is-success would book a request the
            // user saw fail as succeeded. Hence the per-call flag rather than a status range.
            succeeded = result.IsSuccessStatusCode
                || (redirectIsSuccess && (int)result.StatusCode >= 300 && (int)result.StatusCode < 400);

            // Log throttle proximity and diagnostic headers to verbose.
            // These headers warn that requests are approaching throttle limits before 429s hit.
            LogThrottleHeaders(result);

            // Feed the pacer's signal state: throttle-proximity percentage, and a final 429
            // that exhausted retries (OnRetry never fires for the last attempt).
            AdaptiveRequestPacer.RecordResponse(bucket, result);

            // Track x-ms-resource-unit for telemetry (Identity/Access uses RU-based throttling).
            // Safe to record here: _pipeline.ExecuteAsync returns only the final response;
            // retried responses are disposed in OnRetry and never reach this point.
            if (result.Headers.TryGetValues("x-ms-resource-unit", out var ruValues)
                && long.TryParse(ruValues.FirstOrDefault(), out var ru)
                && ru > 0)
            {
                MgxTelemetryCollector.Current.RecordResourceUnit(ru);
            }

            return result;
        }
        // The caller stopped this request's retries while it was waiting to be retried, and the
        // wait ended with them. What the server last said is what the request is reported as: a
        // retry that never went out does not turn a throttle into a call that got no answer.
        catch (OperationCanceledException) when (stopRetries.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            // Nothing of this request went out: the stop reached it while it was still waiting
            // for its turn to send. Said in its own words rather than as a refusal - a refusal
            // means the server may have applied the writes, and whether the caller sends them
            // again turns on exactly that difference.
            if (Volatile.Read(ref sendState) == SendState.BeforeFirstAttempt)
                throw new RequestNotSentException();
            throw new HttpRequestException(RetriesStoppedMessage, null, lastStatus);
        }
        finally
        {
            MgxTelemetryCollector.Current.RecordRequest(succeeded, totalSw.ElapsedMilliseconds);
            stopReg.Dispose();
            stopLink?.Dispose();
            preSendLink?.Dispose();
            ResilienceContextPool.Shared.Return(context);
            lease?.Dispose();
        }
    }

    /// <summary>
    /// Send a GET request through the resilience pipeline.
    /// </summary>
    public Task<HttpResponseMessage> GetAsync(
        string requestUri,
        CancellationToken cancellationToken = default,
        Dictionary<string, string>? headers = null)
        => SendAsync(HttpMethod.Get, requestUri, headers: headers, cancellationToken: cancellationToken);

    /// <summary>
    /// Send a POST request through the resilience pipeline.
    /// </summary>
    public Task<HttpResponseMessage> PostAsync(
        string requestUri,
        HttpContent content,
        CancellationToken cancellationToken = default,
        Dictionary<string, string>? headers = null,
        int permitCount = 1,
        bool paceGate = true,
        CancellationToken stopRetries = default)
        => SendAsync(HttpMethod.Post, requestUri, content, headers, cancellationToken, permitCount, paceGate,
            stopRetries: stopRetries);

    /// <summary>
    /// Fetch content bytes ($value / /content endpoints), optionally a byte range.
    /// Two hops: the authenticated Graph request through the full pipeline, then - when Graph
    /// 302s to a pre-authenticated download host - a token-free fetch through
    /// GraphContentClient. See GraphContentClient for the transport preconditions.
    /// </summary>
    public Task<GraphContentResult> GetContentAsync(
        string requestUri,
        System.Net.Http.Headers.RangeHeaderValue? range = null,
        Dictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
        => GraphContentClient.GetContentAsync(this, requestUri, range, headers, cancellationToken);

    /// <summary>
    /// Fetch a collection page and deserialize.
    /// </summary>
    public async Task<GraphRawCollectionResponse> GetCollectionPageAsync(
        string requestUri,
        CancellationToken cancellationToken = default,
        Dictionary<string, string>? headers = null)
    {
        using var response = await GetAsync(requestUri, cancellationToken, headers);
        await ThrowIfGraphErrorAsync(response, cancellationToken);
        using var bodyCts = CreateBodyReadCts(cancellationToken);
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(bodyCts.Token);
            return await JsonSerializer.DeserializeAsync<GraphRawCollectionResponse>(stream, JsonOptions, bodyCts.Token)
                ?? new GraphRawCollectionResponse();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException(BodyReadTimedOutMessage);
        }
    }

    /// <summary>
    /// What a caller sees when it stopped this request's retries while it was waiting to be
    /// retried. Nothing further was sent; the status the exception carries is the last one the
    /// server gave, so a stopped retry does not read as a request that never got an answer.
    /// </summary>
    internal const string RetriesStoppedMessage =
        "The caller stopped retrying this request. No further attempt was sent.";

    /// <summary>
    /// What a caller sees when a server sends headers and then stops sending the body.
    /// </summary>
    internal const string BodyReadTimedOutMessage =
        "Response body read timed out. The server sent headers but the body stream stalled.";

    /// <summary>
    /// Read a response body as bytes under <see cref="BodyReadTimeout"/>. Requests are sent
    /// with HttpCompletionOption.ResponseHeadersRead, and under that option HttpClient.Timeout
    /// does not bound the content read at all - nor does the pipeline's attempt timeout, which
    /// ends when the headers arrive. A read on the caller's token alone therefore never returns
    /// against a stream that stalls without closing.
    /// </summary>
    public async Task<byte[]> ReadBodyAsBytesAsync(
        HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        using var bodyCts = CreateBodyReadCts(cancellationToken);
        try
        {
            return await response.Content.ReadAsByteArrayAsync(bodyCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException(BodyReadTimedOutMessage);
        }
    }

    /// <summary>
    /// Read a response body as text under <see cref="BodyReadTimeout"/>, for the same reason as
    /// <see cref="ReadBodyAsBytesAsync"/>. An error body stalls exactly as a success body does.
    /// </summary>
    public async Task<string> ReadBodyAsStringAsync(
        HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        using var bodyCts = CreateBodyReadCts(cancellationToken);
        try
        {
            return await response.Content.ReadAsStringAsync(bodyCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException(BodyReadTimedOutMessage);
        }
    }

    /// <summary>
    /// Create a linked CancellationTokenSource that adds BodyReadTimeout to the caller's token.
    /// Used for all response body reads to prevent hangs on stalled streams.
    /// </summary>
    internal CancellationTokenSource CreateBodyReadCts(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(BodyReadTimeout);
        return cts;
    }

    /// <summary>
    /// Buffer the response body and queue a trace line for it. Buffering is what makes the body
    /// readable twice: the caller still reads it normally afterwards.
    /// A trace must never break a request, so a failed read degrades to a headers-only line.
    /// </summary>
    private async Task TraceResponseAsync(HttpResponseMessage response, long elapsedMs, CancellationToken ct)
    {
        string? body = null;
        try
        {
            using var bodyCts = CreateBodyReadCts(ct);
            // WaitAsync rather than the LoadIntoBufferAsync(CancellationToken) overload: that
            // overload is .NET 9+, and taking it here would raise the module's floor from
            // PowerShell 7.4 to 7.5 for the sake of one debug-only trace. The buffering itself
            // is uncancellable this way, but BodyReadTimeout still bounds the wait.
            await response.Content.LoadIntoBufferAsync().WaitAsync(bodyCts.Token);
            body = await response.Content.ReadAsStringAsync(bodyCts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _pendingDebug.Enqueue($"[Mgx] Response body could not be traced: {ex.Message}");
        }

        _pendingDebug.Enqueue(GraphRequestTracer.FormatResponse(response, elapsedMs, body));
    }

    private async Task ThrowIfGraphErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        using var bodyCts = CreateBodyReadCts(ct);
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(bodyCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // An error body stalls as a success body does, and the caller's contract has no
            // room for the raw cancellation: it catches HttpRequestException.
            throw new HttpRequestException(BodyReadTimedOutMessage);
        }
        throw new GraphServiceException(response.StatusCode, body);
    }

    /// <summary>
    /// Log Graph throttle proximity headers to verbose output.
    /// These headers are officially documented but conditionally sent by Graph:
    /// - x-ms-throttle-limit-percentage: only appears when >80% of throttle budget consumed
    /// - x-ms-throttle-scope: typically only on 429 responses (format: Scope/Limit/AppId/TenantId)
    /// - x-ms-throttle-information: diagnostic reason on 429 (e.g., CPULimitExceeded, ResourceUnitLimitExceeded)
    /// Reliability varies by Graph endpoint. Some workloads never send these headers.
    /// Tested against live tenant: headers do not appear at low request volumes (50 req).
    /// Only logs when headers are present and VerboseWriter is set.
    /// </summary>
    private void LogThrottleHeaders(HttpResponseMessage response)
    {
        if (VerboseWriter == null && WarningWriter == null) return;

        if (response.Headers.TryGetValues("x-ms-throttle-limit-percentage", out var pctValues))
        {
            var pctStr = pctValues.FirstOrDefault();
            var scope = response.Headers.TryGetValues("x-ms-throttle-scope", out var scopeValues)
                ? scopeValues.FirstOrDefault()
                : null;
            var info = response.Headers.TryGetValues("x-ms-throttle-information", out var infoValues)
                ? infoValues.FirstOrDefault()
                : null;

            // Header value is a ratio (0.8 = 80%, 1.2 = 120%). Scale: 0.8-1.8.
            // Display as percentage for clarity; fall back to raw value if unparseable.
            string msg;
            if (double.TryParse(pctStr, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var pct))
            {
                msg = $"Throttle proximity: {pct * 100:F0}% of limit consumed";
            }
            else
            {
                msg = $"Throttle proximity: {pctStr} (raw) of limit consumed";
                pct = -1; // sentinel: unparseable, skip warning threshold check
            }
            if (scope != null) msg += $" (scope: {scope})";
            if (info != null) msg += $" [{info}]";
            _pendingVerbose.Enqueue(msg);

            // Warn when at or over throttle budget (429 responses imminent)
            if (pct >= 1.0)
            {
                _pendingWarnings.Enqueue(
                    $"Throttle budget at {pct * 100:F0}% of limit. 429 responses may be imminent."
                    + (scope != null ? $" Scope: {scope}." : ""));
            }
        }
    }

    public void Dispose()
    {
        // Pipeline and rate limiter are shared via ResiliencePipelineFactory.
        // Don't dispose _httpClient either: it's owned by the caller (MgxCmdletBase).
    }
}

/// <summary>
/// A request the caller's stop ended before any attempt of it went out. Distinct from
/// <see cref="ResilientGraphClient.RetriesStoppedMessage" />, which ends a request that had
/// already been sent at least once and carries the status the server last gave it: this one
/// carries no status because there is nothing to report, and it is the only shape of stop
/// after which a caller can resubmit without asking whether the write already landed.
/// </summary>
internal sealed class RequestNotSentException : Exception
{
    public RequestNotSentException()
        : base("The caller stopped sending before this request went out. Nothing was sent.")
    {
    }
}
