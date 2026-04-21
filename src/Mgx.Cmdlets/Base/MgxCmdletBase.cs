using System.Collections.Concurrent;
using System.Globalization;
using System.Management.Automation;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mgx.Engine.Http;
using Mgx.Engine.Models;
using Polly.CircuitBreaker;

namespace Mgx.Cmdlets.Base;

/// <summary>
/// Lightweight base class for Mgx cmdlets that need Graph client access.
/// Provides auth, client lifecycle, and JSON-to-PSObject conversion.
/// Used by Invoke-MgxRequest and Invoke-MgxBatchRequest.
/// </summary>
public abstract class MgxCmdletBase : PSCmdlet, IDisposable
{
    private ResilientGraphClient? _client;
    private CancellationTokenSource _cts = new();
    private int _disposed; // 0 = not disposed, 1 = disposed (Interlocked for thread safety)

    private static readonly object s_initLock = new();
    private static HttpClient? s_graphHttpClient;
    private static bool s_ownsHttpClient; // false when using SDK fallback (don't dispose SDK's client)
    private static string? s_cachedTenantId;
    internal static volatile string s_graphEndpoint = "https://graph.microsoft.com";
    internal static volatile ResilientGraphClientOptions s_clientOptions = ResilientGraphClientOptions.Default;

    // Regex gate for DateTime parsing: requires YYYY-MM-DDT prefix.
    // Prevents false positives on version strings, GUIDs, numeric IDs.
    private static readonly Regex Iso8601Pattern = new(
        @"^\d{4}-\d{2}-\d{2}[T ]", RegexOptions.Compiled);

    protected CancellationToken CancellationToken => _cts.Token;

    /// <summary>
    /// Base URL for Graph API requests (e.g., "https://graph.microsoft.com/v1.0").
    /// Respects sovereign clouds via GraphSession environment.
    /// </summary>
    protected string GraphBaseUrl => $"{s_graphEndpoint}/v1.0";

    /// <summary>
    /// Get the resilient Graph client with auth-only HttpClient (no Kiota retry/redirect).
    /// Detects tenant changes and rebuilds the client when needed.
    /// </summary>
    protected ResilientGraphClient GetClient()
    {
        if (_client != null) return _client;

        var currentTenantId = GetCurrentTenantId(WriteVerbose);
        if (string.IsNullOrEmpty(currentTenantId))
        {
            ThrowTerminatingError(new ErrorRecord(
                new InvalidOperationException("Not connected to Microsoft Graph. Run Connect-MgGraph first."),
                "NotConnected",
                ErrorCategory.ConnectionError,
                null));
            return null!;
        }

        // Lock protects concurrent runspaces from racing on static client/endpoint init.
        // Capture locals inside lock to prevent TOCTOU race: another thread could enter
        // the lock and replace/dispose s_graphHttpClient between lock exit and usage.
        HttpClient httpClient;
        ResilientGraphClientOptions clientOptions;
        lock (s_initLock)
        {
            if (s_graphHttpClient == null || s_cachedTenantId != currentTenantId)
            {
                // Reset-before-Build is intentional here (unlike TryPreInitHttpClient which
                // builds first then resets). GetClient() has a fallback path (SDK client), so
                // resetting circuit breaker state from the old tenant before attempting to build
                // is safe: if BuildCleanHttpClient fails, GetSdkHttpClientFallback provides a
                // working client. If both fail, ThrowTerminatingError is the correct response.
                ResiliencePipelineFactory.Reset();
                // Schedule delayed disposal: in-flight ResilientGraphClient instances
                // may still hold a reference to the old client via their constructor
                ScheduleDelayedHttpClientDispose(s_graphHttpClient);
                s_graphHttpClient = BuildCleanHttpClient();
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

                s_cachedTenantId = currentTenantId;
                s_graphEndpoint = GetGraphEndpoint(WriteWarning, WriteVerbose) ?? "https://graph.microsoft.com";
            }
            httpClient = s_graphHttpClient!;
            clientOptions = s_clientOptions;
        }

        _client = new ResilientGraphClient(httpClient, clientOptions);
        _client.BodyReadTimeout = TimeSpan.FromSeconds(clientOptions.AttemptTimeoutSeconds);
        _client.VerboseWriter = msg => WriteVerbose(msg);
        _client.WarningWriter = msg => WriteWarning(msg);
        return _client;
    }

    private static string? GetCurrentTenantId(Action<string>? verbose)
    {
        try
        {
            using var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);
            ps.AddCommand("Get-MgContext");
            var results = ps.Invoke();
            if (ps.HadErrors || results.Count == 0) return null;
            return results[0].Properties["TenantId"]?.Value?.ToString();
        }
        catch (Exception ex)
        {
            verbose?.Invoke($"Failed to get tenant ID: {ex.Message}");
            return null;
        }
    }

    internal static string? GetGraphEndpoint(Action<string>? warn, Action<string>? verbose)
    {
        try
        {
            var graphSessionType = FindType("Microsoft.Graph.PowerShell.Authentication.GraphSession");
            if (graphSessionType == null) return null;

            var instance = graphSessionType.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (instance == null) return null;

            var env = instance.GetType().GetProperty("Environment")?.GetValue(instance);
            if (env == null) return null;

            return env.GetType().GetProperty("GraphEndpoint")?.GetValue(env)?.ToString();
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
    private HttpClient? BuildCleanHttpClient() => BuildCleanHttpClient(WriteWarning, WriteVerbose);

    private static HttpClient? BuildCleanHttpClient(Action<string> warn, Action<string> verbose)
    {
        try
        {
            var graphSessionType = FindType("Microsoft.Graph.PowerShell.Authentication.GraphSession");
            if (graphSessionType == null) return null;

            var instance = graphSessionType.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
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
                Timeout = TimeSpan.FromSeconds(s_clientOptions.TotalTimeoutSeconds + 60)
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
        // Quick early exit: avoid PS runspace overhead on hot path (idempotent Enable calls).
        // Volatile.Read ensures ARM64 memory visibility - without it, non-volatile statics
        // read outside a lock have no acquire barrier and may return stale values.
        if (Volatile.Read(ref s_graphHttpClient) != null &&
            Volatile.Read(ref s_cachedTenantId) != null) return;

        var tenantId = GetCurrentTenantId(verbose);
        if (string.IsNullOrEmpty(tenantId)) return;

        lock (s_initLock)
        {
            if (s_graphHttpClient != null && s_cachedTenantId == tenantId) return;

            // Build first. Only dispose/reset after we have a confirmed replacement.
            // If BuildCleanHttpClient fails, the existing s_graphHttpClient must remain valid
            // and callers fall back via GetClient() on first use.
            var client = BuildCleanHttpClient(warn, verbose);
            if (client == null) return; // BuildCleanHttpClient already warned with ex.Message

            ResiliencePipelineFactory.Reset();
            ScheduleDelayedHttpClientDispose(s_graphHttpClient);
            s_graphHttpClient = client;
            s_ownsHttpClient = true;
            s_cachedTenantId = tenantId;
            s_graphEndpoint = GetGraphEndpoint(warn, verbose) ?? "https://graph.microsoft.com";
        }
    }

    private HttpClient? GetSdkHttpClientFallback()
    {
        try
        {
            var graphSessionType = FindType("Microsoft.Graph.PowerShell.Authentication.GraphSession");
            if (graphSessionType == null) return null;

            var instance = graphSessionType.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (instance == null) return null;

            var httpClient = instance.GetType().GetProperty("GraphHttpClient")
                ?.GetValue(instance) as HttpClient;
            if (httpClient != null) return httpClient;

            // Use detected endpoint instead of hardcoded graph.microsoft.com
            // (supports sovereign clouds: GCC-High, DoD, China)
            var endpoint = GetGraphEndpoint(WriteWarning, WriteVerbose) ?? "https://graph.microsoft.com";

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

            return instance.GetType().GetProperty("GraphHttpClient")
                ?.GetValue(instance) as HttpClient;
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
    private static readonly ConcurrentDictionary<string, Type> s_typeCache = new();

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
            ScheduleDelayedHttpClientDispose(s_graphHttpClient);
            s_graphHttpClient = null;
            s_cachedTenantId = null;
            ResiliencePipelineFactory.Reset();
        }
    }

    /// <summary>
    /// Disposes an HttpClient after a delay. In-flight ResilientGraphClient instances
    /// may still hold a reference to the old client, so we wait for the total timeout
    /// window to ensure all in-flight requests complete before disposing.
    /// Same pattern as ResiliencePipelineFactory.ScheduleDelayedDispose for rate limiters.
    /// </summary>
    private static void ScheduleDelayedHttpClientDispose(HttpClient? client)
    {
        // Only dispose clients we own. SDK fallback clients are owned by GraphSession;
        // disposing them would break the SDK's own HTTP pipeline.
        if (client == null || !s_ownsHttpClient) return;
        var delaySeconds = s_clientOptions.TotalTimeoutSeconds;
        _ = Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ContinueWith(_ =>
        {
            try { client.Dispose(); } catch { /* best-effort cleanup */ }
        }, TaskScheduler.Default);
    }

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
    }

    internal static void SetClientOptions(ResilientGraphClientOptions options)
    {
        s_clientOptions = options ?? ResilientGraphClientOptions.Default;
    }

    /// <summary>
    /// Convert a JsonElement to a PSObject with all properties preserved.
    /// </summary>
    protected static PSObject JsonToPSObject(JsonElement element)
    {
        var pso = new PSObject();

        // Non-Object elements (string, number, etc.) must wrap value in a property
        if (element.ValueKind != JsonValueKind.Object)
        {
            pso.Properties.Add(new PSNoteProperty("Value", ConvertJsonValue(element)));
            return pso;
        }

        string? odataType = null;

        foreach (var prop in element.EnumerateObject())
        {
            // Preserve @odata.type as ODataType (critical for polymorphic queries)
            if (prop.Name.Equals("@odata.type", StringComparison.OrdinalIgnoreCase))
            {
                odataType = prop.Value.GetString();
                if (odataType != null)
                    pso.Properties.Add(new PSNoteProperty("ODataType", odataType));
                continue;
            }

            // Strip other @odata.* metadata (nextLink, context, count)
            if (prop.Name.StartsWith("@odata.", StringComparison.OrdinalIgnoreCase))
                continue;

            pso.Properties.Add(new PSNoteProperty(prop.Name, ConvertJsonValue(prop.Value)));
        }

        // Decorate with PSTypeName from @odata.type for Format.ps1xml / polymorphic dispatch
        // e.g., "#microsoft.graph.user" -> "Mgx.User"
        if (odataType != null)
        {
            var psTypeName = MapODataTypeToPSTypeName(odataType);
            if (psTypeName != null)
                pso.TypeNames.Insert(0, psTypeName);
        }

        return pso;
    }

    private static object? ConvertJsonValue(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var str = element.GetString();
            if (str != null && Iso8601Pattern.IsMatch(str) &&
                DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var dto))
                return dto.UtcDateTime;
            return str;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.Object ? (object?)JsonToPSObject(item) : ConvertJsonValue(item))
                .ToArray(),
            JsonValueKind.Object => JsonToPSObject(element),
            _ => element.GetRawText()
        };
    }

    private static string? MapODataTypeToPSTypeName(string odataType)
    {
        const string prefix = "#microsoft.graph.";
        if (!odataType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var typePart = odataType.Substring(prefix.Length);
        if (string.IsNullOrEmpty(typePart))
            return null;

        var pascalName = char.ToUpperInvariant(typePart[0]) + typePart.Substring(1);
        return $"Mgx.{pascalName}";
    }

    #region Shared URL and header builders

    protected static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        return path.StartsWith('/') ? path : $"/{path}";
    }

    protected static Dictionary<string, string>? BuildRequestHeaders(
        string? consistencyLevel, System.Collections.Hashtable? extraHeaders)
    {
        Dictionary<string, string>? headers = null;

        // Apply extraHeaders first so dedicated parameters can override
        if (extraHeaders != null)
        {
            headers = new Dictionary<string, string>();
            foreach (var key in extraHeaders.Keys)
                headers[key.ToString()!] = extraHeaders[key]?.ToString() ?? string.Empty;
        }

        // Dedicated -ConsistencyLevel parameter always wins over -Headers key
        if (!string.IsNullOrEmpty(consistencyLevel))
        {
            headers ??= new Dictionary<string, string>();
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
    {
        var baseUrl = $"{versionedBaseUrl}{NormalizePath(relativeUri)}";
        var queryParams = new List<string>();

        if (!p.NoPageSize)
        {
            var effectiveTop = p.Top > 0 ? Math.Min(p.Top, p.PageSize) : p.PageSize;
            queryParams.Add($"$top={effectiveTop}");
        }

        if (!string.IsNullOrEmpty(p.Filter))
            queryParams.Add($"$filter={Uri.EscapeDataString(p.Filter)}");

        if (p.Property is { Length: > 0 })
            queryParams.Add($"$select={Uri.EscapeDataString(string.Join(",", p.Property))}");

        if (p.Sort is { Length: > 0 })
            queryParams.Add($"$orderby={Uri.EscapeDataString(string.Join(",", p.Sort))}");

        if (!string.IsNullOrEmpty(p.Search))
        {
            // Graph API requires $search values wrapped in double quotes: $search="displayName:John"
            var searchValue = p.Search;
            if (!searchValue.StartsWith('"') || !searchValue.EndsWith('"'))
                searchValue = $"\"{searchValue}\"";
            queryParams.Add($"$search={Uri.EscapeDataString(searchValue)}");
        }

        if (p.Skip > 0)
            queryParams.Add($"$skip={p.Skip}");

        if (p.ExpandProperty is { Length: > 0 })
            queryParams.Add($"$expand={Uri.EscapeDataString(string.Join(",", p.ExpandProperty))}");

        // $count=true: required explicitly via -CountVariable, or implicitly when $search is used
        // (Graph advanced query capabilities require $count=true alongside $search)
        if (p.IncludeCount || !string.IsNullOrEmpty(p.Search))
            queryParams.Add("$count=true");

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

    protected void WriteBetaHintIfApplicable(HttpStatusCode statusCode, string apiVersion)
    {
        if (statusCode == HttpStatusCode.NotFound &&
            string.Equals(apiVersion, "v1.0", StringComparison.OrdinalIgnoreCase))
        {
            WriteWarning("This endpoint may only be available in beta. Retry with -ApiVersion beta.");
        }
    }

    protected static ErrorCategory MapStatusToCategory(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.NotFound => ErrorCategory.ObjectNotFound,
        HttpStatusCode.Unauthorized => ErrorCategory.AuthenticationError,
        HttpStatusCode.Forbidden => ErrorCategory.PermissionDenied,
        HttpStatusCode.BadRequest => ErrorCategory.InvalidArgument,
        HttpStatusCode.Conflict => ErrorCategory.ResourceExists,
        (HttpStatusCode)429 => ErrorCategory.LimitsExceeded,
        _ => ErrorCategory.NotSpecified
    };

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
                    WriteBetaHintIfApplicable(gex.StatusCode, apiVersion);
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
    // 10% tolerance prevents noise from eventual consistency lag;
    // 100-item floor avoids false alarms on small collections.
    protected const double CountDiscrepancyThreshold = 0.9;
    protected const long CountDiscrepancyMinItems = 100;

    protected void WriteCountDiscrepancyWarning(
        string resource, long reportedCount, long actualCount, string? filter)
    {
        if (reportedCount < CountDiscrepancyMinItems) return;
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

    protected override void StopProcessing()
    {
        _cts.Cancel();
        Dispose();
    }

    protected override void EndProcessing()
    {
        Dispose();
    }

    public void Dispose()
    {
        // Thread-safe: StopProcessing (pipeline-stopping thread) and EndProcessing (pipeline thread)
        // can race. Interlocked ensures only one thread enters the dispose body.
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
        {
            _cts.Cancel();
            _cts.Dispose();
            _client?.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
