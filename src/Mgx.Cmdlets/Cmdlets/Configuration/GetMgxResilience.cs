using System.Management.Automation;

namespace Mgx.Cmdlets.Cmdlets.Configuration;

/// <summary>
/// Returns the current state of MgxResilience injection.
/// Outputs a PSObject with IsEnabled and whether the injected client is still active.
/// </summary>
[Cmdlet(VerbsCommon.Get, "MgxResilience")]
[OutputType(typeof(PSObject))]
public class GetMgxResilience : PSCmdlet
{
    protected override void ProcessRecord()
    {
        bool isEnabled;
        bool isActive;

        lock (EnableMgxResilience.StateLock)
        {
            isEnabled = EnableMgxResilience.IsEnabled;

            // Check if our resilient client is still the one in GraphSession
            // (it may have been replaced by Connect-MgGraph or Set-MgRequestContext)
            isActive = false;
            if (isEnabled && EnableMgxResilience.ResilientSdkClient != null)
            {
                var currentSdkClient = GetCurrentGraphHttpClient();
                isActive = ReferenceEquals(currentSdkClient, EnableMgxResilience.ResilientSdkClient);
            }
        }

        var result = new Models.MgxResilienceOutput
        {
            IsEnabled = isEnabled,
            IsActive = isActive,
        };

        if (isEnabled && !isActive)
        {
            result.Warning =
                "Resilience was enabled but the SDK client was replaced " +
                "(e.g., by Connect-MgGraph). Run Enable-MgxResilience to re-inject.";
        }

        WriteObject(result);
    }

    private static HttpClient? GetCurrentGraphHttpClient()
    {
        try
        {
            var graphSessionType = Base.MgxCmdletBase.FindType(
                "Microsoft.Graph.PowerShell.Authentication.GraphSession");
            if (graphSessionType == null) return null;

            var instance = graphSessionType.GetProperty("Instance",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                ?.GetValue(null);
            if (instance == null) return null;

            return instance.GetType().GetProperty("GraphHttpClient")
                ?.GetValue(instance) as HttpClient;
        }
        catch
        {
            return null;
        }
    }
}
