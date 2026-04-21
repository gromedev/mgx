using System.Diagnostics;
using System.Management.Automation;
using System.Net;
using System.Text.Json;
using Mgx.Cmdlets.Base;
using Mgx.Engine.Http;
using Mgx.Engine.Models;
using Mgx.Engine.Pagination;
using Polly.CircuitBreaker;

namespace Mgx.Cmdlets.Cmdlets.Delta;

/// <summary>
/// Sync-MgxDelta: Incremental sync via Microsoft Graph delta queries.
/// First run performs a full sync and saves the delta token.
/// Subsequent runs retrieve only items changed since the last sync.
/// Delta state persists across successful completions (unlike CheckpointPath which is ephemeral).
/// </summary>
[Cmdlet(VerbsData.Sync, "MgxDelta")]
[OutputType(typeof(PSObject))]
public class SyncMgxDelta : MgxCmdletBase
{
    [Parameter(Mandatory = true, Position = 0)]
    public string Uri { get; set; } = string.Empty;

    [Parameter(Mandatory = true)]
    public string DeltaPath { get; set; } = string.Empty;

    [Parameter]
    [Alias("Select")]
    public string[]? Property { get; set; }

    [Parameter]
    public string? Filter { get; set; }

    [Parameter]
    [ValidateRange(1, 999)]
    public int Top { get; set; }

    [Parameter]
    public string? OutputFile { get; set; }

    [Parameter]
    public SwitchParameter FullSync { get; set; }

    [Parameter]
    [ValidateSet("v1.0", "beta")]
    [ArgumentCompleter(typeof(ApiVersionCompleter))]
    public string ApiVersion { get; set; } = "v1.0";

    [Parameter]
    public System.Collections.Hashtable? Headers { get; set; }

    private string VersionedBaseUrl => $"{s_graphEndpoint}/{ApiVersion}";

    /// <summary>
    /// Normalize $select for stable comparison: sort, deduplicate, trim, case-insensitive.
    /// Saved to DeltaState.Select so future comparisons are order-independent.
    /// </summary>
    private static string NormalizeSelect(string? s) =>
        string.IsNullOrEmpty(s) ? "" : string.Join(",",
            s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

    protected override void BeginProcessing()
    {
        // Reject absolute URLs (relative paths only)
        if (Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            ThrowTerminatingError(new ErrorRecord(
                new ArgumentException(
                    $"-Uri must be a relative path (e.g., /users/delta), not an absolute URL. "
                    + $"Got: '{Uri}'"),
                "AbsoluteUriNotAllowed", ErrorCategory.InvalidArgument, null));
            return;
        }

        // Fail fast: validate delta file is writable before HTTP calls
        var resolvedDeltaPath = GetUnresolvedProviderPathFromPSPath(DeltaPath);
        DeltaState.ValidateWriteAccess(resolvedDeltaPath);

        // Validate -OutputFile writability before HTTP calls
        if (OutputFile != null)
        {
            var resolvedOutputPath = GetUnresolvedProviderPathFromPSPath(OutputFile);
            if (string.Equals(resolvedDeltaPath, resolvedOutputPath, StringComparison.OrdinalIgnoreCase))
            {
                ThrowTerminatingError(new ErrorRecord(
                    new ArgumentException("-DeltaPath and -OutputFile cannot be the same file."),
                    "DeltaPathOutputFileCollision", ErrorCategory.InvalidArgument, null));
                return;
            }
            DeltaState.ValidateWriteAccess(resolvedOutputPath);
        }

        // Warn if URI doesn't look like a delta endpoint
        if (!Uri.Contains("/delta", StringComparison.OrdinalIgnoreCase))
        {
            WriteWarning(
                $"URI '{Uri}' does not contain '/delta'. Delta queries require a delta endpoint "
                + "(e.g., /users/delta, /groups/delta). The response may not contain a delta token.");
        }
    }

    protected override void ProcessRecord()
    {
        var sw = Stopwatch.StartNew();
        var resolvedDeltaPath = GetUnresolvedProviderPathFromPSPath(DeltaPath);
        var resolvedOutputPath = OutputFile != null
            ? GetUnresolvedProviderPathFromPSPath(OutputFile)
            : null;

        // Handle -FullSync: delete existing delta state
        if (FullSync.IsPresent && File.Exists(resolvedDeltaPath))
        {
            if (DeltaState.Delete(resolvedDeltaPath))
            {
                WriteVerbose("Full sync requested. Deleted existing delta state.");
            }
            else
            {
                WriteWarning($"Full sync requested but could not delete '{DeltaPath}' (file may be locked). " +
                    "The existing delta state will be ignored and a full sync will proceed.");
            }
        }

        // Normalize $select for order-independent comparison
        var normalizedSelect = NormalizeSelect(Property != null ? string.Join(",", Property) : null);
        var currentFilter = Filter;
        string requestUrl;

        // LoadWithResult distinguishes "not found" from "corrupt".
        // Validate delta state BEFORE GetClient() so validation errors
        // are surfaced without requiring a Graph connection.
        var (existingState, loadResult) = DeltaState.LoadWithResult(resolvedDeltaPath);
        if (loadResult == DeltaLoadResult.Corrupt)
        {
            WriteWarning($"Delta state file '{DeltaPath}' is corrupt. Starting full sync.");
        }

        if (existingState != null)
        {
            // Validate graph endpoint matches current session
            if (!string.Equals(existingState.GraphEndpoint, s_graphEndpoint, StringComparison.OrdinalIgnoreCase))
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException(
                        $"Delta state was created against '{existingState.GraphEndpoint}' "
                        + $"but current session is connected to '{s_graphEndpoint}'. "
                        + "Use -FullSync to start fresh, or reconnect to the original endpoint."),
                    "DeltaEndpointMismatch", ErrorCategory.InvalidOperation, null));
                return;
            }

            // Detect resource/URI change between runs
            if (!string.Equals(existingState.Resource, Uri, StringComparison.OrdinalIgnoreCase))
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException(
                        $"Delta state was created for '{existingState.Resource}' but current URI is '{Uri}'. "
                        + "Use -FullSync to start fresh with the new resource."),
                    "DeltaResourceMismatch", ErrorCategory.InvalidOperation, null));
                return;
            }

            // Normalized $select comparison (order-independent, deduplicated)
            var storedSelect = NormalizeSelect(existingState.Select);
            if (!string.Equals(storedSelect, normalizedSelect, StringComparison.OrdinalIgnoreCase))
            {
                WriteWarning(
                    "Property selection changed since last sync "
                    + $"(was: '{existingState.Select ?? "(all)"}', now: '{(string.IsNullOrEmpty(normalizedSelect) ? "(all)" : normalizedSelect)}')."
                    + " Starting full re-sync to capture all selected properties.");
                if (!DeltaState.Delete(resolvedDeltaPath))
                    WriteVerbose($"Could not delete old delta state at '{DeltaPath}' (file may be locked). It will be overwritten.");
                existingState = null;
            }

            // Detect filter change between runs
            if (existingState != null &&
                !string.Equals(existingState.Filter ?? "", currentFilter ?? "", StringComparison.OrdinalIgnoreCase))
            {
                WriteWarning(
                    "Filter changed since last sync "
                    + $"(was: '{existingState.Filter ?? "(none)"}', now: '{currentFilter ?? "(none)"}')."
                    + " Starting full re-sync.");
                if (!DeltaState.Delete(resolvedDeltaPath))
                    WriteVerbose($"Could not delete old delta state at '{DeltaPath}' (file may be locked). It will be overwritten.");
                existingState = null;
            }
        }

        if (existingState != null)
        {
            // SSRF validation: deltaLink is untrusted (from a file on disk)
            var deltaUri = new System.Uri(s_graphEndpoint);
            var validated = NextLinkValidator.Validate(existingState.DeltaLink, deltaUri);
            if (validated == null)
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException(
                        "Delta state contains an invalid or untrusted URL. "
                        + "Use -FullSync to start fresh."),
                    "DeltaLinkValidationFailed", ErrorCategory.SecurityError, null));
                return;
            }

            // Resource path validation: verify the deltaLink's path contains the expected
            // resource. Prevents a tampered delta file from redirecting queries to a different
            // Graph resource (e.g., /me/messages instead of /users/delta).
            var expectedPath = NormalizePath(Uri); // e.g., "/users/delta"
            if (System.Uri.TryCreate(validated, UriKind.Absolute, out var parsedDelta)
                && !parsedDelta.AbsolutePath.Contains(expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException(
                        $"Delta state URL path does not match expected resource '{expectedPath}'. "
                        + "The delta state file may have been tampered with. Use -FullSync to start fresh."),
                    "DeltaLinkPathMismatch", ErrorCategory.SecurityError, null));
                return;
            }

            requestUrl = validated;
            WriteVerbose($"Resuming delta sync from {existingState.LastSync:u} ({existingState.ItemCount} items in previous sync).");
        }
        else
        {
            requestUrl = BuildListUrl(VersionedBaseUrl, Uri,
                new ODataListParams(false, Top, Top > 0 ? Top : 999, Filter, Property, null, null, 0, null));
            WriteVerbose("No existing delta state. Performing full initial sync.");
        }

        // GetClient() after validation so delta state errors surface without Graph connection
        var client = GetClient();
        ExecuteDeltaSync(client, requestUrl, resolvedDeltaPath, resolvedOutputPath,
            normalizedSelect, currentFilter, sw);
    }

    private void ExecuteDeltaSync(
        ResilientGraphClient client,
        string requestUrl,
        string deltaPath,
        string? outputPath,
        string? select,
        string? filter,
        Stopwatch sw)
    {
        bool isFullResync = false;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var headers = BuildRequestHeaders(null, Headers);
                var iterator = new PageIterator(client);
                string? capturedDeltaLink = null;
                long itemCount = 0;

                var enumerable = iterator.StreamAllWithCountAsync(
                    requestUrl,
                    maxItems: 0,
                    onCount: null,
                    headers: headers,
                    onDeltaLink: dl => capturedDeltaLink = dl,
                    cancellationToken: CancellationToken);

                if (outputPath != null)
                {
                    // JSONL output mode
                    var writePath = $"{outputPath}.{Guid.NewGuid():N}.tmp";
                    try
                    {
                        using (var writer = new StreamWriter(writePath, append: false))
                        {
                            var enumerator = enumerable.GetAsyncEnumerator(CancellationToken);
                            try
                            {
                                while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                                {
                                    writer.WriteLine(enumerator.Current.GetRawText());
                                    itemCount++;
                                    DrainClientMessages();

                                    if (itemCount % 500 == 0)
                                        writer.Flush();
                                }
                            }
                            finally
                            {
                                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
                            }
                        }
                        File.Move(writePath, outputPath, overwrite: true);
                    }
                    catch
                    {
                        try { if (File.Exists(writePath)) File.Delete(writePath); } catch { }
                        throw;
                    }
                }
                else
                {
                    // Pipeline output mode
                    var enumerator = enumerable.GetAsyncEnumerator(CancellationToken);
                    try
                    {
                        while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                        {
                            var pso = JsonToPSObject(enumerator.Current);
                            WriteObject(pso);
                            itemCount++;
                            DrainClientMessages();
                        }
                    }
                    finally
                    {
                        enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                }

                // Save delta state ONLY after successful completion (Architect P0).
                // Zero-item responses still save the token (Adversarial P0).
                if (capturedDeltaLink != null)
                {
                    new DeltaState
                    {
                        DeltaLink = capturedDeltaLink,
                        Select = select, // Normalized value for stable future comparisons
                        Filter = filter,
                        Resource = Uri,
                        ItemCount = itemCount,
                        GraphEndpoint = s_graphEndpoint
                    }.Save(deltaPath);
                    WriteVerbose($"Delta state saved to '{deltaPath}'.");
                }
                else
                {
                    WriteWarning("No delta token received from Graph. The endpoint may not support delta queries.");
                }

                DrainClientMessages();
                sw.Stop();

                WriteVerbose(
                    $"Delta sync complete: {itemCount} items in {sw.Elapsed.TotalSeconds:F1}s"
                    + (isFullResync ? " (full re-sync after 410 Gone)" : "")
                    + (outputPath != null ? $". Output: {outputPath}" : "."));

                return;
            }
            catch (GraphServiceException ex) when (
                attempt == 0
                && ex.StatusCode == HttpStatusCode.Gone)
            {
                // 410 Gone: delta token expired (>7 days for directory objects).
                // Delete delta state and restart with full sync.
                // Second attempt builds fresh URL (no delta token), so 410 won't recur.
                DrainClientMessages();
                if (!DeltaState.Delete(deltaPath))
                    WriteVerbose("Could not delete expired delta state (file may be locked). It will be overwritten.");
                isFullResync = true;
                requestUrl = BuildListUrl(VersionedBaseUrl, Uri,
                    new ODataListParams(false, Top, Top > 0 ? Top : 999, Filter, Property, null, null, 0, null));
                WriteWarning(
                    "Delta token expired (HTTP 410 Gone). Starting full re-sync. "
                    + "Tokens expire after ~7 days for directory objects.");
                continue;
            }
            catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
            {
                DrainClientMessages();
                WriteWarning("Delta sync cancelled.");
                return;
            }
            catch (Exception ex) when (ex is GraphServiceException or BrokenCircuitException or HttpRequestException)
            {
                WriteGraphError(ex, Uri);
                return;
            }
            catch (IOException ex)
            {
                DrainClientMessages();
                WriteError(new ErrorRecord(ex, "IOError",
                    ErrorCategory.WriteError, OutputFile));
                return;
            }
            catch (Exception)
            {
                // Drain buffered messages for unexpected exception types
                // (e.g., JsonException, OutOfMemoryException) so diagnostic
                // context is not silently lost.
                DrainClientMessages();
                throw;
            }
        }
    }

}
