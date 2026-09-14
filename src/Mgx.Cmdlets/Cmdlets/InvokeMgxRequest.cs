using System.Collections;
using System.Management.Automation;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mgx.Cmdlets.Base;
using Mgx.Engine.Http;
using Mgx.Engine.Models;
using Mgx.Engine.Pagination;
using Polly.CircuitBreaker;

namespace Mgx.Cmdlets.Cmdlets;

/// <summary>
/// Invoke-MgxRequest: General-purpose resilient client for any Microsoft Graph endpoint.
/// Supports streaming pagination, fan-out concurrency, write operations, and checkpoint/resume.
/// For bulk writes (>10 items), consider Invoke-MgxBatchRequest (measured ~1.5x faster
/// than fan-out for PATCH at 1k scale; fewer HTTP round-trips and server-side pacing).
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "MgxRequest", DefaultParameterSetName = "Direct",
    SupportsShouldProcess = true)]
[OutputType(typeof(Hashtable), typeof(string))]
public class InvokeMgxRequest : MgxCmdletBase
{
    #region Common parameters

    [Parameter(Mandatory = true, Position = 0)]
    [Alias("Resource")]
    public string Uri { get; set; } = string.Empty;

    [Parameter]
    [ValidateSet("GET", "POST", "PATCH", "PUT", "DELETE")]
    public string Method { get; set; } = "GET";

    [Parameter]
    public object? Body { get; set; }

    [Parameter]
    [Alias("Select")]
    public string[]? Property { get; set; }

    [Parameter]
    [Alias("Expand")]
    public string[]? ExpandProperty { get; set; }

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
    public SwitchParameter Raw { get; set; }

    #endregion

    #region List parameters

    [Parameter]
    public string? Filter { get; set; }

    [Parameter]
    [Alias("OrderBy")]
    public string[]? Sort { get; set; }

    [Parameter]
    public string? Search { get; set; }

    [Parameter]
    [ValidateRange(0, int.MaxValue)]
    public int Skip { get; set; }

    [Parameter]
    public SwitchParameter All { get; set; }

    [Parameter]
    [ValidateRange(0, int.MaxValue)]
    public int Top { get; set; }

    [Parameter]
    [ValidateRange(1, 999)]
    public int PageSize { get; set; } = 999;

    [Parameter]
    [Alias("CV")]
    public string? CountVariable { get; set; }

    [Parameter]
    public string? CheckpointPath { get; set; }

    [Parameter]
    public SwitchParameter NoPageSize { get; set; }

    #endregion

    #region Fan-out parameters

    /// <summary>
    /// Entity ID, or an object carrying one. Accepts a plain string, a Hashtable (what the
    /// Mgx cmdlets emit), or a PSCustomObject; the 'id' member is extracted in ProcessRecord.
    /// </summary>
    [Parameter(ValueFromPipeline = true, ParameterSetName = "Pipeline")]
    [Alias("Id")]
    public object? InputObject { get; set; }

    [Parameter(ParameterSetName = "Pipeline")]
    [ValidateRange(1, 128)]
    public int Concurrency { get; set; } = 5;

    [Parameter(ParameterSetName = "Pipeline")]
    public SwitchParameter SkipNotFound { get; set; }

    [Parameter(ParameterSetName = "Pipeline")]
    public SwitchParameter SkipForbidden { get; set; }

    #endregion

    private readonly List<string> _pipelineIds = [];
    private bool _isFanOut;

    /// <summary>
    /// The checkpoint gate's answer for this invocation, asked once and reused. -CheckpointPath,
    /// -Confirm and -WhatIf are all bound once for the whole run, but a -Uri without a {id}
    /// placeholder is not a fan-out: ProcessRecord runs the direct branch below once per piped
    /// item, and each of those calls reaches ExecuteList's checkpoint gate on its own. Without
    /// this, three piped values asked ShouldProcess three times over the same file - three
    /// prompts, and three "What if" lines under -WhatIf, for one run. Set on whichever item
    /// reaches the gate first; every later item on that route reuses the answer instead of
    /// asking again, a refusal included.
    /// </summary>
    private bool? _mayWriteCheckpoint;

    /// <summary>
    /// Full base URL including API version (e.g., "https://graph.microsoft.com/v1.0").
    /// </summary>
    private string VersionedBaseUrl => $"{s_graphEndpoint}/{ApiVersion}";

    /// <summary>
    /// Whether the current invocation is a collection/list operation.
    /// </summary>
    private bool IsCollectionMode =>
        All.IsPresent || Top > 0 || !string.IsNullOrEmpty(Filter) ||
        !string.IsNullOrEmpty(Search) || Sort is { Length: > 0 } ||
        !string.IsNullOrEmpty(CountVariable) || Skip > 0;

    protected override void BeginProcessing()
    {
        // A fresh cmdlet instance answers this fresh each run; reset explicitly rather than
        // relying on that, so the field's own comment is the one place its lifetime is stated.
        _mayWriteCheckpoint = null;

        // Reject absolute URLs (relative paths only); concatenation onto the versioned
        // base URL would otherwise silently produce /v1.0/https:/... on the wire.
        if (Uri.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            Uri.TrimStart().StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            ThrowTerminatingError(new ErrorRecord(
                new ArgumentException(
                    $"-Uri must be a relative path (e.g., /users), not an absolute URL. Got: '{Uri}'"),
                "AbsoluteUriNotAllowed", ErrorCategory.InvalidArgument, null));
            return;
        }

        _isFanOut = Uri.Contains("{id}", StringComparison.OrdinalIgnoreCase);

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
        // auto-add when -CountVariable or -Filter is used (enables count discrepancy detection)
        if ((!string.IsNullOrEmpty(CountVariable) || !string.IsNullOrEmpty(Filter))
            && string.IsNullOrEmpty(ConsistencyLevel))
        {
            ConsistencyLevel = "eventual";
            WriteVerbose("Auto-adding ConsistencyLevel:eventual header (required by -Filter/-CountVariable for $count=true).");
        }

        // $skip is not supported by most Graph directory endpoints (silently ignored)
        if (Skip > 0)
            WriteWarning("-Skip ($skip) is not supported by many Graph API endpoints (e.g., /users, /groups). The parameter may be silently ignored.");
    }

    protected override void ProcessRecord()
    {
        if (_isFanOut)
        {
            if (InputObject == null)
            {
                WriteVerbose("Skipping null pipeline input.");
                return;
            }

            var id = ResolvePipelineId(InputObject);
            if (string.IsNullOrEmpty(id))
            {
                WriteError(new ErrorRecord(
                    new ArgumentException(
                        "Pipeline input is missing an 'id' property. Pipe entity IDs, or objects with an 'id' property."),
                    "MissingPipelineId", ErrorCategory.InvalidArgument, InputObject));
                return;
            }

            _pipelineIds.Add(id);
            return;
        }

        // Direct mode (no fan-out): execute immediately
        try
        {
            ExecuteRequest(Uri, sourceId: null);
        }
        catch (Exception)
        {
            // Unexpected exception types skip the drains inside; the buffered verbose and
            // warning messages are the context that explains the failure.
            DrainClientMessages();
            throw;
        }
    }

    /// <summary>
    /// Extract the entity ID from pipeline input: a bare string, or the 'id' member of a
    /// Hashtable / PSCustomObject.
    /// </summary>
    internal static string? ResolvePipelineId(object input)
    {
        var value = UnwrapPSObject(input);

        if (value is string s)
            return s;

        return TryGetMember(value, "id")?.ToString();
    }

    protected override void EndProcessing()
    {
        try
        {
            if (_isFanOut)
            {
                if (_pipelineIds.Count == 0)
                {
                    // Error on {id} URI without pipeline input
                    ThrowTerminatingError(new ErrorRecord(
                        new ArgumentException(
                            "URI contains '{id}' placeholder but no pipeline input was provided. Pipe entity IDs to this cmdlet."),
                        "MissingPipelineInput", ErrorCategory.InvalidArgument, Uri));
                    return;
                }

                ExecuteFanOut();
            }
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            WriteWarning("Request cancelled by user.");
        }
        catch (Exception)
        {
            // Unexpected exception types skip the drains above; the buffered verbose and
            // warning messages are the context that explains the failure.
            DrainClientMessages();
            throw;
        }
        finally
        {
            base.EndProcessing();
        }
    }

    #region Request execution

    private void ExecuteRequest(string relativeUri, string? sourceId)
    {
        // Ensure client is initialized before using VersionedBaseUrl
        // (populates s_graphEndpoint for sovereign clouds)
        GetClient();

        var httpMethod = new HttpMethod(Method.ToUpperInvariant());

        if (httpMethod == HttpMethod.Get)
        {
            // Graph GET has no request body, and neither GET path sends one
            if (Body != null)
                WriteWarning("-Body is ignored on GET requests.");

            if (IsCollectionMode)
                ExecuteList(relativeUri, sourceId);
            else
                ExecuteGet(relativeUri, sourceId);
        }
        else
        {
            ExecuteWrite(httpMethod, relativeUri, sourceId);
        }
    }

    private void ExecuteList(string relativeUri, string? sourceId)
    {
        // Track whether $count=true was auto-added (not user-requested via -CountVariable,
        // and not already written into -Uri, where dropping it changes nothing and the
        // "retry without count" rebuild would re-send a byte-identical request).
        bool countAutoAdded = !string.IsNullOrEmpty(Filter) && string.IsNullOrEmpty(CountVariable)
            && !ExistingQueryOptions(Uri).Contains("$count");
        bool includeAutoCount = countAutoAdded;
        bool suppressTop = false;

        // Consumer-owned checkpoint: resolve path once before the retry loop
        var cpPath = CheckpointPath != null
            ? GetUnresolvedProviderPathFromPSPath(CheckpointPath)
            : null;

        // -CheckpointPath is the one file this read path writes, and it is the caller's own
        // resume position: every page boundary saves the next link over it, completion deletes
        // it, and a checkpoint describing another enumeration is deleted where it is found.
        // Reads go out under -WhatIf, by the convention above ExecuteWrite, but a preview that
        // overwrites or removes the position a later run resumes from has changed the thing it
        // claimed only to describe. So the file gets a gate of its own and the reads keep the
        // convention: a refusal skips the saves and the deletes, and the enumeration goes on to
        // page and emit.
        //
        // One decision, taken here rather than at each site. The page-boundary callback runs on
        // whichever thread the iterator resumes on - a page that yields nothing resumes on the
        // thread pool - and a cmdlet API call from there throws. -Confirm therefore asks once
        // per run about one file, which is the question it has to answer anyway - and "once per
        // run" has to hold across ProcessRecord calls too, not just within this one: a -Uri with
        // no {id} placeholder is not a fan-out, so several piped values still reach ExecuteList
        // once each through the direct branch in ProcessRecord, each with its own attempt loop
        // and its own call here. _mayWriteCheckpoint (reset once in BeginProcessing) is what
        // makes the second and later calls consult the first item's answer instead of asking
        // ShouldProcess again - a refusal on the first item refuses the rest.
        //
        // Reading the checkpoint is not gated: resuming is what the run being previewed would
        // do, and a preview that ignored it would page from the start and describe a different
        // set of requests. The branches that refuse a checkpoint leave resume null and enumerate
        // from the beginning either way - the delete is all the gate withholds.
        var mayWriteCheckpoint = cpPath == null
            || (_mayWriteCheckpoint ??= ShouldProcess(cpPath, "Save resume checkpoint"));

        // If checkpoint was saved during a previous retry (URL without $count=true),
        // match the checkpoint's URL to avoid mismatch on resume
        if (countAutoAdded && cpPath != null)
        {
            var existingCp = PaginationCheckpoint.Load(cpPath);
            if (existingCp?.Resource != null && !existingCp.Resource.Contains("$count=true"))
                includeAutoCount = false;
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            long? reportedODataCount = null;
            try
            {
                var url = BuildCollectionUrl(relativeUri,
                    includeCount: !string.IsNullOrEmpty(CountVariable) || includeAutoCount,
                    noPageSize: suppressTop);
                var client = GetClient();
                var iterator = new PageIterator(client);
                // -All says how far to page, -Top says how much to return, and they are not the
                // same question: -All used to zero the cap, so asking for a bounded slice of a
                // large collection walked all of it. Worse, -Top also sets the page size, so the
                // walk ran at the slice's page size - 150 rows at a time across the whole tenant.
                var maxItems = Top > 0 ? Top : 0;
                var headers = BuildHeaders();
                long itemCount = 0;

                ResumeState? resume = null;

                if (cpPath != null)
                {
                    var checkpoint = PaginationCheckpoint.Load(cpPath);
                    if (checkpoint != null)
                    {
                        if (checkpoint.NextLink == null)
                        {
                            // Completion marker: previous run finished
                            if (mayWriteCheckpoint) PaginationCheckpoint.Delete(cpPath);
                        }
                        else if (string.Equals(checkpoint.Resource, url, StringComparison.Ordinal))
                        {
                            var expectedHost = new System.Uri(url);
                            var validated = NextLinkValidator.Validate(checkpoint.NextLink, expectedHost);
                            if (validated != null && checkpoint.ItemsCollected >= 0)
                            {
                                // Page-boundary only: skipOnFirstPage = 0 because pipeline items are
                                // ephemeral (no file to dedup against). On resume, the interrupted page
                                // may re-emit items already sent downstream. Downstream consumers
                                // (e.g., Export-Csv -Append) are responsible for their own dedup.
                                resume = new ResumeState(validated, 0, checkpoint.ItemsCollected);
                            }
                            else
                            {
                                if (mayWriteCheckpoint) PaginationCheckpoint.Delete(cpPath);
                            }
                        }
                        else
                        {
                            if (mayWriteCheckpoint) PaginationCheckpoint.Delete(cpPath);
                        }
                    }
                }

                var enumerable = iterator.StreamAllWithCountAsync(
                    url,
                    maxItems,
                    count =>
                    {
                        if (!string.IsNullOrEmpty(CountVariable))
                            SessionState.PSVariable.Set(CountVariable, count);
                        reportedODataCount = count;
                    },
                    headers,
                    resume: resume,
                    onPageComplete: info =>
                    {
                        // Save page-boundary checkpoint
                        if (mayWriteCheckpoint && cpPath != null && info.NextPageUrl != null)
                        {
                            try
                            {
                                new PaginationCheckpoint
                                {
                                    Resource = url,
                                    NextLink = info.NextPageUrl,
                                    ItemsCollected = itemCount + (resume?.ItemsAlreadyCollected ?? 0)
                                }.Save(cpPath);
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                // Buffered rather than written here. This callback runs on
                                // whichever thread the iterator resumed on, and a page that
                                // yields nothing resumes on the thread pool - so a WriteWarning
                                // from here throws PSInvalidOperationException, and the disk
                                // problem it was reporting ends the enumeration instead of
                                // being reported. The drains below write it from the pipeline
                                // thread, on the same channel the client's own warnings take.
                                client.EnqueueWarning($"Checkpoint save failed: {ex.Message}");
                            }
                        }
                    },
                    cancellationToken: CancellationToken);

                var enumerator = enumerable.GetAsyncEnumerator(CancellationToken);
                try
                {
                    while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                    {
                        DrainClientMessages();
                        itemCount++;
                        OutputItem(enumerator.Current, sourceId);
                    }
                }
                catch (PipelineStoppedException)
                {
                    // Pipeline consumer is done (e.g., Select-Object -First N); stop gracefully
                    throw;
                }
                finally
                {
                    enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                // Warn if actual items significantly fewer than @odata.count
                if (reportedODataCount.HasValue && maxItems == 0 && resume == null)
                    WriteCountDiscrepancyWarning(relativeUri, reportedODataCount.Value, itemCount, Filter);

                // Delete checkpoint on successful completion
                if (mayWriteCheckpoint && cpPath != null) PaginationCheckpoint.Delete(cpPath);
                return; // Success, exit the retry loop
            }
            catch (PipelineStoppedException)
            {
                throw;
            }
            catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
            {
                WriteWarning("Request cancelled by user.");
                return;
            }
            catch (JsonException ex)
            {
                // A page body that does not parse. Items from earlier pages have already been
                // emitted; report the failure instead of crashing the pipeline.
                WriteError(new ErrorRecord(
                    new InvalidOperationException($"A response page declared JSON but does not parse: {ex.Message}", ex),
                    "MalformedJsonResponse", ErrorCategory.InvalidData, relativeUri));
                return;
            }
            catch (GraphServiceException ex) when (IsCountRejection(ex, includeAutoCount && countAutoAdded))
            {
                WriteVerbose(CountRejectedVerbose);
                includeAutoCount = false;
                continue;
            }
            catch (GraphServiceException ex) when (IsTopRejection(ex, suppressTop, NoPageSize.IsPresent))
            {
                WriteVerbose(TopRejectedVerbose);
                suppressTop = true;
                continue;
            }
            catch (GraphServiceException ex) when (ShouldSkipGraphError(ex))
            {
                return;
            }
            catch (Exception ex) when (ex is GraphServiceException or BrokenCircuitException or HttpRequestException)
            {
                WriteGraphError(ex, relativeUri, ApiVersion);
                return;
            }
            finally
            {
                // Every way out of the attempt above - the success path, a retry via continue,
                // or any catch - drains here once, from the pipeline thread. The last catch
                // already drains inside WriteGraphError before it writes its error record, so
                // that exit reaches this a second time; TryDequeue found the queue already
                // empty from the first drain, so the second call writes nothing and nothing
                // doubles.
                DrainClientMessages();
            }
        }
    }

    private void ExecuteGet(string relativeUri, string? sourceId)
    {
        try
        {
            var url = BuildGetUrl(relativeUri);
            var client = GetClient();
            var headers = BuildHeaders();

            using var response = client.GetAsync(url, CancellationToken, headers)
                .GetAwaiter().GetResult();
            DrainClientMessages();

            if (!response.IsSuccessStatusCode)
            {
                var body = client.ReadBodyAsStringAsync(response, CancellationToken).GetAwaiter().GetResult();
                throw new GraphServiceException(response.StatusCode, body);
            }

            var bodyBytes = client.ReadBodyAsBytesAsync(response, CancellationToken).GetAwaiter().GetResult();
            var json = ReadJsonPayload(response, bodyBytes, relativeUri);
            if (json != null)
                OutputPayload(json.Value, sourceId);
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            WriteWarning("Request cancelled by user.");
        }
        catch (GraphServiceException ex) when (ShouldSkipGraphError(ex))
        {
            DrainClientMessages();
        }
        catch (Exception ex) when (ex is GraphServiceException or BrokenCircuitException or HttpRequestException)
        {
            WriteGraphError(ex, relativeUri, ApiVersion);
        }
    }

    private void ExecuteWrite(HttpMethod method, string relativeUri, string? sourceId)
    {
        // The gate sits on the write and on the bulk write below, and on nothing else: reads go
        // out under -WhatIf, per the PowerShell convention, so that a dry run under
        // $WhatIfPreference has real data for the gated writes to be previewed against. The help
        // says so, because "the cmdlet is not run" would not be true of a GET that connects,
        // pages, spends resource units and emits objects. Invoke-MgxBatchRequest gates its reads
        // too, for the reason stated above its own gate; the two contracts are documented apart.
        if (!ShouldProcess(relativeUri, method.Method))
            return;

        string? serializedBody;
        try
        {
            serializedBody = ResolveRequestBody(method);
        }
        catch (ArgumentException ex)
        {
            // Pre-flight: nothing was sent. Terminating, per about_Mgx_Errors.
            ThrowTerminatingError(new ErrorRecord(ex, "InvalidBodyValue",
                ErrorCategory.InvalidArgument, relativeUri));
            return;
        }
        NoteNonJsonStringBody();

        try
        {
            var url = $"{VersionedBaseUrl}{NormalizePath(relativeUri)}";
            var client = GetClient();
            var headers = BuildHeaders();
            HttpContent? content = serializedBody != null
                ? new StringContent(serializedBody, Encoding.UTF8, "application/json")
                : null;

            try
            {
                using var response = client.SendAsync(method, url, content, headers, CancellationToken)
                    .GetAwaiter().GetResult();
                DrainClientMessages();

                if (!response.IsSuccessStatusCode)
                {
                    var body = client.ReadBodyAsStringAsync(response, CancellationToken).GetAwaiter().GetResult();
                    throw new GraphServiceException(response.StatusCode, body);
                }

                // DELETE typically returns 204 No Content
                if (response.StatusCode == HttpStatusCode.NoContent)
                    return;

                // stream.Length throws NotSupportedException on network/decompression streams.
                // Read as bytes to safely handle null ContentLength (chunked transfer) and empty bodies.
                var bodyBytes = client.ReadBodyAsBytesAsync(response, CancellationToken).GetAwaiter().GetResult();
                var jsonEl = ReadJsonPayload(response, bodyBytes, relativeUri);
                if (jsonEl != null)
                    OutputPayload(jsonEl.Value, sourceId);
            }
            finally
            {
                content?.Dispose();
            }
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            WriteWarning("Request cancelled by user.");
        }
        catch (GraphServiceException ex) when (ShouldSkipGraphError(ex))
        {
            DrainClientMessages();
        }
        catch (Exception ex) when (ex is GraphServiceException or BrokenCircuitException or HttpRequestException)
        {
            WriteGraphError(ex, relativeUri, ApiVersion);
        }
    }

    #endregion

    #region Fan-out

    private void ExecuteFanOut()
    {
        // -CountVariable with multi-ID fan-out is ambiguous
        if (!string.IsNullOrEmpty(CountVariable) && _pipelineIds.Count > 1)
        {
            ThrowTerminatingError(new ErrorRecord(
                new ArgumentException(
                    "-CountVariable is not supported with multi-ID fan-out. The count would be ambiguous across multiple entities."),
                "CountVariableNotSupported", ErrorCategory.InvalidArgument, CountVariable));
            return;
        }

        if (_pipelineIds.Count == 1)
        {
            // Single ID: direct execution, no ConcurrentFanOut overhead
            var resolved = ResolveTemplate(_pipelineIds[0]);
            ExecuteRequest(resolved, _pipelineIds[0]);
            return;
        }

        // Deduplicate pipeline IDs to avoid dict key collision and redundant HTTP calls
        var uniqueIds = _pipelineIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (uniqueIds.Count < _pipelineIds.Count)
            WriteVerbose($"Deduplicated {_pipelineIds.Count} pipeline IDs to {uniqueIds.Count} unique IDs.");

        // Ensure client is initialized (populates s_graphEndpoint for sovereign clouds)
        var client = GetClient();
        var fanOut = new ConcurrentFanOut(client, Concurrency);
        var headers = BuildHeaders();

        // Route write methods to bulk write fan-out
        var httpMethod = new HttpMethod(Method.ToUpperInvariant());
        if (httpMethod != HttpMethod.Get)
        {
            ExecuteWriteFanOut(fanOut, uniqueIds, headers, httpMethod);
            return;
        }

        // Branch on collection vs entity mode
        if (IsCollectionMode)
        {
            ExecuteCollectionFanOut(fanOut, uniqueIds, headers);
        }
        else
        {
            ExecuteEntityFanOut(fanOut, uniqueIds, headers);
        }
    }

    /// <summary>
    /// Collection fan-out: each ID resolves to a collection endpoint (e.g., /groups/{id}/members).
    /// Uses FetchAllAsync which calls GetCollectionPageAsync (expects "value" array).
    /// </summary>
    private void ExecuteCollectionFanOut(ConcurrentFanOut fanOut, List<string> uniqueIds, Dictionary<string, string>? headers)
    {
        var urls = uniqueIds.Select(id => BuildCollectionUrl(ResolveTemplate(id), includeCount: false)).ToList();

        // Map URL → sourceId for correlation
        var urlToSourceId = new Dictionary<string, string>();
        for (int i = 0; i < uniqueIds.Count; i++)
            urlToSourceId[urls[i]] = uniqueIds[i];

        // Respect -Top limit per URL, -All or not.
        var maxItems = Top > 0 ? Top : 0;

        // Pass headers to FetchAllAsync
        var fanOutResult = fanOut.FetchAllAsync(urls, maxItems, headers, CancellationToken)
            .GetAwaiter().GetResult();
        DrainClientMessages();

        int totalItems = 0;
        foreach (var (url, items) in fanOutResult.Results)
        {
            var sourceId = urlToSourceId.GetValueOrDefault(url);
            foreach (var item in items)
            {
                totalItems++;
                OutputItem(item, sourceId);
            }
        }

        HandleFanOutErrors(fanOutResult.Errors);
    }

    /// <summary>
    /// Entity fan-out: each ID resolves to a single entity endpoint (e.g., /users/{id}).
    /// Uses ForEachAsync with GetAsync per entity since the response is a flat object, not a collection.
    /// </summary>
    private void ExecuteEntityFanOut(ConcurrentFanOut fanOut, List<string> uniqueIds, Dictionary<string, string>? headers)
    {
        // Clear results from any previous invocation
        _entityFanOutResults.Clear();

        int totalItems = 0;
        var client = GetClient();

        try
        {
            var errors = fanOut.ForEachAsync(
                uniqueIds,
                async (id, ct) =>
                {
                    var resolved = ResolveTemplate(id);
                    var url = BuildGetUrl(resolved);
                    using var response = await client.GetAsync(url, ct, headers);

                    if (!response.IsSuccessStatusCode)
                    {
                        var body = await client.ReadBodyAsStringAsync(response, ct);
                        throw new GraphServiceException(response.StatusCode, body);
                    }

                    // The body crosses to the cmdlet thread undecoded. ReadJsonPayload answers
                    // the charset, the empty body, the non-JSON body and the parse failure, and
                    // every one of those answers is a verbose, output or error record - none of
                    // which a worker thread may write.
                    var bodyBytes = await client.ReadBodyAsBytesAsync(response, ct);
                    var contentType = response.Content.Headers.ContentType;

                    lock (_entityFanOutResults)
                    {
                        _entityFanOutResults.Add((id, response.StatusCode, contentType, bodyBytes));
                    }
                },
                CancellationToken).GetAwaiter().GetResult();
            DrainClientMessages();

            // Output results on the cmdlet thread
            foreach (var (sourceId, status, contentType, bodyBytes) in _entityFanOutResults)
            {
                var json = ReadJsonPayload(status, contentType, bodyBytes, ResolveTemplate(sourceId));
                if (json == null)
                    continue;
                totalItems++;
                OutputItem(json.Value, sourceId);
            }

            HandleFanOutErrors(errors);
            }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            WriteWarning("Entity fan-out cancelled by user.");
        }
    }

    private readonly List<(string sourceId, HttpStatusCode status,
        MediaTypeHeaderValue? contentType, byte[] bodyBytes)> _entityFanOutResults = [];

    /// <summary>
    /// Write fan-out: execute POST/PATCH/PUT/DELETE for each piped ID concurrently.
    /// Same body is applied to all operations. URIs are resolved via {id} template.
    /// </summary>
    private void ExecuteWriteFanOut(ConcurrentFanOut fanOut, List<string> uniqueIds, Dictionary<string, string>? headers, HttpMethod httpMethod)
    {
        if (!ShouldProcess($"{httpMethod.Method} {uniqueIds.Count} items via {Uri}", "Bulk write"))
            return;

        string? serializedBody;
        try
        {
            // Serialize body once (shared across all operations)
            serializedBody = ResolveRequestBody(httpMethod);
        }
        catch (ArgumentException ex)
        {
            ThrowTerminatingError(new ErrorRecord(ex, "InvalidBodyValue",
                ErrorCategory.InvalidArgument, Uri));
            return;
        }
        NoteNonJsonStringBody();

        try
        {

            // Build operations list: (id, resolved URL)
            var operations = uniqueIds.Select(id =>
            {
                var resolved = ResolveTemplate(id);
                var url = $"{VersionedBaseUrl}{NormalizePath(resolved)}";
                return (id, url);
            }).ToList();

            var telemetryBefore = MgxTelemetryCollector.Current.GetSummary();

            var result = fanOut.BulkWriteAsync(
                httpMethod,
                operations,
                serializedBody,
                headers,
                onProgress: null, // WriteProgress cannot be called from background threads
                CancellationToken).GetAwaiter().GetResult();
            DrainClientMessages();

            // Output response bodies (created/updated entities)
            foreach (var (sourceId, json) in result.Responses)
            {
                OutputPayload(json, sourceId);
            }

            // Handle errors with SkipNotFound/SkipForbidden filtering
            HandleBulkWriteErrors(result.Errors);

            // Summary with timing breakdown
            if (result.Succeeded > 0 || result.Failed > 0)
            {
                var telemetryAfter = MgxTelemetryCollector.Current.GetSummary();
                var elapsedSec = result.ElapsedMs / 1000.0;
                var throttles = telemetryAfter.ThrottleRetries - telemetryBefore.ThrottleRetries;
                var retryDelayMs = telemetryAfter.RetryDelayMs - telemetryBefore.RetryDelayMs;
                var rateLimiterMs = telemetryAfter.RateLimiterWaitMs - telemetryBefore.RateLimiterWaitMs;
                var httpMs = telemetryAfter.HttpMs - telemetryBefore.HttpMs;
                var throughput = result.ElapsedMs > 0 ? result.Succeeded / elapsedSec : 0;

                var summary = $"Bulk {Method}: {result.Succeeded} succeeded, {result.Failed} failed in {elapsedSec:F1}s ({throughput:F1}/sec)";
                if (throttles > 0 || retryDelayMs > 0 || rateLimiterMs > 0)
                {
                    var parts = new System.Collections.Generic.List<string>();
                    if (httpMs > 0) parts.Add($"HTTP {httpMs / 1000.0:F1}s");
                    if (retryDelayMs > 0) parts.Add($"throttle wait {retryDelayMs / 1000.0:F1}s ({throttles} 429s)");
                    if (rateLimiterMs > 0) parts.Add($"rate-limiter {rateLimiterMs / 1000.0:F1}s");
                    summary += $" | {string.Join(", ", parts)}";
                }
                WriteVerbose(summary);
            }

        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            WriteWarning("Bulk write cancelled by user.");
        }
    }

    private void HandleBulkWriteErrors(IReadOnlyList<BulkWriteError> errors)
    {
        int skipped404 = 0;
        int skipped403 = 0;
        bool has404 = false;
        foreach (var error in errors)
        {
            var statusCode = (HttpStatusCode)error.StatusCode;

            // A batch result carries no error code, only a status and a message, so a missing
            // object cannot be told from a missing endpoint here the way it can on the fan-out
            // path below. This is the write path, where a 404 is the likelier of the two.
            if (statusCode == HttpStatusCode.NotFound)
                has404 = true;

            if (SkipNotFound.IsPresent && statusCode == HttpStatusCode.NotFound)
            {
                skipped404++;
                continue;
            }
            if (SkipForbidden.IsPresent && statusCode == HttpStatusCode.Forbidden)
            {
                skipped403++;
                continue;
            }

            var ex = new InvalidOperationException($"HTTP {error.StatusCode} for '{error.Id}': {error.Message}");
            // StatusCode 0 = a write that reached no HTTP status at all: the network, an open
            // circuit. Not a body that stalls or does not parse - the server answered those, and
            // they carry the status it answered with, so they take the branch below and read as
            // what they are rather than as a request that never arrived.
            var (errorId, category) = error.StatusCode == 0
                ? ("BulkWriteInfraError", ErrorCategory.ConnectionError)
                : ("BulkWriteError", MapStatusToCategory(statusCode));
            WriteError(new ErrorRecord(ex, errorId, category, error.Id));
        }

        if (has404)
            WriteBetaHintIfApplicable(HttpStatusCode.NotFound, ApiVersion);

        WriteSkipSummaryWarning(skipped404, skipped403, "operations");
    }

    private void HandleFanOutErrors(Dictionary<string, Exception> errors)
    {
        int skipped404 = 0;
        int skipped403 = 0;
        bool has404 = false;
        foreach (var (key, ex) in errors)
        {
            var statusCode = MgxErrorPresentation.TryGetStatus(ex);

            // Only a 404 that might mean "no such endpoint" is worth a beta hint. A 404 naming
            // a missing object says the path was fine, and hinting over it sends the caller to
            // re-run against beta for a request that fails there too.
            if (statusCode == HttpStatusCode.NotFound && !IsObjectMissing(ex))
                has404 = true;

            if (SkipNotFound.IsPresent && statusCode == HttpStatusCode.NotFound)
            {
                skipped404++;
                continue;
            }
            if (SkipForbidden.IsPresent && statusCode == HttpStatusCode.Forbidden)
            {
                skipped403++;
                continue;
            }

            var (errorId, category, report) =
                MgxErrorPresentation.PresentItemFailure(ex, "FanOutError", CircuitBreakerMessage);
            WriteError(new ErrorRecord(report, errorId, category, key));
        }

        if (has404)
            WriteBetaHintIfApplicable(HttpStatusCode.NotFound, ApiVersion);

        WriteSkipSummaryWarning(skipped404, skipped403, "entities");
    }

    private void WriteSkipSummaryWarning(int skipped404, int skipped403, string noun)
    {
        var total = skipped404 + skipped403;
        if (total == 0) return;
        var reasons = new List<string>();
        if (skipped404 > 0) reasons.Add("404 (Not Found)");
        if (skipped403 > 0) reasons.Add("403 (Forbidden)");
        WriteWarning($"Skipped {total} {noun} due to {string.Join(" and ", reasons)} responses.");
    }

    private string ResolveTemplate(string id)
    {
        // Replace {id} (case-insensitive) with the escaped entity ID
        return System.Text.RegularExpressions.Regex.Replace(
            Uri, @"\{id\}", System.Uri.EscapeDataString(id),
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Check if a GraphServiceException should be silently skipped based on
    /// -SkipNotFound / -SkipForbidden switches. Used by single-request paths
    /// (ExecuteGet, ExecuteWrite, ExecuteList) so that these switches work
    /// consistently regardless of whether the pipeline has 1 or N items.
    /// </summary>
    private bool ShouldSkipGraphError(GraphServiceException ex)
    {
        if (SkipNotFound.IsPresent && ex.StatusCode == HttpStatusCode.NotFound)
        {
            WriteVerbose($"Skipping 404 (Not Found) for request: {ex.Message}");
            return true;
        }
        if (SkipForbidden.IsPresent && ex.StatusCode == HttpStatusCode.Forbidden)
        {
            WriteVerbose($"Skipping 403 (Forbidden) for request: {ex.Message}");
            return true;
        }
        return false;
    }

    #endregion

    #region URL building

    private string BuildCollectionUrl(string relativeUri) => BuildCollectionUrl(relativeUri,
        includeCount: !string.IsNullOrEmpty(CountVariable) || !string.IsNullOrEmpty(Filter));

    private int _warnedDeferredOptions;

    private void WarnDeferredOptions(List<string> deferred)
    {
        // Interlocked: URL building runs inside fan-out lambdas that can resume off the
        // pipeline thread; the warning must fire exactly once and from one caller.
        if (deferred.Count == 0 || Interlocked.Exchange(ref _warnedDeferredOptions, 1) == 1) return;
        WriteWarning(DescribeDeferredOptions(deferred));
    }

    private string BuildCollectionUrl(string relativeUri, bool includeCount, bool noPageSize = false)
    {
        var url = BuildListUrl(
            VersionedBaseUrl, relativeUri,
            new ODataListParams(NoPageSize.IsPresent || noPageSize, Top, PageSize, Filter,
                Property, Sort, Search, Skip, ExpandProperty,
                IncludeCount: includeCount),
            out var deferred);
        WarnDeferredOptions(deferred);
        return url;
    }

    private string BuildGetUrl(string relativeUri)
    {
        var baseUrl = $"{VersionedBaseUrl}{NormalizePath(relativeUri)}";
        var queryParams = new List<string>();

        if (Property is { Length: > 0 })
            queryParams.Add($"$select={EscapeQueryValue(string.Join(",", Property))}");

        if (ExpandProperty is { Length: > 0 })
            queryParams.Add($"$expand={EscapeQueryValue(string.Join(",", ExpandProperty))}");

        var existing = ExistingQueryOptions(baseUrl);
        WarnDeferredOptions(queryParams.Where(qp => existing.Contains(qp.Split('=', 2)[0]))
            .Select(qp => qp.Split('=', 2)[0]).ToList());
        queryParams.RemoveAll(qp => existing.Contains(qp.Split('=', 2)[0]));

        if (queryParams.Count == 0)
            return baseUrl;

        // If URI already contains query parameters, append with & instead of ?
        var separator = baseUrl.Contains('?') ? "&" : "?";
        return $"{baseUrl}{separator}{string.Join("&", queryParams)}";
    }

    #endregion

    #region Helpers

    private Dictionary<string, string>? BuildHeaders() =>
        BuildRequestHeaders(ConsistencyLevel, Headers);

    /// <summary>
    /// Emit a Graph response payload. A collection envelope ({"value":[...]}, returned by GET and by
    /// action endpoints such as /directoryObjects/getByIds) is unwrapped into one item per element;
    /// anything else is emitted whole.
    /// </summary>
    /// <summary>
    /// A string -Body travels verbatim under Content-Type: application/json. When it is not
    /// JSON that is usually intentional (a raw upload) - but the endpoint sees the wrong
    /// content type unless -Headers overrides it, and the resulting 400 names neither.
    /// A verbose note, not a warning: raw string bodies are a supported path.
    /// </summary>
    private void NoteNonJsonStringBody()
    {
        if (Body is null || UnwrapPSObject(Body) is not string s || string.IsNullOrWhiteSpace(s)) return;
        if (Headers != null && Headers.Keys.Cast<object>()
                .Any(k => string.Equals(k.ToString(), "Content-Type", StringComparison.OrdinalIgnoreCase)))
            return;
        try
        {
            using var _ = JsonDocument.Parse(s);
        }
        catch (JsonException)
        {
            WriteVerbose("-Body is a string that is not JSON; it is sent verbatim with "
                + "Content-Type: application/json. Add a Content-Type to -Headers to declare its real type.");
        }
    }

    /// <summary>
    /// A response body as JSON for output, or null when nothing further should be emitted:
    /// an empty body (204, or 200 with no content), a non-JSON body (emitted as text under
    /// -Raw, otherwise an error), or a body that declares JSON and does not parse.
    /// </summary>
    private JsonElement? ReadJsonPayload(HttpResponseMessage response, byte[] bodyBytes, string relativeUri)
        => ReadJsonPayload(response.StatusCode, response.Content.Headers.ContentType, bodyBytes, relativeUri);

    /// <summary>
    /// The same read for a caller that no longer holds the response: the entity fan-out reads
    /// the body on a worker thread and decodes it here, where writing to the streams is legal.
    /// </summary>
    private JsonElement? ReadJsonPayload(HttpStatusCode statusCode, MediaTypeHeaderValue? declaredType,
        byte[] bodyBytes, string relativeUri)
    {
        var payload = ReadJsonPayloadCore(declaredType, bodyBytes);
        switch (payload.Kind)
        {
            case JsonPayloadKind.Empty:
                WriteVerbose($"HTTP {(int)statusCode}: response has no content; nothing to emit.");
                return null;

            case JsonPayloadKind.NotJson:
                if (Raw.IsPresent)
                {
                    WriteObject(payload.Text);
                    return null;
                }
                WriteError(new ErrorRecord(
                    new InvalidOperationException(
                        $"The response is {payload.MediaType}, not JSON. Use -Raw to receive it as text, or Get-MgxContent for file and media content."),
                    "NonJsonResponse", ErrorCategory.InvalidData, relativeUri));
                return null;

            case JsonPayloadKind.Malformed:
                var snippet = payload.Text[..Math.Min(payload.Text.Length, 200)];
                WriteError(new ErrorRecord(
                    new InvalidOperationException(
                        $"The response declared JSON but does not parse. Body starts: {snippet}"),
                    "MalformedJsonResponse", ErrorCategory.InvalidData, relativeUri));
                return null;

            default:
                return payload.Json;
        }
    }

    private void OutputPayload(JsonElement json, string? sourceId)
    {
        var items = TryUnwrapCollection(json, out var truncated);
        if (items == null)
        {
            OutputItem(json, sourceId);
            return;
        }

        if (truncated)
            WriteWarning("Response contains more items. Use -All to retrieve all pages, or -Top to limit results.");

        foreach (var item in items)
            OutputItem(item, sourceId);
    }

    /// <summary>
    /// The elements of a Graph collection envelope ({"value":[...]}), or null when the payload is a
    /// single entity. <paramref name="truncated"/> reports whether the envelope carried @odata.nextLink.
    /// The gate is structural: an entity whose own 'value' property happens to be an array is
    /// indistinguishable from an envelope and unwraps too (use -Raw to see such a payload whole).
    /// </summary>
    internal static List<JsonElement>? TryUnwrapCollection(JsonElement json, out bool truncated)
    {
        truncated = false;

        if (json.ValueKind != JsonValueKind.Object
            || !json.TryGetProperty("value", out var valueArray)
            || valueArray.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        truncated = json.TryGetProperty("@odata.nextLink", out _);
        return valueArray.EnumerateArray().ToList();
    }

    private void OutputItem(JsonElement element, string? sourceId)
    {
        if (Raw.IsPresent)
        {
            WriteObject(element.GetRawText());
            return;
        }

        var ht = JsonToHashtable(element);

        if (sourceId != null)
        {
            // Unique prefix avoids collision with Graph entity properties. The indexer
            // overwrites, so a repeated key does not need removing first.
            ht["_MgxSourceId"] = sourceId;
        }

        // Single-argument WriteObject does not enumerate, so the Hashtable is emitted whole
        WriteObject(ht);
    }

    /// <summary>
    /// Serialized request body for a write method, or null when no content should be sent.
    /// Graph requires Content-Type: application/json on POST/PATCH/PUT even with an empty body,
    /// so those default to "{}". DELETE sends no content.
    /// </summary>
    private string? ResolveRequestBody(HttpMethod method)
    {
        var serialized = Body != null ? SerializeBody(Body) : null;
        if (!string.IsNullOrWhiteSpace(serialized))
            return serialized;

        return method != HttpMethod.Delete ? "{}" : null;
    }

    /// <summary>
    /// Options for -Body serialization, chosen for what Graph accepts rather than STJ's
    /// defaults: enums as camelCase names (Graph never takes them numerically), TimeSpan as
    /// an Edm.Duration string, a Kind-less DateTime pinned to UTC (Graph rejects a bare
    /// timestamp), and readable non-ASCII instead of \uXXXX - both forms decode the same,
    /// but dead-letter files and -Debug traces are read by people.
    /// </summary>
    internal static readonly JsonSerializerOptions BodyJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters =
        {
            // Claims [Flags] enums only; every other enum falls through to the converter below.
            new GraphFlagsEnumConverter(),
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
            new GraphDurationConverter(),
            new GraphDateTimeConverter(),
        },
    };

    /// <summary>
    /// OData spells a flags combination "ignoreCase,multiline"; JsonStringEnumConverter writes
    /// ", " between the names, which Graph will not parse back into a flags-typed enum.
    ///
    /// Only that multi-name form is rewritten, and only its separator. Each name in it is
    /// written by the converter STJ would otherwise have used, one member at a time, so a
    /// combination spells its members exactly as they spell themselves alone. Resolving them
    /// here from Enum.ToString did not: it picks the other name when two members share a value,
    /// and it cannot see [JsonStringEnumMemberName], so a member sent as "read-only" alone went
    /// out as "read" in a combination, which is not a name the service was ever given.
    ///
    /// Everything else - a single member, an alias, a combination with a name of its own, a
    /// value with bits no member covers - is handed to that converter whole, so name resolution
    /// and the numeric fallback stay byte-for-byte what they were. Formatting the number here
    /// instead would carry the current culture's negative sign, which sv-SE writes as U+2212.
    ///
    /// Components are taken largest first, so a member covering several bits wins over the bits
    /// themselves, and are written in ascending value order: {A=1,B=2,C=4,BC=6} at 7 is "a,bc".
    /// </summary>
    private sealed class GraphFlagsEnumConverter : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert)
            => typeToConvert.IsEnum && typeToConvert.IsDefined(typeof(FlagsAttribute), inherit: false);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
            => (JsonConverter)Activator.CreateInstance(
                typeof(FlagsConverter<>).MakeGenericType(typeToConvert),
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase).CreateConverter(typeToConvert, options))!;

        private sealed class FlagsConverter<T> : JsonConverter<T> where T : struct, Enum
        {
            /// <summary>Every declared value with bits of its own, largest first.</summary>
            private static readonly ulong[] s_members = Enum.GetValues<T>()
                .Select(ToBits).Where(bits => bits != 0).Distinct().OrderByDescending(bits => bits).ToArray();

            private readonly JsonConverter<T> _inner;

            public FlagsConverter(JsonConverter<T> inner) => _inner = inner;

            public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
                => _inner.Read(ref reader, typeToConvert, options);

            public override T ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
                => _inner.ReadAsPropertyName(ref reader, typeToConvert, options);

            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            {
                if (JoinNames(value, options) is { } joined) writer.WriteStringValue(joined);
                else _inner.Write(writer, value, options);
            }

            public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            {
                if (JoinNames(value, options) is { } joined) writer.WritePropertyName(joined);
                else _inner.WriteAsPropertyName(writer, value, options);
            }

            /// <summary>The comma-joined form, or null when the value is not a multi-name one.</summary>
            private string? JoinNames(T value, JsonSerializerOptions options)
            {
                var remaining = ToBits(value);
                var names = new List<string>();

                foreach (var member in s_members)
                {
                    if (remaining == 0) break;
                    if ((remaining & member) != member) continue;
                    if (NameOf(member, options) is not { } name) return null;

                    // Chosen largest first, written smallest first.
                    names.Insert(0, name);
                    remaining &= ~member;
                }

                // One name is not this converter's business - a lone member, an alias, a
                // combination named in its own right, zero - and neither is a value with bits
                // left over, which has no names at all and belongs in the numeric fallback.
                return remaining == 0 && names.Count > 1 ? string.Join(",", names) : null;
            }

            /// <summary>What the inner converter writes for one member alone, unquoted.</summary>
            private string? NameOf(ulong member, JsonSerializerOptions options)
            {
                var buffer = new System.Buffers.ArrayBufferWriter<byte>(initialCapacity: 32);
                using (var writer = new Utf8JsonWriter(buffer))
                    _inner.Write(writer, (T)Enum.ToObject(typeof(T), member), options);

                var reader = new Utf8JsonReader(buffer.WrittenSpan);
                return reader.Read() && reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }

            /// <summary>The value's bits, sign-extended rather than refused for a signed enum.</summary>
            private static ulong ToBits(T value) => ((IConvertible)value).GetTypeCode() switch
            {
                TypeCode.Byte or TypeCode.UInt16 or TypeCode.UInt32 or TypeCode.UInt64
                    => ((IConvertible)value).ToUInt64(provider: null),
                _ => unchecked((ulong)((IConvertible)value).ToInt64(provider: null)),
            };
        }
    }

    /// <summary>Edm.Duration is ISO-8601 ("PT1H"); STJ's default "01:00:00" is refused.</summary>
    private sealed class GraphDurationConverter : JsonConverter<TimeSpan>
    {
        public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => System.Xml.XmlConvert.ToTimeSpan(reader.GetString()!);

        public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
            => writer.WriteStringValue(System.Xml.XmlConvert.ToString(value));
    }

    /// <summary>
    /// A DateTime with Kind=Unspecified would serialize with no offset, which Graph rejects.
    /// Assume UTC - the read side already resolves timestamps to UtcDateTime, so a value that
    /// round-trips through mgx keeps its meaning.
    /// </summary>
    private sealed class GraphDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.GetDateTime();

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
                : value);
    }

    /// <summary>
    /// Serialize a -Body argument to the JSON that goes on the wire. A raw JSON string is passed
    /// through verbatim, everything else is serialized.
    /// </summary>
    internal static string SerializeBody(object body)
    {
        var value = UnwrapPSObject(body);
        RefuseUnserializable(value, "-Body");
        if (value is string s) return s;
        // Still a PSObject after unwrapping: a PSCustomObject, whose members live on the wrapper
        if (value is PSObject pso) return JsonSerializer.Serialize(PSOToDict(pso), BodyJsonOptions);
        if (value is IDictionary dict) return JsonSerializer.Serialize(DictionaryToDict(dict), BodyJsonOptions);
        // Handle array body (object[], ArrayList, List<object>, ... from PowerShell)
        if (value is IEnumerable seq and not byte[]) return JsonSerializer.Serialize(EnumerableToArray(seq), BodyJsonOptions);
        return JsonSerializer.Serialize(value, BodyJsonOptions);
    }

    internal static Dictionary<string, object?> PSOToDict(PSObject pso, string path = "-Body")
    {
        // Every property kind, matching what ConvertTo-Json reads - so -Body $obj and
        // -Body ($obj | ConvertTo-Json) produce the same members. ScriptProperties are
        // evaluated; one whose getter throws serializes as null rather than failing the
        // whole body.
        var dict = new Dictionary<string, object?>();
        foreach (var prop in pso.Properties)
        {
            object? raw;
            try { raw = prop.Value; }
            catch (GetValueException) { raw = null; }
            dict[prop.Name] = UnwrapValue(raw, $"{path}.{prop.Name}");
        }
        return dict;
    }

    /// <summary>
    /// Flatten any IDictionary (Hashtable or ordered dictionary) into a serializable
    /// dictionary, unwrapping nested PowerShell values.
    /// </summary>
    internal static Dictionary<string, object?> DictionaryToDict(IDictionary source, string path = "-Body")
    {
        var dict = new Dictionary<string, object?>();
        foreach (DictionaryEntry entry in source)
            dict[entry.Key.ToString()!] = UnwrapValue(entry.Value, $"{path}.{entry.Key}");
        return dict;
    }

    /// <summary>
    /// Unwraps a PowerShell value to its underlying .NET representation.
    /// A PSCustomObject keeps its members on the PSObject (its BaseObject is an empty
    /// marker), so it must be read through PSOToDict rather than its BaseObject.
    /// </summary>
    private const int MaxBodyDepth = 64;

    internal static object? UnwrapValue(object? value, string path = "-Body")
    {
        // Depth from the path: each nesting level appends a segment. A self-referencing
        // hashtable ($h.self = $h) recursed to a StackOverflowException, which no catch
        // can stop - the process died. 64 levels is far beyond any real Graph body.
        if (path.Length > MaxBodyDepth * 8)
        {
            var depth = path.Count(c => c is '.' or '[');
            if (depth > MaxBodyDepth)
                throw new ArgumentException(
                    $"The value at '{path[..64]}...' nests deeper than {MaxBodyDepth} levels. "
                    + "Is the body self-referencing?");
        }
        if (value is PSObject pso)
        {
            var unwrapped = UnwrapPSObject(pso);
            return ReferenceEquals(unwrapped, pso) ? PSOToDict(pso, path) : UnwrapValue(unwrapped, path);
        }
        RefuseUnserializable(value, path);
        if (value is IDictionary dict)
            return DictionaryToDict(dict, path);
        // byte[] is Edm.Binary and serializes as base64; flattening it into object?[]
        // would emit a JSON array of integers instead.
        if (value is IEnumerable seq and not (string or byte[]))
            return EnumerableToArray(seq, path);
        return value;
    }

    /// <summary>
    /// Values that would serialize to something other than what they mean. A SecureString
    /// reflects to {"Length":8} - the request "succeeds" carrying garbage instead of the
    /// secret, or worse, would carry the secret if it round-tripped. NaN and Infinity have
    /// no JSON representation, and STJ's own refusal does not say which property.
    /// </summary>
    private static void RefuseUnserializable(object? value, string path)
    {
        switch (value)
        {
            case System.Security.SecureString:
            case PSCredential:
            case ScriptBlock:
            case System.Security.Cryptography.X509Certificates.X509Certificate:
                throw new ArgumentException(
                    $"The value at '{path}' is a {value.GetType().Name}, which does not serialize "
                    + "to JSON meaningfully. Convert it to what the endpoint expects before sending.");
            case double d when double.IsNaN(d) || double.IsInfinity(d):
                throw new ArgumentException($"The value at '{path}' is {d}; JSON has no representation for it.");
            case float f when float.IsNaN(f) || float.IsInfinity(f):
                throw new ArgumentException($"The value at '{path}' is {f}; JSON has no representation for it.");
        }
    }

    /// <summary>
    /// Flatten any non-string sequence (object[], ArrayList, List&lt;object&gt;, ...) into an array of
    /// unwrapped values.
    /// </summary>
    private static object?[] EnumerableToArray(IEnumerable source, string path = "-Body") =>
        source.Cast<object?>().Select((v, i) => UnwrapValue(v, $"{path}[{i}]")).ToArray();

    #endregion

}
