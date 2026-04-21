using System.Management.Automation;
using Mgx.Cmdlets.Models;
using Mgx.Engine.Http;

namespace Mgx.Cmdlets.Cmdlets.Configuration;

/// <summary>
/// Get-MgxTelemetry: Return accumulated session telemetry from MgxTelemetryCollector.
/// Reports request counts, retry/throttle breakdown, and timing per category so callers
/// can determine whether the bottleneck is throttling, retry backoff, rate-limiter queuing,
/// or network latency.
/// </summary>
[Cmdlet(VerbsCommon.Get, "MgxTelemetry")]
[OutputType(typeof(MgxTelemetryOutput))]
public class GetMgxTelemetry : PSCmdlet
{
    [Parameter]
    public SwitchParameter Reset { get; set; }

    protected override void ProcessRecord()
    {
        var summary = MgxTelemetryCollector.Current.GetSummary();

        if (Reset.IsPresent)
            MgxTelemetryCollector.Current.Reset();

        WriteObject(new MgxTelemetryOutput
        {
            Requests = summary.TotalRequests,
            Succeeded = summary.Succeeded,
            Failed = summary.Failed,
            ThrottleRetries = summary.ThrottleRetries,
            OtherRetries = summary.OtherRetries,
            CircuitBreakerTrips = summary.CircuitBreakerTrips,
            RateLimiterWaitMs = summary.RateLimiterWaitMs,
            RetryDelayMs = summary.RetryDelayMs,
            HttpMs = summary.HttpMs,
            TotalElapsedMs = summary.ElapsedMs,
            ResourceUnitsConsumed = summary.ResourceUnitsConsumed,
            BatchItemThrottles = summary.BatchItemThrottles,
        });
    }
}
