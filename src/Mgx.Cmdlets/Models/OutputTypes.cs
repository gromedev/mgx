namespace Mgx.Cmdlets.Models;

/// <summary>
/// Output type for Get-MgxTelemetry. Real .NET type ensures PowerShell's
/// ListControl Labels render correctly even when mixed with other typed
/// objects in the same output stream.
/// </summary>
public sealed class MgxTelemetryOutput
{
    public long Requests { get; set; }
    public long Succeeded { get; set; }
    public long Failed { get; set; }
    public long ThrottleRetries { get; set; }
    public long OtherRetries { get; set; }
    public long CircuitBreakerTrips { get; set; }
    public long RateLimiterWaitMs { get; set; }
    public long RetryDelayMs { get; set; }
    public long HttpMs { get; set; }
    public long TotalElapsedMs { get; set; }
    public long ResourceUnitsConsumed { get; set; }
    public long BatchItemThrottles { get; set; }
}

/// <summary>
/// Output type for Get-MgxOption.
/// </summary>
public sealed class MgxOptionOutput
{
    public int RateLimitBurst { get; set; }
    public int RateLimitPerSecond { get; set; }
    public bool NoRateLimit { get; set; }
    public int RateLimitQueueLimit { get; set; }
    public int MaxRetryAttempts { get; set; }
    public int MaxRetryAfterSeconds { get; set; }
    public int TotalTimeoutSeconds { get; set; }
    public int AttemptTimeoutSeconds { get; set; }
    public int CircuitBreakerDurationSeconds { get; set; }
    public double CircuitBreakerFailureRatio { get; set; }
    public int CircuitBreakerMinThroughput { get; set; }
    public int CircuitBreakerSamplingDurationSeconds { get; set; }
    public int BatchChunkConcurrency { get; set; }
    public int BatchItemsPerSecond { get; set; }
}

/// <summary>
/// Output type for Get-MgxResilience.
/// </summary>
public sealed class MgxResilienceOutput
{
    public bool IsEnabled { get; set; }
    public bool IsActive { get; set; }
    public string? Warning { get; set; }
}

/// <summary>
/// Output type for Export-MgxCollection summary.
/// </summary>
public sealed class MgxExportResult
{
    public long ItemCount { get; set; }
    public string OutputFile { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public long? ResumedFrom { get; set; }
}
