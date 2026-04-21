using System.Management.Automation;
using Mgx.Cmdlets.Base;
using Mgx.Engine.Http;

namespace Mgx.Cmdlets.Cmdlets.Configuration;

/// <summary>
/// Set-MgxOption: Configure resilience options for all Mgx cmdlets.
/// Only parameters explicitly passed are updated; unspecified values retain their current settings.
/// Options take effect on the next cmdlet invocation.
/// Use -Reset to restore all options to their defaults.
/// </summary>
[Cmdlet(VerbsCommon.Set, "MgxOption", SupportsShouldProcess = true)]
public class SetMgxOption : PSCmdlet
{
    [Parameter]
    [ValidateRange(1, 10_000)]
    public int RateLimitBurst { get; set; }

    [Parameter]
    [ValidateRange(1, 10_000)]
    public int RateLimitPerSecond { get; set; }

    [Parameter]
    public SwitchParameter NoRateLimit { get; set; }

    [Parameter]
    [ValidateRange(0, 100_000)]
    public int RateLimitQueueLimit { get; set; }

    [Parameter]
    [ValidateRange(1, 600)]
    public int MaxRetryAfterSeconds { get; set; }

    [Parameter]
    [ValidateRange(1, 50)]
    public int MaxRetryAttempts { get; set; }

    [Parameter]
    [ValidateRange(1, 3600)]
    public int TotalTimeoutSeconds { get; set; }

    [Parameter]
    [ValidateRange(1, 300)]
    public int AttemptTimeoutSeconds { get; set; }

    [Parameter]
    [ValidateRange(1, 300)]
    public int CircuitBreakerDurationSeconds { get; set; }

    [Parameter]
    [ValidateRange(0.01, 1.0)]
    public double CircuitBreakerFailureRatio { get; set; }

    [Parameter]
    [ValidateRange(1, 1000)]
    public int CircuitBreakerMinThroughput { get; set; }

    [Parameter]
    [ValidateRange(5, 300)]
    public int CircuitBreakerSamplingDurationSeconds { get; set; }

    [Parameter]
    [ValidateRange(1, 10)]
    public int BatchChunkConcurrency { get; set; }

    [Parameter]
    [ValidateRange(0, 1000)]
    public int BatchItemsPerSecond { get; set; }

    [Parameter]
    public SwitchParameter Reset { get; set; }

    protected override void ProcessRecord()
    {
        var bound = MyInvocation.BoundParameters;

        var target = Reset.IsPresent
            ? "MgxOptions (reset to defaults)"
            : $"MgxOptions ({string.Join(", ", bound.Keys)})";
        if (!ShouldProcess(target, "Set"))
            return;

        // -Reset: restore all defaults and return
        if (Reset.IsPresent)
        {
            MgxCmdletBase.SetClientOptions(ResilientGraphClientOptions.Default);
            WriteVerbose("Mgx options reset to defaults.");
            WarnIfResilienceActive();
            return;
        }

        // No parameters passed: nothing to do (avoids unnecessary pipeline rebuild
        // which would destroy circuit breaker failure history)
        if (bound.Count == 0)
        {
            WriteVerbose("No parameters specified. Options unchanged.");
            return;
        }

        // Start from current options, only override values the user actually passed
        var current = MgxCmdletBase.s_clientOptions;

        // If user explicitly set rate params but NOT NoRateLimit,
        // implicitly re-enable rate limiting (clear sticky NoRateLimit)
        var noRateLimit = bound.ContainsKey(nameof(NoRateLimit))
            ? NoRateLimit.IsPresent
            : (bound.ContainsKey(nameof(RateLimitBurst)) || bound.ContainsKey(nameof(RateLimitPerSecond)))
                ? false  // Setting rate params implicitly re-enables rate limiting
                : current.NoRateLimit;

        var options = new ResilientGraphClientOptions
        {
            RateLimitBurst = bound.ContainsKey(nameof(RateLimitBurst)) ? RateLimitBurst : current.RateLimitBurst,
            RateLimitPerSecond = bound.ContainsKey(nameof(RateLimitPerSecond)) ? RateLimitPerSecond : current.RateLimitPerSecond,
            NoRateLimit = noRateLimit,
            RateLimitQueueLimit = bound.ContainsKey(nameof(RateLimitQueueLimit)) ? RateLimitQueueLimit : current.RateLimitQueueLimit,
            MaxRetryAfterSeconds = bound.ContainsKey(nameof(MaxRetryAfterSeconds)) ? MaxRetryAfterSeconds : current.MaxRetryAfterSeconds,
            MaxRetryAttempts = bound.ContainsKey(nameof(MaxRetryAttempts)) ? MaxRetryAttempts : current.MaxRetryAttempts,
            TotalTimeoutSeconds = bound.ContainsKey(nameof(TotalTimeoutSeconds)) ? TotalTimeoutSeconds : current.TotalTimeoutSeconds,
            AttemptTimeoutSeconds = bound.ContainsKey(nameof(AttemptTimeoutSeconds)) ? AttemptTimeoutSeconds : current.AttemptTimeoutSeconds,
            CircuitBreakerDurationSeconds = bound.ContainsKey(nameof(CircuitBreakerDurationSeconds)) ? CircuitBreakerDurationSeconds : current.CircuitBreakerDurationSeconds,
            CircuitBreakerFailureRatio = bound.ContainsKey(nameof(CircuitBreakerFailureRatio)) ? CircuitBreakerFailureRatio : current.CircuitBreakerFailureRatio,
            CircuitBreakerMinThroughput = bound.ContainsKey(nameof(CircuitBreakerMinThroughput)) ? CircuitBreakerMinThroughput : current.CircuitBreakerMinThroughput,
            CircuitBreakerSamplingDurationSeconds = bound.ContainsKey(nameof(CircuitBreakerSamplingDurationSeconds)) ? CircuitBreakerSamplingDurationSeconds : current.CircuitBreakerSamplingDurationSeconds,
            BatchChunkConcurrency = bound.ContainsKey(nameof(BatchChunkConcurrency)) ? BatchChunkConcurrency : current.BatchChunkConcurrency,
            BatchItemsPerSecond = bound.ContainsKey(nameof(BatchItemsPerSecond)) ? BatchItemsPerSecond : current.BatchItemsPerSecond
        };

        // Cross-property validation: warn if per-attempt timeout makes retries impossible
        if (options.AttemptTimeoutSeconds >= options.TotalTimeoutSeconds)
        {
            WriteWarning($"AttemptTimeoutSeconds ({options.AttemptTimeoutSeconds}) >= TotalTimeoutSeconds ({options.TotalTimeoutSeconds}). " +
                        "Retries are effectively disabled because the total timeout will fire on the first attempt.");
        }

        MgxCmdletBase.SetClientOptions(options);
        WriteVerbose($"Mgx options updated: Burst={options.RateLimitBurst}, Rate={options.RateLimitPerSecond}/s, " +
                    $"NoRateLimit={options.NoRateLimit}, QueueLimit={options.RateLimitQueueLimit}, " +
                    $"MaxRetry={options.MaxRetryAttempts}, TotalTimeout={options.TotalTimeoutSeconds}s, " +
                    $"AttemptTimeout={options.AttemptTimeoutSeconds}s, CBDuration={options.CircuitBreakerDurationSeconds}s, " +
                    $"CBFailureRatio={options.CircuitBreakerFailureRatio}, CBMinThroughput={options.CircuitBreakerMinThroughput}, " +
                    $"CBSampling={options.CircuitBreakerSamplingDurationSeconds}s, " +
                    $"BatchChunkConcurrency={options.BatchChunkConcurrency}, " +
                    $"BatchItemsPerSecond={options.BatchItemsPerSecond}");

        WarnIfResilienceActive();
    }

    private void WarnIfResilienceActive()
    {
        // Warn if resilience is injected into SDK cmdlets: the injected handler
        // was built with the old options and won't pick up the new ones.
        if (EnableMgxResilience.IsEnabled)
        {
            WriteWarning("MgxResilience is active. New options apply to Invoke-MgxRequest but NOT " +
                        "to SDK cmdlets until you run: Disable-MgxResilience; Enable-MgxResilience");
        }
    }
}
