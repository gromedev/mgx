using System.Management.Automation;
using System.Reflection;
using Mgx.Cmdlets.Base;

namespace Mgx.Cmdlets.Cmdlets.Configuration;

/// <summary>
/// Removes the Polly resilience injection from the Microsoft.Graph SDK's HTTP transport.
/// Restores the original SDK HttpClient that was saved by Enable-MgxResilience.
/// </summary>
[Cmdlet(VerbsLifecycle.Disable, "MgxResilience", SupportsShouldProcess = true)]
public class DisableMgxResilience : PSCmdlet
{
    protected override void ProcessRecord()
    {
        lock (EnableMgxResilience.StateLock)
        {
            if (!EnableMgxResilience.IsEnabled)
            {
                WriteWarning("MgxResilience is not currently enabled.");
                return;
            }

            var originalClient = EnableMgxResilience.OriginalSdkClient;
            if (originalClient == null)
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException(
                        "Original SDK client was not saved. Cannot restore. " +
                        "Restart your PowerShell session to reset."),
                    "OriginalClientMissing", ErrorCategory.InvalidOperation, null));
                return;
            }

            var graphSessionType = MgxCmdletBase.FindType(
                "Microsoft.Graph.PowerShell.Authentication.GraphSession");
            if (graphSessionType == null)
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException(
                        "GraphSession type not found. Module may have been unloaded."),
                    "GraphSessionNotFound", ErrorCategory.ObjectNotFound, null));
                return;
            }

            var instance = graphSessionType.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (instance == null)
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException("GraphSession.Instance is null."),
                    "GraphSessionNull", ErrorCategory.InvalidOperation, null));
                return;
            }

            var clientProp = instance.GetType().GetProperty("GraphHttpClient");
            if (clientProp == null)
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException(
                        "GraphHttpClient property not found on GraphSession. SDK version may be incompatible."),
                    "PropertyNotFound", ErrorCategory.ObjectNotFound, null));
                return;
            }

            if (!ShouldProcess("Microsoft.Graph SDK HttpClient",
                "Restore original SDK HttpClient (remove Polly resilience)"))
                return;

            // Verify the current client is actually ours before restoring
            var currentClient = clientProp.GetValue(instance) as HttpClient;
            if (currentClient != null && !ReferenceEquals(currentClient, EnableMgxResilience.ResilientSdkClient))
            {
                WriteWarning("The current GraphHttpClient is not the one injected by Enable-MgxResilience. " +
                           "Another module or Connect-MgGraph may have replaced it. Restoring original anyway.");
            }

            clientProp.SetValue(instance, originalClient);

            // Dispose the resilient client to release the ResilientDelegatingHandler
            // and its references to the Polly pipeline.
            var resilientClient = EnableMgxResilience.ResilientSdkClient;
            EnableMgxResilience.IsEnabled = false;
            EnableMgxResilience.OriginalSdkClient = null;
            EnableMgxResilience.ResilientSdkClient = null;
            resilientClient?.Dispose();

            WriteVerbose("MgxResilience disabled. SDK cmdlets restored to original behavior.");
        }
    }
}
