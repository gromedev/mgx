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

    /// <summary>Maximum request body size (4MB). Graph API rejects larger bodies on most endpoints.</summary>
    internal const int MaxRequestBodyBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Timeout for reading response bodies after headers have been received.
    /// ResponseHeadersRead means SendAsync returns immediately after headers arrive;
    /// the body is read lazily. Without this timeout, a stalled body stream hangs forever
    /// because HttpClient.Timeout and Polly's TotalTimeout only cover the SendAsync call.
    /// </summary>
    public TimeSpan BodyReadTimeout { get; set; } = TimeSpan.FromSeconds(30);

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
        int permitCount = 1)
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
        var context = ResilienceContextPool.Shared.Get(cancellationToken);
        context.Properties.Set(ResiliencePipelineFactory.IsIdempotentKey, IsIdempotent(method));
        // Always set VerboseWriterKey to buffer messages (prevents stale writers from pooled contexts)
        context.Properties.Set(ResiliencePipelineFactory.VerboseWriterKey,
            (Action<string>)(msg => _pendingVerbose.Enqueue(msg)));

        // One GUID per logical request, shared across retry attempts for correlation
        var clientRequestId = Guid.NewGuid().ToString();

        var totalSw = Stopwatch.StartNew();
        bool succeeded = false;
        try
        {
            if (_rateLimiter != null)
            {
                var limiterSw = Stopwatch.StartNew();
                lease = await _rateLimiter.AcquireAsync(permitCount, cancellationToken);
                MgxTelemetryCollector.Current.RecordRateLimiterWait(limiterSw.ElapsedMilliseconds);
                if (!lease.IsAcquired)
                    throw new InvalidOperationException("Rate limit exceeded. Too many concurrent requests. Reduce -Concurrency on fan-out cmdlets, increase the queue with Set-MgxOption -RateLimitQueueLimit, or disable with Set-MgxOption -NoRateLimit.");
            }

            var result = await _pipeline.ExecuteAsync(
                async ctx =>
                {
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
                            request.Headers.TryAddWithoutValidation(key, value);
                    }
                    request.Headers.TryAddWithoutValidation("SdkVersion", MgxSdkVersion.Value);
                    request.Headers.TryAddWithoutValidation("client-request-id", clientRequestId);
                    var httpSw = Stopwatch.StartNew();
                    // Stream response headers immediately instead of buffering entire body
                    var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ctx.CancellationToken);
                    MgxTelemetryCollector.Current.RecordHttpTime(httpSw.ElapsedMilliseconds);
                    return response;
                },
                context);
            succeeded = result.IsSuccessStatusCode;

            // Log throttle proximity and diagnostic headers to verbose.
            // These headers warn that requests are approaching throttle limits before 429s hit.
            LogThrottleHeaders(result);

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
        finally
        {
            MgxTelemetryCollector.Current.RecordRequest(succeeded, totalSw.ElapsedMilliseconds);
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
        int permitCount = 1)
        => SendAsync(HttpMethod.Post, requestUri, content, headers, cancellationToken, permitCount);

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
            throw new HttpRequestException("Response body read timed out. The server sent headers but the body stream stalled.");
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

    private async Task ThrowIfGraphErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        using var bodyCts = CreateBodyReadCts(ct);
        var body = await response.Content.ReadAsStringAsync(bodyCts.Token);
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
