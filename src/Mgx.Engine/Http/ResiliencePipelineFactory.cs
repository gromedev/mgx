using System.Net;
using System.Net.Http.Headers;
using System.Threading.RateLimiting;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace Mgx.Engine.Http;

/// <summary>
/// Manages shared Polly resilience pipeline and rate limiter instances.
/// Circuit breaker and rate limiter MUST be shared across cmdlet invocations
/// to function correctly: circuit breaker needs failure history across calls,
/// rate limiter needs cumulative token consumption.
/// </summary>
public static class ResiliencePipelineFactory
{
    private static readonly object s_lock = new();
    private static ResiliencePipeline<HttpResponseMessage>? s_pipeline;
    private static TokenBucketRateLimiter? s_rateLimiter;
    private static ResilientGraphClientOptions? s_cachedOptions;

    /// <summary>
    /// Property key for passing idempotency info into the Polly retry predicate.
    /// POST is the only non-idempotent Graph method; it only retries on 429.
    /// </summary>
    internal static readonly ResiliencePropertyKey<bool> IsIdempotentKey = new("IsIdempotent");

    /// <summary>
    /// Property key for passing a per-invocation verbose writer into OnRetry.
    /// Set on the ResilienceContext before each pipeline execution so the shared
    /// pipeline can log without capturing per-invocation state in closures.
    /// </summary>
    internal static readonly ResiliencePropertyKey<Action<string>?> VerboseWriterKey = new("VerboseWriter");

    /// <summary>
    /// Get or create a shared resilience pipeline and rate limiter.
    /// Rebuilds when options change (detected by reference equality, since
    /// Set-MgxOption creates a new ResilientGraphClientOptions each time).
    /// Old rate limiters are disposed after a delay to avoid racing with in-flight clients.
    /// </summary>
    public static (ResiliencePipeline<HttpResponseMessage> Pipeline, TokenBucketRateLimiter? RateLimiter)
        GetOrCreate(ResilientGraphClientOptions options)
    {
        lock (s_lock)
        {
            if (s_pipeline != null && ReferenceEquals(s_cachedOptions, options))
                return (s_pipeline, s_rateLimiter);

            // Schedule delayed disposal of the old rate limiter. It may still be
            // referenced by in-flight ResilientGraphClient instances, so we wait
            // TotalTimeoutSeconds to ensure all in-flight requests have completed.
            ScheduleDelayedDispose(s_rateLimiter, options.TotalTimeoutSeconds);

            TokenBucketRateLimiter? rateLimiter = null;
            if (!options.NoRateLimit)
            {
                rateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
                {
                    TokenLimit = options.RateLimitBurst,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                    TokensPerPeriod = options.RateLimitPerSecond,
                    QueueLimit = options.RateLimitQueueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true
                });
            }

            s_pipeline = BuildPipeline(options);
            s_rateLimiter = rateLimiter;
            s_cachedOptions = options;

            return (s_pipeline, rateLimiter);
        }
    }

    /// <summary>
    /// Force rebuild on next access. Call when tenant changes to reset
    /// circuit breaker state (failure history from old tenant is irrelevant).
    /// </summary>
    public static void Reset()
    {
        lock (s_lock)
        {
            s_pipeline = null;
            // Dispose after delay: in-flight clients may still reference the old limiter.
            // Default 300s covers the maximum total timeout window.
            ScheduleDelayedDispose(s_rateLimiter, s_cachedOptions?.TotalTimeoutSeconds ?? 300);
            s_rateLimiter = null;
            s_cachedOptions = null;
        }
    }

    /// <summary>
    /// Disposes a rate limiter after a delay. TokenBucketRateLimiter holds an internal
    /// Timer (via AutoReplenishment) that acts as a GC root. Immediate disposal would
    /// cause ObjectDisposedException in in-flight clients, so we wait for the total
    /// timeout window to expire before disposing.
    /// </summary>
    private static void ScheduleDelayedDispose(TokenBucketRateLimiter? limiter, int delaySeconds)
    {
        if (limiter == null) return;
        _ = Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ContinueWith(_ =>
        {
            try { limiter.Dispose(); } catch { /* best-effort cleanup */ }
        }, TaskScheduler.Default);
    }

    private static ResiliencePipeline<HttpResponseMessage> BuildPipeline(ResilientGraphClientOptions options)
    {
        var maxRetryAfterCap = options.MaxRetryAfterSeconds;

        return new ResiliencePipelineBuilder<HttpResponseMessage>()
            // Total timeout
            .AddTimeout(TimeSpan.FromSeconds(options.TotalTimeoutSeconds))
            // Retry
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = options.MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromSeconds(maxRetryAfterCap),
                ShouldHandle = args =>
                {
                    // 429 is safe to retry for all methods including POST (matches Kiota SDK behavior)
                    if (args.Outcome.Result?.StatusCode == (HttpStatusCode)429)
                        return ValueTask.FromResult(true);

                    // For non-idempotent methods (POST), only retry 429.
                    // 500/502/503/504 may mean the request was partially processed.
                    var isIdempotent = args.Context.Properties.GetValue(IsIdempotentKey, true);
                    if (!isIdempotent)
                        return ValueTask.FromResult(false);

                    if (args.Outcome.Result?.StatusCode is HttpStatusCode.InternalServerError
                        or HttpStatusCode.BadGateway
                        or HttpStatusCode.ServiceUnavailable
                        or HttpStatusCode.GatewayTimeout
                        or HttpStatusCode.RequestTimeout)
                        return ValueTask.FromResult(true);

                    if (args.Outcome.Exception is HttpRequestException)
                        return ValueTask.FromResult(true);

                    // Retry TaskCanceledException only if NOT caused by user cancellation (Ctrl+C).
                    // Polly's attempt timeout throws TimeoutRejectedException (not TaskCanceledException),
                    // so TaskCanceledException here means either user cancelled or HttpClient timeout.
                    if (args.Outcome.Exception is TaskCanceledException &&
                        !args.Context.CancellationToken.IsCancellationRequested)
                        return ValueTask.FromResult(true);

                    // Retry when Polly's per-attempt timeout fires (idempotent methods only).
                    // This is critical for the Enable-MgxResilience path: the SDK's internal
                    // RetryHandler may be honoring a Retry-After delay that exceeds
                    // AttemptTimeoutSeconds. Without this, requests fail permanently when
                    // Graph returns Retry-After > AttemptTimeoutSeconds.
                    // The outer TotalTimeout still bounds the overall operation.
                    if (args.Outcome.Exception is TimeoutRejectedException)
                        return ValueTask.FromResult(isIdempotent);

                    return ValueTask.FromResult(false);
                },
                DelayGenerator = args =>
                {
                    // Respect Retry-After header from Graph API (429 responses)
                    if (args.Outcome.Result?.Headers.RetryAfter is RetryConditionHeaderValue retryAfter)
                    {
                        if (retryAfter.Delta.HasValue)
                        {
                            var delay = retryAfter.Delta.Value;
                            var cap = TimeSpan.FromSeconds(maxRetryAfterCap);
                            return ValueTask.FromResult<TimeSpan?>(delay > cap ? cap : delay);
                        }
                        if (retryAfter.Date.HasValue)
                        {
                            var delay = retryAfter.Date.Value - DateTimeOffset.UtcNow;
                            if (delay > TimeSpan.Zero)
                            {
                                var cap = TimeSpan.FromSeconds(maxRetryAfterCap);
                                return ValueTask.FromResult<TimeSpan?>(delay > cap ? cap : delay);
                            }
                        }
                    }
                    return ValueTask.FromResult<TimeSpan?>(null);
                },
                OnRetry = args =>
                {
                    var response = args.Outcome.Result;
                    var status = response?.StatusCode;
                    var retryAfter = response?.Headers.RetryAfter;
                    var attempt = args.AttemptNumber;
                    var retryDelay = args.RetryDelay;

                    // Dispose the previous response to drain the connection back to the pool.
                    // With HttpCompletionOption.ResponseHeadersRead, the body stream stays open
                    // until explicitly read or the response is disposed. Without this, retried
                    // responses leak connections until GC finalizes them.
                    response?.Dispose();

                    MgxTelemetryCollector.Current.RecordRetry(
                        isThrottle: status == (HttpStatusCode)429,
                        delayMs: (long)retryDelay.TotalMilliseconds);

                    if (!args.Context.Properties.TryGetValue(VerboseWriterKey, out var writer) || writer == null)
                        return default;

                    // Timeout retries have no HTTP response - log a distinct message
                    if (args.Outcome.Exception is TimeoutRejectedException tre)
                    {
                        writer($"Retry attempt {attempt}: per-attempt timeout ({tre.Timeout.TotalSeconds:F0}s) exceeded, waiting {retryDelay.TotalSeconds:F1}s");
                        return default;
                    }

                    if (retryAfter is RetryConditionHeaderValue ra)
                    {
                        TimeSpan? serverRequested = ra.Delta
                            ?? (ra.Date.HasValue ? ra.Date.Value - DateTimeOffset.UtcNow : null);
                        if (serverRequested.HasValue &&
                            serverRequested.Value > TimeSpan.FromSeconds(maxRetryAfterCap))
                        {
                            writer($"Retry attempt {attempt}: server requested {serverRequested.Value.TotalSeconds:F0}s delay (clamped to {maxRetryAfterCap}s). Status: {(int?)status}");
                            return default;
                        }
                    }

                    writer($"Retry attempt {attempt}: waiting {retryDelay.TotalSeconds:F1}s. Status: {(int?)status}");
                    return default;
                }
            })
            // Circuit breaker (5xx, not 429 or 408)
            // 408 excluded: it's a client-perceived timeout, not a server-side failure indicator.
            // Including it would cause proxy timeouts to trip the circuit breaker incorrectly.
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                FailureRatio = options.CircuitBreakerFailureRatio,
                SamplingDuration = TimeSpan.FromSeconds(options.CircuitBreakerSamplingDurationSeconds),
                MinimumThroughput = options.CircuitBreakerMinThroughput,
                BreakDuration = TimeSpan.FromSeconds(options.CircuitBreakerDurationSeconds),
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .HandleResult(r => r.StatusCode == HttpStatusCode.InternalServerError)
                    .HandleResult(r => r.StatusCode == HttpStatusCode.BadGateway)
                    .HandleResult(r => r.StatusCode == HttpStatusCode.ServiceUnavailable)
                    .HandleResult(r => r.StatusCode == HttpStatusCode.GatewayTimeout)
                    .Handle<HttpRequestException>()
                    // Exclude user cancellation (Ctrl+C) from circuit breaker failure counting.
                    // Only count non-user TaskCanceledException (e.g., HttpClient timeout).
                    .Handle<TaskCanceledException>(e => !e.CancellationToken.IsCancellationRequested)
                    // Count per-attempt timeouts as failures. Without this, repeated
                    // timeouts (e.g., downstream hung) never trip the circuit breaker,
                    // wasting MaxRetryAttempts * AttemptTimeoutSeconds before giving up.
                    .Handle<TimeoutRejectedException>(),
                OnOpened = _ =>
                {
                    MgxTelemetryCollector.Current.RecordCircuitBreakerTrip();
                    return default;
                }
            })
            // Per-attempt timeout
            .AddTimeout(TimeSpan.FromSeconds(options.AttemptTimeoutSeconds))
            .Build();
    }
}
