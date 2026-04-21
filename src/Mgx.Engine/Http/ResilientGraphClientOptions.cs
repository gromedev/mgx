namespace Mgx.Engine.Http;

/// <summary>
/// Configuration options for ResilientGraphClient.
/// All properties have sensible defaults and are validated on construction.
/// Upper bounds prevent self-DoS configurations.
/// </summary>
public sealed class ResilientGraphClientOptions
{
    // Retry-After clamping
    private readonly int _maxRetryAfterSeconds = 120;

    // Rate limiting
    private readonly int _rateLimitBurst = 200;
    private readonly int _rateLimitPerSecond = 50;
    private readonly int _rateLimitQueueLimit = 500;

    // Pipeline configuration
    private readonly int _maxRetryAttempts = 7;
    private readonly int _totalTimeoutSeconds = 300;
    private readonly int _attemptTimeoutSeconds = 30;
    private readonly int _circuitBreakerDurationSeconds = 15;
    private readonly double _circuitBreakerFailureRatio = 0.1;
    private readonly int _circuitBreakerMinThroughput = 40;
    private readonly int _circuitBreakerSamplingDurationSeconds = 30;

    // Batch configuration
    private readonly int _batchChunkConcurrency = 1;
    private readonly int _batchItemsPerSecond = 20;

    /// <summary>
    /// Maximum Retry-After delay in seconds. Caps server-requested delays to prevent
    /// a single throttled request from consuming the entire timeout budget. Applied in both
    /// the resilience pipeline DelayGenerator and batch client retry logic.
    /// Graph API commonly returns Retry-After: 150s during sustained throttling; honoring
    /// this (rather than clamping aggressively) reduces wasted retry attempts.
    /// Range: 1-600. Default: 120.
    /// </summary>
    public int MaxRetryAfterSeconds
    {
        get => _maxRetryAfterSeconds;
        init => _maxRetryAfterSeconds = value is > 0 and <= 600
            ? value
            : throw new ArgumentOutOfRangeException(nameof(MaxRetryAfterSeconds), value, "Must be between 1 and 600.");
    }

    /// <summary>Token bucket burst capacity. Range: 1-10,000. Default: 200.</summary>
    public int RateLimitBurst
    {
        get => _rateLimitBurst;
        init => _rateLimitBurst = value is > 0 and <= 10_000
            ? value
            : throw new ArgumentOutOfRangeException(nameof(RateLimitBurst), value, "Must be between 1 and 10,000.");
    }

    /// <summary>Token bucket sustained rate (tokens per second). Range: 1-10,000. Default: 50.</summary>
    public int RateLimitPerSecond
    {
        get => _rateLimitPerSecond;
        init => _rateLimitPerSecond = value is > 0 and <= 10_000
            ? value
            : throw new ArgumentOutOfRangeException(nameof(RateLimitPerSecond), value, "Must be between 1 and 10,000.");
    }

    /// <summary>Set to true to disable the rate limiter entirely. Default: false.</summary>
    public bool NoRateLimit { get; init; }

    /// <summary>Maximum queue depth before rejecting requests. Range: 0-100,000. Default: 500.</summary>
    public int RateLimitQueueLimit
    {
        get => _rateLimitQueueLimit;
        init => _rateLimitQueueLimit = value is >= 0 and <= 100_000
            ? value
            : throw new ArgumentOutOfRangeException(nameof(RateLimitQueueLimit), value, "Must be between 0 and 100,000.");
    }

    /// <summary>Maximum retry attempts (not total attempts). Range: 1-50. Default: 7.</summary>
    public int MaxRetryAttempts
    {
        get => _maxRetryAttempts;
        init => _maxRetryAttempts = value is > 0 and <= 50
            ? value
            : throw new ArgumentOutOfRangeException(nameof(MaxRetryAttempts), value, "Must be between 1 and 50.");
    }

    /// <summary>Total timeout across all retries in seconds. Range: 1-3,600. Default: 300.</summary>
    public int TotalTimeoutSeconds
    {
        get => _totalTimeoutSeconds;
        init => _totalTimeoutSeconds = value is > 0 and <= 3600
            ? value
            : throw new ArgumentOutOfRangeException(nameof(TotalTimeoutSeconds), value, "Must be between 1 and 3,600.");
    }

    /// <summary>Per-attempt timeout in seconds. Range: 1-300. Default: 30.</summary>
    public int AttemptTimeoutSeconds
    {
        get => _attemptTimeoutSeconds;
        init => _attemptTimeoutSeconds = value is > 0 and <= 300
            ? value
            : throw new ArgumentOutOfRangeException(nameof(AttemptTimeoutSeconds), value, "Must be between 1 and 300.");
    }

    /// <summary>Duration of circuit breaker open state in seconds. Range: 1-300. Default: 15.</summary>
    public int CircuitBreakerDurationSeconds
    {
        get => _circuitBreakerDurationSeconds;
        init => _circuitBreakerDurationSeconds = value is > 0 and <= 300
            ? value
            : throw new ArgumentOutOfRangeException(nameof(CircuitBreakerDurationSeconds), value, "Must be between 1 and 300.");
    }

    /// <summary>Failure ratio to trip circuit breaker. Range: 0.01-1.0. Default: 0.1.</summary>
    public double CircuitBreakerFailureRatio
    {
        get => _circuitBreakerFailureRatio;
        init => _circuitBreakerFailureRatio = value is >= 0.01 and <= 1.0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(CircuitBreakerFailureRatio), value, "Must be between 0.01 and 1.0.");
    }

    /// <summary>Minimum requests before circuit breaker evaluates. Range: 1-1,000. Default: 40.</summary>
    public int CircuitBreakerMinThroughput
    {
        get => _circuitBreakerMinThroughput;
        init => _circuitBreakerMinThroughput = value is > 0 and <= 1000
            ? value
            : throw new ArgumentOutOfRangeException(nameof(CircuitBreakerMinThroughput), value, "Must be between 1 and 1,000.");
    }

    /// <summary>Circuit breaker sampling window in seconds. Range: 5-300. Default: 30.</summary>
    public int CircuitBreakerSamplingDurationSeconds
    {
        get => _circuitBreakerSamplingDurationSeconds;
        init => _circuitBreakerSamplingDurationSeconds = value is >= 5 and <= 300
            ? value
            : throw new ArgumentOutOfRangeException(nameof(CircuitBreakerSamplingDurationSeconds), value, "Must be between 5 and 300.");
    }

    /// <summary>
    /// Number of batch chunks to execute concurrently. Range: 1-10. Default: 1 (sequential).
    /// At 1, batch chunks execute sequentially with cross-chunk backpressure delays (safest for throttled workloads).
    /// At 2+, chunks execute in parallel via SemaphoreSlim, improving throughput for non-throttled workloads.
    /// Higher values consume throttle budget faster; use with caution on large tenants.
    /// </summary>
    public int BatchChunkConcurrency
    {
        get => _batchChunkConcurrency;
        init => _batchChunkConcurrency = value is >= 1 and <= 10
            ? value
            : throw new ArgumentOutOfRangeException(nameof(BatchChunkConcurrency), value, "Must be between 1 and 10.");
    }

    /// <summary>
    /// Target throughput for batch item pacing in items/sec. Range: 0-1000. Default: 20.
    /// Controls inter-chunk delay in sequential batch execution to avoid burst-and-stall
    /// against Graph's server-side write throttle (~20 items/sec for directory objects).
    /// Set to 0 to disable pacing. Does not affect the HTTP-level rate limiter.
    /// </summary>
    public int BatchItemsPerSecond
    {
        get => _batchItemsPerSecond;
        init => _batchItemsPerSecond = value is >= 0 and <= 1000
            ? value
            : throw new ArgumentOutOfRangeException(nameof(BatchItemsPerSecond), value, "Must be between 0 and 1,000.");
    }

    public static ResilientGraphClientOptions Default { get; } = new();
}
