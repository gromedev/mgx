using System.Management.Automation;
using Mgx.Cmdlets.Base;
using Mgx.Cmdlets.Models;

namespace Mgx.Cmdlets.Cmdlets.Configuration;

/// <summary>
/// Get-MgxOption: Display current resilience and rate limiting configuration.
/// </summary>
[Cmdlet(VerbsCommon.Get, "MgxOption")]
[OutputType(typeof(MgxOptionOutput))]
public class GetMgxOption : PSCmdlet
{
    protected override void ProcessRecord()
    {
        var opts = MgxCmdletBase.s_clientOptions;

        WriteObject(new MgxOptionOutput
        {
            RateLimitBurst = opts.RateLimitBurst,
            RateLimitPerSecond = opts.RateLimitPerSecond,
            NoRateLimit = opts.NoRateLimit,
            RateLimitQueueLimit = opts.RateLimitQueueLimit,
            MaxRetryAttempts = opts.MaxRetryAttempts,
            MaxRetryAfterSeconds = opts.MaxRetryAfterSeconds,
            TotalTimeoutSeconds = opts.TotalTimeoutSeconds,
            AttemptTimeoutSeconds = opts.AttemptTimeoutSeconds,
            CircuitBreakerDurationSeconds = opts.CircuitBreakerDurationSeconds,
            CircuitBreakerFailureRatio = opts.CircuitBreakerFailureRatio,
            CircuitBreakerMinThroughput = opts.CircuitBreakerMinThroughput,
            CircuitBreakerSamplingDurationSeconds = opts.CircuitBreakerSamplingDurationSeconds,
            BatchChunkConcurrency = opts.BatchChunkConcurrency,
            BatchItemsPerSecond = opts.BatchItemsPerSecond,
        });
    }
}
