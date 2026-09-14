using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Management.Automation;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mgx.Engine.Http;
using Mgx.Engine.Models;
using Mgx.Engine.Pagination;
using Polly.CircuitBreaker;

namespace Mgx.Cmdlets.Base;

/// <summary>
/// Lightweight base class for Mgx cmdlets that need Graph client access.
/// Provides auth and client lifecycle on top of <see cref="MgxCmdletCore"/>,
/// which supplies cancellation, disposal, and JSON-to-PSObject conversion.
/// Used by Invoke-MgxRequest and Invoke-MgxBatchRequest.
/// </summary>
public abstract class MgxCmdletBase : MgxCmdletCore
{
    private ResilientGraphClient? _client;

    // Which test-transport generation _client was built for. Only consulted while the seam
    // below is armed; -1 means "built outside a scope", which never matches a live epoch.
    private int _clientEpoch = -1;

    private static readonly object s_initLock = new();

    /// <summary>
    /// The transport every ResilientGraphClient is built on. A replaced one is dropped, never
    /// closed: each client captured it as a readonly field in its constructor and goes on
    /// sending through it, one page at a time, for as long as its enumeration runs. It used to
    /// be disposed TotalTimeoutSeconds after a swap - a per-REQUEST timeout used as a resource
    /// lifetime, the same construction the shared rate limiter dropped for the reason recorded
    /// in ResiliencePipelineFactory.GetOrCreate. No such bound is correct: an export may hold
    /// its transport for hours, and a Connect-MgGraph to another identity, a Set-MgxOption, an
    /// Enable-MgxResilience or a Remove-Module on another thread killed it underneath, so the
    /// next page threw ObjectDisposedException mid-stream. Letting go is enough: the handler's
    /// sockets are closed by its own idle and lifetime timeouts once nothing is sending
    /// through them.
    /// </summary>
    private static HttpClient? s_graphHttpClient;
    private static bool s_ownsHttpClient; // false when using SDK fallback (the SDK's own client)

    // Identity the cached client was built for
    private static volatile string? s_cachedAuthFingerprint;

    // WeakReference so a disconnected AuthContext (and the X509Certificate2 it holds) is not
    // kept alive by Mgx. A collected target can never be the current context.
    private static volatile WeakReference<object>? s_cachedAuthContextRef;

    // TotalTimeoutSeconds the cached client's HttpClient.Timeout was derived from.
    private static int s_cachedTotalTimeoutSeconds;

    /// <summary>
    /// The cloud a process answers for before any session has been read, and what a module
    /// removal puts the endpoint back to.
    /// </summary>
    internal const string DefaultGraphEndpoint = "https://graph.microsoft.com";

    internal static volatile string s_graphEndpoint = DefaultGraphEndpoint;
    internal static volatile ResilientGraphClientOptions s_clientOptions = ResilientGraphClientOptions.Default;

    // Test transport seam. The xUnit assembly arms these three through MgxTransportScope, and
    // every shipping path leaves s_testTransport null. InternalsVisibleTo names that assembly
    // and neither assembly is signed, so the grant is by simple name: it keeps the seam out of
    // the public surface and out of the way, not out of reach of code that means to find it.
    internal static volatile HttpClient? s_testTransport;

    // Whether an armed test transport presents as mgx-owned. Get-MgxContent fails closed over a
    // transport it does not own, so a content test arms this and the rest of the suite does not.
    internal static volatile bool s_testTransportOwned;

    // Bumped whenever the armed transport changes. A cmdlet instance that cached a client under
    // an earlier scope rebuilds instead of answering from a transport the scope has retired.
    internal static int s_testTransportEpoch;

    /// <summary>
    /// Base URL for Graph API requests (e.g., "https://graph.microsoft.com/v1.0").
    /// Respects sovereign clouds via GraphSession environment.
    /// </summary>
    protected string GraphBaseUrl => $"{s_graphEndpoint}/v1.0";

    /// <summary>
    /// Get the resilient Graph client with auth-only HttpClient (no Kiota retry/redirect).
    /// Detects auth context changes and rebuilds the client when needed, so re-running
    /// Connect-MgGraph with a different tenant, application, or credential takes effect
    /// without restarting the session.
    /// </summary>
    protected ResilientGraphClient GetClient()
    {
        // The seam is read before the auth probe below on purpose: a test process has no Graph
        // session, so reaching GetCurrentAuthIdentity would take the IsGraphAuthLoaded branch
        // and terminate instead of running the cmdlet under the mock wire.
        //
        // The epoch, then the transport, then the epoch again. A scope arms the transport before
        // it bumps the epoch, so a pair read this way either spans no bump - and belongs together
        // - or is read again. Taking the transport first can pair one a scope has already retired
        // with the epoch that retired it, and this instance then answers from that transport for
        // as long as it lives.
        int epoch;
        HttpClient? testTransport;
        do
        {
            epoch = Volatile.Read(ref s_testTransportEpoch);
            testTransport = s_testTransport;
        }
        while (epoch != Volatile.Read(ref s_testTransportEpoch));

        // The epoch gates the cached client on both paths, not just the seam's. It never moves
        // outside a test process, so this is the plain cache hit in every shipping path - but a
        // cmdlet instance that outlives the scope which armed it would otherwise keep answering
        // from a transport that scope has already disposed.
        if (_client != null && _clientEpoch == epoch) return _client;
        _clientEpoch = epoch;

        if (testTransport != null)
            return ConfigureClient(testTransport, s_clientOptions);

        var identity = GetCurrentAuthIdentity(WriteVerbose);
        if (string.IsNullOrEmpty(identity.Fingerprint))
        {
            var (message, errorId) = DescribeMissingConnection(IsGraphAuthLoaded());

            ThrowTerminatingError(new ErrorRecord(
                new InvalidOperationException(message),
                errorId,
                ErrorCategory.ConnectionError,
                null));
            return null!;
        }

        // Lock protects concurrent runspaces from racing on static client/endpoint init.
        // Capture locals inside lock to prevent TOCTOU race: another thread could enter
        // the lock and replace/dispose s_graphHttpClient between lock exit and usage.
        HttpClient httpClient;
        var identityChanged = false;
        var clientOptions = s_clientOptions;
        lock (s_initLock)
        {
            var previousFingerprint = s_cachedAuthFingerprint;
            var credentialChanged = !string.Equals(
                previousFingerprint, identity.Fingerprint, StringComparison.Ordinal);
            var contextReplaced = AuthContextInstanceChanged(identity.AuthContext);

            // A client borrowed from the SDK goes stale on its own terms, e.g. after Connect-MgGraph swaps
            // GraphSession.GraphHttpClient, and the instance we cached still carries the old auth.
            var sessionClient = s_ownsHttpClient ? null : TryGetSessionGraphHttpClient();
            var borrowedClientStale = sessionClient != null && s_graphHttpClient != null
                && !ReferenceEquals(s_graphHttpClient, sessionClient);

            // Set-MgxOption -TotalTimeoutSeconds only reaches HttpClient.Timeout through a
            // rebuild. The property is immutable once the first request has gone out.
            var timeoutStale = s_ownsHttpClient && s_graphHttpClient != null
                && s_cachedTotalTimeoutSeconds != clientOptions.TotalTimeoutSeconds;

            if (s_graphHttpClient == null || credentialChanged || contextReplaced
                || borrowedClientStale || timeoutStale)
            {
                if (timeoutStale)
                {
                    WriteVerbose($"TotalTimeoutSeconds changed ({s_cachedTotalTimeoutSeconds} -> "
                        + $"{clientOptions.TotalTimeoutSeconds}). Rebuilding the Mgx HTTP client.");
                }

                // previousFingerprint == null is the first build of the session, not a change.
                identityChanged = previousFingerprint != null && (credentialChanged || contextReplaced);
                if (identityChanged)
                {
                    WriteVerbose($"Graph identity changed ({Shorten(previousFingerprint)} -> "
                        + $"{Shorten(identity.Fingerprint)}). Rebuilding the Mgx HTTP client.");
                }

                // Reset-before-Build is intentional here (unlike TryPreInitHttpClient which
                // builds first then resets). GetClient() has a fallback path (SDK client), so
                // resetting circuit breaker state from the old tenant before attempting to build
                // is safe: if BuildCleanHttpClient fails, GetSdkHttpClientFallback provides a
                // working client. If both fail, ThrowTerminatingError is the correct response.
                // Only on a real credential change: a same-identity reconnect should keep its
                // warm rate limiter instead of earning a fresh burst allowance.
                if (credentialChanged) ResiliencePipelineFactory.Reset();
                // The old client is let go, not closed - see the note on s_graphHttpClient.
                s_graphHttpClient = BuildCleanHttpClient(clientOptions.TotalTimeoutSeconds);
                if (s_graphHttpClient != null)
                {
                    s_ownsHttpClient = true;
                }
                else
                {
                    WriteWarning("Could not build auth-only HTTP client. Falling back to SDK client.");
                    s_graphHttpClient = GetSdkHttpClientFallback();
                    s_ownsHttpClient = false; // SDK owns this client; do NOT dispose
                }

                if (s_graphHttpClient == null)
                {
                    ThrowTerminatingError(new ErrorRecord(
                        new InvalidOperationException(
                            "Failed to initialize Graph HTTP client. Ensure Connect-MgGraph has been called."),
                        "HttpClientInitFailed",
                        ErrorCategory.ConnectionError,
                        null));
                    return null!;
                }

                s_cachedAuthFingerprint = identity.Fingerprint;
                s_cachedAuthContextRef = identity.AuthContext is null
                    ? null
                    : new WeakReference<object>(identity.AuthContext);
                s_cachedTotalTimeoutSeconds = clientOptions.TotalTimeoutSeconds;
                s_graphEndpoint = GetGraphEndpoint(WriteWarning, WriteVerbose) ?? DefaultGraphEndpoint;
            }
            httpClient = s_graphHttpClient!;
        }

        // Outside s_initLock by design. Enable-MgxResilience takes StateLock and then s_initLock
        // (via TryPreInitHttpClient), so taking StateLock while holding s_initLock inverts the
        // lock order and can deadlock. Never acquire StateLock inside s_initLock.
        if (identityChanged)
            Cmdlets.Configuration.EnableMgxResilience.RefreshInjectedClient(WriteWarning, WriteVerbose);

        return ConfigureClient(httpClient, clientOptions);
    }

    /// <summary>
    /// Wraps a transport in the ResilientGraphClient this cmdlet invocation uses and caches it.
    /// Split out of GetClient so the transport seam above reaches the same configuration the
    /// auth path produces, rather than a second copy of it that can drift.
    /// </summary>
    private ResilientGraphClient ConfigureClient(HttpClient httpClient, ResilientGraphClientOptions clientOptions)
    {
        _client = new ResilientGraphClient(httpClient, clientOptions);
        _client.BodyReadTimeout = TimeSpan.FromSeconds(clientOptions.AttemptTimeoutSeconds);
        _client.VerboseWriter = msg => WriteVerbose(msg);
        _client.WarningWriter = msg => WriteWarning(msg);
        _client.DebugWriter = msg => WriteDebug(msg);
        _client.DebugEnabled = IsDebugRequested();
        return _client;
    }

    /// <summary>
    /// The Graph identity a client is built for. It's a comparable fingerprint plus the AuthContext
    /// instance it was derived from (null when only the Get-MgContext fallback could run).
    /// An empty fingerprint means "not connected".
    /// </summary>
    internal readonly record struct AuthIdentity(string Fingerprint, object? AuthContext);

    private static AuthIdentity GetCurrentAuthIdentity(Action<string>? verbose)
    {
        try
        {
            var instance = TryGetGraphSessionInstance();
            if (instance != null)
            {
                var authContext = instance.GetType().GetProperty("AuthContext")?.GetValue(instance);
                var fingerprint = BuildAuthFingerprint(authContext, GetGraphEndpointFrom(instance));
                // GraphSession is reachable, so its answer is authoritative - including
                // "no context", which means disconnected.
                return fingerprint.Length == 0 ? default : new AuthIdentity(fingerprint, authContext);
            }
        }
        catch (Exception ex)
        {
            verbose?.Invoke($"Failed to read GraphSession.AuthContext: {ex.Message}");
        }

        // Fallback for SDK internals drift using Get-MgContext
        try
        {
            using var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);
            ps.AddCommand("Get-MgContext");
            var results = ps.Invoke();
            if (ps.HadErrors || results.Count == 0 || results[0] == null) return default;

            var context = UnwrapPSObject(results[0]);
            var fingerprint = BuildAuthFingerprint(context, null);
            return fingerprint.Length == 0 ? default : new AuthIdentity(fingerprint, context);
        }
        catch (Exception ex)
        {
            verbose?.Invoke($"Failed to get Graph auth context: {ex.Message}");
            return default;
        }
    }

    /// <summary>
    /// True when the live AuthContext is a different object than the one the cached client was
    /// built from. Connect-MgGraph replaces the object, so this catches identity changes the
    /// value fingerprint cannot see (a rotated ClientSecret above all).
    /// </summary>
    private static bool AuthContextInstanceChanged(object? current)
    {
        var cachedRef = s_cachedAuthContextRef;
        if (current is null || cachedRef is null) return false;
        return !(cachedRef.TryGetTarget(out var cached) && ReferenceEquals(cached, current));
    }

    private static readonly string[] AuthFingerprintMembers =
    [
        "TenantId", "ClientId", "AuthType", "TokenCredentialType", "ContextScope",
        "Environment", "Account", "AppName", "ManagedIdentityId",
        "CertificateThumbprint", "CertificateSubjectName", "SendCertificateChain", "WamEnabled"
    ];

    /// <summary>
    /// Builds a comparable fingerprint of the effective Graph identity.
    /// </summary>
    internal static string BuildAuthFingerprint(object? authContext, string? graphEndpoint)
    {
        if (authContext == null) return string.Empty;
        if (Stringify(ReadAuthMember(authContext, "TenantId")).Length == 0) return string.Empty;

        var sb = new StringBuilder("mgx-auth-v1");
        foreach (var member in AuthFingerprintMembers)
            AppendField(sb, Stringify(ReadAuthMember(authContext, member)));

        // Certificate is an X509Certificate2, only its thumbprint identifies the credential.
        AppendField(sb, Stringify(ReadAuthMember(ReadAuthMember(authContext, "Certificate"), "Thumbprint")));

        // Scope order is not significant to Graph, so sort before hashing.
        AppendField(sb, ReadAuthMember(authContext, "Scopes") is IEnumerable scopes
            ? string.Join(",", scopes.Cast<object?>().Select(Stringify).OrderBy(s => s, StringComparer.Ordinal))
            : string.Empty);

        AppendField(sb, graphEndpoint ?? string.Empty);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private const char FieldSeparator = (char)0x1f;

    private static void AppendField(StringBuilder sb, string value) =>
        sb.Append(FieldSeparator).Append(value.Length).Append(':').Append(value);

    /// <summary>
    /// Reads a named member off an AuthContext-shaped object.
    /// </summary>
    internal static object? ReadAuthMember(object? source, string name)
    {
        if (source == null) return null;
        try
        {
            if (source is PSObject or IDictionary) return TryGetMember(source, name);
            return source.GetType().GetProperty(name)?.GetValue(source);
        }
        catch
        {
            return null;
        }
    }

    private static string Stringify(object? value) =>
        value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Shorten(string? fingerprint) =>
        string.IsNullOrEmpty(fingerprint) ? "none" : fingerprint[..Math.Min(8, fingerprint.Length)];

    internal static object? TryGetGraphSessionInstance()
    {
        var graphSessionType = FindType("Microsoft.Graph.PowerShell.Authentication.GraphSession");
        return graphSessionType?.GetProperty("Instance",
            BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
    }

    /// <summary>
    /// Why no token could be obtained, given whether Microsoft.Graph.Authentication is present.
    /// It is a soft dependency (see mgx.psd1), so an empty fingerprint has two distinct causes
    /// needing different advice: while it sat in RequiredModules the SDK was guaranteed present
    /// and "run Connect-MgGraph" was always the right answer, and without it that message sends
    /// someone to a cmdlet that does not exist in their session.
    /// </summary>
    internal static (string Message, string ErrorId) DescribeMissingConnection(bool graphAuthLoaded) =>
        graphAuthLoaded
            ? ("Not connected to Microsoft Graph. Run Connect-MgGraph first.",
               "NotConnected")
            : ("Microsoft.Graph.Authentication is not loaded. Install it "
               + "(Install-PSResource -Name Microsoft.Graph.Authentication) and run "
               + "Connect-MgGraph, or supply your own transport via Enable-MgxResilience.",
               "GraphAuthModuleNotLoaded");

    /// <summary>
    /// Whether Microsoft.Graph.Authentication is present in the session. Because mgx does not
    /// declare it in RequiredModules, "absent" is a real state that has to be told apart from
    /// "present but disconnected" when reporting why a token could not be obtained.
    /// <para>
    /// Checks the type first, then Get-MgContext: the SDK's internal type layout has moved
    /// before, and the module can be loaded even when GraphSession is not where we look.
    /// FindType caches only successful lookups, so a module imported later is still seen.
    /// </para>
    /// </summary>
    internal static bool IsGraphAuthLoaded()
    {
        if (FindType("Microsoft.Graph.PowerShell.Authentication.GraphSession") != null)
            return true;

        try
        {
            using var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);
            ps.AddCommand("Get-Command")
              .AddParameter("Name", "Get-MgContext")
              .AddParameter("ErrorAction", "SilentlyContinue");
            return ps.Invoke().Count > 0;
        }
        catch
        {
            // No runspace (hosted/test process) means no module either.
            return false;
        }
    }

    /// <summary>
    /// The HttpClient the Microsoft.Graph SDK is currently using, or null when it is not
    /// initialized. Used to detect that a borrowed SDK client has been replaced underneath us,
    /// and to borrow from when mgx cannot build a client of its own.
    /// </summary>
    internal static HttpClient? TryGetSessionGraphHttpClient()
    {
        try
        {
            return GenuineSessionClient(TryGetGraphSessionInstance());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// GraphSession's client with any wrapper mgx installed there resolved away.
    /// Enable-MgxResilience leaves a client of ours on that property, and it already carries a
    /// resilience pipeline: borrowing it puts mgx's own pipeline on top of that one, and each
    /// layer retries the one beneath it. The configured retry budget then multiplies on the
    /// wire, telemetry counts each attempt once per layer, and the pacer halves per layer.
    /// <para>
    /// The comparison that decides a borrowed client has gone stale reads through here too. It
    /// is against the client mgx borrowed, so both sides have to be resolved the same way or a
    /// session holding a wrapper reads as a replacement on every invocation.
    /// </para>
    /// </summary>
    private static HttpClient? GenuineSessionClient(object? graphSessionInstance) =>
        graphSessionInstance?.GetType().GetProperty("GraphHttpClient")
            ?.GetValue(graphSessionInstance) is HttpClient client
            ? Cmdlets.Configuration.EnableMgxResilience.ResolveGenuineSdkClient(client)
            : null;

    private static string? GetGraphEndpointFrom(object instance)
    {
        var env = instance.GetType().GetProperty("Environment")?.GetValue(instance);
        return env?.GetType().GetProperty("GraphEndpoint")?.GetValue(env)?.ToString();
    }

    internal static string? GetGraphEndpoint(Action<string>? warn, Action<string>? verbose)
    {
        try
        {
            var instance = TryGetGraphSessionInstance();
            return instance == null ? null : GetGraphEndpointFrom(instance);
        }
        catch (Exception ex)
        {
            warn?.Invoke("Failed to detect Graph endpoint. Falling back to graph.microsoft.com. "
                + "This may be incorrect for sovereign clouds (data sovereignty risk).");
            verbose?.Invoke($"Endpoint detection error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Builds an auth-only HttpClient using MSAL's AuthenticationHandler from the Graph SDK.
    /// Token lifecycle: MSAL's AuthenticationHandler refreshes tokens proactively
    /// (5 min before expiry). For operations spanning 2+ hours, token refresh is
    /// transparent as long as the Connect-MgGraph session remains valid and the
    /// refresh token has not been revoked.
    /// </summary>
    private HttpClient? BuildCleanHttpClient(int totalTimeoutSeconds) =>
        BuildCleanHttpClient(WriteWarning, WriteVerbose, totalTimeoutSeconds);

    private static HttpClient? BuildCleanHttpClient(
        Action<string> warn, Action<string> verbose, int totalTimeoutSeconds)
    {
        try
        {
            var instance = TryGetGraphSessionInstance();
            if (instance == null) return null;

            var authContext = instance.GetType().GetProperty("AuthContext")?.GetValue(instance);
            if (authContext == null) return null;

            // Save AzureADEndpoint before GetAuthenticationProviderAsync. A prior SDK call
            // (Connect-MgGraph or Invoke-MgGraphRequest) may have replaced GraphSession.Environment
            // with a new object that has empty AzureADEndpoint. Restore it before calling MSAL.
            var envObj = instance.GetType().GetProperty("Environment")?.GetValue(instance);
            var savedAadEndpoint = envObj?.GetType().GetProperty("AzureADEndpoint")?.GetValue(envObj)?.ToString();
            if (string.IsNullOrEmpty(savedAadEndpoint))
            {
                // AzureADEndpoint already corrupted (a prior Invoke-MgGraphRequest set it to empty).
                // Try to recover the base AAD host from AuthContext.Authority first.
                // AuthContext.Authority is computed as AzureADEndpoint + "/" + tenantId - so when
                // AzureADEndpoint was already empty at the time of computation, Authority is a
                // relative path like "/tenantId" and cannot be parsed as an absolute URI.
                var aadEndpoint = authContext.GetType().GetProperty("Authority")?.GetValue(authContext)?.ToString();
                var aadProp = envObj?.GetType().GetProperty("AzureADEndpoint");
                if (aadProp?.CanWrite == true)
                {
                    string? baseAuthority = null;
                    if (!string.IsNullOrEmpty(aadEndpoint) &&
                        System.Uri.TryCreate(aadEndpoint, UriKind.Absolute, out var authorityUri) &&
                        authorityUri.Scheme == "https")
                    {
                        // AuthContext.Authority is intact - extract scheme+host as the AAD base.
                        baseAuthority = $"{authorityUri.Scheme}://{authorityUri.Host}";
                    }
                    else
                    {
                        // Both AzureADEndpoint and AuthContext.Authority are corrupted.
                        // Infer the correct AAD base from GraphEndpoint (sovereign cloud mapping),
                        // falling back to global AAD for unknown or missing endpoints.
                        var graphEndpoint = envObj?.GetType().GetProperty("GraphEndpoint")?.GetValue(envObj)?.ToString();
                        baseAuthority = graphEndpoint switch
                        {
                            string e when !string.IsNullOrEmpty(e) && e.Contains("graph.microsoft.us")
                                => "https://login.microsoftonline.us",
                            string e when !string.IsNullOrEmpty(e) && e.Contains("microsoftgraph.chinacloudapi.cn")
                                => "https://login.chinacloudapi.cn",
                            _ => "https://login.microsoftonline.com"
                        };
                    }

                    aadProp.SetValue(envObj, baseAuthority);
                    verbose($"Restored AzureADEndpoint to {baseAuthority} (recovered from: {aadEndpoint ?? "null"})");
                }
            }

            var authHelpersType = FindType(
                "Microsoft.Graph.PowerShell.Authentication.Core.Utilities.AuthenticationHelpers");
            if (authHelpersType == null) return null;

            var getProviderMethod = authHelpersType.GetMethod("GetAuthenticationProviderAsync",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (getProviderMethod == null) return null;

            var taskObj = getProviderMethod.Invoke(null, [authContext])!;
            ((Task)taskObj).GetAwaiter().GetResult();
            var authProvider = taskObj.GetType().GetProperty("Result")!.GetValue(taskObj);
            if (authProvider == null) return null;

            var authHandlerType = FindType(
                "Microsoft.Graph.PowerShell.Authentication.Handlers.AuthenticationHandler");
            if (authHandlerType == null) return null;

            var innerHandler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = TransportDefaults.Decompression,
                PooledConnectionLifetime = TransportDefaults.PooledConnectionLifetime,
                MaxConnectionsPerServer = TransportDefaults.MaxConnectionsPerServer,
                EnableMultipleHttp2Connections = TransportDefaults.EnableMultipleHttp2Connections,
                ConnectTimeout = TransportDefaults.ConnectTimeout
            };

            DelegatingHandler? authHandler;
            try
            {
                authHandler = (DelegatingHandler)Activator.CreateInstance(
                    authHandlerType, authProvider, innerHandler)!;
            }
            catch
            {
                authHandler = (DelegatingHandler)Activator.CreateInstance(
                    authHandlerType, authProvider)!;
                authHandler.InnerHandler = innerHandler;
            }

            return new HttpClient(authHandler)
            {
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                // Set HttpClient timeout as a safety net above Polly's TotalTimeoutSeconds.
                // Polly handles all normal timeout semantics. This outer timeout catches
                // edge cases where a connection bypasses Polly (pool exhaustion, DNS hang,
                // stale TLS). Set above Polly's TotalTimeoutSeconds so Polly fires first;
                // 60s of headroom prevents HttpClient from cancelling before Polly can react.
                Timeout = TimeSpan.FromSeconds(totalTimeoutSeconds + 60)
            };
        }
        catch (Exception ex)
        {
            warn($"Failed to build auth-only HTTP client: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Pre-initializes Mgx's static HTTP client before any SDK probe runs.
    /// Called by Enable-MgxResilience before ForceInitializeAndGetClient as a
    /// performance optimization: builds the clean client while AzureADEndpoint
    /// is still intact, avoiding the save/restore overhead on subsequent calls.
    /// The root cause fix (RestoreAzureADEndpoint in ForceInitializeAndGetClient)
    /// handles the auth poisoning; this method is a belt-and-suspenders optimization.
    /// </summary>
    internal static void TryPreInitHttpClient(Action<string> warn, Action<string> verbose)
    {
        var identity = GetCurrentAuthIdentity(verbose);
        if (string.IsNullOrEmpty(identity.Fingerprint)) return;

        var options = s_clientOptions;

        // Quick early exit on the hot path (idempotent Enable calls).
        // Volatile.Read ensures ARM64 memory visibility - without it, non-volatile statics
        // read outside a lock have no acquire barrier and may return stale values.
        if (Volatile.Read(ref s_graphHttpClient) != null && !ClientIsStale(identity, options)) return;

        lock (s_initLock)
        {
            if (s_graphHttpClient != null && !ClientIsStale(identity, options)) return;

            // Build first. Only dispose/reset after we have a confirmed replacement.
            // If BuildCleanHttpClient fails, the existing s_graphHttpClient must remain valid
            // and callers fall back via GetClient() on first use.
            var client = BuildCleanHttpClient(warn, verbose, options.TotalTimeoutSeconds);
            if (client == null) return; // BuildCleanHttpClient already warned with ex.Message

            ResiliencePipelineFactory.Reset();
            // The old client is let go, not closed - see the note on s_graphHttpClient.
            s_graphHttpClient = client;
            s_ownsHttpClient = true;
            s_cachedAuthFingerprint = identity.Fingerprint;
            s_cachedAuthContextRef = identity.AuthContext is null
                ? null
                : new WeakReference<object>(identity.AuthContext);
            s_cachedTotalTimeoutSeconds = options.TotalTimeoutSeconds;
            s_graphEndpoint = GetGraphEndpoint(warn, verbose) ?? DefaultGraphEndpoint;
        }
    }

    /// <summary>
    /// True when the cached client no longer matches the given identity (by either signal) or
    /// the timeout it was built with. Callers must hold s_initLock, or accept a benign rebuild.
    /// </summary>
    private static bool ClientIsStale(AuthIdentity identity, ResilientGraphClientOptions options) =>
        !string.Equals(s_cachedAuthFingerprint, identity.Fingerprint, StringComparison.Ordinal)
        || AuthContextInstanceChanged(identity.AuthContext)
        || s_cachedTotalTimeoutSeconds != options.TotalTimeoutSeconds;

    private HttpClient? GetSdkHttpClientFallback()
    {
        try
        {
            var instance = TryGetGraphSessionInstance();
            if (instance == null) return null;

            // Resolved, never taken verbatim - see GenuineSessionClient.
            var httpClient = GenuineSessionClient(instance);
            if (httpClient != null) return httpClient;

            // Use detected endpoint instead of hardcoded graph.microsoft.com
            // (supports sovereign clouds: GCC-High, DoD, China)
            var endpoint = GetGraphEndpoint(WriteWarning, WriteVerbose) ?? DefaultGraphEndpoint;

            // Save AzureADEndpoint before probe (same issue as ForceInitializeAndGetClient:
            // Invoke-MgGraphRequest replaces GraphSession.Environment with a new object
            // that has empty AzureADEndpoint, breaking GetAuthenticationProviderAsync).
            var envObj = instance.GetType().GetProperty("Environment")?.GetValue(instance);
            var savedAadEndpoint = envObj?.GetType().GetProperty("AzureADEndpoint")?.GetValue(envObj)?.ToString();

            using var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);
            ps.AddCommand("Invoke-MgGraphRequest");
            ps.AddParameter("Method", "GET");
            ps.AddParameter("Uri", $"{endpoint}/v1.0/organization?$top=1&$select=id");
            ps.AddParameter("ErrorAction", "Stop");
            ps.AddParameter("WarningAction", "SilentlyContinue"); // suppress incidental probe warnings
            ps.AddCommand("Out-Null"); // suppress output from flowing to caller's pipeline
            ps.Invoke();

            // Restore AzureADEndpoint on the NEW Environment object the probe created.
            RestoreAzureADEndpoint(instance, savedAadEndpoint, WriteVerbose);

            return GenuineSessionClient(instance);
        }
        catch (Exception ex)
        {
            WriteWarning($"Failed to get Graph HttpClient: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Restores AzureADEndpoint on the GraphSession Environment if the SDK probe cleared it.
    /// The probe replaces GraphSession.Environment with a new object that has empty AzureADEndpoint,
    /// so we must re-read the property after the probe to patch the new object.
    /// </summary>
    internal static void RestoreAzureADEndpoint(object graphSessionInstance, string? savedAadEndpoint, Action<string>? verbose)
    {
        if (string.IsNullOrEmpty(savedAadEndpoint)) return;

        var envObj = graphSessionInstance.GetType().GetProperty("Environment")?.GetValue(graphSessionInstance);
        var aadProp = envObj?.GetType().GetProperty("AzureADEndpoint");
        if (aadProp?.CanWrite != true) return;

        var current = aadProp.GetValue(envObj)?.ToString();
        if (string.IsNullOrEmpty(current))
        {
            aadProp.SetValue(envObj, savedAadEndpoint);
            verbose?.Invoke($"Restored AzureADEndpoint after SDK fallback probe: {savedAadEndpoint}");
        }
    }

    // Cache for FindType: avoids scanning all loaded assemblies on every call.
    // ConcurrentDictionary is safe for concurrent runspaces.
    // Only non-null results are cached: assemblies load lazily in PowerShell,
    // so a miss now may succeed after the user imports additional modules.
    // Internal on the same terms as the transport seam above: the suite asserts on it.
    internal static readonly ConcurrentDictionary<string, Type> s_typeCache = new();

    // Whether OnAssemblyLoad is subscribed. A process can import the module more than once, and
    // += is not idempotent: a second subscription would outlive the -= that removal does, and
    // clear the cache once per subscription on every load thereafter.
    private static int s_assemblyLoadHooked;

    static MgxCmdletBase()
    {
        AttachAssemblyLoadHandler();
    }

    // A cached Type carries the identity of the assembly that defined it. Re-importing
    // Microsoft.Graph.Authentication - a different version, or into a fresh load context -
    // produces a second GraphSession type whose Instance is a different singleton, so a stale
    // entry would silently point Mgx at a session nobody else is using. Assemblies only ever
    // appear, never disappear, so any load is the signal to re-resolve. Clearing an almost
    // always tiny dictionary is cheaper than validating entries on the hot path.
    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args) => s_typeCache.Clear();

    /// <summary>
    /// Subscribes the assembly-load hook, once. Module import calls this: the static constructor
    /// runs the first time anything touches this type and never again, so after a removal has
    /// detached the hook, nothing would re-arm invalidation for the rest of the process.
    /// </summary>
    internal static void AttachAssemblyLoadHandler()
    {
        if (Interlocked.Exchange(ref s_assemblyLoadHooked, 1) == 1) return;
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
    }

    /// <summary>
    /// Detaches the assembly-load hook. Called on module removal so the handler does not
    /// root this type after the module is gone.
    /// </summary>
    internal static void DetachAssemblyLoadHandler()
    {
        if (Interlocked.Exchange(ref s_assemblyLoadHooked, 0) == 0) return;
        AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
    }

    /// <summary>
    /// Drops every type resolved so far. Module removal does this because the entries carry the
    /// identity of the assemblies that defined them: a Microsoft.Graph.Authentication loading
    /// after the removal would otherwise keep resolving to the first one's GraphSession, whose
    /// Instance is a singleton nobody else is using.
    /// </summary>
    internal static void ClearTypeCache() => s_typeCache.Clear();

    internal static Type? FindType(string fullName)
    {
        if (s_typeCache.TryGetValue(fullName, out var cached))
            return cached;

        var found = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch { return []; }
            })
            .FirstOrDefault(t => t.FullName == fullName);

        if (found != null)
            s_typeCache.TryAdd(fullName, found);

        return found;
    }

    public static void ResetHttpClient()
    {
        lock (s_initLock)
        {
            // The old client is let go, not closed - see the note on s_graphHttpClient.
            s_graphHttpClient = null;
            s_ownsHttpClient = false;
            s_cachedAuthFingerprint = null;
            s_cachedAuthContextRef = null;
            ResiliencePipelineFactory.Reset();
        }
    }

    /// <summary>
    /// Whether the active transport is the mgx-owned clean client (AllowAutoRedirect off)
    /// rather than the borrowed SDK client. The content path requires ownership: the SDK
    /// client ships a RedirectHandler that auto-follows a content 302 to a host mgx never
    /// validated, so Get-MgxContent fails closed when this is false.
    /// </summary>
    protected static bool TransportIsOwned =>
        s_testTransport != null ? s_testTransportOwned : s_ownsHttpClient;

    /// <summary>
    /// Drain buffered verbose messages from the resilience pipeline.
    /// Must be called on the pipeline thread (after .GetAwaiter().GetResult() returns).
    /// OnRetry fires on thread pool threads after Task.Delay, so WriteVerbose cannot
    /// be called directly from OnRetry. Messages are buffered and drained here.
    /// </summary>
    protected void DrainClientMessages()
    {
        _client?.DrainVerboseMessages();
        _client?.DrainWarningMessages();
        _client?.DrainDebugMessages();
    }

    /// <summary>
    /// Whether the caller asked for the HTTP trace, via -Debug on this invocation or a
    /// $DebugPreference that would display the messages anyway.
    /// </summary>
    protected bool IsDebugRequested()
    {
        if (MyInvocation.BoundParameters.TryGetValue("Debug", out var debug)
            && debug is SwitchParameter { IsPresent: true })
        {
            return true;
        }

        return GetVariableValue("DebugPreference") is ActionPreference preference
            && preference != ActionPreference.SilentlyContinue
            && preference != ActionPreference.Ignore;
    }

    internal static void SetClientOptions(ResilientGraphClientOptions options)
    {
        s_clientOptions = options ?? ResilientGraphClientOptions.Default;
    }

    /// <summary>
    /// Puts back the two values a client build derives rather than a caller sets: the endpoint
    /// read off the session it built for, and the TotalTimeoutSeconds the transport was sized to.
    /// Both are per-process, so a removal that leaves them set gives the next import the cloud
    /// the one before it connected to - in the VersionedBaseUrl every cmdlet composes its request
    /// URLs from - until that import's own first build derives one.
    /// <para>
    /// Beside SetClientOptions in ReleaseStaticState rather than inside ResetHttpClient, for the
    /// reason the options release is: a caller that saves and restores these around
    /// ResetHttpClient, as the suite's session scopes do, would have its restore released
    /// instead of the state.
    /// </para>
    /// </summary>
    internal static void ReleaseEndpointAndTimeout()
    {
        lock (s_initLock)
        {
            s_graphEndpoint = DefaultGraphEndpoint;
            s_cachedTotalTimeoutSeconds = 0;
        }
    }

    /// <summary>
    /// Why an exclusive claim on a file did not succeed, where it did not. Asked of a temp a
    /// checkpoint names, and of the output a checkpoint records its items into: both are files
    /// a second run over the same command line can be writing right now, and neither is one
    /// this run may take from under it.
    /// </summary>
    private enum FileClaim
    {
        /// <summary>Nothing else holds it, and this account can open it read-write.</summary>
        Taken,

        /// <summary>
        /// Something has it open. FileShare.None is honored between .NET processes on both
        /// Windows and Unix, so a writer that holds the file makes the claim fail rather than
        /// let a sweep take it out from under them.
        /// </summary>
        Held,

        /// <summary>
        /// It is not a file this run can write at the offset a checkpoint records: mode bits, a
        /// denying ACL, a read-only filesystem, a directory standing where the file should be, a
        /// symlink loop or an over-long path, or a FIFO that opens and then has no length and no
        /// offset to write at. Every failure but the sharing one, in other words, and no second
        /// run is implied by any of them - which is the whole reason this is told apart from the
        /// case above.
        /// </summary>
        Unopenable,

        /// <summary>
        /// There is no such file, or no such directory to hold it. Nothing is wrong and no
        /// second run is implied: it is the case a caller answers by starting over, and it is
        /// told apart from the two above because <see cref="FileMode.Open"/> on a file that is
        /// not there raises the same IOException a sharing violation does - so read as one, a
        /// missing file reported a second run over a file neither run has.
        /// </summary>
        Absent,
    }

    /// <summary>
    /// Ask for the file, and say what happened. The failures are not one answer: a file another
    /// run holds is a file nothing is wrong with, a file this account cannot open read-write has
    /// no other run behind it, and a file that is not there is neither - so a caller that
    /// collapses them tells the reader to go looking for a run that does not exist.
    /// <para>
    /// The claim is not finished at the open on Unix. .NET backs the share mode there with a
    /// non-blocking flock taken AFTER open(2), so the handle binds to whatever inode the name
    /// resolved to and the exclusion over it is granted several syscalls later - and a second
    /// run that renamed its own file onto the name in between leaves this one holding an inode
    /// with no name on it, with nothing about the handle to say so. So the name is asked again
    /// where <see cref="HeldEntry.StillStandsAt"/> says it moved, and a name that keeps moving
    /// is <see cref="FileClaim.Held"/>: another run is mutating it right now, which is what
    /// every caller here reads Held as. Windows answers this question at the open itself and
    /// keeps the answer it had.
    /// </para>
    /// </summary>
    /// <param name="share">
    /// What a second opener may still do. <see cref="FileShare.None"/> for a claim taken and let
    /// go in the same breath; the output hold asks for
    /// <see cref="OutputHoldShare"/> instead, since it keeps the handle for the whole run.
    /// </param>
    /// <param name="stream">
    /// The open file where the answer is <see cref="FileClaim.Taken"/>, and where a claim that
    /// was granted could not then be verified - answered <see cref="FileClaim.Unopenable"/> with
    /// the file still open, since a handle this never hands over is one nothing releases
    /// deterministically: only the finalizer does, whenever the GC gets to it. Null for every
    /// other answer. The caller owns it wherever it comes with one.
    /// </param>
    /// <param name="reason">
    /// Why the file could not be claimed, where the answer is <see cref="FileClaim.Unopenable"/>
    /// - the open's own refusal, or the failure of the question that finishes a claim the open
    /// was granted; null for every other answer. A caller building a stop names this instead of
    /// guessing at a cause, and reads <see cref="UnopenableReason.IsPermission"/> to know
    /// whether granting write access is advice this run can honestly give.
    /// </param>
    /// <param name="mode">
    /// <see cref="FileMode.Open"/> for a claim on a file that has to be there already, which is
    /// every caller that reads <see cref="FileClaim.Absent"/> as an answer.
    /// <see cref="FileMode.OpenOrCreate"/> for a claim whose point is to exist and be refused
    /// even where the file does not: what it makes is reported in <paramref name="created"/>,
    /// and it answers Absent for the four names no create can be made through - a link
    /// pointing at nothing, which is followed rather than replaced; a parent directory that is
    /// not there, which is nothing to make a file in; a path whose parent component is a file
    /// rather than a directory, which is nothing to make one in either and reaches the same
    /// answer by ENOTDIR; and a name whose entry has gone by the time it is opened, where the
    /// attempts left to ask again in have run out or something is standing at the name again by
    /// the time the answer is read.
    /// </param>
    /// <param name="created">
    /// Whether the handle is over a file this open made, because nothing stood at the name. True
    /// wherever the attempt made the file, including a claim that could not be verified - which
    /// is what arms the finally's take-back - and false on every attempt after one the
    /// verification found had moved: a claim asked again over a name that has moved is a claim
    /// on whatever stands there now, which is not this run's to take away.
    /// <para>
    /// The name is read a syscall in front of the open, so what this answers is the state the
    /// open was asked for in. A file a second mgx run put there in between is one that run's own
    /// claim refuses, and no claim of this one's comes away holding it; a file that nothing
    /// holds - a shell's touch, an older mgx, any writer that takes no claim - is opened rather
    /// than made and reported here as made all the same. What that costs is the one thing this
    /// is read for: the empty output a promotion takes back where it stops is, in that window, a
    /// file the run found rather than one it made.
    /// </para>
    /// </param>
    /// <param name="askedToMakeIt">
    /// Whether the attempt this answer came from asked the open to make the file, because
    /// nothing stood at the name and no link stood there either. <paramref name="created"/> says
    /// a file was made; this says one was asked for, which is what tells an open refused over a
    /// file that is there from a create the directory would not take. Decided per attempt like
    /// the create itself, so it describes the look this answer came out of and no earlier one.
    /// </param>
    private static FileClaim OpenExclusively(string path, FileShare share, out FileStream? stream,
        out UnopenableReason? reason, FileMode mode = FileMode.Open) =>
        OpenExclusively(path, share, out stream, out reason, mode, out _, out _);

    /// <inheritdoc cref="OpenExclusively(string,FileShare,out FileStream,out UnopenableReason,FileMode)"/>
    private static FileClaim OpenExclusively(string path, FileShare share, out FileStream? stream,
        out UnopenableReason? reason, FileMode mode, out bool created, out bool askedToMakeIt)
    {
        for (var attempt = 1; ; attempt++)
        {
            // A file is made here only where nothing stands at the name and no link does either:
            // a create follows a link out of the directory and puts this run's file wherever it
            // points, and a name something already stands at is one this claim opens rather than
            // makes. Decided per attempt, since a claim is asked again precisely when the name
            // has moved - and a second attempt that created a file over what a first attempt
            // found would be holding an entry it did not make.
            var make = mode == FileMode.OpenOrCreate && NothingStandsAt(path);
            var claim = AskForTheFile(path, share, make ? FileMode.OpenOrCreate : FileMode.Open,
                out stream, out reason);
            created = make && claim == FileClaim.Taken;
            askedToMakeIt = make;

            // A file that stood at the name a line ago and does not now: opened as one that has
            // to be there, it answers Absent, and a caller that asked for OpenOrCreate is one
            // that reads a name with nothing at it as a file it makes. So the name is asked
            // again, where it is free to be made now. A link pointing at nothing is not that -
            // it stands at the name, a create through it goes elsewhere, and Absent is what its
            // caller already reads.
            if (claim == FileClaim.Absent && mode == FileMode.OpenOrCreate
                && attempt < HeldEntry.ClaimAttempts && NothingStandsAt(path))
            {
                continue;
            }

            if (claim != FileClaim.Taken) return claim;

            bool standing;
            try
            {
                standing = HeldEntry.StillStandsAt(stream!, path);
            }
            catch (Exception ex)
            {
                // The exclusion is granted and the question that finishes it cannot be
                // answered: a parent that stopped answering for the name, a share that went, a
                // call that failed for a reason this cannot read. A claim that cannot be
                // verified is not a claim, so it is refused - and refused as an answer rather
                // than as a throw, with the handle and what the attempt made handed back
                // exactly as a Taken answer hands them over. Every caller releases what it was
                // given on its own path, and already reads Unopenable as a file it does not
                // take; one of them releases in a finally, and takes the empty output it made
                // back under that same claim. Emptying the out parameters here disarms that
                // finally, which then holds nothing and unlinks nothing - and a file with no
                // rows in it that no run holds is left standing at the caller's -OutputFile.
                //
                // The reason is the failure's own sentence, which names the call and the errno
                // and says the claim on the file could not be finished. It is not a permission
                // on the file: the search bit that went is the parent's, and a stale handle or
                // a filesystem error is nobody's grant to give, so advice to open up the file
                // would send the reader at the wrong thing.
                reason = new UnopenableReason(FirstLine(ex.Message).TrimEnd('.'),
                    IsPermission: false);
                return FileClaim.Unopenable;
            }

            if (standing) return claim;

            // Granted over a file that has left the name. Dropped rather than reported, and the
            // name asked again from the open: the run that moved it either holds what stands
            // there now, which the next claim is refused by, or has let go, which the next claim
            // takes honestly.
            stream!.Dispose();
            stream = null;
            created = false;
            if (attempt == HeldEntry.ClaimAttempts) return FileClaim.Held;
        }
    }

    /// <summary>
    /// Nothing stands at the name, and no link stands there either - the one state a claim may
    /// make its own file in. Read off the directory entry rather than through an open, which is
    /// how a dangling link is told from an absent name at all: both answer that nothing is
    /// there, and only one of them takes a create somewhere else.
    /// </summary>
    private static bool NothingStandsAt(string path) =>
        !Path.Exists(path) && new FileInfo(path).LinkTarget == null;

    /// <summary>
    /// The open itself, and which of the four answers it gave. Every caller reaches it through
    /// <see cref="OpenExclusively"/>, which is where a claim is finished.
    /// </summary>
    private static FileClaim AskForTheFile(string path, FileShare share, FileMode mode,
        out FileStream? stream, out UnopenableReason? reason)
    {
        stream = null;
        reason = null;
        try
        {
            stream = new FileStream(path, mode, FileAccess.ReadWrite, share);
            if (!stream.CanSeek)
            {
                // Something opened, and it is not a file with a length and an offset in it. A
                // FIFO standing at the output path opens read-write without blocking, and every
                // question asked of the handle afterwards - how long is it, cut it back to the
                // recorded length, append at the end - is one a stream that cannot seek answers
                // with a NotSupportedException, which is neither of the two failures a caller
                // here knows how to report and went past both guards with the handle still open.
                // So it is decided at the open: nothing this run can resume into, and let go
                // before a caller is told to wait for it. Nothing here throws, so the reason is
                // this method's own words rather than an exception's - a FIFO is not a
                // permission, and granting write access to one would not give it a length.
                stream.Dispose();
                stream = null;
                reason = new UnopenableReason("the file has no offset to write at",
                    IsPermission: false);
                return FileClaim.Unopenable;
            }
            return FileClaim.Taken;
        }
        catch (FileNotFoundException)
        {
            return FileClaim.Absent;
        }
        catch (DirectoryNotFoundException)
        {
            // The same answer one level up: ENOENT on a path component. Both are IOExceptions,
            // and both have to be caught above the sharing case below rather than reasoned about
            // from an HRESULT afterwards.
            return FileClaim.Absent;
        }
        catch (UnauthorizedAccessException ex)
        {
            reason = ReasonForUnopenable(path, ex);
            return FileClaim.Unopenable;
        }
        catch (IOException ex)
        {
            // The sharing violation is named, and every other way an open can fail is a file
            // this run cannot open. Decided the other way round - anything not on a list of
            // access errnos is another run - a symlink loop and an over-long path at the output
            // path both stopped the run with "Another export is still writing", sending the
            // reader to wait for a second export over a path no run of any kind could open.
            if (IsSharingViolation(ex)) return FileClaim.Held;
            reason = ReasonForUnopenable(path, ex);
            return FileClaim.Unopenable;
        }
    }

    /// <summary>
    /// Why an open of <paramref name="path"/> failed, refined once for every site that has to
    /// say it. The claim in <see cref="OpenExclusively"/> is one; the create a promotion stages
    /// its copy with is the other, and it built its own words out of the raw exception - so one
    /// disk state was two sentences, and a directory at the staging name read "Access to the
    /// path '...' is denied" in the applying pass and "a directory stands at the path" in the
    /// preview of the same run.
    /// <para>
    /// <see cref="UnopenableReason.Reason"/> carries the runtime's own first line with its
    /// sentence-ending period taken off, since every caller quotes it inside a sentence it
    /// punctuates itself. The two causes told apart from the rest are the two with a different
    /// way out than granting write access on the file: a directory standing at the path, which
    /// no grant makes writable at an offset, and a parent directory this account cannot search,
    /// where the grant belongs to the directory and not to the file the sentence names.
    /// </para>
    /// <para>
    /// A directory is asked of the filesystem rather than of the errno, since it reaches an
    /// access failure as EISDIR or as EACCES depending on the mode on it and both say the same
    /// thing about the path - and, from a create, as neither: a create refuses an existing name
    /// with "already exists" whatever stands there, so a directory that appears at the staging
    /// name between the sweep and the create is asked about on that route too, and the two
    /// instructions call one disk state by one name. An unsearchable parent is read off the
    /// entry the open failed on,
    /// by <see cref="TheParentRefusesToBeSearched"/>: a parent that is not there reaches
    /// <see cref="DirectoryNotFoundException"/> above and never gets here, so what is left to
    /// tell apart is a directory this account may not look inside from a file inside one it may.
    /// </para>
    /// </summary>
    private static UnopenableReason ReasonForUnopenable(string path, Exception ex,
        bool fromCreate = false)
    {
        // Only an access failure can be one of the two named causes. Every other way an open
        // fails - a symlink loop, an over-long path, a socket, a full volume - is the runtime's
        // words and nothing this run adds to them.
        if (ex is not UnauthorizedAccessException)
        {
            // Except the one a create has of its own: it refuses a name that already exists
            // with "The file '...' already exists", whatever kind of entry stands there, so a
            // directory appearing at the staging name between the sweep and the create read as
            // a file in the way while the sweep, one instruction earlier, called the same disk
            // state a directory. Asked of the filesystem, the way the access failure below asks
            // it, so one entry reads one way wherever the run met it.
            if (fromCreate && Directory.Exists(path))
                return new UnopenableReason("a directory stands at the path", IsPermission: false,
                    IsDirectory: true);
            return new UnopenableReason(FirstLine(ex.Message).TrimEnd('.'), IsPermission: false);
        }

        if (Directory.Exists(path))
            return new UnopenableReason("a directory stands at the path", IsPermission: false,
                IsDirectory: true);

        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent)
            && TheParentRefusesToBeSearched(path))
            return new UnopenableReason($"the directory '{parent}' cannot be searched",
                IsPermission: true, IsUnsearchableParent: true);

        return new UnopenableReason(FirstLine(ex.Message).TrimEnd('.'), IsPermission: true);
    }

    /// <summary>
    /// Whether the entry at <paramref name="path"/> cannot even be asked about, which is what a
    /// directory missing its search bit refuses - and the one thing that separates a file this
    /// account may not write from a file it cannot reach at all. Asked of the entry rather than
    /// of the directory, because a directory answers for itself either way: mode 000 or not, a
    /// stat of the directory needs the search bit on ITS parent and not on itself, so
    /// <see cref="Directory.Exists(string)"/> is true of a directory nothing can be looked up
    /// inside. What the missing bit refuses is every name under it, present or absent alike.
    /// <para>
    /// Told from a name that is simply not there, which is what a create is about to make: a
    /// searchable directory answers an absent name with ENOENT, and only an unsearchable one
    /// answers a permission. Listing is not the question either - a directory with the search
    /// bit and not the read bit refuses to be listed and resolves every name in it - so nothing
    /// here enumerates.
    /// </para>
    /// </summary>
    private static bool TheParentRefusesToBeSearched(string path)
    {
        try { File.GetAttributes(path); }
        catch (UnauthorizedAccessException) { return true; }
        catch (Exception ex) when (ex is IOException or ArgumentException
                                      or NotSupportedException) { }
        return false;
    }

    /// <summary>
    /// The exception's own explanation, first line only. A caller quotes the runtime's words for
    /// why an open failed, and quoting more than the first line risks folding a second sentence,
    /// or a path repeated on its own line, into what reads as one clause.
    /// </summary>
    private static string FirstLine(string message)
    {
        var newline = message.IndexOfAny(['\r', '\n']);
        return newline < 0 ? message : message[..newline];
    }

    /// <inheritdoc cref="OpenExclusively"/>
    /// <summary>
    /// Ask for the file exclusively and let it go again, which is all a caller wants that only
    /// has to report on it. The reason a failed claim is unopenable is not asked for here: every
    /// caller of this overload already has its own words for the one failure it distinguishes
    /// from a hold, or does not report a cause at all.
    /// </summary>
    private static FileClaim ClaimExclusively(string path, out UnopenableReason? reason)
    {
        var claim = OpenExclusively(path, FileShare.None, out var stream, out reason);
        stream?.Dispose();
        return claim;
    }

    /// <summary>
    /// Whether an IOException from opening a file is the sharing violation the share mode exists
    /// to produce, rather than one of the other ways an open fails. The engine reads it for the
    /// scratch names it sweeps, and one implementation is what keeps the claim here and that
    /// sweep from disagreeing about which errno means a second run.
    /// </summary>
    /// <inheritdoc cref="ScratchName.IsSharingViolation" path="/summary/para"/>
    private static bool IsSharingViolation(IOException ex) =>
        ScratchName.IsSharingViolation(ex);

    /// <summary>
    /// True when nothing else holds the file and this account can open it read-write. The sweep
    /// and the adoption search ask only this much: a file they cannot claim is a file they pass
    /// over, whichever of the two reasons it is.
    /// </summary>
    private static bool CanTakeExclusively(string path) =>
        ClaimExclusively(path, out _) == FileClaim.Taken;

    /// <summary>
    /// What each directory answered when it was asked whether it keeps two spellings of a name
    /// apart. Keyed by full path: a volume does not change its mind, so one answer per directory
    /// per process is all of it, and two callers racing for the same key cost a second look and
    /// nothing else. An answer read off the directory's own entries is kept here like a probed
    /// one; the platform rule a caller falls back to where it may not write is not, so the next
    /// caller that may write still asks the volume.
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> s_caseSensitiveDirectories =
        new(StringComparer.Ordinal);

    /// <summary>The answer where the directory cannot be asked: the rule the platform applies by default.</summary>
    protected static bool PlatformCaseRule => OperatingSystem.IsLinux();

    /// <summary>
    /// Whether the filesystem under <paramref name="directory"/> keeps "Users.jsonl" and
    /// "users.jsonl" apart. Asked of the directory rather than of the operating system, because
    /// the operating system does not answer for it: an APFS volume can be created
    /// case-sensitive, a Windows directory can be flagged case-sensitive on its own, and where
    /// the guess went the wrong way the temp helpers matched names the filesystem does not - a
    /// sweep reaching another export's live temp, and two distinct outputs comparing as one file.
    ///
    /// It is answered without touching the directory wherever it can be: an entry already there,
    /// looked for under its own spelling inverted, settles it - present under the other spelling
    /// and the volume folds case, absent and it keeps the two apart. Only a directory with no
    /// such entry needs a file written, and <paramref name="mayWrite"/> says whether this caller
    /// may write one. Everything reached above a ShouldProcess gate passes false: -WhatIf
    /// reports what a run would do, and creating and removing a file to find out moves the
    /// caller's directory's write time. A caller that may not write, in a directory that cannot
    /// answer for itself, gets the platform rule - which is what every release before the probe
    /// used everywhere - and that answer is not remembered, so the run below still asks the
    /// volume. The two therefore differ only in a directory holding nothing, on a volume whose
    /// rule is not its platform's default.
    ///
    /// A directory this run cannot write to answers with the platform rule as well: the probe is
    /// a better answer where it can be taken, not a precondition, and a run that cannot write
    /// its own output directory has its own error to raise rather than this one.
    /// </summary>
    internal static bool DirectoryNamesAreCaseSensitive(string? directory, bool mayWrite)
    {
        if (string.IsNullOrEmpty(directory)) return PlatformCaseRule;
        string key;
        try { key = Path.GetFullPath(directory); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                       or PathTooLongException or IOException)
        {
            return PlatformCaseRule;
        }

        if (s_caseSensitiveDirectories.TryGetValue(key, out var settled)) return settled;

        // Before this module writes a probe here, take back the one an earlier run left. Only
        // on the write path: a caller that may not write deletes nothing either.
        if (mayWrite) ReclaimCaseProbeLitter(key);

        if (ReadOnlyCaseAnswer(key) is { } observed)
            return s_caseSensitiveDirectories.GetOrAdd(key, observed);

        if (!mayWrite) return PlatformCaseRule;

        return s_caseSensitiveDirectories.GetOrAdd(key, ProbeCaseSensitivity);
    }

    /// <summary>
    /// Whether answering this directory's case rule would still take a write. False once the
    /// answer is known, and false where an entry already in the directory gives it - which this
    /// keeps, since having asked, it costs nothing more. True only for a directory that exists
    /// and has nothing in it to read the answer off, which is the one place a preview and the
    /// run it describes can disagree, and the only place worth a line saying so. A directory
    /// that is not there is answered no: neither side of a gate can ask one, so they agree.
    /// </summary>
    protected static bool DirectoryCaseRuleNeedsProbe(string? directory)
    {
        if (string.IsNullOrEmpty(directory)) return false;
        string key;
        try { key = Path.GetFullPath(directory); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                       or PathTooLongException or IOException)
        {
            return false;
        }
        if (s_caseSensitiveDirectories.ContainsKey(key)) return false;
        if (!Directory.Exists(key)) return false;
        if (ReadOnlyCaseAnswer(key) is not { } observed) return true;
        s_caseSensitiveDirectories.GetOrAdd(key, observed);
        return false;
    }

    /// <summary>
    /// The answer read off the directory's own entries, or null where they do not give one. An
    /// entry that is there under one spelling is there under the inverted spelling too exactly
    /// when the volume folds case, so asking for the inverted name settles it with nothing
    /// written.
    ///
    /// Three kinds of candidate are passed over rather than answered from. A name with no ASCII
    /// letter inverts to itself, and a name carrying anything outside ASCII inverts through case
    /// mappings the filesystem need not share - a sharp s and a dotted capital I are not their
    /// own round trip - so a folding volume could be read as a splitting one. A directory that
    /// already holds BOTH spellings says nothing either: that is what a case-sensitive volume
    /// looks like, not a fold. Neither does an entry unlinked between the listing and the
    /// lookup, which is a file that is simply gone rather than a name the volume refused, so the
    /// entry is looked for again before its absence is read as an answer. The probe files below
    /// are skipped for the same reason: one of them may be another run's, about to vanish.
    /// </summary>
    private static bool? ReadOnlyCaseAnswer(string directory)
    {
        try
        {
            var names = Directory.GetFileSystemEntries(directory)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .ToList();
            var present = new HashSet<string>(names, StringComparer.Ordinal);
            foreach (var name in names)
            {
                if (IsCaseProbeName(name)) continue;
                if (InvertCase(name) is not { } inverted) continue;
                if (present.Contains(inverted)) continue;
                var other = Path.Combine(directory, inverted);
                if (File.Exists(other) || Directory.Exists(other)) return false;
                var mine = Path.Combine(directory, name);
                if (File.Exists(mine) || Directory.Exists(mine)) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>
    /// The name with every ASCII letter's case turned over, or null where that is not a
    /// different name this filesystem has to have an opinion about: a name with no ASCII letter
    /// in it, or one carrying anything outside ASCII.
    /// </summary>
    private static string? InvertCase(string name)
    {
        var chars = name.ToCharArray();
        var sawLetter = false;
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c > 0x7F) return null;
            if (c is >= 'a' and <= 'z') { chars[i] = (char)(c - 32); sawLetter = true; }
            else if (c is >= 'A' and <= 'Z') { chars[i] = (char)(c + 32); sawLetter = true; }
        }
        return sawLetter ? new string(chars) : null;
    }

    /// <summary>
    /// True for a name the probe below gives its own file: ".mgx-case-", 32 lowercase hex
    /// digits, and nothing else. Nothing but this writes one, which is what makes deleting a
    /// leftover safe - and holding the exact shape is what keeps that promise about a directory
    /// the caller also keeps their own ".mgx-case-notes" in.
    /// </summary>
    private static bool IsCaseProbeName(string candidate)
    {
        const string prefix = ".mgx-case-";
        if (candidate.Length != prefix.Length + 32) return false;
        if (!candidate.StartsWith(prefix, StringComparison.Ordinal)) return false;
        for (var i = prefix.Length; i < candidate.Length; i++)
        {
            if (candidate[i] is (>= '0' and <= '9') or (>= 'a' and <= 'f')) continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Delete the probe files an earlier run left in this directory. DeleteOnClose is the
    /// kernel's promise on Windows and is kept however the process ends; on Unix the unlink
    /// happens where the stream is disposed, so a process killed outright leaves its probe
    /// behind, and nothing on disk refers to it again. One is reclaimed the way the stale-temp
    /// sweep reclaims a temp - claimed exclusively first - because a probe another run is taking
    /// right now is open, and unlinking it under them would answer their question with the
    /// wrong half of it.
    /// </summary>
    private static void ReclaimCaseProbeLitter(string directory)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFiles(directory, ".mgx-case-*").ToList())
            {
                if (!IsCaseProbeName(Path.GetFileName(entry))) continue;
                if (!CanTakeExclusively(entry)) continue;
                try { File.Delete(entry); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// One probe file, created under a name of its own so nothing else can be reading it, and
    /// deleted by hand as well as by the handle. The name is not one a run gives its own temp,
    /// so neither the sweep nor adoption can reach it even in the moment it exists.
    ///
    /// DeleteOnClose is not the same promise on both platforms. Windows unlinks at the last
    /// handle close, which the kernel performs however the process ends; Unix unlinks where the
    /// stream is disposed, so a process killed outright leaves the file where it is. That is
    /// what the reclaim above collects, and it runs before this writes anything.
    /// </summary>
    private static bool ProbeCaseSensitivity(string directory)
    {
        var name = $".mgx-case-{Guid.NewGuid():N}";
        var probe = Path.Combine(directory, name);
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       bufferSize: 1, FileOptions.DeleteOnClose))
            {
                // It exists under the name it was created with. Whether it also exists under
                // the other casing is the whole question.
                return !File.Exists(Path.Combine(directory, name.ToUpperInvariant()));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PlatformCaseRule;
        }
        finally
        {
            try { File.Delete(probe); } catch { }
        }
    }

    /// <summary>
    /// Test seam: pins the answer for one directory, so the case-sensitive decision path runs on
    /// a host whose own filesystem is not. Null forgets it and the next question probes again.
    /// Internal, assigned directly rather than reflected; every shipping path leaves it alone.
    /// </summary>
    internal static void SetDirectoryCaseSensitivity(string directory, bool? caseSensitive)
    {
        var key = Path.GetFullPath(directory);
        if (caseSensitive is { } value) s_caseSensitiveDirectories[key] = value;
        else s_caseSensitiveDirectories.TryRemove(key, out _);
    }

    /// <summary>
    /// How two file names in one directory are compared. Directory.EnumerateFiles matches its
    /// pattern the way the filesystem does - so where "Users.jsonl" answers to "users.jsonl" the
    /// glob returns it - and a predicate filtering those results with an ordinal comparison
    /// disagreed with the enumeration that produced them: an export whose -OutputFile differed
    /// only in case abandoned its own temp and re-enumerated in silence. Both sides are keyed
    /// from the one answer above, so they cannot disagree again.
    /// </summary>
    private static StringComparison FileNameComparisonIn(string? directory, bool mayWrite) =>
        DirectoryNamesAreCaseSensitive(directory, mayWrite)
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// How the "{output}.*.tmp" glob is matched in one directory. The two-argument
    /// EnumerateFiles takes its casing from the platform, which is the guess the comparison
    /// above no longer makes; the rest of these are that overload's own defaults - Win32
    /// globbing, nothing skipped by attribute, an inaccessible entry raised rather than
    /// swallowed - spelled out so that naming the casing does not quietly change them too.
    /// </summary>
    private static EnumerationOptions TempGlobOptions(string directory, bool mayWrite) => new()
    {
        MatchCasing = DirectoryNamesAreCaseSensitive(directory, mayWrite)
            ? MatchCasing.CaseSensitive
            : MatchCasing.CaseInsensitive,
        MatchType = MatchType.Win32,
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
    };

    /// <summary>
    /// Whether two paths name the same file, normalized and compared the way the filesystem
    /// would answer. A checkpoint records the output it was collecting into so a run can tell
    /// its own from someone else's, and a leaf name cannot carry that: "a/users.jsonl" and
    /// "b/users.jsonl" are one name and two files. False for anything that is not a path -
    /// a checkpoint is untrusted input once it is on disk, and an unusable name is not this
    /// run's output.
    /// </summary>
    protected static bool SamePath(string? left, string? right, bool mayWrite)
    {
        if (left == null || right == null) return false;
        try
        {
            var recorded = Path.GetFullPath(left);
            var mine = Path.GetFullPath(right);
            // Keyed on the second path, which is this run's own output at every call site, and
            // so names the directory whose rule decides whether the other spelling is that file.
            return string.Equals(recorded, mine,
                FileNameComparisonIn(Path.GetDirectoryName(mine), mayWrite));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                       or PathTooLongException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// What a URL addresses, independently of where Graph is reached: the path from its API
    /// version onward, plus the query. Everything in front of the version is the endpoint, and
    /// a gateway prefix, a trailing slash on the configured endpoint and a sovereign cloud all
    /// change that without changing which resource is named - so comparing whole URLs called a
    /// checkpoint another run's for reasons that have nothing to do with what it enumerated.
    /// Null when the argument is not a URL.
    /// </summary>
    protected static string? ResourceIdentity(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return null;
        var segments = parsed.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var version = Array.FindIndex(segments, seg =>
            string.Equals(seg, "v1.0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(seg, "beta", StringComparison.OrdinalIgnoreCase));
        // No version segment to anchor on: nothing can be told apart from the endpoint, so the
        // whole path stands, which can only refuse where it would have refused before.
        var resource = version < 0 ? segments : segments[version..];
        return "/" + string.Join('/', resource) + parsed.Query;
    }

    /// <summary>
    /// Whether two <see cref="ResourceIdentity"/> values name the same enumeration. The path is
    /// compared without case: Graph answers "/users" and "/Users" from one collection, so an
    /// ordinal comparison turned a -Uri typed differently between two runs into a different
    /// export, and the resume was refused and the collection enumerated again. The query is
    /// compared as written - a $filter value is the service's to interpret, and two spellings of
    /// one are not demonstrably the same enumeration. A null identity is one that could not be
    /// read as a URL, and matches nothing.
    /// </summary>
    protected static bool SameResourceIdentity(string? recorded, string? current)
    {
        if (recorded == null || current == null) return false;
        var r = recorded.IndexOf('?');
        var c = current.IndexOf('?');
        return string.Equals(r < 0 ? recorded : recorded[..r],
                   c < 0 ? current : current[..c], StringComparison.OrdinalIgnoreCase)
            && string.Equals(r < 0 ? string.Empty : recorded[r..],
                   c < 0 ? string.Empty : current[c..], StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the checkpoint records anything for <see cref="RecordedOutputMatches"/> to weigh:
    /// a path to compare, or a length to hold a file against. A checkpoint recording neither has
    /// nothing there to be weighed, and asking anyway answers no for want of a measurement - so
    /// the shape every release before 2.1 wrote was read as another run's, its temp swept by the
    /// sweep a refusal holds off over the temp it names, and the position counting those items
    /// left standing to refuse the next -Latest. That shape is decided on the evidence it does
    /// have - a temp carrying this output's own name, beside an output that is not there - by
    /// the reconcile, which refuses it wherever an output exists. Both cmdlets ask this one
    /// question in front of the predicate, so one checkpoint reads one way in either.
    /// </summary>
    protected static bool RecordsAFileToWeigh(PaginationCheckpoint checkpoint) =>
        checkpoint.OutputFile != null || checkpoint.DataLength != null;

    /// <summary>
    /// Whether the file a checkpoint counted its items into is one this run collects into. A
    /// checkpoint that records the output answers directly. One written before that field
    /// existed records only where the bytes are - a temp of the output, or the output itself -
    /// and reading that silence as "mine" is what let an older release's checkpoint drive a
    /// recovery against a file it had never been measured against. It is believed only where
    /// the file it points at corroborates it: a named temp has to be one this output's own name
    /// produces and hold at least the bytes counted, and with no temp named the output itself
    /// has to be there and be at least that long. Nothing to corroborate is not evidence, and a
    /// recorded length below zero is not a measurement.
    /// </summary>
    protected static bool RecordedOutputMatches(
        string? recordedOutput, string? recordedTemp, long? dataLength, string outputPath,
        bool mayWrite)
    {
        if (recordedOutput != null) return SamePath(recordedOutput, outputPath, mayWrite);
        if (dataLength is not { } length || length < 0) return false;
        if (recordedTemp != null)
            return ResolveNamedTemp(outputPath, recordedTemp, length, mayWrite) != null;
        try
        {
            var info = new FileInfo(outputPath);
            return info.Exists && info.Length >= length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Remove leftover "{outputPath}.{guid}.tmp" files, and the copy a promotion stages at
    /// "{outputPath}.adopt". Called only when no resume is pending, where every such file is an
    /// orphan by definition - except one a live run still holds, which the claim keeps out of
    /// reach. "Such a file" is decided by name and not by the glob alone: the glob reaches a
    /// longer output's temps too, and those describe work this run knows nothing about. The name
    /// has to be one a run gives its own temp, 32 hex digits and all: anything looser is a
    /// promise that no file mgx did not write is deleted, made about a directory the caller also
    /// keeps their own "users.jsonl.backup.tmp" in. The staging name is exact and reached by
    /// name, since no glob of this shape covers it.
    /// </summary>
    protected void DeleteStaleTemps(string outputPath)
    {
        // "Orphan" is an assumption about a file this run did not create, and a second export
        // running against the same output right now owns a file matching the same glob. Windows
        // refuses to delete a file someone holds open, so it declined by accident; Unix does
        // not, and the other run went on writing into an unlinked inode and lost everything it
        // had fetched. Ask for the file exclusively first - if that fails, someone is using it
        // and it is not an orphan.
        //
        // And which of the two failures it was. A file another run holds is a file nothing is
        // wrong with; a file this account cannot open read-write has no second run behind it at
        // all, and one sentence for both sent the caller looking for a run that does not exist.
        //
        // Which of the second kind it was is the open's own answer and not this line's to guess:
        // "the open was refused on permissions, not on sharing" was said of a directory, of a
        // pipe with no length to write at and of a path too long for the volume, and a caller
        // who went and granted write access to the file found nothing had changed.
        void SweepIfUnheld(string stale, string deleted)
        {
            var claim = ClaimExclusively(stale, out var reason);
            // A file that has gone between the directory listing and the claim is a file this
            // sweep has nothing left to do about, and nothing to say either.
            if (claim == FileClaim.Absent) return;
            if (claim != FileClaim.Taken)
            {
                WriteVerbose(claim == FileClaim.Held
                    ? $"Left '{Path.GetFileName(stale)}' alone: another run is writing to it."
                    : $"Left '{Path.GetFileName(stale)}' alone: this run cannot open it for "
                      + $"writing - {reason?.Reason ?? "the open was refused"}.");
                return;
            }
            try
            {
                File.Delete(stale);
                WriteVerbose($"{deleted}: {Path.GetFileName(stale)}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                WriteWarning($"Could not delete orphaned temp file '{stale}': {ex.Message}. Delete it manually.");
            }
        }

        try
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            var outputName = Path.GetFileName(outputPath);
            foreach (var stale in Directory.EnumerateFiles(dir, outputName + ".*.tmp",
                             TempGlobOptions(dir, mayWrite: true))
                         .Where(p => IsRunTempName(dir, outputName, Path.GetFileName(p),
                                        mayWrite: true))
                         .ToList())
            {
                SweepIfUnheld(stale,
                    "Deleted an orphaned temp file from an earlier interrupted run");
            }

            // And the copy a promotion stages beside the output, which the glob above cannot
            // reach: no run leaves a file under that name on purpose - it is renamed away where
            // the promotion lands and removed where it stops - so one standing here is a run
            // killed between those two, holding a partial copy of somebody's rows that nothing
            // on disk refers to.
            //
            // Through the promotion's own sweep of that name, and not the claim-then-delete a
            // temp takes. The claim answers for a regular file and for nothing else: a link at
            // the name was followed to whatever it pointed at, a pipe and a socket came back
            // unopenable and were left standing - reported as a file this account could not
            // open - and the next promotion then met exactly the entry that sweep exists to
            // clear. One primitive decides the name wherever a run reaches it.
            var adoptPath = StagingPathFor(outputPath);
            if (SweepStagingName(adoptPath, apply: true, out var adoptFailure, out var adoptSwept))
            {
                if (adoptSwept is { } removed) WriteVerbose(removed.Sentence);
            }
            else
            {
                WriteVerbose($"Left '{Path.GetFileName(adoptPath)}' alone: "
                    + $"{adoptFailure!.Value.Reason}.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; a sweep failure must never stop the run.
        }
    }

    /// <summary>
    /// The temp a checkpoint names, or null when it cannot be used. A checkpoint is untrusted
    /// input once it is on disk, so the recorded name must be one a run could actually have
    /// written - "{output}.{32-hex}.tmp" - and must not be the output itself. Anything else
    /// (the checkpoint file, the delta state, a crafted path) would be copied into the output
    /// as data and then deleted as the spent temp. The file must also be at least as long as
    /// the checkpoint promised, since a shorter one means the items it counted are not all
    /// there.
    /// </summary>
    private static string? ResolveNamedTemp(string outputPath, string tempFileName, long dataLength,
        bool mayWrite)
    {
        if (dataLength <= 0) return null;
        var dir = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        if (!string.Equals(tempFileName, Path.GetFileName(tempFileName), StringComparison.Ordinal))
            return null;
        if (!IsRunTempName(dir, Path.GetFileName(outputPath), tempFileName, mayWrite)) return null;
        var tempPath = Path.Combine(dir, tempFileName);
        if (string.Equals(Path.GetFullPath(tempPath), Path.GetFullPath(outputPath),
                StringComparison.OrdinalIgnoreCase))
            return null;
        if (!File.Exists(tempPath)) return null;
        if (new FileInfo(tempPath).Length < dataLength) return null;
        return tempPath;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is a name a fresh run gives its temp:
    /// the output's own name, a dot, 32 lowercase hex digits (Guid "N"), and ".tmp". Both names
    /// live in <paramref name="directory"/>, which is what decides whether a spelling that
    /// differs only in case is the same name.
    /// </summary>
    protected static bool IsRunTempName(string directory, string outputFileName, string candidate,
        bool mayWrite)
    {
        var comparison = FileNameComparisonIn(directory, mayWrite);
        var prefix = outputFileName + ".";
        const string suffix = ".tmp";
        if (candidate.Length != prefix.Length + 32 + suffix.Length) return false;
        if (!candidate.StartsWith(prefix, comparison)) return false;
        if (!candidate.EndsWith(suffix, comparison)) return false;
        for (var i = prefix.Length; i < prefix.Length + 32; i++)
        {
            if (candidate[i] is (>= '0' and <= '9') or (>= 'a' and <= 'f')) continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Whether the file's last byte ends a line. Every whole row a run writes goes out through
    /// WriteLine, which terminates it, so a file that does not end in a newline ends in a row
    /// that was cut short - and only the last row can be, since every row before it is followed
    /// by its own terminator.
    /// </summary>
    private static bool EndsWithNewline(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        if (fs.Length == 0) return false;
        fs.Seek(-1, SeekOrigin.End);
        return fs.ReadByte() == '\n';
    }

    /// <summary>What promoting the temp a checkpoint names answered.</summary>
    protected enum TempPromotion
    {
        /// <summary>
        /// Nothing was promoted: the temp is absent, shorter than the checkpoint promised, or
        /// still held by the run writing it. The items it counted are in no file this run can
        /// reach, and the caller must not resume past them.
        /// </summary>
        NotPromoted,

        /// <summary>
        /// The items are in the output now, and the output is this run's until the writing ends.
        /// </summary>
        Taken,

        /// <summary>
        /// Another run has the output open. Nothing was promoted into it - the claim is asked
        /// for before the move - so the temp, and the checkpoint naming it, are as they were
        /// found.
        /// </summary>
        OutputHeld,

        /// <summary>
        /// The output is not a file this run can write at the recorded offset, and nothing was
        /// promoted into it either.
        /// </summary>
        OutputUnopenable,

        /// <summary>
        /// The copy a promotion stages beside the output could not be written, or could not be
        /// moved onto the output once it was: a directory or another run's handle at the staging
        /// name, a full disk, a directory that turned read-only between the claim and the copy.
        /// Nothing was replaced and nothing was removed - the temp holds every item the
        /// checkpoint counts, and the checkpoint still names it - so this is not the temp that
        /// went missing, which is the one answer a caller may delete a checkpoint over.
        /// </summary>
        StagingFailed,
    }

    /// <summary>
    /// Replace the output with the first <paramref name="dataLength"/> bytes of a named temp,
    /// take the output for the rest of the run, and then remove the temp. A fresh run that
    /// finishes moves its temp over whatever the output held, so recovering an unfinished one
    /// has to reach that same file; appending instead would leave the previous run's rows -
    /// already consumed - in front of this one's. Unlike the glob-and-newest form, this takes
    /// exactly the file the checkpoint recorded, so a leftover from an unrelated run cannot be
    /// merged in. Bytes rather than lines: the length was taken from the writer's own position,
    /// so it cannot disagree with itself about line endings or a torn final line.
    /// <para>
    /// What follows a promotion is an append onto the file it landed in, which is the same thing
    /// the resumed route does and takes the same hold. The output is asked for BEFORE the move
    /// that replaces it, since a rename asks nothing of the lock on either side;
    /// <see cref="MoveOntoTheOutputAndKeepIt"/> is what each platform's lock lets that be.
    /// </para>
    /// </summary>
    protected TempPromotion TryPromoteNamedTemp(string outputPath, string tempFileName,
        long dataLength, out UnopenableReason? reason, out StagingFailure? staging,
        out StagingRemoval? swept)
    {
        reason = null;
        staging = null;
        swept = null;
        try
        {
            var tempPath = ResolveNamedTemp(outputPath, tempFileName, dataLength, mayWrite: true);
            if (tempPath == null) return TempPromotion.NotPromoted;

            // The claim adoption and the stale-temp sweep take, for the reason they take it:
            // this copies the file's rows into the output and then unlinks it, so a run that is
            // still writing that temp went on writing into an unlinked inode, lost everything it
            // had fetched, and had its rows handed over as this run's. Naming the file changes
            // nothing about that - a checkpoint names the temp of the enumeration that wrote it,
            // and that enumeration is either over, in which case nothing holds the file, or
            // running right now, in which case this is its live output. Taken before anything
            // reads the file, so both platforms decide it here rather than Windows failing the
            // unlink afterwards with the rows already copied.
            //
            // And held from here through the move, rather than taken and let go before the copy.
            // Two runs over one command line reach this line together, and a claim released
            // before the copy is one the second run passes as well: both staged the same bytes,
            // one of them lost the staged file's own claim, and the run that lost it read the
            // temp as missing, deleted the position counting its items, and exported fresh over
            // the output the other was holding and appending to. One claim over the whole
            // promotion refuses the second run for as long as the first is promoting - and by
            // the time it is let go the temp is gone and the output is this run's, which is what
            // refuses it from there on.
            FileStream? claimedTemp = null;
            FileStream? standing = null;
            FileStream? writer = null;
            var adoptPath = StagingPathFor(outputPath);
            var staged = false;
            var createdOutput = false;
            var landed = TempPromotion.NotPromoted;
            try
            {
                // The staging name, cleared before either claim below is taken, and the reason
                // nothing is ever opened through that name with a mode that follows it or waits
                // on it. First, because the sweep asks for what stands at the name exclusively
                // and a hard link there is an ALIAS for an inode this run is about to hold: run
                // it under those claims and a link to the temp, or to the output, answered its
                // own claim Held - the run stopped saying another run had the staged copy open,
                // over a name nothing but itself was holding, while the preview holding neither
                // deleted the link and answered that the promotion would land. Nothing of this
                // promotion's is open here, so an alias for either file answers Taken and is
                // unlinked as the alias it is, leaving what it pointed at alone.
                //
                // A failure here is its own answer, and not the temp's. Read as "the temp is
                // missing or incomplete" - which is what the catch around this whole method used
                // to make of a directory at the staging name, another run's handle on it or a
                // full disk - the caller said the items were in no file at all, deleted the
                // position counting them and swept the temp that was holding every one of them.
                if (!SweepStagingName(adoptPath, apply: true, out staging, out swept))
                    return landed = TempPromotion.StagingFailed;

                if (OpenExclusively(tempPath, FileShare.None, out claimedTemp, out _)
                    != FileClaim.Taken)
                    return TempPromotion.NotPromoted;

                // The output the move will replace, asked for before a byte of it is staged.
                // Held or unopenable is the whole answer on either platform, and it is reached
                // with the directory beside the output untouched: staging first and asking after
                // wrote the whole temp out under a name of this run's - 172 ms and a moved
                // directory mtime for a 256 MiB temp - to be taken straight back again.
                //
                // On Unix the handle is kept from here through the move, which is what closes
                // the interval a claim released in front of the rename left open: rename(2)
                // honors no lock on either side of it, so a second run that took the output in
                // between had its rows replaced under it, in an unnamed inode, with no warning
                // on any stream. Windows cannot keep it - it refuses to rename over a
                // destination another handle has open - so there the claim is let go where it
                // is taken and asked for again past the move.
                //
                // An output that is not there yet is created here in order to be held, and
                // removed again by the finally on every answer but Taken: a promotion that
                // stops leaves the directory as it found it, and one that lands replaced the
                // file with the staged copy in the rename itself.
                landed = ClaimStandingOutput(outputPath, out standing, out reason,
                    out createdOutput);
                if (landed != TempPromotion.Taken) return landed;

                // Staged like the other forms, so the destination is replaced in one Move rather
                // than truncated and refilled in place. Under the name the sweep above cleared,
                // and created by an open that refuses to use anything already standing there:
                // what appears at the name in the instants between the two is this run's staging
                // failure and never its copy's destination.
                try
                {
                    // CreateNew, so the name is this run's own new entry or the open fails:
                    // nothing at it is followed, truncated or waited on. ReadWrite, because this
                    // handle is the hold the run keeps past the rename. FileShare.None from the
                    // instant the entry exists, so the copy is never a file a second promotion
                    // can claim, and the discard below can only ever unlink a file this handle
                    // holds.
                    writer = ScratchName.CreateNew(adoptPath);
                    staged = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Something appeared at the name in the instants since the sweep, or the
                    // directory beside the output refuses a new file in it at all - a mode this
                    // account may not write, a volume with no room. Neither is a temp that has
                    // gone missing.
                    staging = StagingFailureAt(adoptPath, ex, fromCreate: true);
                    return landed = TempPromotion.StagingFailed;
                }
                try
                {
                    var buffer = new byte[81920];
                    long remaining = dataLength;
                    while (remaining > 0)
                    {
                        var read = claimedTemp!.Read(buffer, 0,
                            (int)Math.Min(buffer.Length, remaining));
                        if (read <= 0) break;
                        writer.Write(buffer, 0, read);
                        remaining -= read;
                    }
                    writer.Flush();
                    // Shorter than the length recorded for it, which is the one thing the
                    // resolve above measured and cannot have measured wrong twice: the temp has
                    // lost bytes since. Those items are not all in a file this run can reach,
                    // which is the caller's own route for a temp that is missing or short - the
                    // only route left to it, now that a staging failure answers for itself.
                    if (remaining > 0) return landed = TempPromotion.NotPromoted;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    staging = StagingFailureAt(adoptPath, ex);
                    return landed = TempPromotion.StagingFailed;
                }

                landed = MoveOntoTheOutputAndKeepIt(writer, adoptPath, outputPath, dataLength,
                    out reason, out staging);
                // The staged copy's handle is the run's hold on the output past a move that went
                // through, and on Windows it was closed before the move. Either way it is no
                // longer this frame's to close.
                if (landed == TempPromotion.Taken) writer = null;
                if (landed != TempPromotion.Taken) return landed;

                // Unlinked after the output was taken, and only where it was taken. A promotion
                // that does not land leaves the temp, and the checkpoint still naming it, exactly
                // as they were found: the position and the file it counts are the pair they were,
                // and the run that comes back promotes the same bytes to the same length over the
                // same output, which is the one answer here that costs nothing.
                //
                // The unlink is the finally below, under the claim this run has held the temp by
                // since before it read a byte of it - on Windows past that handle instead, since
                // a file this process has open cannot be unlinked there without
                // FILE_SHARE_DELETE. Nothing is left open for a second run to pass by then: the
                // output is this run's, and that is the claim that stops it.
                return TempPromotion.Taken;
            }
            finally
            {
                // The empty output the standing claim made where there was none. It exists only
                // so that an absent output can be refused to a second run, and a promotion that
                // did not land has nothing to leave at the path: the run before it left nothing
                // there, and a run that comes back to this checkpoint reaches the same branch.
                // Taken back under the claim that made it, in the one order this platform takes
                // a name back in - a name goes only while the run unlinking it holds what
                // stands there, and a file that is no longer this claim's is one it leaves.
                //
                // Every other answer lets the handle go and unlinks nothing: past the move what
                // it locks is an inode with no name left on it, and the file at the path is the
                // one the adopt handle holds; on a stop it is let go with nothing replaced.
                if (createdOutput && landed != TempPromotion.Taken)
                    ScratchName.Discard(outputPath, standing);
                else standing?.Dispose();

                // The temp, taken back under the claim it was read through, and only where the
                // output was taken: a promotion that did not land leaves it and the checkpoint
                // naming it exactly as it found them, which is what lets the next run promote
                // the same bytes to the same length over the same output.
                if (landed == TempPromotion.Taken) ScratchName.Discard(tempPath, claimedTemp);
                else claimedTemp?.Dispose();

                // Every answer but Taken leaves the disk as it was found, so the copy staged
                // beside the output goes with the promotion that did not land: no glob of a
                // later run reaches that name, and no run puts it there to keep. Guarded on
                // this run having created it, which is now the only way the name can hold a
                // file at all - the create refuses anything already there, and the handle it
                // comes away with is held until the move - so what this unlinks is always the
                // entry this run made and never a staging file a second promotion has open.
                if (landed != TempPromotion.Taken && staged) DiscardStagedFile(adoptPath, writer);
                else writer?.Dispose();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TempPromotion.NotPromoted;
        }
    }

    /// <summary>
    /// The name a promotion stages its copy under: the output's own name with a suffix, in the
    /// output's own directory, so the move that puts it at the output path is a rename within one
    /// filesystem. No run leaves a file under this name on purpose - it is renamed away where the
    /// promotion lands and removed where it stops - so one standing there is a run killed between
    /// the two, and <see cref="DeleteStaleTemps"/> takes it with the temps.
    /// </summary>
    protected static string StagingPathFor(string outputPath) => outputPath + ".adopt";

    /// <summary>
    /// Clear the staging name, or say why the promotion stops there: <see cref="ScratchName"/>
    /// decides the entry and this puts its answer into the words a promotion says it in. The
    /// kinds and the order they are decided in are that primitive's - the same one the
    /// checkpoint and the delta state clear their own scratch names with - and the reason
    /// nothing is ever opened through this name with a mode that follows it or waits on it: a
    /// promotion that opened it Create/Write staged its copy into whatever stood there, taking
    /// a symlink's target away with it and renaming the link onto the output path, and a FIFO
    /// with no reader blocked the open forever with this run holding the temp and the output
    /// and no way for StopProcessing to reach it.
    /// <para>
    /// Run first in both passes - before the claim on the temp and the claim on the standing
    /// output, and so before anything of this promotion's is open. That place in the order is
    /// what lets the sweep's own claim answer at all: run under the promotion's claims, it
    /// asked for an entry that may be a hard link to one of the two inodes those claims hold,
    /// and a second open file description over a locked inode is refused even from this process
    /// - so an alias for the temp, or for the output, read as a staged copy another run had
    /// open.
    /// </para>
    /// </summary>
    /// <param name="apply">
    /// Whether the name is cleared or only read. The applying pass takes what stands there off
    /// the name; the preview leaves it and reports what a run would take, because a preview
    /// removes nothing - its deletions were as real as a run's, so a caller asking what would
    /// be touched had a file of theirs deleted to be told that nothing would be.
    /// </param>
    /// <param name="failure">Why the promotion stops here, where it does; null where the name is
    /// clear. One wording for one disk state on either pass: a run and a preview of it refuse a
    /// directory, or another run's copy, in the same words.</param>
    /// <param name="found">What was taken off the name, or - on the preview - what stands there
    /// and would be; null where nothing did. The sentence is the pass's own and the kind is the
    /// same phrase in both, so a stop that names it and a warning that reports it agree.</param>
    private static bool SweepStagingName(string adoptPath, bool apply,
        out StagingFailure? failure, out StagingRemoval? found)
    {
        failure = null;
        found = null;
        var name = Path.GetFileName(adoptPath);
        var entry = apply
            ? ScratchName.Sweep(adoptPath, out var stood)
            : ScratchName.Inspect(adoptPath, out stood);
        switch (entry)
        {
            case ScratchEntry.Clear:
                return true;
            case ScratchEntry.Directory:
                failure = new StagingFailure(adoptPath, "a directory stands at the path");
                return false;
            case ScratchEntry.Held:
                failure = new StagingFailure(adoptPath, "another run has the staged copy open");
                return false;
            case ScratchEntry.Unreadable:
                // The entry could not be read, or could not be unlinked. Refined the way every
                // other failed open here is refined, so one disk state is one sentence wherever
                // the run met it.
                failure = StagingFailureAt(adoptPath, stood!);
                return false;
            default:
                var kind = StagingEntryKind(entry);
                found = new StagingRemoval(
                    apply ? SweptFromStagingName(name, kind) : WouldSweepStagingName(name, kind),
                    kind);
                return true;
        }
    }

    /// <summary>
    /// What stood at the staging name, in the phrase every sentence about it uses: the warning a
    /// run writes when it took the entry off the name, the -WhatIf line naming what a run would
    /// take off it, and the stop that has to say the name was cleared before the staging failed.
    /// One phrase per kind, because the same entry read three ways in three sentences is three
    /// disk states as far as the reader can tell.
    /// </summary>
    private static string StagingEntryKind(ScratchEntry entry) => entry switch
    {
        ScratchEntry.Link => "a link, and not what it pointed at",
        ScratchEntry.Leftover => "a copy left by an interrupted promotion",
        // A regular file, and the one thing that is known about it is that this account could
        // not open it. Read into the sentence below, a 0444 or a mode-000 file was reported as a
        // pipe or a socket - a kind of entry it is not, and one the caller cannot have put there
        // by the means they actually used.
        ScratchEntry.Unopenable => "a copy this account cannot open",
        _ => "an entry no copy could be staged in, such as a pipe or a socket",
    };

    /// <summary>
    /// What a preview says about an entry at the staging name: what the run would take off it
    /// and what it would put there instead. Said as a WARNING beside the gate's own line, since
    /// the gate names one of two actions on -OutputFile and this is neither - and the create
    /// test is skipped for that name, because nothing standing at it can be tested without
    /// removing it and this pass removes nothing.
    /// </summary>
    private static string WouldSweepStagingName(string name, string kind) =>
        $"The run would remove what stands at the staging name '{name}' ({kind}) and stage its "
        + "copy there.";

    /// <summary>
    /// What a sweep of the staging name took off it, or what a preview found standing there: the
    /// sentence the pass reports and the phrase another sentence names it by.
    /// </summary>
    protected readonly record struct StagingRemoval(string Sentence, string Kind);

    /// <summary>
    /// What came off the staging name, in the one sentence both cmdlets say it in. Said as a
    /// WARNING, since written to the verbose stream it reached nobody who had not asked for
    /// verbose - and said once the run's own outcome for the recovery is known, because a
    /// warning is a terminating error under -WarningAction Stop: written where the deletion
    /// happens, it ended the run with the entry gone, the temp still holding the items and
    /// nothing recovered. The verbose line stays at the deletion as the record of when it
    /// happened, so -WarningAction SilentlyContinue does not hide it entirely.
    /// </summary>
    private static string SweptFromStagingName(string name, string kind) =>
        $"Removed what stood at the staging name '{name}': {kind}.";

    /// <summary>
    /// Why a promotion could not put its copy at the output path, and where it was writing it.
    /// The reason is <see cref="ReasonForUnopenable"/>'s, which is the same refinement the claim
    /// on a file carries out of a failed open: a caller quotes it inside a sentence it
    /// punctuates itself, and the two passes over one disk state have to answer in one wording.
    /// </summary>
    protected readonly record struct StagingFailure(string Path, string Reason);

    /// <inheritdoc cref="StagingFailure"/>
    /// <param name="fromCreate">Whether the open that failed was the create the copy is staged
    /// with, whose refusal of an existing name is the runtime's "already exists" for every kind
    /// of entry alike - so the refinement has to ask the filesystem what the entry is.</param>
    private static StagingFailure StagingFailureAt(string adoptPath, Exception ex,
        bool fromCreate = false) =>
        new(adoptPath, ReasonForUnopenable(adoptPath, ex, fromCreate).Reason);

    /// <summary>
    /// Put the staged file at the output path and come away holding what is now there, or replace
    /// nothing and say why. A promotion is followed by an append onto the file it landed in, so
    /// that file has to be this run's from the move onward - and the output standing at the path
    /// has to be nobody else's before the move, because a rename asks nothing of the lock on
    /// either side of it.
    /// <para>
    /// On Unix .NET backs <see cref="FileShare.None"/> with an advisory flock, which belongs to
    /// the open file description, and <c>rename(2)</c> moves a directory entry: so the lock the
    /// staged copy was created under is still held over the same inode once that inode is the
    /// output. The hold therefore travels with the move, and it is the handle the copy was
    /// written through - the one the create came away with, never let go and never asked for a
    /// second time. Measured on this host: a FileShare.None open of the moved-to path, from this
    /// process and from another, is refused while that handle is open, which is also why the
    /// output is not asked for again afterwards - flock refuses a second open file description
    /// from this very process.
    /// </para>
    /// <para>
    /// The claim on the output the move replaces is <see cref="ClaimStandingOutput"/>'s, taken by
    /// the caller before it staged anything and still standing here. Asking for it and letting it
    /// go in front of the rename, which is what this did, left the interval between the two
    /// instructions open: rename(2) does not honor the lock on the destination either, so a
    /// second run that took the output in that interval had it replaced under it - its rows in an
    /// unlinked inode, and no warning on any stream. Kept across the move instead, the claim
    /// refuses that run for as long as the promotion lasts, and past the move the handle this run
    /// has open IS the output. That holds on both branches of the claim: an output that is not
    /// there is created by it in order to be held, so a promotion over an absent output refuses
    /// a second one the same way a promotion over an existing output does.
    /// </para>
    /// <para>
    /// Windows will not rename over a destination another handle has open, nor rename a source
    /// this process has open, so neither hold can travel with the move there. The order this had
    /// stands: claim the standing output and let it go, stage, close the copy, move, take the
    /// output on the far side. What that leaves is a window two statements wide rather than a
    /// page fetch wide, and an output already replaced where the claim on the far side fails.
    /// </para>
    /// </summary>
    private TempPromotion MoveOntoTheOutputAndKeepIt(FileStream staged, string adoptPath,
        string outputPath, long dataLength, out UnopenableReason? reason,
        out StagingFailure? staging)
    {
        reason = null;
        staging = null;
        if (OperatingSystem.IsWindows())
        {
            // Closed before the move, which is the order that platform allows: it will not
            // rename a source this process has open. What that leaves is a name unheld for the
            // width of the two instructions, where every other platform holds it from the
            // create through the rename.
            staged.Dispose();
            if (!TryMoveStagedFile(adoptPath, outputPath, out staging))
                return TempPromotion.StagingFailed;
            return TakeOutputForCheckpoint(outputPath, dataLength, out reason) switch
            {
                CheckpointOutput.Taken => TempPromotion.Taken,
                CheckpointOutput.HeldByAnotherRun => TempPromotion.OutputHeld,
                CheckpointOutput.CannotBeOpened => TempPromotion.OutputUnopenable,
                // The file this run wrote a moment ago is gone, or shorter than it wrote it.
                // Something else has replaced it, so those rows are in no file again and the
                // caller's own route for a promotion that did not land is the answer.
                _ => TempPromotion.NotPromoted,
            };
        }

        // The handle the copy was written through, still open, so the lock travels with the
        // inode through the rename. Asking for the name again here - which is what this did -
        // let go of it between the write and the claim: a second promotion took the copy in
        // that interval and had it unlinked under its handle by the run that wrote it. There is
        // no interval now, and no second claim to be refused: the name has been this run's
        // since the create made it.
        if (!TryMoveStagedFile(adoptPath, outputPath, out staging))
            return TempPromotion.StagingFailed;

        // The handle names the output now, and the run keeps it until its writing ends. Its
        // length is the length just written, which is what the caller repoints the checkpoint
        // at; nothing reads the path it was opened under, which is a name no longer on disk.
        ReleaseOutputHold();
        _outputHold = new OutputHold(staged);
        return TempPromotion.Taken;
    }

    /// <summary>
    /// The rename itself, and whether it went through. Everything up to it leaves the disk as it
    /// was found, so a move that fails has replaced nothing either - and saying so is the answer,
    /// where the catch around the whole promotion would have called it a temp that had gone.
    /// </summary>
    private static bool TryMoveStagedFile(string adoptPath, string outputPath,
        out StagingFailure? staging)
    {
        staging = null;
        try
        {
            File.Move(adoptPath, outputPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            staging = StagingFailureAt(adoptPath, ex);
            return false;
        }
    }

    /// <summary>
    /// Claim the output a promotion is about to replace and, on Unix, come away holding it. A
    /// rename asks nothing of the lock on either side of it, so the interval between letting this
    /// claim go and the move is one a second run can take the output in - and it is measured in
    /// instructions only until something widens it. The handle is therefore kept from before the
    /// staging through the rename, and let go on the far side, where what it locks is an inode
    /// with no name left on it.
    /// <para>
    /// Windows keeps nothing: it refuses to rename over a destination another handle has open, so
    /// a claim held here would fail the move itself. There the claim is let go where it is taken
    /// and the output is asked for again past the move, which is the order that platform's lock
    /// allows.
    /// </para>
    /// <para>
    /// The file's length is no part of the question, which is what tells this from
    /// <see cref="WouldTakeOutputForCheckpoint"/>, whose open this otherwise is. No caller of
    /// this one can read anything from a length. A promotion replaces the output whatever length
    /// it has. And a checkpoint naming a temp that has gone says its items were in that temp, so
    /// an output nothing holds is evidence of nothing at all: two rows of one shape are two rows
    /// of another shape's length, so a previous run's file measures exactly what a promotion
    /// would have left about as often as a promoted one does.
    /// </para>
    /// <para>
    /// FileMode.OpenOrCreate, so the claim is taken on the one branch it used to have nothing to
    /// hold. An output that is not there is the state a fresh run's promotion is the ordinary end
    /// of, and FileMode.Open answered that Absent with no handle: nothing was held through the
    /// staging or the rename, so two runs with different temps over one output path both got
    /// past here, both renamed a copy onto that path, and the first one's rows ended in an
    /// unlinked inode with its own handle appending into nothing on any disk. The claim makes
    /// the file in order to hold it, which refuses the second run exactly as an existing output
    /// would - and <paramref name="created"/> comes back, so a promotion that does not land can
    /// take away again the empty file it made.
    /// </para>
    /// </summary>
    /// <param name="created">
    /// Whether the file at the path is this claim's own, made by the open because nothing stood
    /// there. The claim answers it, since OpenOrCreate never reports an absent file and the
    /// claim is the one that knows which of its attempts came away with the handle: a claim
    /// asked again over a name that moved is a claim on a file it did not make, and a file a
    /// second mgx run put there between the question and the open is one that run's own claim
    /// refuses. What is not told apart is a file nothing holds that appeared in that window - a
    /// shell's touch, an older mgx, any writer that takes no claim - which this claim opens,
    /// reports here as its own, and takes back again where the promotion stops.
    /// </param>
    /// <param name="standing">The claim, still open, for the caller to let go past the move;
    /// null on Windows and where the open itself was refused. Open where the claim was granted
    /// and could not be verified, for the finally to take back what the attempt made. Where
    /// <paramref name="created"/> is true this is a handle on an empty file of this run's
    /// making.</param>
    private static TempPromotion ClaimStandingOutput(string outputPath, out FileStream? standing,
        out UnopenableReason? reason, out bool created)
    {
        if (OperatingSystem.IsWindows())
        {
            // Nothing is made here, because nothing is kept: this platform lets the claim go
            // where it takes it, so a file created to hold would be released in the same breath
            // and refuse no second run - an entry at the caller's output path for no gain. The
            // window past the move is that platform's, as it was.
            created = false;
            var released = OpenExclusively(outputPath, OutputHoldShare, out standing, out reason);
            standing?.Dispose();
            standing = null;
            return PromotionForStandingClaim(released);
        }

        // OpenOrCreate, and the claim makes the file only where nothing stands at the name and
        // no link stands there either: a link is followed by a create, so a dangling one would
        // put this run's empty file wherever it points - outside the output's own directory -
        // and the move that follows replaces the link itself, which leaves that file behind.
        // That state is read off the directory entry, without opening it, and read again by
        // every attempt the claim makes.
        var claim = OpenExclusively(outputPath, OutputHoldShare, out standing, out reason,
            FileMode.OpenOrCreate, out created, out var askedToMakeIt);

        // The output is not there and could not be made, so there is nothing held and nothing
        // made to take back - and nothing about a second run to report either. What refused it
        // is the directory beside it, and what a run does about a directory it cannot put a file
        // in is stop on the staging create, in that same directory: the one question both passes
        // ask, and the one wording they both answer in. So the answer is left to it rather than
        // named here in words the preview has no way to reach.
        //
        // Read off the attempt that gave the answer, and not off a second look from here. The
        // claim decides per attempt whether it is making the file: a name a first look found a
        // file at is a create the directory refused, once that file leaves the name in between,
        // and a name a create was refused at can have a file standing at it a line later. A
        // look from here reads each of those as the other.
        //
        // And on the create having been refused, which is what leaves nothing held: a create the
        // claim was granted and then could not verify is Unopenable off the same attempt, with
        // the file made and this run holding it. Left to the staging create, that one would go on
        // to rename a copy onto an output no run here can say is still the one it claimed.
        if (claim == FileClaim.Unopenable && askedToMakeIt && standing is null)
        {
            reason = null;
            return TempPromotion.Taken;
        }

        return PromotionForStandingClaim(claim);
    }

    /// <inheritdoc cref="ClaimStandingOutput"/>
    /// <summary>
    /// The same question, let go again with nothing taken, nothing cut and nothing made, for the
    /// callers that only have to report on the file. FileMode.Open here and not the claim's
    /// OpenOrCreate: a pass that holds nothing has no second run to refuse, and an output that is
    /// not there is one the promotion creates - which is the answer this gives it, and it gives
    /// it without leaving a file of its own at the caller's output path. Whether that directory
    /// will take a new file at all is a different question, and the staging create asks it in
    /// the same directory on both passes.
    /// </summary>
    private static TempPromotion StandingOutputRefusal(string outputPath,
        out UnopenableReason? reason)
    {
        var claim = OpenExclusively(outputPath, OutputHoldShare, out var standing, out reason);
        standing?.Dispose();
        return PromotionForStandingClaim(claim);
    }

    /// <summary>
    /// What a claim on the output a promotion would replace means for the promotion. Absent is
    /// not among the answers, and for two different reasons: the claim creates the file rather
    /// than report it missing, and the report-only form reads a missing output as one the
    /// promotion would make.
    /// </summary>
    private static TempPromotion PromotionForStandingClaim(FileClaim claim) => claim switch
    {
        FileClaim.Held => TempPromotion.OutputHeld,
        FileClaim.Unopenable => TempPromotion.OutputUnopenable,
        _ => TempPromotion.Taken,
    };

    /// <inheritdoc cref="ClaimStandingOutput"/>
    /// <summary>The same answer in the words the two cmdlets build a stop out of.</summary>
    protected static ClaimRefusal RefusalForStandingOutput(string outputPath,
        out UnopenableReason? reason) =>
        StandingOutputRefusal(outputPath, out reason) switch
        {
            TempPromotion.OutputHeld => ClaimRefusal.AnotherRunHasIt,
            TempPromotion.OutputUnopenable => ClaimRefusal.CannotBeOpened,
            _ => ClaimRefusal.None,
        };

    /// <summary>
    /// Undo the staging. A promotion that stops before its move replaced nothing, and the file it
    /// had written beside the output goes with it: no glob of a later run reaches that name, and
    /// no run put it there to keep.
    /// <para>
    /// Unlinked under the handle that holds it, which is what closes the interval between the
    /// two: released first, the name was decided and unheld for as long as the discard took, and
    /// a second run that swept it in that interval created its own file at the name and had it
    /// unlinked from under the handle it was holding it by. Windows keeps dispose-then-delete,
    /// since a file this process has open cannot be unlinked there without FILE_SHARE_DELETE.
    /// </para>
    /// </summary>
    /// <param name="held">This run's own handle on the entry, disposed here; null where it has
    /// none.</param>
    private static void DiscardStagedFile(string adoptPath, FileStream? held) =>
        ScratchName.Discard(adoptPath, held);

    /// <summary>
    /// Point the checkpoint at the output this run now holds, and save it there. The items are in
    /// that file, so a second interruption must not promote the same temp again - and the file it
    /// would promote is already gone.
    /// <para>
    /// The length is the held stream's, read off the handle this run has open rather than stat'd
    /// off the name a second time. They are the same number where nothing has moved; where
    /// something has, the handle is the file this run wrote and the name is whatever stands there
    /// now.
    /// </para>
    /// </summary>
    protected void RepointCheckpointAtHeldOutput(PaginationCheckpoint checkpoint,
        string checkpointPath, string outputPath, OutputHold hold)
    {
        try
        {
            checkpoint.TempFile = null;
            checkpoint.OutputFile = Path.GetFullPath(outputPath);
            checkpoint.DataLength = hold.Length;
            checkpoint.Save(checkpointPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WriteWarning($"Checkpoint save failed after recovery: {ex.Message}");
        }
    }

    /// <summary>
    /// What a second opener of the output may still do while a resumed run holds it, which is
    /// not the same question on the two platforms because the lock is not the same lock.
    /// <para>
    /// .NET on Unix backs FileShare with an advisory <c>flock</c>: <see cref="FileShare.None"/>
    /// takes LOCK_EX, every other mode takes LOCK_SH, and LOCK_SH does not refuse a second
    /// writer - measured on this host, where a second FileStream opened Append/Write/Read
    /// against a LOCK_SH holder succeeds. LOCK_EX is therefore the only mode that makes a second
    /// run's claim fail, and it refuses every .NET opener including a reader; a reader that does
    /// not take a lock at all, such as tail or cat, reads the file as before.
    /// </para>
    /// <para>
    /// Windows needs no such trade. The handle is opened for writing, so a second writer is
    /// refused by its access rather than by its sharing, and <see cref="FileShare.Read"/> is
    /// what lets a reader keep looking at the file for the length of the run.
    /// </para>
    /// </summary>
    private static FileShare OutputHoldShare =>
        OperatingSystem.IsWindows() ? FileShare.Read : FileShare.None;

    /// <summary>
    /// The output of a resumed run, open, cut back to the length its checkpoint records, and
    /// this run's until the writing ends. The claim on that file and the handle the writing goes
    /// through are one handle: taken and let go a page-fetch apart, they left a window in which
    /// a second run over the same command line passed the same claim, cut the same file back and
    /// appended to it - nine lines where seven belonged, and in a sync a delta token advanced
    /// past the difference.
    /// </summary>
    protected sealed class OutputHold : IDisposable
    {
        private readonly FileStream _stream;

        internal OutputHold(FileStream stream) => _stream = stream;

        /// <summary>The file's length, read off the open handle rather than the directory entry.</summary>
        internal long Length => _stream.Length;

        /// <summary>
        /// Cut the file back to the length the checkpoint recorded, dropping whatever the
        /// interrupted run wrote after its last save. Those items are re-fetched, so dropping
        /// them is what keeps a resume from writing them twice.
        /// </summary>
        internal void TrimTo(long dataLength) => _stream.SetLength(dataLength);

        /// <summary>
        /// A writer onto the end of the same handle. Never a second open of the output: on Unix
        /// the hold's own LOCK_EX refuses it - flock belongs to the open file description, so
        /// even this process is refused - and on Windows the hold's write access does.
        /// </summary>
        internal StreamWriter AppendingWriter()
        {
            _stream.Position = _stream.Length;
            // What StreamWriter(path, append) would have used: UTF-8 with no byte-order mark,
            // throwing on unpaired surrogates, over a 1 KiB buffer. leaveOpen keeps the hold
            // standing when the writer closes - the run has a checkpoint to delete and a
            // summary to write before the file is anyone else's.
            return new StreamWriter(_stream, new UTF8Encoding(false, true), 1024, leaveOpen: true);
        }

        public void Dispose() => _stream.Dispose();
    }

    /// <summary>
    /// The hold this run has on the output its own checkpoint records items into, from the
    /// reconcile that took it until the writing ends. Null on every other run: a fresh export
    /// collects into a temp of its own and promotes it, and a run with nothing to resume has no
    /// output to keep.
    /// </summary>
    private OutputHold? _outputHold;

    /// <summary>The hold, where this run has one.</summary>
    protected OutputHold? HeldOutput => _outputHold;

    /// <summary>
    /// Let the output go. Called where the writing ends, on every way out of the attempt loop,
    /// and again from <see cref="MgxCmdletCore.Dispose"/> by way of
    /// <see cref="DisposeCore"/> - so a run that ends between the reconcile and the loop, on a
    /// missing Graph connection or a pipeline stop, releases it too. Idempotent.
    /// </summary>
    protected void ReleaseOutputHold()
    {
        var hold = Interlocked.Exchange(ref _outputHold, null);
        hold?.Dispose();
    }

    /// <summary>
    /// Releases the output hold when the enclosing scope ends, whichever way it ends: the
    /// summary object, a reported error, a cancellation, or an exception on its way out of the
    /// attempt loop. Declared with <c>using</c> above the loop rather than written as a finally
    /// around it, which is the same guarantee without moving five hundred lines sideways.
    /// </summary>
    protected readonly struct OutputHoldScope(MgxCmdletBase cmdlet) : IDisposable
    {
        public void Dispose() => cmdlet.ReleaseOutputHold();
    }

    /// <inheritdoc cref="OutputHoldScope"/>
    protected OutputHoldScope ReleasingOutputWhenWritingEnds() => new(this);

    /// <summary>
    /// What this run may do with the output its own checkpoint records its items into: take it,
    /// stop, or start over. One open answers both halves of the question - whether the file is
    /// this run's to write and whether it is the file the checkpoint describes - because they
    /// are one file and one handle. Asking them separately, with a claim let go in between,
    /// answered a directory entry twice and a file's contents never: <c>File.Exists</c> is false
    /// for a file in a directory this account cannot search, so a whole unreadable output was
    /// reported as no longer holding the items it held and the position counting them deleted.
    /// </summary>
    protected enum CheckpointOutput
    {
        /// <summary>
        /// It is the file the checkpoint describes, it now ends exactly at the recorded length,
        /// and this run holds it for the rest of the writing.
        /// </summary>
        Taken,

        /// <summary>Another run has it open.</summary>
        HeldByAnotherRun,

        /// <summary>
        /// It is not a file this run can write at the recorded offset - mode bits, a denying
        /// ACL, a read-only filesystem, a directory standing at the path, a symlink loop, or a
        /// FIFO with no offset to write at. Nothing here knows whether the items are in it, so
        /// the caller stops rather than report a file it never read as a file that has lost its
        /// contents.
        /// </summary>
        CannotBeOpened,

        /// <summary>
        /// It is not the file the checkpoint describes: absent, shorter than the recorded
        /// length, or recorded under a length that is not one. The items counted are in no file,
        /// which is what the callers delete the checkpoint over and enumerate again from the
        /// beginning.
        /// </summary>
        NotTheRecordedFile,
    }

    /// <summary>
    /// Take the output for the rest of this run and cut it back to the length its checkpoint
    /// records. The handle is kept in <see cref="HeldOutput"/> where the answer is
    /// <see cref="CheckpointOutput.Taken"/>, and released by
    /// <see cref="ReleaseOutputHold"/> where the run stops appending to it.
    /// </summary>
    protected CheckpointOutput TakeOutputForCheckpoint(string outputPath, long dataLength,
        out UnopenableReason? reason)
    {
        // A checkpoint is untrusted input once it is on disk, and SetLength rejects a negative
        // length with an ArgumentOutOfRangeException the catch below does not cover - so a
        // hand-edited length escaped as a terminating error naming neither the checkpoint nor
        // the file, and left itself on disk to fail the same way next run. ResolveNamedTemp
        // already guards its own length; this is the same guard, and it is above the open
        // because a length that is not one describes no file worth opening.
        reason = null;
        if (dataLength < 0) return CheckpointOutput.NotTheRecordedFile;

        var claim = OpenExclusively(outputPath, OutputHoldShare, out var stream, out reason);
        if (claim != FileClaim.Taken)
        {
            // An answer that is not Taken can still carry a handle: a claim granted over the
            // file and then refused because the question that finishes it could not be answered
            // comes back Unopenable with the file open, so that the caller lets go of it the way
            // it lets go of one it kept. Nothing is kept here, so it goes here.
            stream?.Dispose();
            return claim switch
            {
                FileClaim.Held => CheckpointOutput.HeldByAnotherRun,
                FileClaim.Unopenable => CheckpointOutput.CannotBeOpened,
                _ => CheckpointOutput.NotTheRecordedFile,
            };
        }

        var hold = new OutputHold(stream!);
        try
        {
            // Off the handle, not the directory entry: the file this run has is the file it
            // measures, and no second stat can disagree with it.
            if (hold.Length < dataLength)
            {
                hold.Dispose();
                return CheckpointOutput.NotTheRecordedFile;
            }
            if (hold.Length > dataLength) hold.TrimTo(dataLength);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cut failed on a handle this run holds, so nothing else is writing the file and
            // the account can open it. Whatever the filesystem is refusing, this run has not
            // read the file's contents and may not say the items are gone from it. The open
            // itself did not fail, so OpenExclusively left no reason above - this is the one
            // Unopenable a caught exception names rather than a fixed phrase or ex is
            // UnauthorizedAccessException, since a permission that blocks only a truncate and
            // not the open that preceded it is not itself the more common shape.
            hold.Dispose();
            reason = new UnopenableReason(FirstLine(ex.Message).TrimEnd('.'),
                ex is UnauthorizedAccessException);
            return CheckpointOutput.CannotBeOpened;
        }

        ReleaseOutputHold();
        _outputHold = hold;
        return CheckpointOutput.Taken;
    }

    /// <summary>
    /// The same answer without taking anything: the open is made and let go, and nothing is cut.
    /// The export's ShouldProcess gate has to name the action a real run would take before the
    /// run is allowed to take any, and -WhatIf has to leave every file exactly as it found it -
    /// including its length.
    /// </summary>
    protected static CheckpointOutput WouldTakeOutputForCheckpoint(string outputPath,
        long dataLength, out UnopenableReason? reason)
    {
        reason = null;
        if (dataLength < 0) return CheckpointOutput.NotTheRecordedFile;

        var claim = OpenExclusively(outputPath, OutputHoldShare, out var stream, out reason);
        using (stream)
        {
            return claim switch
            {
                FileClaim.Held => CheckpointOutput.HeldByAnotherRun,
                FileClaim.Unopenable => CheckpointOutput.CannotBeOpened,
                FileClaim.Absent => CheckpointOutput.NotTheRecordedFile,
                _ => stream!.Length >= dataLength
                    ? CheckpointOutput.Taken
                    : CheckpointOutput.NotTheRecordedFile,
            };
        }
    }

    /// <summary>
    /// The refusal half of <see cref="CheckpointOutput"/>, for the two callers that have to say
    /// which file a stop is about and why.
    /// </summary>
    protected static ClaimRefusal RefusalFrom(CheckpointOutput taken) => taken switch
    {
        CheckpointOutput.HeldByAnotherRun => ClaimRefusal.AnotherRunHasIt,
        CheckpointOutput.CannotBeOpened => ClaimRefusal.CannotBeOpened,
        _ => ClaimRefusal.None,
    };

    /// <summary>
    /// What TryPromoteNamedTemp would answer, without promoting anything. The ShouldProcess gate
    /// has to name the action a real run would take before the run is allowed to take any, and
    /// -WhatIf has to leave every staged file exactly as it found it. The claim on the temp is
    /// asked for here too, and released again: a temp another run holds is one a real run would
    /// refuse, so answering from the file's existence alone reported a recovery that would not
    /// have happened. Which of the two ways that claim failed is not reported - a temp another
    /// run holds and a temp this account cannot open are both temps a real run does not promote,
    /// and there is one answer to give the gate either way. The caller that has to tell them
    /// apart in a sentence asks <see cref="RefusalForNamedTemp"/>.
    /// <para>
    /// The output the promotion would land in is asked about as well, the way the applying pass
    /// asks and with nothing taken and nothing cut. A promotion over an output another run holds
    /// stops the run, which is neither of the two actions the gate names, and a preview that
    /// never asked reported an append over a file the run would not have reached.
    /// </para>
    /// <para>
    /// The staging name is the one place a real run is not read-only, and the preview reads it
    /// anyway: asking only whether something already stands there previewed a recovery over an
    /// output directory this account cannot write in at all. Where the name is clear, the
    /// preview makes the same create a real run makes, at no length and removed again under the
    /// handle that made it, so the two passes fail on the same open for the same reason. Where
    /// something already stands at the name, the preview leaves it alone: sweeping it would
    /// make deletions as real as a run's, so a caller asking what would be touched would have
    /// had a link, a pipe or a leftover copy of theirs deleted just to be told that nothing
    /// would be. What stands there comes back instead, for the caller to warn about. What it
    /// leaves behind is what it found: the two files the checkpoint stands for are untouched,
    /// and the staging name holds what it always held.
    /// </para>
    /// </summary>
    protected static TempPromotion CanPromoteNamedTemp(string outputPath, string tempFileName,
        long dataLength, out UnopenableReason? reason, out StagingFailure? staging,
        out StagingRemoval? swept)
    {
        reason = null;
        staging = null;
        swept = null;
        try
        {
            var tempPath = ResolveNamedTemp(outputPath, tempFileName, dataLength, mayWrite: false);
            if (tempPath == null) return TempPromotion.NotPromoted;

            // The name the copy is staged under, read in the same place in the order a real run
            // sweeps it: first, before either file the checkpoint stands for is asked about, so
            // a hard link at the name is an alias nothing of this pass's holds. The gate names
            // the action a run would take, and a run that would stop on its own staging was
            // previewed as a recovery: a directory there, or another run's copy, is refused in
            // the run and refused here in the same words.
            //
            // Read and not swept. A preview that cleared the name made deletions as real as a
            // run's - a caller asking what would be touched had a link, a pipe or a leftover
            // copy of theirs deleted in order to be told that nothing would be, and on -WhatIf
            // over a checkpoint the run would then have stopped on. What stands there comes back
            // instead, for the caller to say what the run would take off the name.
            var adoptPath = StagingPathFor(outputPath);
            if (!SweepStagingName(adoptPath, apply: false, out staging, out swept))
                return TempPromotion.StagingFailed;

            if (!CanTakeExclusively(tempPath)) return TempPromotion.NotPromoted;

            var standing = StandingOutputRefusal(outputPath, out reason);
            if (standing != TempPromotion.Taken) return standing;

            // And the create itself, at no length and taken back again, where the name is clear.
            // Claiming the name was not the question a run answers there: the create is what a
            // directory this account may not write in refuses, and asking only whether something
            // already stands at the name previewed a recovery over an output the run could never
            // have staged a copy beside. Made with the mode the run makes it with, so the two
            // passes fail on the same open for the same reason.
            //
            // Skipped where something stands at the name: the run would remove that entry and
            // create its own, and there is no way to make the same create without taking the
            // entry off the name first. The caller reports what the run would remove instead of
            // this pass removing it.
            if (swept == null)
            {
                FileStream probe;
                try
                {
                    probe = ScratchName.CreateNew(adoptPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    staging = StagingFailureAt(adoptPath, ex, fromCreate: true);
                    return TempPromotion.StagingFailed;
                }

                // The entry this pass made is the only thing in the directory that was not there
                // before it, and a preview leaves the directory as it found it. Unlinked under
                // the handle that made it, so no second run can take the name in between.
                DiscardStagedFile(adoptPath, probe);
            }

            return TempPromotion.Taken;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TempPromotion.NotPromoted;
        }
    }

    /// <summary>
    /// Why one of the two files a checkpoint stands for - the temp it names, or the output it
    /// records its items into - is not this run's to take, where it is not.
    /// </summary>
    protected enum ClaimRefusal
    {
        /// <summary>
        /// Nothing refuses it: there is no such file of this checkpoint's, or nothing stands
        /// between this run and it - which, for a temp whose promotion has already failed,
        /// means the items it counted are not all in a file this run can reach.
        /// </summary>
        None,

        /// <summary>Another run has it open.</summary>
        AnotherRunHasIt,

        /// <summary>This account cannot open it read-write.</summary>
        CannotBeOpened,
    }

    /// <summary>Which of the two files a checkpoint stands for a stop is about.</summary>
    protected enum CheckpointFile
    {
        /// <summary>
        /// The temp it names, holding the items it counts while the output still holds an
        /// earlier run's.
        /// </summary>
        NamedTemp,

        /// <summary>
        /// The output it records its items into, which a run that has already resumed writes
        /// straight to - so its checkpoint names no temp at all.
        /// </summary>
        Output,
    }

    /// <summary>
    /// How this run reached a checkpoint's output: appending straight into it, promoting a
    /// named temp over it, or exporting fresh after the named temp it counted on had already
    /// vanished. A stop over the output reads differently on each - where the checkpoint's
    /// items are, and what going on would have done to the file - so a caller building the
    /// sentence needs to be told which one it is on rather than assuming the appending shape,
    /// which used to be the only one that reached a temp-based checkpoint's own claim on the
    /// output at all.
    /// </summary>
    protected enum CheckpointStopRoute
    {
        /// <summary>
        /// The checkpoint names no temp: a run that had already resumed was appending straight
        /// into the output, so the items it counts are in that file, and going on would cut it
        /// back to the recorded length under the run still writing past it.
        /// </summary>
        Appending,

        /// <summary>
        /// The checkpoint names a temp that is still there: the items it counts are in that
        /// temp, and going on would promote them, replacing the output outright rather than
        /// appending to it.
        /// </summary>
        Promoting,

        /// <summary>
        /// The checkpoint names a temp that is gone: the items it counted are recoverable from
        /// no file this run can reach, and going on would enumerate the collection again from
        /// the beginning and replace the output with that result.
        /// </summary>
        FreshAfterVanishedTemp,
    }

    /// <summary>
    /// Why a claim answered <see cref="FileClaim.Unopenable"/>, carried out of
    /// <see cref="OpenExclusively"/> with the verdict so a caller building a stop can name the
    /// open's own reason instead of guessing at one. Built by
    /// <see cref="ReasonForUnopenable"/>, which is also what a promotion's staging failure is
    /// refined by, so one disk state reads the same whichever site reached it.
    /// <see cref="Reason"/> carries no sentence-ending period of its own - taken off where the
    /// runtime's words are read - since a caller quotes it inside a sentence it punctuates
    /// itself. <see cref="IsPermission"/> is read off which failure produced
    /// <see cref="Reason"/>, not out of its text: the text is the runtime's own words, in
    /// whatever language this host reports errors in, and is not this run's to parse for a
    /// cause it already knows structurally.
    /// <para>
    /// <see cref="IsDirectory"/> and <see cref="IsUnsearchableParent"/> are the two causes worth
    /// telling from a permission on the file, because they are the two with a different way out.
    /// A directory standing at the output path opens as an access failure on both platforms -
    /// EISDIR and a denying ACL arrive in the same catch - and no grant makes a directory
    /// writable at an offset: what the caller does about it is remove it or point -OutputFile
    /// somewhere else. A file whose parent cannot be searched is an access failure naming the
    /// file, and the grant that opens it belongs to the directory - so the advice that named the
    /// file sent the caller to change a mode that was never the one refusing them. Both are read
    /// off the filesystem rather than the errno, since which failure an open produces depends on
    /// the mode standing in the way.
    /// </para>
    /// </summary>
    protected readonly record struct UnopenableReason(string Reason, bool IsPermission,
        bool IsDirectory = false, bool IsUnsearchableParent = false);

    /// <summary>
    /// Which of the two files a claim refused, and why, or <see cref="ClaimRefusal.None"/>
    /// where neither did. The file and the reason decide what the run can honestly say: the
    /// file settles what going on would do to it, and the reason settles whether there is a
    /// second run to wait for or a mode to change. <see cref="Route"/> and
    /// <see cref="Reason"/> refine that for an output refusal - which of the three ways this
    /// run reached the output, and, where the claim failed on a mode rather than a hold, the
    /// open's own explanation - and are not read for a temp refusal, which has never had more
    /// than one route to it.
    /// </summary>
    protected readonly record struct CheckpointFileRefusal(CheckpointFile File,
        ClaimRefusal Refusal, CheckpointStopRoute? Route = null, UnopenableReason? Reason = null)
    {
        /// <summary>Nothing refused either file, and the run goes on.</summary>
        public static CheckpointFileRefusal None => new(CheckpointFile.NamedTemp, ClaimRefusal.None);

        /// <summary>Whether a file was refused at all.</summary>
        public bool Refuses => Refusal != ClaimRefusal.None;

        /// <summary>Whether the reason was another run holding it, rather than the mode on it.</summary>
        public bool Held => Refusal == ClaimRefusal.AnotherRunHasIt;
    }

    /// <summary>
    /// Why the temp a checkpoint names could not be claimed. Two of the answers leave both
    /// files exactly where they are, and they are not the same thing to tell the caller.
    ///
    /// Another run having it open is the single reason a promotion fails that says nothing is
    /// wrong with the files: every row the checkpoint counted is in that temp, and the
    /// enumeration that counted them is still collecting into it - so the checkpoint is the
    /// position a running export resumes from, and deleting it, or reporting its items as not
    /// on disk, describes a run that has not failed and takes away what it would have come
    /// back to.
    ///
    /// A temp this account cannot open read-write reaches the same refusal and has no second
    /// run behind it at all. Said in the other's words, it sent the reader looking for an
    /// export that was never there rather than at the file's owner and mode.
    ///
    /// The remaining failures - no temp, a temp shorter than the recorded length - mean those
    /// items are in no file, which is what the callers delete the checkpoint over and
    /// re-enumerate.
    /// </summary>
    protected static ClaimRefusal RefusalForNamedTemp(
        string outputPath, string tempFileName, long dataLength, bool mayWrite)
    {
        try
        {
            var tempPath = ResolveNamedTemp(outputPath, tempFileName, dataLength, mayWrite);
            if (tempPath == null) return ClaimRefusal.None;
            return ClaimExclusively(tempPath, out _) switch
            {
                FileClaim.Held => ClaimRefusal.AnotherRunHasIt,
                FileClaim.Unopenable => ClaimRefusal.CannotBeOpened,
                _ => ClaimRefusal.None,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ClaimRefusal.None;
        }
    }

    /// <summary>
    /// Why the checkpoint at -CheckpointPath is not this run's to delete. Every refusal that
    /// leaves the file standing records this, and the doors that would otherwise delete it -
    /// the completion path, and the sync's 410 - read it and nothing else. A run's own saved
    /// position, and a checkpoint refused because the request was rebuilt without a query
    /// option the endpoint would not answer, record none: those the doors take with the run.
    /// A checkpoint one of whose files - the temp it names, or the output it appends to - could
    /// not be claimed reaches no door, because the run that found that out did not go on.
    /// </summary>
    protected enum SparedCheckpoint
    {
        /// <summary>
        /// The ownership verdict: the file records another output, or an enumeration nothing
        /// beside this run's output corroborates. Whatever wrote it resumes from exactly that.
        /// </summary>
        NotThisRuns,
    }

    /// <summary>
    /// What a door says when it leaves the checkpoint where it was. One reading reaches a door
    /// at all: a run that cannot claim a file its own checkpoint stands for stops where it finds
    /// out, so no door of a run that got as far as finishing is answering for that file.
    /// </summary>
    protected static string SparedCheckpointVerbose(string occasion) =>
        $"Left the resume checkpoint alone ({occasion}): it records an enumeration this "
        + "run refused as not its own and never wrote over.";

    /// <summary>
    /// The sentence a stop ends with, and the one a -WhatIf pass over the same condition ends
    /// with instead. Both cmdlets refuse a checkpoint whose temp, or whose output, is held or
    /// unopenable by ending the run there, and the difference between doing that and reporting
    /// that it would be done is a tense.
    /// </summary>
    protected const string RunStopsHere = "This run stops here; nothing was written.";

    /// <inheritdoc cref="RunStopsHere"/>
    protected const string RunWouldStopHere = "This run would stop here; nothing would be written.";

    /// <summary>
    /// The same for a staging failure, where "nothing was written" is not what the run has to
    /// say: the copy it was writing beside the output is gone again, and what the reader needs
    /// to hear is that neither of the two files the checkpoint stands for moved.
    /// </summary>
    protected const string NothingWasChanged = "Nothing was changed;";

    /// <inheritdoc cref="NothingWasChanged"/>
    protected const string NothingWouldBeChanged = "Nothing would be changed;";

    /// <summary>
    /// The newest "{output}.{32-hex}.tmp" beside the output that nothing else holds open, or
    /// null when there is none. Adoption copies the file it picks into the output and then
    /// unlinks it, so a temp a second export is writing into right now must not be picked at
    /// all: that run went on writing into an unlinked inode, lost everything it had fetched,
    /// and its rows were handed over as this export's. The claim is the one DeleteStaleTemps
    /// takes before it deletes, and it settles the same question - FileShare.None holds
    /// between processes on Windows and Unix alike, so a run with its own temp open makes it
    /// fail, and a file nobody can be found holding is an orphan. A candidate that cannot be
    /// claimed is passed over rather than ending the search: newest-first reaches a live run's
    /// temp before an older one, and the next candidate down may be the orphan recovery exists
    /// for. Taking the claim before anything reads the file also keeps Windows declining here,
    /// where a live writer already conflicts with the reads below rather than with this.
    /// </summary>
    private static FileInfo? NewestUnheldTemp(string outputPath, bool mayWrite)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        var outputName = Path.GetFileName(outputPath);
        return Directory.EnumerateFiles(dir, outputName + ".*.tmp", TempGlobOptions(dir, mayWrite))
            .Where(p => IsRunTempName(dir, outputName, Path.GetFileName(p), mayWrite))
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault(f => CanTakeExclusively(f.FullName));
    }

    /// <summary>
    /// Whether TryAdoptOrphanedTemp would find a temp holding the counted items, without
    /// merging it into anything.
    /// <para>
    /// The staging name is the one place a real run is not read-only, and the preview reads it
    /// anyway: asking only whether something already stands there previewed a recovery where a
    /// run would stop instead, since an output directory this account may read and not write
    /// refuses the create and nothing standing at the name says so. Where the name is clear,
    /// the preview makes the same create a real run makes, at no length and removed again under
    /// the handle that made it, so the two passes fail on the same open for the same reason.
    /// Where something already stands at the name, the preview leaves it alone and reports what
    /// stands there instead, for the caller to warn about. What it leaves behind is what it
    /// found: the temp is untouched, the output is still not there, and the staging name holds
    /// what it always held.
    /// </para>
    /// </summary>
    /// <param name="tempName">The file adoption would take the items from, as soon as it is
    /// picked; null where there is none. What a caller names in a staging stop, since a
    /// checkpoint on this route records no temp of its own.</param>
    /// <param name="staging">Why the adoption stops at the staging name, where it does. Its own
    /// answer and not a temp that has gone: the items are all in the file
    /// <paramref name="tempName"/> names, and a caller that read this as a missing temp deleted
    /// the position counting them.</param>
    /// <param name="swept">What stands at the staging name and what a real run would remove,
    /// for the caller to warn about; null where nothing stands there.</param>
    protected static bool CanAdoptOrphanedTemp(string outputPath, long itemCount,
        out string? tempName, out StagingFailure? staging, out StagingRemoval? swept)
    {
        tempName = null;
        staging = null;
        swept = null;
        try
        {
            if (itemCount <= 0) return false;
            if (File.Exists(outputPath)) return false;
            var temp = NewestUnheldTemp(outputPath, mayWrite: false);
            if (temp == null) return false;
            tempName = temp.Name;

            // Whole rows only, as below. Read and let go before the name beside the output is
            // touched, so nothing of this pass's is open over the sweep.
            var endsWhole = EndsWithNewline(temp.FullName);
            long counted = 0;
            using (var reader = new StreamReader(temp.FullName))
            {
                while (counted < itemCount && reader.ReadLine() != null) counted++;
                if (counted < itemCount || !(endsWhole || reader.ReadLine() != null)) return false;
            }

            // And the name the copy is staged under, read where the run below clears it, so the
            // two passes answer one disk state in the same words - and read only, because a
            // preview removes nothing. The create test follows it where the name is clear; where
            // something stands there the run would remove that entry first, and nothing at the
            // name can be tested without doing so.
            var adoptPath = StagingPathFor(outputPath);
            if (!SweepStagingName(adoptPath, apply: false, out staging, out swept)) return false;
            if (swept == null)
            {
                FileStream probe;
                try
                {
                    probe = ScratchName.CreateNew(adoptPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    staging = StagingFailureAt(adoptPath, ex, fromCreate: true);
                    return false;
                }

                // The entry this pass made is the only thing in the directory that was not there
                // before it, and a preview leaves the directory as it found it. Unlinked under
                // the handle that made it, so no second run can take the name in between.
                DiscardStagedFile(adoptPath, probe);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Recovers a fresh-run JSONL job that was interrupted before its temp file was promoted,
    /// from a checkpoint that predates the recorded temp name and length. The candidates are
    /// the names a run gives its own temp - "{output}.{32-hex}.tmp" - and nothing else: the
    /// glob that finds them also reaches a caller's own "users.jsonl.backup.tmp", and adoption
    /// copies what it takes into the output and then deletes it. With only a line count and the
    /// newest of those nothing else holds open to go on, this is safe solely when no output exists:
    /// everything the run wrote is then in its temp, and creating the output from it is what a
    /// finishing run's own promotion would have done. Against an existing output there is no
    /// way to tell whose items the temp holds, so the caller must re-enumerate instead.
    /// Copies exactly <paramref name="itemCount"/> lines - content beyond the last flush may
    /// be absent or torn - then removes the temp. The itemCount-th of them has to be a whole
    /// row: ReadLine hands back an unterminated tail as though it were a line, so a temp cut
    /// inside a counted row copied the fragment into the output as an item, reported it as
    /// recovered, and left the rest of the enumeration appended behind it. A run mgx interrupts
    /// cannot leave that state - every checkpoint site flushes the writer before recording the
    /// count, so the rows a checkpoint counts are always terminated - but a temp truncated
    /// under it by something outside the run can. Returns false when nothing usable exists,
    /// leaving the caller to the stale-checkpoint path, which re-enumerates and loses nothing -
    /// except where <paramref name="staging"/> came back with it, which is the one false that
    /// route must not be taken for.
    /// </summary>
    /// <inheritdoc cref="CanAdoptOrphanedTemp" path="/param"/>
    protected static bool TryAdoptOrphanedTemp(string outputPath, long itemCount,
        out string? tempName, out StagingFailure? staging, out StagingRemoval? swept)
    {
        tempName = null;
        staging = null;
        swept = null;
        try
        {
            if (itemCount <= 0) return false;
            if (File.Exists(outputPath)) return false;
            var temp = NewestUnheldTemp(outputPath, mayWrite: true);
            if (temp == null) return false;
            tempName = temp.Name;

            // A file that ends mid-row can only end mid-row in its LAST one, so the counted
            // rows are whole unless the count reaches that far - which is the case where
            // another line still follows the itemCount-th.
            var endsWhole = EndsWithNewline(temp.FullName);
            var countedRowIsWhole = false;

            // Staged so the output appears in one Move. A merge that dies halfway leaves only
            // the staging file behind, and the caller keeps its checkpoint - the safe direction.
            //
            // Under the same claim on the name a promotion stages by, and for the same reasons:
            // the copy went into whatever stood at the name, so a symlink there sent it into the
            // link's target and the move then put the link at the output path. The name is
            // cleared first and created by an open that refuses to use anything already there,
            // and the handle is held from the create through the rename.
            long copied = 0;
            var adoptPath = StagingPathFor(outputPath);
            if (!SweepStagingName(adoptPath, apply: true, out staging, out swept)) return false;
            FileStream? staged = null;
            try
            {
                try
                {
                    staged = ScratchName.CreateNew(adoptPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Something appeared at the name in the instants since the sweep, or the
                    // directory beside the output refuses a new file in it at all. Neither is a
                    // temp that has gone missing, and the caller's route for one of those
                    // deletes the position counting items this temp holds every one of.
                    staging = StagingFailureAt(adoptPath, ex, fromCreate: true);
                    return false;
                }
                // What StreamWriter(path, append) wrote through: UTF-8 with no byte-order mark,
                // throwing on unpaired surrogates.
                using (var writer = new StreamWriter(staged, new UTF8Encoding(false, true), 1024,
                           leaveOpen: true))
                {
                    using var reader = new StreamReader(temp.FullName);
                    string? line;
                    while (copied < itemCount && (line = reader.ReadLine()) != null)
                    {
                        writer.WriteLine(line);
                        copied++;
                    }
                    countedRowIsWhole = endsWhole || reader.ReadLine() != null;
                }
                if (copied < itemCount || !countedRowIsWhole)
                {
                    // Temp holds less than the checkpoint promises, or ends inside the last row
                    // it promises - unusable either way. The copy goes with the adoption that
                    // did not land, unlinked under the handle that holds the name.
                    DiscardStagedFile(adoptPath, staged);
                    staged = null;
                    return false;
                }
                // Windows will not rename a source this process has open, so there the handle
                // goes before the move; everywhere else it holds the name across it.
                if (OperatingSystem.IsWindows())
                {
                    staged.Dispose();
                    staged = null;
                }
                File.Move(adoptPath, outputPath, overwrite: true);
            }
            finally { staged?.Dispose(); }
            temp.Delete();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    #region Shared URL and header builders

    protected static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        // A request URI has no use for a fragment - the server never sees one - so a raw '#'
        // is always data (a filename, a filter value). Left alone, System.Uri would treat
        // everything after it as a fragment and silently drop it from the wire.
        path = path.Replace("#", "%23");
        return path.StartsWith('/') ? path : $"/{path}";
    }

    /// <summary>
    /// Query-option names already present in a URL's own query string. Graph rejects a URL
    /// carrying the same option twice, so a typed parameter defers to what the caller
    /// already wrote into -Uri.
    /// </summary>
    protected static HashSet<string> ExistingQueryOptions(string url)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var q = url.IndexOf('?');
        if (q < 0) return set;
        foreach (var part in url[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            // Decoded: %24top and $top are the same option to the server.
            set.Add(Uri.UnescapeDataString(part.Split('=', 2)[0]));
        return set;
    }

    private static readonly Dictionary<string, string> s_optionParameterNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["$filter"] = "-Filter", ["$select"] = "-Property", ["$orderby"] = "-Sort",
        ["$search"] = "-Search", ["$expand"] = "-ExpandProperty", ["$skip"] = "-Skip",
        ["$top"] = "-Top",
    };

    /// <summary>One warning line for typed parameters that deferred to options in -Uri.</summary>
    protected static string DescribeDeferredOptions(IReadOnlyList<string> deferred)
    {
        var names = deferred.Select(o =>
            s_optionParameterNames.TryGetValue(o, out var param) ? $"{param} ({o})" : o);
        return $"{string.Join(", ", names)}: -Uri already carries the option, so the parameter's " +
               "value was not added to the query. The -Uri value was sent.";
    }

    /// <summary>The decoded value of a query option in a built URL, or null when absent.</summary>
    protected static string? GetQueryOptionValue(string url, string name)
    {
        var q = url.IndexOf('?');
        if (q < 0) return null;
        foreach (var part in url[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            if (string.Equals(Uri.UnescapeDataString(pieces[0]), name, StringComparison.OrdinalIgnoreCase))
                return pieces.Length > 1 ? Uri.UnescapeDataString(pieces[1]) : string.Empty;
        }
        return null;
    }

    /// <summary>
    /// Query-degradation retries, shared by the collection paths: some endpoints refuse an
    /// auto-added $count=true with a bare 400, and some (e.g. /directoryRoles) refuse $top
    /// with Request_UnsupportedQuery. Both recover by dropping the option and retrying.
    /// </summary>
    protected static bool IsCountRejection(GraphServiceException ex, bool countWasAutoAdded)
        => countWasAutoAdded && ex.StatusCode == HttpStatusCode.BadRequest;

    protected static bool IsTopRejection(GraphServiceException ex, bool topSuppressed, bool noPageSize)
        => !topSuppressed && !noPageSize
           && ex.StatusCode == HttpStatusCode.BadRequest
           && string.Equals(ex.ErrorCode, "Request_UnsupportedQuery", StringComparison.OrdinalIgnoreCase);

    protected const string CountRejectedVerbose =
        "Endpoint rejected $count=true (HTTP 400). Retrying without count parameter.";
    protected const string TopRejectedVerbose =
        "Endpoint rejected $top (Request_UnsupportedQuery). Retrying without page size.";

    private static readonly Regex s_encodedTriplet = new("%[0-9A-Fa-f]{2}", RegexOptions.Compiled);

    /// <summary>
    /// Escapes a query-option value, leaving anything already percent-encoded alone - a
    /// pre-encoded filter must not be encoded twice (%27 becoming %2527), and a raw
    /// apostrophe or space must still be escaped. Any %XX triplet counts as already
    /// encoded; a value that means a literal percent-then-hex sequence has to arrive
    /// pre-encoded (%25XX) to survive as data.
    /// </summary>
    protected static string EscapeQueryValue(string value)
    {
        var sb = new StringBuilder(value.Length + 16);
        int i = 0;
        foreach (Match m in s_encodedTriplet.Matches(value))
        {
            sb.Append(Uri.EscapeDataString(value[i..m.Index]));
            sb.Append(m.Value);
            i = m.Index + m.Length;
        }
        sb.Append(Uri.EscapeDataString(value[i..]));
        return sb.ToString();
    }

    /// <summary>
    /// A header value from a Hashtable. An array (Prefer takes several values) joins with
    /// ", " - HTTP's list form - instead of .NET's array ToString(), which would put the
    /// literal text "System.String[]" on the wire.
    /// </summary>
    private static string HeaderValueToString(object? value)
    {
        if (value == null) return string.Empty;
        if (value is string s) return s;
        if (value is IEnumerable seq)
            return string.Join(", ", seq.Cast<object?>().Select(HeaderScalarToString));
        return HeaderScalarToString(value);
    }

    /// <summary>
    /// One header value, rendered the same way on every machine. object.ToString() formats
    /// under the thread's culture, so a double or a DateTimeOffset in -Headers put "0,5" on
    /// the wire under de-DE where en-US put "0.5" - the same script sending different bytes.
    /// CA1305 does not see it: the receiver is typed object, not IFormattable.
    /// </summary>
    private static string HeaderScalarToString(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    protected static Dictionary<string, string>? BuildRequestHeaders(
        string? consistencyLevel, System.Collections.Hashtable? extraHeaders)
    {
        Dictionary<string, string>? headers = null;

        // Apply extraHeaders first so dedicated parameters can override
        if (extraHeaders != null)
        {
            // Case-insensitive: HTTP header names are, and a case-variant duplicate
            // (If-Match and if-match) must not become two wire headers.
            headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // The name is rendered the same way as the value: a Hashtable key is typed object
            // too, and object.ToString() puts a numeric one on the wire under the operator's
            // culture.
            foreach (var key in extraHeaders.Keys)
                headers[HeaderScalarToString(key)] = HeaderValueToString(extraHeaders[key]);
        }

        // Dedicated -ConsistencyLevel parameter always wins over -Headers key
        if (!string.IsNullOrEmpty(consistencyLevel))
        {
            headers ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            headers["ConsistencyLevel"] = consistencyLevel;
        }

        return headers;
    }

    protected record ODataListParams(
        bool NoPageSize,
        int Top,
        int PageSize,
        string? Filter,
        string[]? Property,
        string[]? Sort,
        string? Search,
        int Skip,
        string[]? ExpandProperty,
        bool IncludeCount = false);

    protected static string BuildListUrl(string versionedBaseUrl, string relativeUri, ODataListParams p)
        => BuildListUrl(versionedBaseUrl, relativeUri, p, out _);

    /// <summary>
    /// <paramref name="deferredToUri"/> reports typed parameters that were NOT sent because
    /// -Uri already carries the option. Callers warn: silently preferring the -Uri value
    /// turns "-Property displayName" into a different projection with no sign anything was
    /// ignored. Excluded from the report: the automatic page-size $top (only an explicit
    /// -Top counts) and the automatic $count.
    /// </summary>
    protected static string BuildListUrl(string versionedBaseUrl, string relativeUri, ODataListParams p,
        out List<string> deferredToUri)
    {
        var baseUrl = $"{versionedBaseUrl}{NormalizePath(relativeUri)}";
        var queryParams = new List<string>();

        if (!p.NoPageSize)
        {
            var effectiveTop = p.Top > 0 ? Math.Min(p.Top, p.PageSize) : p.PageSize;
            queryParams.Add($"$top={effectiveTop}");
        }

        if (!string.IsNullOrEmpty(p.Filter))
            queryParams.Add($"$filter={EscapeQueryValue(p.Filter)}");

        if (p.Property is { Length: > 0 })
            queryParams.Add($"$select={EscapeQueryValue(string.Join(",", p.Property))}");

        if (p.Sort is { Length: > 0 })
            queryParams.Add($"$orderby={EscapeQueryValue(string.Join(",", p.Sort))}");

        if (!string.IsNullOrEmpty(p.Search))
        {
            // Graph API requires $search values wrapped in double quotes: $search="displayName:John"
            var searchValue = p.Search;
            if (!searchValue.StartsWith('"') || !searchValue.EndsWith('"'))
                searchValue = $"\"{searchValue}\"";
            queryParams.Add($"$search={EscapeQueryValue(searchValue)}");
        }

        if (p.Skip > 0)
            queryParams.Add($"$skip={p.Skip}");

        if (p.ExpandProperty is { Length: > 0 })
            queryParams.Add($"$expand={EscapeQueryValue(string.Join(",", p.ExpandProperty))}");

        // $count=true: required explicitly via -CountVariable, or implicitly when $search is used
        // (Graph advanced query capabilities require $count=true alongside $search)
        if (p.IncludeCount || !string.IsNullOrEmpty(p.Search))
            queryParams.Add("$count=true");

        var existing = ExistingQueryOptions(baseUrl);
        var removed = queryParams.Where(qp => existing.Contains(qp.Split('=', 2)[0]))
            .Select(qp => qp.Split('=', 2)[0]).ToList();
        queryParams.RemoveAll(qp => existing.Contains(qp.Split('=', 2)[0]));
        deferredToUri = removed.Where(name =>
                !string.Equals(name, "$count", StringComparison.OrdinalIgnoreCase)
                && (!string.Equals(name, "$top", StringComparison.OrdinalIgnoreCase) || p.Top > 0))
            .ToList();

        if (queryParams.Count == 0)
            return baseUrl;

        // If URI already contains query parameters, append with & instead of ?
        var separator = baseUrl.Contains('?') ? "&" : "?";
        return $"{baseUrl}{separator}{string.Join("&", queryParams)}";
    }

    #endregion

    protected string CircuitBreakerMessage =>
        $"Circuit breaker tripped: too many failures caused Mgx to temporarily stop requests. " +
        $"Wait {s_clientOptions.CircuitBreakerDurationSeconds}s or run Get-MgxTelemetry for details. " +
        $"Tune with Set-MgxOption -CircuitBreakerFailureRatio / -CircuitBreakerMinThroughput.";

    /// <summary>
    /// Codes measured to mean the PATH was fine and the OBJECT was not there, so a beta hint
    /// over them sends the caller to re-run a request that fails there too. Only codes with
    /// demonstrated semantics belong here. Request_ResourceNotFound does NOT qualify: Graph
    /// returns it both for a missing directory object and for a beta-only segment on v1.0
    /// (measured against /users/{id} and /users/{id}/profile), so it cannot be told apart and
    /// the hedged hint stays. itemNotFound is the drive service reporting an absent item - an
    /// unknown drive segment is a 400, not a 404, so the ambiguity does not arise there.
    /// </summary>
    private static readonly HashSet<string> ObjectMissingCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "itemNotFound",
    };

    /// <summary>True when the exception is a Graph 404 that names a missing object.</summary>
    protected static bool IsObjectMissing(Exception ex) =>
        ex is GraphServiceException { StatusCode: HttpStatusCode.NotFound } g
        && g.ErrorCode != null
        && ObjectMissingCodes.Contains(g.ErrorCode);

    protected void WriteBetaHintIfApplicable(HttpStatusCode statusCode, string apiVersion,
        string? errorCode = null)
    {
        if (statusCode != HttpStatusCode.NotFound ||
            !string.Equals(apiVersion, "v1.0", StringComparison.OrdinalIgnoreCase))
            return;

        // A missing user, group or drive item is not an absent endpoint.
        if (errorCode != null && ObjectMissingCodes.Contains(errorCode))
            return;

        WriteWarning("This endpoint may only be available in beta. Retry with -ApiVersion beta.");
    }

    // Derived from the failure class, so every status the classifier knows gets a real
    // category: a 500 is ResourceUnavailable and a 408 ConnectionError where the old
    // six-entry map left both NotSpecified.
    protected static ErrorCategory MapStatusToCategory(HttpStatusCode statusCode)
        => MgxErrorPresentation.CategoryForStatus(statusCode);

    /// <summary>
    /// Handles the three common terminal exception types (GraphServiceException,
    /// BrokenCircuitException, HttpRequestException) that appear in every cmdlet's
    /// catch cascade. Drains buffered messages, writes beta hint if applicable,
    /// and writes the error record.
    /// Returns true if the exception was handled; false if unrecognized.
    /// </summary>
    protected bool WriteGraphError(Exception ex, object? target, string? apiVersion = null)
    {
        DrainClientMessages();

        switch (ex)
        {
            case GraphServiceException gex:
                if (apiVersion != null)
                    WriteBetaHintIfApplicable(gex.StatusCode, apiVersion, gex.ErrorCode);
                WriteError(new ErrorRecord(gex, gex.ErrorCode ?? "GraphError",
                    MapStatusToCategory(gex.StatusCode), target));
                return true;

            case BrokenCircuitException bcex:
                WriteError(new ErrorRecord(
                    new InvalidOperationException(CircuitBreakerMessage, bcex),
                    "CircuitBroken", ErrorCategory.ResourceUnavailable, target));
                return true;

            case HttpRequestException hex:
                WriteError(new ErrorRecord(hex, "HttpError",
                    ErrorCategory.ConnectionError, target));
                return true;

            default:
                return false;
        }
    }

    // Count discrepancy detection thresholds.
    // Not user-configurable (YAGNI). Change these constants if defaults prove problematic.
    // Undercount: 10% tolerance prevents noise from eventual consistency lag;
    // 100-item floor avoids false alarms on small collections.
    // Overcount: much tighter (0.5%, 50-item floor) - the observed failure mode is a
    // duplicated page from a service-side skiptoken overlap (~one $top of extras),
    // which a symmetric 10% tolerance would never catch at scale.
    protected const double CountDiscrepancyThreshold = 0.9;
    protected const long CountDiscrepancyMinItems = 100;
    protected const double CountOvershootThreshold = 0.005;
    protected const long CountOvershootMinItems = 50;

    protected void WriteCountDiscrepancyWarning(
        string resource, long reportedCount, long actualCount, string? filter)
    {
        if (reportedCount < CountDiscrepancyMinItems) return;

        if (actualCount > reportedCount)
        {
            var overshoot = actualCount - reportedCount;
            if (overshoot <= Math.Max(CountOvershootMinItems, (long)(reportedCount * CountOvershootThreshold)))
                return;
            WriteWarning(
                $"[{resource}] Graph returned {actualCount} items but reported a count of {reportedCount} "
                + $"({overshoot} extra). This can indicate a duplicated page during pagination "
                + "(observed as a transient service-side skiptoken overlap). If the output feeds a "
                + "downstream system, deduplicate on 'id'.");
            return;
        }

        if (actualCount >= (long)(reportedCount * CountDiscrepancyThreshold)) return;

        var pct = reportedCount > 0 ? (int)((1.0 - (double)actualCount / reportedCount) * 100) : 0;
        var cause = !string.IsNullOrEmpty(filter)
            ? "This may indicate insufficient permissions for the applied $filter. "
              + "Verify the required scopes at https://learn.microsoft.com/graph/permissions-reference"
            : "Items may have been removed during enumeration, "
              + "or eventual consistency lag produced a stale count";
        WriteWarning(
            $"[{resource}] Graph reported {reportedCount} items but only {actualCount} "
            + $"were returned ({pct}% shortfall). {cause}.");
    }

    /// <summary>What a response body turned out to be, once its declared charset and its
    /// byte-order mark are accounted for.</summary>
    internal enum JsonPayloadKind
    {
        /// <summary><see cref="JsonPayload.Json"/> holds the parsed body.</summary>
        Parsed,
        /// <summary>No body at all - a 204, or a 200 that sent nothing.</summary>
        Empty,
        /// <summary>The body declares a content type that is not JSON. Text is the decoded body.</summary>
        NotJson,
        /// <summary>The body declares JSON and does not parse. Text is the decoded body.</summary>
        Malformed,
    }

    /// <summary>
    /// The read, without the reporting. <see cref="Text"/> is the decoded body: as it arrived
    /// for a non-JSON one, with any byte-order mark removed for a body meant to parse.
    /// </summary>
    internal readonly record struct JsonPayload(
        JsonPayloadKind Kind, JsonElement Json, string Text, string? MediaType);

    /// <summary>
    /// Decode and parse a response body: the declared charset, the byte-order mark that
    /// GetString leaves as U+FEFF and the serializer refuses, and whether the body claims to
    /// be JSON at all. Every caller reads a body the same way; what each does about a body it
    /// cannot use differs, and that stays with the caller - this writes to no stream, so a
    /// fan-out worker can call it too.
    /// </summary>
    internal static JsonPayload ReadJsonPayloadCore(
        System.Net.Http.Headers.MediaTypeHeaderValue? declaredType, byte[] bodyBytes)
    {
        var mediaType = declaredType?.MediaType;
        if (bodyBytes.Length == 0)
            return new JsonPayload(JsonPayloadKind.Empty, default, string.Empty, mediaType);

        var text = ResolveEncoding(declaredType?.CharSet).GetString(bodyBytes);
        if (mediaType != null && !mediaType.EndsWith("json", StringComparison.OrdinalIgnoreCase))
            return new JsonPayload(JsonPayloadKind.NotJson, default, text, mediaType);

        text = text.TrimStart('\uFEFF');
        try
        {
            return new JsonPayload(JsonPayloadKind.Parsed,
                JsonSerializer.Deserialize<JsonElement>(text), text, mediaType);
        }
        catch (JsonException)
        {
            return new JsonPayload(JsonPayloadKind.Malformed, default, text, mediaType);
        }
    }

    /// <summary>The response's declared charset, defaulting to UTF-8. GetString strips a
    /// matching BOM, which the byte-level Deserialize refused.</summary>
    internal static Encoding ResolveEncoding(string? charset)
    {
        if (string.IsNullOrEmpty(charset)) return Encoding.UTF8;
        // Any charset .NET will not construct falls back to UTF-8. An unknown or malformed name
        // is an ArgumentException, but a name .NET knows and refuses to build - utf-7, disabled
        // since SYSLIB0001 - is a NotSupportedException, and catching only the first let a
        // response header end the pipeline with a .NET deprecation message.
        try { return Encoding.GetEncoding(charset.Trim('"')); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.UTF8;
        }
    }

    /// <summary>
    /// Release the Graph client, and the output a resumed run holds. Invoked by
    /// <see cref="MgxCmdletCore.Dispose"/> under the Interlocked guard, so this runs exactly once
    /// even when StopProcessing (pipeline-stopping thread) races EndProcessing (pipeline thread).
    /// <para>
    /// The hold is released where the writing ends, which is inside the attempt loop's own
    /// finally - this is the backstop for every way out that never reaches it: a run that stops
    /// between the reconcile and the loop because there is no Graph connection, and a pipeline
    /// stopped from outside.
    /// </para>
    /// </summary>
    protected override void DisposeCore()
    {
        _client?.Dispose();
        ReleaseOutputHold();
    }
}
