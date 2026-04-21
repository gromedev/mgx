using System.Management.Automation;
using System.Net;
using System.Reflection;
using Mgx.Engine.Http;
using Mgx.Cmdlets.Base;

namespace Mgx.Cmdlets.Cmdlets.Configuration;

/// <summary>
/// Injects Polly resilience (retry, circuit breaker, rate limiting) into the
/// Microsoft.Graph SDK's HTTP transport. After calling this, all SDK cmdlets
/// (Get-MgUser, Get-MgGroup, etc.) automatically gain resilience with zero
/// script changes required.
///
/// Wraps the existing SDK HttpClient (preserving its full handler chain:
/// ODataQueryOptionsHandler, NationalCloudHandler, RedirectHandler,
/// AuthenticationHandler, etc.) with a ResilientDelegatingHandler on top.
///
/// Calling Enable-MgxResilience when already enabled re-injects if the SDK
/// reset the client (e.g., after Connect-MgGraph or Set-MgRequestContext).
/// The SDK's built-in RetryHandler still runs inside the wrapped chain;
/// retries can compound, bounded by TotalTimeoutSeconds and circuit breaker.
/// </summary>
[Cmdlet(VerbsLifecycle.Enable, "MgxResilience", SupportsShouldProcess = true)]
public class EnableMgxResilience : PSCmdlet
{
    // Lock protecting all static state transitions. Used by both Enable and Disable.
    internal static readonly object StateLock = new();

    // State for Disable-MgxResilience to restore
    internal static HttpClient? OriginalSdkClient { get; set; }
    internal static HttpClient? ResilientSdkClient { get; set; }
    internal static bool IsEnabled { get; set; }
    internal static ResilientDelegatingHandler? ActiveHandler { get; set; }

    protected override void ProcessRecord()
    {
        lock (StateLock)
        {
            var graphSessionType = MgxCmdletBase.FindType(
                "Microsoft.Graph.PowerShell.Authentication.GraphSession");
            if (graphSessionType == null)
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException(
                        "Microsoft.Graph.Authentication module not loaded. Run Connect-MgGraph first."),
                    "GraphSessionNotFound", ErrorCategory.ObjectNotFound, null));
                return;
            }

            var instance = graphSessionType.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (instance == null)
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException("GraphSession.Instance is null. Run Connect-MgGraph first."),
                    "GraphSessionNull", ErrorCategory.InvalidOperation, null));
                return;
            }

            var clientProp = instance.GetType().GetProperty("GraphHttpClient");
            var currentClient = clientProp?.GetValue(instance) as HttpClient;

            // Pre-initialize Mgx's own HTTP client before the SDK probe runs.
            // The probe calls Invoke-MgGraphRequest which changes Azure Identity internal
            // state and breaks GetAuthenticationProviderAsync for subsequent callers.
            // Building Mgx's clean client first ensures it is cached before that happens.
            MgxCmdletBase.TryPreInitHttpClient(WriteWarning, WriteVerbose);

            // Force SDK to initialize its HttpClient if not yet initialized
            if (currentClient == null)
            {
                WriteVerbose("GraphHttpClient not initialized. Triggering initialization...");
                currentClient = ForceInitializeAndGetClient(instance, clientProp);
            }

            if (currentClient == null)
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException(
                        "Could not access GraphHttpClient. Ensure Connect-MgGraph has been called."),
                    "HttpClientNotFound", ErrorCategory.ConnectionError, null));
                return;
            }

            // If already enabled, check if our client is still active
            if (IsEnabled)
            {
                if (ReferenceEquals(currentClient, ResilientSdkClient))
                {
                    WriteVerbose("MgxResilience is already active.");
                    return;
                }
                // Our client was replaced (e.g., by Connect-MgGraph or Set-MgRequestContext).
                // Dispose the old wrapped client to release its handler chain and sockets.
                WriteVerbose("MgxResilience was reset by SDK. Re-injecting resilience...");
                ResilientSdkClient?.Dispose();
                ResilientSdkClient = null;
                // Reset circuit breaker / rate limiter state from the previous tenant
                ResiliencePipelineFactory.Reset();
            }

            if (!ShouldProcess("Microsoft.Graph SDK HttpClient",
                "Replace with Polly resilience pipeline (retry, circuit breaker, rate limiting)"))
                return;

            // Save the current SDK client AFTER we know build will be attempted
            OriginalSdkClient = currentClient;

            var resilientClient = BuildResilientSdkClient(currentClient);
            if (resilientClient == null)
            {
                // Rollback: don't leave stale OriginalSdkClient on failure
                OriginalSdkClient = null;
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException(
                        "Failed to build resilient HTTP client."),
                    "ResilientClientBuildFailed", ErrorCategory.InvalidOperation, null));
                return;
            }

            // Replace the SDK's HttpClient
            clientProp!.SetValue(instance, resilientClient);
            ResilientSdkClient = resilientClient;
            IsEnabled = true;

            WriteVerbose("MgxResilience enabled. All Microsoft.Graph SDK cmdlets now use " +
                          "Polly retry, circuit breaker, and rate limiting.");
        }
    }

    private HttpClient? ForceInitializeAndGetClient(object instance, PropertyInfo? clientProp)
    {
        // Use the Graph endpoint from the session (sovereign cloud support)
        var endpoint = MgxCmdletBase.GetGraphEndpoint(WriteWarning, WriteVerbose) ?? "https://graph.microsoft.com";

        // Save AzureADEndpoint before probe. Invoke-MgGraphRequest replaces
        // GraphSession.Environment with a new object that has an empty AzureADEndpoint,
        // which permanently breaks GetAuthenticationProviderAsync (GetAuthorityUrl returns
        // "/tenantId" instead of "https://login.microsoftonline.com/tenantId").
        var envObj = instance.GetType().GetProperty("Environment")?.GetValue(instance);
        var savedAadEndpoint = envObj?.GetType().GetProperty("AzureADEndpoint")?.GetValue(envObj)?.ToString();

        using var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);
        ps.AddCommand("Invoke-MgGraphRequest")
            .AddParameter("Method", "GET")
            .AddParameter("Uri", $"{endpoint}/v1.0/organization?$top=1&$select=id")
            .AddParameter("ErrorAction", "Stop")
            .AddParameter("WarningAction", "SilentlyContinue"); // suppress incidental probe warnings
        ps.AddCommand("Out-Null"); // suppress output from flowing to caller's pipeline
        try { ps.Invoke(); }
        catch (Exception ex)
        {
            WriteVerbose($"Initialization probe failed (expected): {ex.Message}");
        }

        // Restore AzureADEndpoint on the NEW Environment object the probe created.
        MgxCmdletBase.RestoreAzureADEndpoint(instance, savedAadEndpoint, WriteVerbose);

        return clientProp?.GetValue(instance) as HttpClient;
    }

    private HttpClient? BuildResilientSdkClient(HttpClient sdkClient)
    {
        try
        {
            var (pipeline, rateLimiter) = ResiliencePipelineFactory.GetOrCreate(MgxCmdletBase.s_clientOptions);

            // Wrap the existing SDK client (preserving its full handler chain:
            // ODataQueryOptionsHandler, NationalCloudHandler, RedirectHandler,
            // AuthenticationHandler, etc.) with our resilience layer on top.
            //
            // Handler chain: ResilientDelegatingHandler -> SdkClientBridgeHandler -> sdkClient
            //   The bridge handler delegates SendAsync to the original SDK HttpClient,
            //   which processes through its complete handler pipeline internally.
            //
            // The SDK's built-in RetryHandler still runs inside, so
            // a persistent 429 may retry internally (SDK: ~3 times) before our outer
            // handler retries again. This is bounded by TotalTimeoutSeconds (300s)
            // and circuit breaker. Both paths share the same pipeline, rate limiter,
            // and circuit breaker to prevent cache thrashing and ensure consistent
            // failure detection across SDK and direct Mgx cmdlets.
            var resilientHandler = new ResilientDelegatingHandler(pipeline, rateLimiter)
            {
                InnerHandler = new SdkClientBridgeHandler(sdkClient)
            };
            ActiveHandler = resilientHandler;

            return new HttpClient(resilientHandler)
            {
                Timeout = sdkClient.Timeout
            };
        }
        catch (Exception ex)
        {
            WriteWarning($"Failed to build resilient client: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Bridges from a DelegatingHandler chain to an existing HttpClient, preserving
    /// the SDK's full handler pipeline (OData, NationalCloud, Redirect, Auth, etc.).
    /// </summary>
    private sealed class SdkClientBridgeHandler : HttpMessageHandler
    {
        private readonly HttpClient _sdkClient;
        internal SdkClientBridgeHandler(HttpClient sdkClient) => _sdkClient = sdkClient;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => _sdkClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }
}
