using System.Management.Automation;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mgx.Cmdlets.Base;
using Mgx.Engine.Http;
using Mgx.Engine.Models;
using Polly.CircuitBreaker;

namespace Mgx.Cmdlets.Cmdlets.Batch;

/// <summary>
/// Invoke-MgxBatchRequest: Bundle multiple Graph API requests into /$batch calls.
/// Supports GET, POST, PATCH, PUT, DELETE with optional request bodies.
/// Auto-chunks into 20-request batches per Graph API limit.
/// Returns PSObjects with Url, Status, and Body properties per request.
/// Preferred over fan-out (Invoke-MgxRequest) for bulk writes: 3-4x faster due to fewer HTTP round-trips.
///
/// Pipeline input can be:
///   - String URLs (for GET, or combined with -Method/-Body for same method/body on all)
///   - PSObjects with Url, Method, Body properties (for per-item method/body)
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "MgxBatchRequest", SupportsShouldProcess = true)]
[OutputType(typeof(PSObject))]
public class InvokeMgxBatchRequest : MgxCmdletBase
{
    /// <summary>
    /// Graph API URLs to batch. Accepts absolute URLs (https://graph.microsoft.com/v1.0/users/id)
    /// or relative URLs (/users/id). Also accepts PSObjects with Url/Method/Body properties.
    /// </summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    [Alias("Url")]
    public object[] Uri { get; set; } = [];

    /// <summary>
    /// HTTP method for all requests (when piping string URLs). Default: GET.
    /// Ignored when pipeline input contains PSObjects with their own Method property.
    /// </summary>
    [Parameter]
    [ValidateSet("GET", "POST", "PATCH", "PUT", "DELETE")]
    public string Method { get; set; } = "GET";

    /// <summary>
    /// Request body for all requests (when piping string URLs).
    /// Ignored when pipeline input contains PSObjects with their own Body property.
    /// </summary>
    [Parameter]
    public object? Body { get; set; }

    /// <summary>
    /// ConsistencyLevel header added to each individual batch item.
    /// Required when any batch item URL contains $search (Graph advanced query capabilities).
    /// Graph requires this header on each item inside the batch JSON body, not the outer POST.
    /// </summary>
    [Parameter]
    [ArgumentCompleter(typeof(ConsistencyLevelCompleter))]
    public string? ConsistencyLevel { get; set; }

    /// <summary>
    /// Custom headers applied to each individual batch item.
    /// Merged with ConsistencyLevel (if specified). Keys are header names, values are header values.
    /// </summary>
    [Parameter]
    public System.Collections.Hashtable? Headers { get; set; }

    /// <summary>
    /// Throttle priority hint for Graph API. Graph uses this to prioritize requests under throttling pressure.
    /// Valid values: Low, Normal, High. Sets x-ms-throttle-priority header on each batch item.
    /// </summary>
    [Parameter]
    [ValidateSet("Low", "Normal", "High", IgnoreCase = true)]
    [ArgumentCompleter(typeof(ThrottlePriorityCompleter))]
    public string? ThrottlePriority { get; set; }

    /// <summary>
    /// Graph API version. Default: v1.0. Use "beta" for preview endpoints.
    /// </summary>
    [Parameter]
    [ValidateSet("v1.0", "beta")]
    [ArgumentCompleter(typeof(ApiVersionCompleter))]
    public string ApiVersion { get; set; } = "v1.0";

    /// <summary>
    /// Path to a JSONL file where failed batch items (status >= 400) are appended.
    /// Each line contains Url, Method, Body (original request), Status, and Error.
    /// The file can be re-piped to Invoke-MgxBatchRequest for retry:
    ///   Get-Content dead.jsonl | ConvertFrom-Json | Invoke-MgxBatchRequest
    /// </summary>
    [Parameter]
    public string? DeadLetterPath { get; set; }

    private string VersionedBaseUrl => $"{s_graphEndpoint}/{ApiVersion}";

    private readonly List<BatchInput> _collected = [];

    protected override void ProcessRecord()
    {
        foreach (var item in Uri)
        {
            var input = ParsePipelineInput(item);
            if (input != null)
                _collected.Add(input);
        }
    }

    protected override void EndProcessing()
    {
        GraphBatchClient? batchClient = null;
        try
        {
            if (_collected.Count == 0)
            {
                base.EndProcessing();
                return;
            }

            // Resolve dead-letter path early (before network calls)
            string? resolvedDeadLetterPath = DeadLetterPath != null
                ? GetUnresolvedProviderPathFromPSPath(DeadLetterPath)
                : null;

            // Validate: $search in any URL requires ConsistencyLevel
            var hasSearch = _collected.Any(c =>
                c.Url.Contains("$search", StringComparison.OrdinalIgnoreCase));
            if (hasSearch && string.IsNullOrEmpty(ConsistencyLevel))
            {
                ThrowTerminatingError(new ErrorRecord(
                    new ArgumentException(
                        "One or more batch URLs contain $search, which requires -ConsistencyLevel eventual. "
                        + "Without it, Graph returns empty or incomplete results."),
                    "ConsistencyLevelRequired", ErrorCategory.InvalidArgument, null));
                return;
            }

            // ShouldProcess gate: only for batches containing write operations
            var writeOps = _collected.Where(c =>
                !string.Equals(c.Method, "GET", StringComparison.OrdinalIgnoreCase)).ToList();

            if (writeOps.Count > 0)
            {
                string target;
                if (writeOps.All(o => string.Equals(o.Method, writeOps[0].Method, StringComparison.OrdinalIgnoreCase)))
                {
                    target = $"{writeOps[0].Method} {_collected.Count} requests via $batch";
                }
                else
                {
                    var breakdown = writeOps
                        .GroupBy(o => o.Method.ToUpperInvariant())
                        .OrderByDescending(g => g.Count())
                        .Select(g => $"{g.Count()} {g.Key}");
                    target = $"{_collected.Count} requests ({string.Join(", ", breakdown)}) via $batch";
                }

                if (!ShouldProcess(target, "Send batch"))
                    return;
            }

            var client = GetClient();
            var mergedHeaders = Headers != null ? new System.Collections.Hashtable(Headers) : null;
            if (!string.IsNullOrEmpty(ThrottlePriority))
            {
                mergedHeaders ??= new System.Collections.Hashtable();
                mergedHeaders["x-ms-throttle-priority"] = ThrottlePriority;
            }
            var itemHeaders = BuildRequestHeaders(ConsistencyLevel, mergedHeaders);
            batchClient = new GraphBatchClient(client, VersionedBaseUrl,
                s_clientOptions.MaxRetryAfterSeconds, s_clientOptions.BatchChunkConcurrency,
                s_clientOptions.NoRateLimit ? 0 : s_clientOptions.BatchItemsPerSecond)
            {
                VerboseWriter = msg => WriteVerbose(msg),
                ItemHeaders = itemHeaders
            };

            // Convert to BatchOperation list
            var operations = _collected.Select(input =>
            {
                var relativeUrl = NormalizeToRelativeUrl(input.Url);
                JsonElement? body = null;
                if (input.Body != null)
                {
                    var json = InvokeMgxRequest.SerializeBody(input.Body);
                    body = JsonSerializer.Deserialize<JsonElement>(json);
                }
                return new BatchOperation(relativeUrl, input.Method, body);
            }).ToList();

            var batchResult = batchClient.ExecuteBatchIndexedAsync(operations, CancellationToken)
                .GetAwaiter().GetResult();

            var results = batchResult.Results;
            var telemetry = batchResult.Telemetry;

            // Output all results as PSObjects (success and failure)
            for (int i = 0; i < results.Count; i++)
            {
                var (_, item) = results[i];
                var input = _collected[i];

                var pso = new PSObject();
                pso.TypeNames.Insert(0, "Mgx.BatchResult");
                pso.Properties.Add(new PSNoteProperty("Url", input.Url));
                pso.Properties.Add(new PSNoteProperty("Method", input.Method));
                pso.Properties.Add(new PSNoteProperty("Status", item.Status));

                if (item.Body.HasValue && item.Body.Value.ValueKind != JsonValueKind.Null)
                {
                    pso.Properties.Add(new PSNoteProperty("Body", JsonToPSObject(item.Body.Value)));
                }
                else
                {
                    pso.Properties.Add(new PSNoteProperty("Body", null));
                }

                WriteObject(pso);
            }

            // Emit errors for failed items (enables -ErrorAction Stop, populates $Error)
            for (int i = 0; i < results.Count; i++)
            {
                var (_, item) = results[i];
                if (item.Status >= 400)
                {
                    var input = _collected[i];
                    var graphMessage = TryExtractBatchErrorMessage(item);
                    var errorMessage = graphMessage != null
                        ? $"{input.Method} {input.Url}: {graphMessage}"
                        : $"HTTP {item.Status} for {input.Method} {input.Url}";
                    var ex = new InvalidOperationException(errorMessage);
                    WriteError(new ErrorRecord(ex, "BatchItemError",
                        MapStatusToCategory((HttpStatusCode)item.Status), input.Url));
                }
            }

            // Write failed items to dead-letter file (append mode)
            if (resolvedDeadLetterPath != null)
            {
                var failedCount = 0;
                try
                {
                    using var writer = new StreamWriter(resolvedDeadLetterPath, append: true);
                    for (int i = 0; i < results.Count; i++)
                    {
                        var (_, item) = results[i];
                        if (item.Status < 400) continue;

                        var input = _collected[i];
                        var deadLetter = new JsonObject
                        {
                            ["Timestamp"] = DateTime.UtcNow.ToString("o"),
                            ["Url"] = input.Url,
                            ["Method"] = input.Method,
                            ["Status"] = item.Status,
                        };

                        if (input.Body != null)
                        {
                            var bodyJson = InvokeMgxRequest.SerializeBody(input.Body);
                            var bodyNode = JsonNode.Parse(bodyJson);
                            RedactSensitiveFields(bodyNode);
                            deadLetter["Body"] = bodyNode;
                        }

                        var errorMsg = TryExtractBatchErrorMessage(item);
                        if (errorMsg != null)
                            deadLetter["Error"] = errorMsg;

                        writer.WriteLine(deadLetter.ToJsonString());
                        failedCount++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    WriteWarning($"Failed to write dead-letter file '{resolvedDeadLetterPath}': {ex.Message}");
                }

                if (failedCount > 0)
                    WriteVerbose($"Wrote {failedCount} failed items to dead-letter file: {resolvedDeadLetterPath}");
            }

            // Structured telemetry summary
            WriteBatchTelemetry(telemetry);
        }
        catch (Exception ex) when (ex is GraphServiceException or BrokenCircuitException or HttpRequestException)
        {
            WriteGraphError(ex, null);
        }
        catch (JsonException ex)
        {
            WriteError(new ErrorRecord(ex, "BatchSerializationError",
                ErrorCategory.InvalidData, null));
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            WriteWarning("Batch request cancelled by user.");
        }
        finally
        {
            // Drain verbose messages even on exception so retry/throttle history is visible
            DrainClientMessages();
            batchClient?.DrainVerboseMessages();
            base.EndProcessing();
        }
    }

    /// <summary>
    /// Parse pipeline input into a BatchInput. Supports:
    /// - String: use as URL with shared -Method/-Body parameters
    /// - PSObject with Url property: use per-item Url/Method/Body
    /// </summary>
    private BatchInput? ParsePipelineInput(object item)
    {
        if (item is string url)
        {
            return new BatchInput(url, Method, Body);
        }

        if (item is PSObject pso)
        {
            // Check if it's a structured batch input (has Url property)
            var urlProp = pso.Properties["Url"]?.Value?.ToString();
            if (urlProp != null)
            {
                var method = (pso.Properties["Method"]?.Value?.ToString() ?? Method).ToUpperInvariant();
                if (method is not ("GET" or "POST" or "PATCH" or "PUT" or "DELETE"))
                {
                    WriteWarning($"Skipping invalid HTTP method '{method}' for URL: {urlProp}");
                    return null;
                }
                var body = pso.Properties["Body"]?.Value;
                return new BatchInput(urlProp, method, body);
            }

            // If it has a BaseObject that's a string, treat as URL
            if (pso.BaseObject is string baseStr)
            {
                return new BatchInput(baseStr, Method, Body);
            }
        }

        WriteWarning($"Skipping unrecognized pipeline input: {item}");
        return null;
    }

    /// <summary>
    /// Converts an absolute Graph URL to a relative path for /$batch.
    /// </summary>
    private string NormalizeToRelativeUrl(string url)
    {
        if (url.StartsWith('/'))
            return url;

        if (url.StartsWith(VersionedBaseUrl, StringComparison.OrdinalIgnoreCase))
            return url[VersionedBaseUrl.Length..];

        if (System.Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var path = uri.PathAndQuery;
            string[] knownPrefixes = ["/v1.0/", "/beta/"];
            foreach (var prefix in knownPrefixes)
            {
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var version = prefix.Trim('/');
                    if (!string.Equals(ApiVersion, version, StringComparison.OrdinalIgnoreCase))
                        WriteWarning($"URL contains {prefix} but -ApiVersion is '{ApiVersion}'. The batch will use {ApiVersion}.");
                    return path[(prefix.Length - 1)..]; // -1 to keep leading slash
                }
            }
            return path;
        }

        WriteWarning($"Could not normalize URL to relative path: {url}");
        return url;
    }

    /// <summary>
    /// Redact sensitive fields (passwordProfile, credentials, secrets) from a JSON body
    /// before writing to the dead-letter file. Modifies the node in-place.
    /// </summary>
    internal static void RedactSensitiveFields(JsonNode? node)
    {
        if (node is JsonArray rootArr)
        {
            foreach (var item in rootArr)
                if (item is JsonObject arrObj)
                    RedactSensitiveFields(arrObj);
            return;
        }
        if (node is not JsonObject obj) return;
        foreach (var key in obj.Select(p => p.Key).ToArray())
        {
            if (key.Equals("passwordProfile", StringComparison.OrdinalIgnoreCase)
                || key.Equals("password", StringComparison.OrdinalIgnoreCase)
                || key.Equals("secretText", StringComparison.OrdinalIgnoreCase)
                || key.Equals("keyCredentials", StringComparison.OrdinalIgnoreCase)
                || key.Equals("passwordCredentials", StringComparison.OrdinalIgnoreCase)
                || key.Equals("clientSecret", StringComparison.OrdinalIgnoreCase)
                || key.Equals("appPassword", StringComparison.OrdinalIgnoreCase)
                || key.Equals("clientAssertion", StringComparison.OrdinalIgnoreCase))
            {
                obj[key] = "***REDACTED***";
            }
            else if (obj[key] is JsonObject child)
            {
                RedactSensitiveFields(child);
            }
            else if (obj[key] is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is JsonObject arrObj)
                        RedactSensitiveFields(arrObj);
                }
            }
        }
    }

    private static string? TryExtractBatchErrorMessage(GraphBatchResponseItem item)
    {
        if (!item.Body.HasValue) return null;
        try
        {
            var body = item.Body.Value;
            if (body.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var c) ? c.GetString() : null;
                var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                if (!string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(message))
                    return $"{code}: {message}";
                return code ?? message;
            }
        }
        catch (InvalidOperationException) { }
        return null;
    }

    private void WriteBatchTelemetry(BatchTelemetry telemetry)
    {
        // Propagate per-item 429 counts to session telemetry
        if (telemetry.ThrottleEncounters > 0)
            MgxTelemetryCollector.Current.RecordBatchItemThrottles(telemetry.ThrottleEncounters);

        // Always emit verbose summary with timing breakdown
        var elapsedSec = telemetry.TotalElapsedMs / 1000.0;
        var throughput = telemetry.TotalElapsedMs > 0 ? telemetry.TotalRequests / elapsedSec : 0;
        var summary = $"Batch: {telemetry.Succeeded} succeeded, {telemetry.Failed} failed out of {telemetry.TotalRequests} requests in {elapsedSec:F1}s ({throughput:F1}/sec).";
        if (telemetry.ItemRetries > 0)
            summary += $" Item retries: {telemetry.ItemRetries}.";
        if (telemetry.ThrottleEncounters > 0)
            summary += $" Throttle (429) encounters: {telemetry.ThrottleEncounters}.";
        if (telemetry.BatchLevelRetries > 0)
            summary += $" Batch-level retries: {telemetry.BatchLevelRetries}.";
        if (telemetry.TotalRetryDelayMs > 0)
            summary += $" Time in retry delays: {telemetry.TotalRetryDelayMs / 1000.0:F1}s.";
        WriteVerbose(summary);

        // Warn if any items failed after all retry attempts
        if (telemetry.Failed > 0)
        {
            WriteWarning(
                $"{telemetry.Failed} of {telemetry.TotalRequests} batch items failed after all retry attempts. "
                + "Check $Error for details on each failed item.");
        }
    }

    private sealed record BatchInput(string Url, string Method, object? Body);
}
