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
    public SwitchParameter NoAdaptivePacing { get; set; }

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
    [ValidateRange(2, 1000)]
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

    /// <summary>
    /// Parameters PowerShell adds to every cmdlet. Present in BoundParameters, but none of them
    /// is a change to an option.
    /// </summary>
    private static readonly HashSet<string> CommonParameterNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Verbose", "Debug", "ErrorAction", "WarningAction", "InformationAction", "ProgressAction",
        "ErrorVariable", "WarningVariable", "InformationVariable", "OutVariable", "OutBuffer",
        "PipelineVariable", "WhatIf", "Confirm",
    };

    protected override void ProcessRecord()
    {
        var bound = MyInvocation.BoundParameters;

        // What the target names is what this run would set, which is not everything bound to it:
        // PowerShell binds the common parameters too, so `-Verbose -WhatIf` previewed a change to
        // "MgxOptions (Verbose, WhatIf)" - two names that set no option - for a run the guard
        // below then reports as unchanged. Filtered through the set that guard counts with, in
        // the order they were bound, dropped entirely when it leaves nothing to name, and never
        // includes -Reset itself: what it names beside a reset is what the reset ignores, not the
        // switch that caused it.
        var optionNames = bound.Keys.Cast<string>()
            .Where(k => !CommonParameterNames.Contains(k) && k != nameof(Reset))
            .ToArray();

        // No -Reset and no option: nothing to do (avoids unnecessary pipeline rebuild, which
        // would destroy circuit breaker failure history). Ahead of ShouldProcess on purpose - the
        // guard used to sit after the gate, so a bare `Set-MgxOption` previewed "Set" on
        // "MgxOptions" and prompted for it under -Confirm before landing here and reporting the
        // run it had just asked about as unchanged.
        //
        // Counted without the common parameters, which BoundParameters includes: -Verbose alone
        // made this look like a real change, so `Set-MgxOption -Verbose` rebuilt the pipeline and
        // discarded exactly the history this guard was written to keep. The same list the target
        // below names, so what a preview says and what a run does cannot come apart.
        if (!Reset.IsPresent && optionNames.Length == 0)
        {
            WriteVerbose("No parameters specified. Options unchanged.");
            return;
        }

        // -Reset restores every default; an option bound beside it does not survive that. Silently
        // dropping it would read as the option having taken effect over the defaults it did not
        // survive, so the run says what it is ignoring before it asks whether to proceed.
        if (Reset.IsPresent && optionNames.Length > 0)
        {
            WriteWarning("-Reset resets every option; "
                + $"{string.Join(", ", optionNames.Select(n => $"-{n}"))} given beside it are ignored.");
        }

        var target = Reset.IsPresent
            ? optionNames.Length == 0
                ? "MgxOptions (reset to defaults)"
                : $"MgxOptions (reset to defaults; ignoring {string.Join(", ", optionNames)})"
            : $"MgxOptions ({string.Join(", ", optionNames)})";
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
            NoAdaptivePacing = bound.ContainsKey(nameof(NoAdaptivePacing))
                ? NoAdaptivePacing.IsPresent
                : current.NoAdaptivePacing,
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

        // -NoRateLimit switches off batch item pacing as well, which is a second mechanism
        // against Graph's server-side WRITE throttle rather than the client-side limiter. Both
        // are documented separately and -BatchItemsPerSecond 0 is the documented way to turn
        // pacing off, so a caller who set both deserves to hear that one of them lost.
        if (options.NoRateLimit && bound.ContainsKey(nameof(BatchItemsPerSecond))
            && options.BatchItemsPerSecond > 0)
        {
            WriteWarning(
                $"-NoRateLimit also disables batch item pacing, so -BatchItemsPerSecond "
                + $"{options.BatchItemsPerSecond} has no effect. Drop -NoRateLimit to keep pacing, "
                + "or set -BatchItemsPerSecond 0 to disable it explicitly.");
        }

        MgxCmdletBase.SetClientOptions(options);
        WriteVerbose($"Mgx options updated: Burst={options.RateLimitBurst}, Rate={options.RateLimitPerSecond}/s, " +
                    $"NoRateLimit={options.NoRateLimit}, NoAdaptivePacing={options.NoAdaptivePacing}, " +
                    $"QueueLimit={options.RateLimitQueueLimit}, " +
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
