using System.Management.Automation;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
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

    // Every wrapper this module has installed, against the genuine SDK client underneath it. The
    // keys are weak, so an entry lasts exactly as long as the wrapper somebody still holds and
    // the table is not itself a reference the injection has to release. It is how a wrapper left
    // by an earlier import is recognized after the statics that tracked it are gone: this
    // assembly is never unloaded, so the table spans any number of import cycles.
    private static readonly ConditionalWeakTable<HttpClient, HttpClient> s_bridgeTargets = new();

    /// <summary>
    /// The genuine SDK client under <paramref name="client"/>, which is <paramref name="client"/>
    /// itself unless mgx wrapped it. Wrapping a wrapper multiplies every layer: under a throttle
    /// each layer retries the one beneath it, so the attempts reaching the wire go up per layer,
    /// telemetry counts each attempt once per layer, and the pacer halves once per layer.
    /// </summary>
    internal static HttpClient ResolveGenuineSdkClient(HttpClient client)
    {
        while (s_bridgeTargets.TryGetValue(client, out var inner) && !ReferenceEquals(inner, client))
            client = inner;
        return client;
    }

    /// <summary>Whether this client is a wrapper mgx installed, in this import or an earlier one.</summary>
    internal static bool IsInjectedWrapper(HttpClient? client) =>
        client != null && s_bridgeTargets.TryGetValue(client, out _);

    /// <summary>Whether GraphSession is holding a wrapper of ours right now.</summary>
    internal static bool SessionHoldsInjectedWrapper()
    {
        try
        {
            var instance = MgxCmdletBase.TryGetGraphSessionInstance();
            var current = instance?.GetType().GetProperty("GraphHttpClient")?.GetValue(instance);
            return IsInjectedWrapper(current as HttpClient);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Mgx] Could not read GraphHttpClient: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Puts GraphSession.GraphHttpClient back on the genuine SDK client when the client installed
    /// there is a wrapper of ours, and reports whether it did.
    /// <para>
    /// The wrapper is not disposed: an SDK request already in flight is sending through it, and
    /// disposing cancels that request mid-enumeration. Restoring the property stops new traffic,
    /// and the wrapper is collected once the requests still using it finish.
    /// </para>
    /// </summary>
    internal static bool TryRestoreGenuineSdkClient()
    {
        try
        {
            var instance = MgxCmdletBase.TryGetGraphSessionInstance();
            var clientProp = instance?.GetType().GetProperty("GraphHttpClient");
            if (instance == null || clientProp == null) return false;

            if (clientProp.GetValue(instance) is not HttpClient current || !IsInjectedWrapper(current))
                return false;

            clientProp.SetValue(instance, ResolveGenuineSdkClient(current));
            return true;
        }
        catch (Exception ex)
        {
            // Module teardown calls this: a reflection failure must not stop the module unloading.
            System.Diagnostics.Debug.WriteLine($"[Mgx] Could not restore the SDK client: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Unwinds the injection: the session goes back to the genuine SDK client before mgx lets go
    /// of the wrapper. Nothing is disposed - see TryRestoreGenuineSdkClient.
    /// <para>
    /// These statics outlive Remove-Module, so what is left here is what the next import finds.
    /// Dropping the references without restoring the session strands the wrapper: it stays
    /// installed, and every SDK request the process makes from then on goes through a handler
    /// belonging to a module that is no longer loaded, until somebody imports mgx and disables
    /// it. What the wrapper bridges to is not lost with these references - s_bridgeTargets is
    /// deliberately not one of them - so a later Enable-MgxResilience still wraps the genuine
    /// client rather than the wrapper, and Disable-MgxResilience still has it to restore.
    /// </para>
    /// </summary>
    internal static void ReleaseInjection()
    {
        lock (StateLock)
        {
            TryRestoreGenuineSdkClient();
            IsEnabled = false;
            ActiveHandler = null;
            ResilientSdkClient = null;
            OriginalSdkClient = null;
        }
    }

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

            // Already enabled and our client is still the one installed: nothing to do. Reading
            // the session is not a change, so this answer is the same with -WhatIf as without.
            // The null test earns its place now that the session's client is only forced below
            // the gate: a session holding nothing would otherwise compare equal to an injection
            // whose build failed, and report itself active.
            if (IsEnabled && currentClient != null && ReferenceEquals(currentClient, ResilientSdkClient))
            {
                WriteVerbose("MgxResilience is already active.");
                return;
            }

            // Everything past this line changes state, so all of it is under the gate. The
            // pre-initialization resets the pipeline factory and the SDK probe sends a real
            // request and leaves a client on the session; dropping the wrapper we installed
            // leaves Disable-MgxResilience with no record of what it is taking off. What -WhatIf
            // reports is decided above it, from state this cmdlet has only read.
            if (!ShouldProcess("Microsoft.Graph SDK HttpClient",
                "Replace with Polly resilience pipeline (retry, circuit breaker, rate limiting)"))
                return;

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

            if (IsEnabled)
            {
                // Our client was replaced (e.g., by Connect-MgGraph or Set-MgRequestContext).
                WriteVerbose("MgxResilience was reset by SDK. Re-injecting resilience...");
                // Not disposed: HttpClient.Dispose cancels its pending-request token source and
                // the bridge handler forwards that token inward, so SDK requests already in
                // flight die. Restoring GraphSession.GraphHttpClient stops new traffic; the old
                // client is collected once the requests still using it finish. It holds no
                // connections of its own to release either - it bridges to the SDK's client,
                // and that client's pool closes its sockets on its own timers.
                _ = ResilientSdkClient;
                ResilientSdkClient = null;
                // Reset circuit breaker / rate limiter state from the previous tenant
                ResiliencePipelineFactory.Reset();
            }

            // Wrap the genuine client, never a wrapper. The session can still be holding one from
            // an earlier import - its statics went with that import - and a second layer over it
            // is what OriginalSdkClient would then be restored to by Disable-MgxResilience.
            currentClient = ResolveGenuineSdkClient(currentClient);

            // Save the current SDK client AFTER we know build will be attempted
            OriginalSdkClient = currentClient;

            var resilientClient = BuildResilientSdkClient(currentClient, WriteWarning);
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
        var endpoint = MgxCmdletBase.GetGraphEndpoint(WriteWarning, WriteVerbose) ?? MgxCmdletBase.DefaultGraphEndpoint;

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

    /// <summary>
    /// Re-injects resilience after the Graph identity changed. Connect-MgGraph builds a new
    /// SDK HttpClient, so the wrapper installed by Enable-MgxResilience is left bridging to a
    /// client that still carries the previous credentials. Called by MgxCmdletBase.GetClient()
    /// once it detects a new identity, which keeps SDK cmdlets (Get-MgUser and friends) both
    /// resilient and correctly authenticated without a second Enable-MgxResilience call.
    ///
    /// Lock ordering: never call this while holding MgxCmdletBase's init lock. ProcessRecord
    /// takes StateLock and then that lock via TryPreInitHttpClient, so the reverse order can
    /// deadlock.
    /// </summary>
    internal static void RefreshInjectedClient(Action<string> warn, Action<string> verbose)
    {
        lock (StateLock)
        {
            if (!IsEnabled) return;

            var instance = MgxCmdletBase.TryGetGraphSessionInstance();
            var clientProp = instance?.GetType().GetProperty("GraphHttpClient");
            if (instance == null || clientProp == null)
            {
                IsEnabled = false;
                warn("Graph identity changed but GraphSession is unavailable, so Mgx resilience "
                    + "could not be re-injected. Run Enable-MgxResilience again.");
                return;
            }

            var currentClient = clientProp.GetValue(instance) as HttpClient;
            if (ReferenceEquals(currentClient, ResilientSdkClient))
            {
                // The SDK kept our wrapper, so it still bridges to the pre-reconnect client.
                // Clear it and let the SDK rebuild lazily against the new AuthContext.
                clientProp.SetValue(instance, null);
                currentClient = null;
            }

            // Not disposed - see the note above; in-flight SDK requests would be cancelled.
            _ = ResilientSdkClient;
            ResilientSdkClient = null;
            ActiveHandler = null;
            OriginalSdkClient = null;
            ResiliencePipelineFactory.Reset();

            if (currentClient == null)
            {
                IsEnabled = false;
                warn("Graph identity changed. Mgx resilience was removed from the Microsoft.Graph "
                    + "SDK client; run Enable-MgxResilience again to re-inject it.");
                return;
            }

            currentClient = ResolveGenuineSdkClient(currentClient);

            var refreshed = BuildResilientSdkClient(currentClient, warn);
            if (refreshed == null)
            {
                IsEnabled = false;
                warn("Graph identity changed but resilience could not be re-injected into the "
                    + "Microsoft.Graph SDK client. Run Enable-MgxResilience again.");
                return;
            }

            OriginalSdkClient = currentClient;
            clientProp.SetValue(instance, refreshed);
            ResilientSdkClient = refreshed;
            verbose("Re-injected Mgx resilience into the Microsoft.Graph SDK client "
                + "after the Graph identity changed.");
        }
    }

    /// <summary>
    /// Turns off the SDK's own retry handler for requests that pass through the wrap.
    ///
    /// That handler sits inside the wrapped chain and answers 429 and 503 itself, so a throttle
    /// never reached Mgx's pipeline: the adaptive pacer stayed in slow start through a live
    /// throttle, Get-MgxTelemetry reported no throttle retries while accumulating two minutes of
    /// retry delay under another name, and the two retriers compounded - four intended attempts
    /// could reach the wire eight times, because the SDK sleeps Retry-After inside the call and
    /// Mgx's own attempt timeout then fires.
    ///
    /// Kiota reads this option per request. The type is resolved reflectively so the engine and
    /// this assembly need no reference to it; if the SDK ever moves or renames it, the wrap keeps
    /// working exactly as it did before.
    ///
    /// Throws rather than warning on the paths it cannot satisfy. It runs from the handler's
    /// option factory, on a request thread with no pipeline to write a warning to - the handler
    /// catches this, notes it, and carries on with the inner retry handler left as it was.
    /// </summary>
    internal static IReadOnlyDictionary<string, object?>? BuildInnerRetryOverride()
    {
        const string OptionType = "Microsoft.Kiota.Http.HttpClientLibrary.Middleware.Options.RetryHandlerOption";

        var type = MgxCmdletBase.FindType(OptionType)
            ?? throw new InvalidOperationException(
                "the Graph SDK's retry option type was not found, so its own retry handler stays "
                + "active inside the wrap and throttling will not reach Mgx's pacer or telemetry "
                + "on this path");

        var option = Activator.CreateInstance(type);
        var maxRetry = type.GetProperty("MaxRetry");
        if (option == null || maxRetry == null || !maxRetry.CanWrite)
            throw new InvalidOperationException(
                "the Graph SDK's retry option could not be configured, so its own retry handler "
                + "stays active inside the wrap");

        // MaxRetry is the only lever that removes a retry. The option also exposes ShouldRetry,
        // which looks like a way to decline 429 alone and leave the handler's 503 and 504
        // retries intact - it is not: the handler ORs it with its own status check, so
        // ShouldRetry can only add retries, never suppress one. Measured against 1.21.1.
        //
        // The cost is that the handler's 503/504 retries go too, including on writes, and Mgx's
        // pipeline will not take those over: it refuses to retry a non-idempotent request on a
        // 5xx because the write may already have been applied. 429 is unaffected - the pipeline
        // retries that for every method - so throttled writes still complete.
        maxRetry.SetValue(option, 0);

        return new Dictionary<string, object?> { [OptionType] = option };
    }

    internal static HttpClient? BuildResilientSdkClient(HttpClient sdkClient, Action<string> warn)
    {
        try
        {
            // Bridge to the genuine client whatever the caller hands over: a wrapper wrapping a
            // wrapper multiplies wire attempts, telemetry counts and pacer steps per layer.
            sdkClient = ResolveGenuineSdkClient(sdkClient);

            var (pipeline, rateLimiter) = ResiliencePipelineFactory.GetOrCreate(MgxCmdletBase.s_clientOptions);

            // Wrap the existing SDK client (preserving its full handler chain:
            // ODataQueryOptionsHandler, NationalCloudHandler, RedirectHandler,
            // AuthenticationHandler, etc.) with our resilience layer on top.
            //
            // Handler chain: ResilientDelegatingHandler -> SdkClientBridgeHandler -> sdkClient
            //   The bridge handler delegates SendAsync to the original SDK HttpClient,
            //   which processes through its complete handler pipeline internally.
            //
            // The SDK's built-in RetryHandler sits inside this wrap and would otherwise answer
            // 429 and 503 itself, before the outer pipeline ever saw them - so the pacer never
            // learned from a throttle and telemetry booked a throttled session as zero retries.
            // AdditionalRequestOptionsFactory disarms it per request. If that cannot be arranged
            // it stays armed and the session behaves as it did before, just without the
            // measurement. Both paths share the same pipeline, rate limiter,
            // and circuit breaker to prevent cache thrashing and ensure consistent
            // failure detection across SDK and direct Mgx cmdlets.
            var resilientHandler = new ResilientDelegatingHandler(pipeline, rateLimiter)
            {
                InnerHandler = new SdkClientBridgeHandler(sdkClient),
                AdditionalRequestOptionsFactory = BuildInnerRetryOverride
            };
            ActiveHandler = resilientHandler;

            // BaseAddress must come along: Invoke-MgGraphRequest resolves a relative -Uri
            // against the active client's BaseAddress before any handler runs. Default
            // request headers are NOT copied - the bridge delegates to sdkClient.SendAsync,
            // which applies the original client's defaults to each request anyway.
            var wrapper = new HttpClient(resilientHandler)
            {
                BaseAddress = sdkClient.BaseAddress,
                Timeout = sdkClient.Timeout
            };

            // What the wrapper bridges to, recorded where it survives module removal. The
            // teardown, Disable-MgxResilience and the next Enable-MgxResilience all need the
            // genuine client back, and after a removal this is the only thing that still knows it.
            s_bridgeTargets.AddOrUpdate(wrapper, sdkClient);
            return wrapper;
        }
        catch (Exception ex)
        {
            warn($"Failed to build resilient client: {ex.Message}");
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
