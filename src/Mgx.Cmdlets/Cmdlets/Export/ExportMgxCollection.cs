using System.Diagnostics;
using System.Management.Automation;
using System.Net;
using Mgx.Cmdlets.Base;
using Mgx.Engine.Http;
using Mgx.Engine.Models;
using Mgx.Engine.Pagination;
using Polly.CircuitBreaker;

namespace Mgx.Cmdlets.Cmdlets.Export;

/// <summary>
/// Export-MgxCollection: Stream paginated Graph API results directly to a JSONL file.
/// One JSON object per line; no PSObject conversion, minimal memory pressure.
/// Supports checkpoint/resume for interrupted exports.
/// Consumer owns checkpoint lifecycle: saves at page boundaries and mid-page flushes
/// to prevent duplicate items on crash resume (H6 dedup fix).
/// </summary>
[Cmdlet(VerbsData.Export, "MgxCollection", SupportsShouldProcess = true)]
[OutputType(typeof(PSObject))]
public class ExportMgxCollection : MgxCmdletBase
{
    [Parameter(Mandatory = true, Position = 0)]
    [Alias("Resource")]
    public string Uri { get; set; } = string.Empty;

    [Parameter(Mandatory = true)]
    public string OutputFile { get; set; } = string.Empty;

    [Parameter]
    [Alias("Select")]
    public string[]? Property { get; set; }

    [Parameter]
    public string? Filter { get; set; }

    [Parameter]
    [Alias("Expand")]
    public string[]? ExpandProperty { get; set; }

    [Parameter]
    public string? Search { get; set; }

    [Parameter]
    [Alias("OrderBy")]
    public string[]? Sort { get; set; }

    [Parameter]
    [ValidateRange(0, int.MaxValue)]
    public int Skip { get; set; }

    [Parameter]
    [ValidateRange(0, int.MaxValue)]
    public int Top { get; set; }

    [Parameter]
    public SwitchParameter All { get; set; }

    [Parameter]
    [ValidateRange(1, 999)]
    public int PageSize { get; set; } = 999;

    [Parameter]
    [ArgumentCompleter(typeof(ConsistencyLevelCompleter))]
    public string? ConsistencyLevel { get; set; }

    [Parameter]
    public System.Collections.Hashtable? Headers { get; set; }

    [Parameter]
    [ValidateSet("v1.0", "beta")]
    [ArgumentCompleter(typeof(ApiVersionCompleter))]
    public string ApiVersion { get; set; } = "v1.0";

    [Parameter]
    public string? CheckpointPath { get; set; }

    [Parameter]
    public SwitchParameter NoPageSize { get; set; }

    private string VersionedBaseUrl => $"{s_graphEndpoint}/{ApiVersion}";

    protected override void BeginProcessing()
    {
        // $search requires ConsistencyLevel: eventual. Error if missing (data loss otherwise)
        if (!string.IsNullOrEmpty(Search) && string.IsNullOrEmpty(ConsistencyLevel))
        {
            ThrowTerminatingError(new ErrorRecord(
                new ArgumentException(
                    "-Search requires -ConsistencyLevel eventual. Without it, Graph returns incomplete results."),
                "ConsistencyLevelRequired", ErrorCategory.InvalidArgument, Search));
            return;
        }

        // $count=true requires ConsistencyLevel: eventual on directory endpoints;
        // auto-add when -Filter is used (enables count discrepancy detection)
        if (!string.IsNullOrEmpty(Filter) && string.IsNullOrEmpty(ConsistencyLevel))
        {
            ConsistencyLevel = "eventual";
            WriteVerbose("Auto-adding ConsistencyLevel:eventual header (required by -Filter for $count=true).");
        }
    }

    protected override void ProcessRecord()
    {
        var sw = Stopwatch.StartNew();

        // Resolve paths (before requiring Graph connection, so -WhatIf works without auth)
        var outputPath = GetUnresolvedProviderPathFromPSPath(OutputFile);
        var cpPath = CheckpointPath != null
            ? GetUnresolvedProviderPathFromPSPath(CheckpointPath)
            : null;

        // Validate CheckpointPath != OutputFile (would corrupt both files)
        if (cpPath != null && string.Equals(cpPath, outputPath, StringComparison.OrdinalIgnoreCase))
        {
            ThrowTerminatingError(new ErrorRecord(
                new ArgumentException(
                    "-CheckpointPath and -OutputFile must be different files. Using the same file would corrupt both the checkpoint and the export data."),
                "CheckpointOutputCollision", ErrorCategory.InvalidArgument, CheckpointPath));
            return;
        }

        // Determine max items: -All = unlimited (overrides -Top), -Top N = N, neither = single page
        int maxItems;
        bool defaultedToPageSize = false;
        if (All.IsPresent)
            maxItems = 0; // unlimited; -All always overrides -Top
        else if (Top > 0)
            maxItems = Top;
        else
        {
            maxItems = PageSize; // single page worth
            defaultedToPageSize = true;
        }

        // Checkpoint safety: if checkpoint exists but output file was deleted,
        // the checkpoint is invalid (items before checkpoint are lost).
        // Delete checkpoint and start fresh.
        if (cpPath != null && File.Exists(cpPath) && !File.Exists(outputPath))
        {
            WriteWarning("Checkpoint found but output file is missing. Deleting stale checkpoint and starting fresh.");
            PaginationCheckpoint.Delete(cpPath);
        }

        // ShouldProcess check (before requiring Graph connection, so -WhatIf works without auth)
        var initialAppend = cpPath != null && File.Exists(cpPath) && File.Exists(outputPath);
        var action = initialAppend ? "Append JSONL data" : "Export JSONL data";
        if (!ShouldProcess(outputPath, action))
            return;

        // Init client after ShouldProcess (populates s_graphEndpoint for sovereign clouds)
        var client = GetClient();

        // Track whether $count=true was auto-added (not user-requested).
        // If the endpoint rejects it with 400, retry without.
        bool countAutoAdded = !string.IsNullOrEmpty(Filter);
        bool includeAutoCount = countAutoAdded;
        bool suppressTop = false;

        // If checkpoint was saved during a previous retry (URL without $count=true),
        // match the checkpoint's URL to avoid mismatch and data loss on resume
        if (countAutoAdded && cpPath != null && File.Exists(cpPath) && File.Exists(outputPath))
        {
            var existingCp = PaginationCheckpoint.Load(cpPath);
            if (existingCp?.Resource != null && !existingCp.Resource.Contains("$count=true"))
                includeAutoCount = false;
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                // Recalculate append (checkpoint may have been invalidated on previous attempt)
                var append = cpPath != null && File.Exists(cpPath) && File.Exists(outputPath);

                var url = BuildUrl(includeAutoCount, suppressTop);
                var headers = BuildHeaders();

                // Load checkpoint and compute resume state (consumer owns checkpoint lifecycle)
                ResumeState? resume = null;
                long resumedItemCount = 0;
                string currentFetchUrl = url;

                if (cpPath != null && append)
                {
                    var checkpoint = PaginationCheckpoint.Load(cpPath);
                    if (checkpoint != null)
                    {
                        if (checkpoint.NextLink == null)
                        {
                            // Completion marker: previous export finished, checkpoint is stale
                            WriteVerbose("Checkpoint indicates previous export completed. Deleting stale checkpoint.");
                            PaginationCheckpoint.Delete(cpPath);
                            append = false;
                        }
                        else if (string.Equals(checkpoint.Resource, url, StringComparison.Ordinal))
                        {
                            // Validate NextLink (SSRF protection)
                            var expectedHost = new System.Uri(url);
                            var validatedLink = NextLinkValidator.Validate(checkpoint.NextLink, expectedHost);
                            if (validatedLink != null
                                && checkpoint.ItemsCollected >= 0
                                && checkpoint.PageItemsAlreadyWritten >= 0)
                            {
                                resume = new ResumeState(
                                    validatedLink,
                                    checkpoint.PageItemsAlreadyWritten,
                                    checkpoint.ItemsCollected);
                                currentFetchUrl = validatedLink;
                                resumedItemCount = checkpoint.ItemsCollected;
                                WriteVerbose($"Resuming from checkpoint: {resumedItemCount} items already exported, skipping {checkpoint.PageItemsAlreadyWritten} items on first page.");
                            }
                            else
                            {
                                WriteWarning("Checkpoint nextLink failed validation. Deleting checkpoint and starting fresh.");
                                PaginationCheckpoint.Delete(cpPath);
                                append = false;
                            }
                        }
                        else
                        {
                            WriteWarning("Checkpoint resource mismatch. Deleting checkpoint and starting fresh.");
                            PaginationCheckpoint.Delete(cpPath);
                            append = false;
                        }
                    }
                }

                // For fresh exports (not resume), write to a temp file first.
                // This protects any pre-existing output file from truncation if
                // the Graph request fails on the first page.
                // Use GUID to prevent collision when multiple exports target the same file.
                var writePath = append ? outputPath : $"{outputPath}.{Guid.NewGuid():N}.tmp";
                long itemCount = 0;
                int pageItemsWritten = 0;
                long totalWritten = resumedItemCount;
                long? reportedODataCount = null;

                try
                {
                    using (var writer = new StreamWriter(writePath, append))
                    {
                        var iterator = new PageIterator(client);

                        var enumerable = iterator.StreamAllWithCountAsync(
                            url,
                            maxItems,
                            count => { reportedODataCount = count; },
                            headers,
                            resume: resume,
                            onPageComplete: info =>
                            {
                                // Save page-boundary checkpoint (PageItemsAlreadyWritten = 0 since page is complete)
                                if (cpPath != null && info.NextPageUrl != null)
                                {
                                    try
                                    {
                                        writer.Flush();
                                        new PaginationCheckpoint
                                        {
                                            Resource = url,
                                            NextLink = info.NextPageUrl,
                                            ItemsCollected = totalWritten,
                                            PageItemsAlreadyWritten = 0
                                        }.Save(cpPath);
                                    }
                                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                                    {
                                        WriteWarning($"Checkpoint save failed (page boundary): {ex.Message}");
                                    }
                                }
                                if (info.NextPageUrl != null)
                                    currentFetchUrl = info.NextPageUrl;
                                pageItemsWritten = 0;
                            },
                            cancellationToken: CancellationToken);

                        var enumerator = enumerable.GetAsyncEnumerator(CancellationToken);
                        try
                        {
                            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                            {
                                writer.WriteLine(enumerator.Current.GetRawText());
                                itemCount++;
                                pageItemsWritten++;
                                totalWritten++;

                                if (totalWritten % 500 == 0)
                                {
                                    DrainClientMessages();
                                    writer.Flush();

                                    // Mid-page checkpoint: tracks items written from current page
                                    // to prevent duplicates on crash resume (H6 fix)
                                    if (cpPath != null)
                                    {
                                        try
                                        {
                                            new PaginationCheckpoint
                                            {
                                                Resource = url,
                                                NextLink = currentFetchUrl,
                                                ItemsCollected = totalWritten,
                                                PageItemsAlreadyWritten = pageItemsWritten
                                            }.Save(cpPath);
                                        }
                                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                                        {
                                            WriteWarning($"Mid-page checkpoint save failed: {ex.Message}");
                                        }
                                    }
                                }
                            }
                        }
                        finally
                        {
                            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        }
                    }

                    // Writer closed. Delete checkpoint before moving temp file.
                    // If crash after delete but before move: temp file orphaned, user re-runs fresh.
                    if (cpPath != null) PaginationCheckpoint.Delete(cpPath);

                    // Move temp file to final path on success.
                    if (!append)
                    {
                        File.Move(writePath, outputPath, overwrite: true);
                    }
                }
                catch
                {
                    // Clean up temp file on any error (don't leave orphaned .tmp files)
                    if (!append)
                    {
                        try { if (File.Exists(writePath)) File.Delete(writePath); } catch { }
                    }
                    throw; // re-throw to retry catch or outer catch blocks
                }

                sw.Stop();
                DrainClientMessages();

                var totalItems = resumedItemCount + itemCount;

                // Count discrepancy warning (only for full exports without resume)
                if (reportedODataCount.HasValue && maxItems == 0 && resume == null)
                    WriteCountDiscrepancyWarning(Uri, reportedODataCount.Value, totalItems, Filter);

                // Warn if 0 items and not resuming (could be a single-entity URI)
                if (totalItems == 0)
                {
                    WriteWarning(
                        "Export completed with 0 items. If you intended to retrieve a single entity, " +
                        "use Invoke-MgxRequest instead of Export-MgxCollection.");
                }

                // Warn if export hit the default page-size cap (may have more data)
                if (defaultedToPageSize && itemCount >= maxItems)
                {
                    WriteWarning(
                        $"Export stopped at {totalItems} items (default page size). " +
                        "Use -All to export everything, or -Top N to set an explicit limit.");
                }


                // Output summary
                var summary = new Models.MgxExportResult
                {
                    ItemCount = totalItems,
                    OutputFile = outputPath,
                    Duration = sw.Elapsed,
                    ResumedFrom = resumedItemCount > 0 ? resumedItemCount : null,
                };
                WriteObject(summary);
                return; // Success, exit the retry loop
            }
            catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
            {
                DrainClientMessages();
                var resumeHint = CheckpointPath != null
                    ? $"Resume with: Export-MgxCollection '{Uri}' -OutputFile '{OutputFile}' -CheckpointPath '{CheckpointPath}'"
                    : "Use -CheckpointPath to enable resume on next run.";
                WriteWarning($"Export cancelled. {resumeHint}");
                return;
            }
            catch (GraphServiceException ex) when (includeAutoCount && countAutoAdded && ex.StatusCode == HttpStatusCode.BadRequest)
            {
                // Auto-added $count=true rejected by this endpoint; retry without it
                DrainClientMessages();
                WriteVerbose("Endpoint rejected $count=true (HTTP 400). Retrying without count parameter.");
                includeAutoCount = false;
                continue;
            }
            catch (GraphServiceException ex) when (
                !suppressTop
                && !NoPageSize.IsPresent
                && ex.StatusCode == HttpStatusCode.BadRequest
                && string.Equals(ex.ErrorCode, "Request_UnsupportedQuery", StringComparison.OrdinalIgnoreCase))
            {
                DrainClientMessages();
                WriteVerbose("Endpoint rejected $top (Request_UnsupportedQuery). Retrying without page size.");
                suppressTop = true;
                continue;
            }
            catch (Exception ex) when (ex is GraphServiceException or BrokenCircuitException or HttpRequestException)
            {
                WriteGraphError(ex, Uri, ApiVersion);
                return;
            }
            catch (IOException ex)
            {
                DrainClientMessages();
                WriteError(new ErrorRecord(ex, "IOError",
                    ErrorCategory.WriteError, OutputFile));
                return;
            }
            catch (UnauthorizedAccessException ex)
            {
                DrainClientMessages();
                WriteError(new ErrorRecord(ex, "AccessDenied",
                    ErrorCategory.PermissionDenied, OutputFile));
                return;
            }
        }
    }

    private string BuildUrl(bool includeCount, bool suppressTop = false) => BuildListUrl(
        VersionedBaseUrl, Uri,
        new ODataListParams(NoPageSize.IsPresent || suppressTop, Top, PageSize, Filter,
            Property, Sort, Search, Skip, ExpandProperty,
            IncludeCount: includeCount));

    private Dictionary<string, string>? BuildHeaders() =>
        BuildRequestHeaders(ConsistencyLevel, Headers);

}
