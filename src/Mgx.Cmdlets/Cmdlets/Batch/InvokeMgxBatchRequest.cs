using System.Collections;
using System.Management.Automation;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mgx.Cmdlets.Base;
using Mgx.Engine.Http;
using Mgx.Engine.Models;
using Mgx.Engine.Pagination;
using Polly.CircuitBreaker;

namespace Mgx.Cmdlets.Cmdlets.Batch;

/// <summary>
/// Invoke-MgxBatchRequest: Bundle multiple Graph API requests into /$batch calls.
/// Supports GET, POST, PATCH, PUT, DELETE with optional request bodies.
/// Auto-chunks into 20-request batches per Graph API limit.
/// Returns Hashtables with Url, Method, Status, and Body keys per request.
/// Preferred over fan-out (Invoke-MgxRequest) for bulk writes: 3-4x faster due to fewer HTTP round-trips.
///
/// Pipeline input can be:
///   - String URLs (for GET, or combined with -Method/-Body for same method/body on all)
///   - Hashtables or PSObjects with Url, Method, Body members (for per-item method/body)
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "MgxBatchRequest", SupportsShouldProcess = true)]
[OutputType(typeof(Hashtable))]
public class InvokeMgxBatchRequest : MgxCmdletBase
{
    /// <summary>Dead-letter lines are read by people; keep non-ASCII readable, matching
    /// the request serializer's escaping.</summary>
    private static readonly JsonSerializerOptions s_deadLetterJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Graph API URLs to batch. Accepts absolute URLs (https://graph.microsoft.com/v1.0/users/id)
    /// or relative URLs (/users/id). Also accepts Hashtables or PSObjects with Url/Method/Body members.
    /// </summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    [Alias("Url")]
    public object[] Uri { get; set; } = [];

    /// <summary>
    /// HTTP method for all requests (when piping string URLs). Default: GET.
    /// Ignored when pipeline input carries its own Method member.
    /// </summary>
    [Parameter]
    [ValidateSet("GET", "POST", "PATCH", "PUT", "DELETE")]
    public string Method { get; set; } = "GET";

    /// <summary>
    /// Request body for all requests (when piping string URLs).
    /// Ignored when pipeline input carries its own Body member.
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
    /// Path to a JSONL file where the batch's failed and never-sent items are appended:
    /// anything the server answered with a status >= 400, the items of a chunk whose own POST
    /// failed, which carry that failure's status, and the items of chunks that were never
    /// POSTed, which carry status 0. Nothing the server confirmed is written, so the file holds
    /// only work that is still outstanding.
    ///
    /// Each line holds Timestamp, Url, Method, Status, Body - when the request had one, with
    /// credential-named fields and pre-authenticated URLs redacted as a -Debug trace redacts
    /// them, or the redaction marker whole when the URL names the secret or the body could not
    /// be read for redaction, which is warned about by item - and Error, in that order. Error is
    /// there only when the item's own response body carried an error code or message: a
    /// never-sent item at status 0 has no response at all, and neither does an item of a refused
    /// chunk the server did not answer, so those lines end at Body.
    ///
    /// It is a record of what to retry rather than a request to replay: a redacted Body is no
    /// longer the body that was sent. Re-piping it for retry:
    ///   Get-Content dead.jsonl | ConvertFrom-Json | Invoke-MgxBatchRequest
    /// A line carrying the redaction marker does not go back out that way: it is refused as an
    /// error record. A Body that is the marker whole is not JSON, so the record names no field;
    /// one carrying the marker in a field parses, and that record names the field - or names
    /// the key, since a pre-authenticated URL used as a field name is cut the same way a value
    /// is. Under -ErrorAction Stop, the first such refusal ends the pipeline before any chunk of
    /// the batch is sent. Rebuild those requests from their Url and Method, supplying the
    /// credential again.
    ///
    /// A status >= 400 does not say the write was not applied - the POST may have reached the
    /// server and gone unanswered - so resending one is a decision about duplicates rather than
    /// a free retry. Only status 0 says nothing was sent; a body the envelope could not
    /// serialize surfaces as a chunk failure at 503 like a refused POST.
    ///
    /// A file system path, and one with something in it: an empty string resolves to the
    /// working directory, and a path on another provider - Env:\X - resolves to a bare name
    /// this run would make a file of wherever it happened to be standing.
    /// </summary>
    [Parameter]
    [ValidateNotNullOrEmpty]
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

        // The dead-letter file, held open for the run. Declared out here so the finally can
        // close it on a run that never reached its own write, and so the three things that
        // decide whether an empty file is left behind - was it this run that created it, did
        // this run put a line in it, and what did the file hold when this run's own handle last
        // looked - are readable from there too. The stream beside the writer is that handle:
        // the writer buffers characters, the stream is what can be asked the file's length.
        StreamWriter? deadLetterWriter = null;
        FileStream? deadLetterStream = null;
        string? deadLetterFileThisRunCreated = null;
        var deadLetterLines = 0;
        long? deadLetterLengthAtClose = null;

        try
        {
            if (_collected.Count == 0)
            {
                base.EndProcessing();
                return;
            }

            // Resolved before any network call, and told to be a file system path. Every other
            // provider resolves to something this run cannot append to, and resolves it
            // silently: "Env:\X" comes back as "X", a name relative to nothing, so the run made
            // a file called X in whatever directory it was standing in and reported the dead
            // letters written - to a file nobody named, under a name taken from a variable.
            string? resolvedDeadLetterPath = null;
            if (DeadLetterPath != null)
            {
                resolvedDeadLetterPath = SessionState.Path.GetUnresolvedProviderPathFromPSPath(
                    DeadLetterPath, out var provider, out _);
                if (provider.ImplementingType.FullName != FileSystemProviderType)
                {
                    ThrowTerminatingError(new ErrorRecord(
                        new ArgumentException(
                            $"-DeadLetterPath '{DeadLetterPath}' is not a file system path."),
                        "DeadLetterPathNotFileSystem", ErrorCategory.InvalidArgument, DeadLetterPath));
                    return;
                }
            }

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

            // Convert to BatchOperation list. An item whose body is not valid JSON fails on
            // its own (non-terminating error) instead of aborting the whole batch; `submitted`
            // keeps result indices aligned with the operations actually sent.
            var operations = new List<BatchOperation>(_collected.Count);
            var submitted = new List<BatchInput>(_collected.Count);
            foreach (var input in _collected)
            {
                JsonElement? body = null;
                if (input.Body != null)
                {
                    try
                    {
                        var json = InvokeMgxRequest.SerializeBody(input.Body);
                        body = JsonSerializer.Deserialize<JsonElement>(json);
                    }
                    catch (Exception ex) when (ex is JsonException or ArgumentException)
                    {
                        // ArgumentException: a value serialization refuses (SecureString, NaN),
                        // named by property path. JsonException: a string body that is not JSON.
                        WriteError(new ErrorRecord(
                            new ArgumentException(
                                $"Body for {input.Method} {input.Url} is not valid JSON: {ex.Message}", ex),
                            "InvalidBatchItemBody", ErrorCategory.InvalidArgument, input.Url));
                        continue;
                    }

                    // A dead-letter line piped back in. The marker stands where something the
                    // file must not carry used to be, so this is not the body that was sent,
                    // and sending it writes the literal marker to the tenant - under the field
                    // name that says what it replaced, or, when the file cut a capability URL
                    // used as a field name, AS the field name. A whole-body marker is refused
                    // already, because the marker alone is not JSON; a marker in a field or in
                    // a key parses, and the recipe in -DeadLetterPath's help ends at the same
                    // place whichever it is.
                    //
                    // The walk reads every string of the body, and a string the JSON parser
                    // accepted is not always one it will hand over: an unpaired surrogate
                    // escape, "Jos\ud83d", parses as a string value and throws when read. That
                    // is one item's body and nothing more, so it is refused like a body that is
                    // not JSON at all - the guard is the only thing that decides whether this
                    // body may be sent, and a body it cannot read is one it cannot clear.
                    string? markerField;
                    try
                    {
                        markerField = FindRedactionMarker(body!.Value);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or JsonException)
                    {
                        WriteError(new ErrorRecord(
                            new ArgumentException(
                                $"Body for {input.Method} {input.Url} could not be read for the "
                                + $"redaction check: {Reason(ex.Message)}. Send it as a hashtable "
                                + "or a string this run can read.", ex),
                            "InvalidBatchItemBody", ErrorCategory.InvalidArgument, input.Url));
                        continue;
                    }

                    if (markerField != null)
                    {
                        WriteError(new ErrorRecord(
                            new ArgumentException(
                                $"Body for {input.Method} {input.Url} carries the redaction marker "
                                + $"'{RedactionMarker}' at '{markerField}'. A dead-letter line is not the "
                                + "body that was sent: rebuild the request from its Url and Method, "
                                + "supplying the credential again. A body of your own must not carry "
                                + "that text."),
                            "InvalidBatchItemBody", ErrorCategory.InvalidArgument, input.Url));
                        continue;
                    }
                }

                operations.Add(new BatchOperation(NormalizeToRelativeUrl(input.Url), input.Method, body));
                submitted.Add(input);
            }

            if (operations.Count == 0)
                return;

            // The gate covers reads as well as writes. A read-only batch changes nothing on the
            // server, but it spends resource units, can be throttled, and emits objects into
            // the pipeline, none of which a caller who typed -WhatIf asked for. The -WhatIf
            // help says so, and says Invoke-MgxRequest makes the other choice.
            //
            // Over the items that will be sent, not the items that were piped in. The body
            // validation and the marker guard have run by now, and a target counting what
            // they refused named requests the run was never going to make, at the one
            // surface whose whole purpose is saying what is about to happen. The refusals
            // are named as their own count beside it: a caller who typed -WhatIf to find
            // out what would happen is owed both halves of the answer, and the error
            // records for them are already in the stream, since a refusal is not something
            // the gate governs.
            var writeOps = submitted.Where(c =>
                !string.Equals(c.Method, "GET", StringComparison.OrdinalIgnoreCase)).ToList();
            var refused = _collected.Count - submitted.Count;

            string target;
            if (writeOps.Count == 0)
            {
                target = $"GET {Requests(submitted.Count)} via $batch";
            }
            // Only when the batch is that method and nothing else. The count is everything
            // that will be sent, reads included, by the reasoning above, and a write verb
            // welded onto it described requests that were not in it: one DELETE among nineteen
            // GETs read "DELETE 20 requests via $batch". With reads present the writes are
            // named as their own count, the way a batch of several write methods already names
            // them.
            else if (writeOps.Count == submitted.Count
                && writeOps.All(o => string.Equals(o.Method, writeOps[0].Method, StringComparison.OrdinalIgnoreCase)))
            {
                target = $"{writeOps[0].Method} {Requests(submitted.Count)} via $batch";
            }
            else
            {
                var breakdown = writeOps
                    .GroupBy(o => o.Method.ToUpperInvariant())
                    .OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Count()} {g.Key}");
                target = $"{Requests(submitted.Count)} ({string.Join(", ", breakdown)}) via $batch";
            }

            if (refused > 0)
                target += $"; {refused} refused";

            if (!ShouldProcess(target, "Send batch"))
                return;

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


            // Opened before the first POST, and the handle kept for the run. Opened where the
            // lines are written - at the end - an unusable path was discovered with the batch
            // already applied: the caller had made every one of those changes and lost the
            // record of which to retry, and the only thing that could have made that
            // recoverable was learning it before anything went out. A directory standing at the
            // path, a parent directory that is not there, a file this account may not write:
            // all of them answer here, with nothing sent.
            //
            // Append, because the parameter's help promises a file a second run adds to rather
            // than replaces, and shared the way each platform has to share it for that promise
            // to hold: see DeadLetterHoldShare.
            if (resolvedDeadLetterPath != null)
            {
                var existed = File.Exists(resolvedDeadLetterPath);
                try
                {
                    deadLetterStream = new FileStream(
                        resolvedDeadLetterPath, FileMode.Append, FileAccess.Write,
                        DeadLetterHoldShare);
                    deadLetterWriter = new StreamWriter(deadLetterStream);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    WriteError(new ErrorRecord(
                        new IOException(
                            $"Failed to write dead-letter file '{resolvedDeadLetterPath}': "
                            + $"{Reason(ex.Message)} before the batch was sent; nothing was sent.", ex),
                        "DeadLetterWriteFailed", ErrorCategory.WriteError, resolvedDeadLetterPath));
                    return;
                }
                if (!existed)
                    deadLetterFileThisRunCreated = resolvedDeadLetterPath;
            }

            var batchResult = batchClient.ExecuteBatchIndexedAsync(operations, CancellationToken)
                .GetAwaiter().GetResult();

            var results = batchResult.Results;
            var telemetry = batchResult.Telemetry;

            // Before anything else this run does with the batch. What the counters hold is
            // what the wire did, and it did it whatever becomes of the run afterwards - so the
            // session view has to be told here rather than at the end of the reporting, past
            // the dead-letter write, the chunk failure and one error record per failed item.
            // Every one of those can end the run under -ErrorAction Stop, and a throttled,
            // retried batch that ended on one contributed nothing at all to Get-MgxTelemetry:
            // the 429s it met and the seconds it spent in Retry-After sleeps were the very
            // numbers a caller reads that view to find. Nothing between the call above and
            // this can throw, so the record needs no finally to be sure of running.
            RecordSessionTelemetry(telemetry);

            // Every line this run prints about itself reads these: the dead-letter line, the
            // chunk-failure target, the verbose summary and the warning. Counting for
            // themselves, they drifted - the warning said the batch-level retry pass had been
            // withheld over runs in which it had gone out, been answered, and been refused on a
            // later chunk, while the verbose line beside it credited the run with the retries.
            var summary = BatchRunSummary.Of(results, telemetry, batchResult.ChunkFailure != null);

            // Output all results as Hashtables (success and failure)
            for (int i = 0; i < results.Count; i++)
            {
                var (_, item) = results[i];
                var input = submitted[i];

                var result = new Hashtable(StringComparer.OrdinalIgnoreCase)
                {
                    ["Url"] = input.Url,
                    ["Method"] = input.Method,
                    ["Status"] = item.Status,
                    ["Body"] = item.Body.HasValue && item.Body.Value.ValueKind != JsonValueKind.Null
                        ? JsonToHashtable(item.Body.Value)
                        : null
                };

                // Status 0 means the operation was never sent, because another chunk failed
                // first. It is not a success and must not read as one - the caller has to be
                // able to tell a write that may have landed from one that certainly did not.
                if (item.Status == GraphBatchClient.NotSentStatus)
                    result["NotSent"] = true;

                // Single-argument WriteObject does not enumerate, so the Hashtable is emitted whole
                WriteObject(result);
            }

            // Write failed items to the file opened before the batch went out. Two things about
            // this write are collected rather than reported immediately. The write failing
            // outright is an error record, written first of the errors below - the path was
            // usable when the run opened it, so a failure here is the file going out from under
            // an applied batch, which is the case a caller most needs the error stream for. A
            // body the redactor could not read is a warning per item, written last of everything
            // - WriteWarning under -WarningAction Stop throws, and a warning about one line of
            // the file must not cost the caller the item errors or the telemetry.
            var withheldBodies = new List<string>();
            Exception? deadLetterWriteFailure = null;
            if (deadLetterWriter != null)
            {
                try
                {
                    for (int i = 0; i < results.Count; i++)
                    {
                        var (_, item) = results[i];
                        if (item.Status < 400 && item.Status != GraphBatchClient.NotSentStatus) continue;

                        var input = submitted[i];
                        // The item's own URL, under the value rule the body's strings are read
                        // by. A pre-authenticated URL is a request a caller can make as well as
                        // a value a body can carry - a PUT to an upload session's uploadUrl, a
                        // GET of a @microsoft.graph.downloadUrl - and the URL written here is
                        // the one the caller gave, so the line carried the live signature while
                        // the same URL inside a body beside it was cut.
                        var deadLetter = new JsonObject
                        {
                            ["Timestamp"] = DateTime.UtcNow.ToString("o"),
                            ["Url"] = SensitiveNames.RedactCapabilityUrl(input.Url, RedactionMarker)
                                ?? input.Url,
                            ["Method"] = input.Method,
                            ["Status"] = item.Status,
                        };

                        if (input.Body != null)
                        {
                            // A URL that names a secret takes the whole Body: on resetPassword or
                            // uploadSecret the credential is the field called `k` or `value`, and
                            // no name rule reaches it. Url, Method, Status and Error still say
                            // what failed, which is what the file is read for.
                            if (UrlNamesSecret(input.Url))
                            {
                                deadLetter["Body"] = RedactionMarker;
                            }
                            else
                            {
                                // A body the redactor cannot walk takes the marker too. The walk
                                // is the only thing that decides which of a body's values may be
                                // written, so a body it cannot read is a body with nothing
                                // cleared for the file - the same position a URL naming the
                                // secret puts it in. A duplicate property name is how a body
                                // gets there: JsonNode.Parse accepts the document and building
                                // the JsonObject's dictionary to enumerate it throws, and Graph
                                // itself takes such a body, so the run must not end on one.
                                try
                                {
                                    var bodyJson = InvokeMgxRequest.SerializeBody(input.Body);
                                    var bodyNode = JsonNode.Parse(bodyJson);
                                    deadLetter["Body"] = RedactSensitiveFields(bodyNode);
                                }
                                catch (Exception ex)
                                {
                                    deadLetter["Body"] = RedactionMarker;
                                    withheldBodies.Add(
                                        $"Body for {input.Method} {input.Url} was withheld from the "
                                        + $"dead-letter file: it could not be read for redaction ({ex.Message}). "
                                        + "The line carries the redaction marker in its place.");
                                }
                            }
                        }

                        var errorMsg = TryExtractBatchErrorMessage(item);
                        if (errorMsg != null)
                            deadLetter["Error"] = errorMsg;

                        deadLetterWriter.WriteLine(deadLetter.ToJsonString(s_deadLetterJsonOptions));
                        deadLetterLines++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Collected, not written - see the comment above the declaration. The
                    // exception itself, not just its message: the error record below keeps it
                    // as the inner exception, so $Error carries what actually refused.
                    deadLetterWriteFailure = ex;
                }

                // Closed here rather than in the finally, so a failure flushing the last line
                // is this run's to report and the file is on disk before the lines below say
                // what is in it - and before an empty one is removed. The length is read off
                // the handle first, while it still answers.
                deadLetterLengthAtClose = DeadLetterLength(deadLetterStream);
                deadLetterWriter.Dispose();
                deadLetterWriter = null;
                deadLetterStream = null;

                // A file this run created and never put a line in says a batch had failures
                // when it had none, and a caller watching that path for one would find it after
                // every clean run. A file that was already there is an earlier run's record and
                // is not this run's to remove; nor is one this run could not finish writing;
                // nor is one that is no longer empty, whoever filled it - the share mode admits
                // a second run appending to the same path, so "this run created it and wrote
                // nothing" says nothing about what stands in it now.
                if (deadLetterFileThisRunCreated != null && deadLetterLines == 0
                    && deadLetterWriteFailure == null && deadLetterLengthAtClose == 0)
                {
                    TryRemoveEmptyDeadLetterFile(deadLetterFileThisRunCreated);
                    deadLetterFileThisRunCreated = null;
                }

                // The file takes both, and they are not the same thing to a caller deciding what
                // to re-pipe: a refused write may already have been applied and one that was
                // never sent certainly was not. Only after the write finished - a line stating
                // what is in a file the run could not finish writing states it of nothing.
                if (deadLetterWriteFailure == null && summary.Failed + summary.NotSent > 0)
                {
                    var written = summary.NotSent > 0
                        ? $"{summary.Failed} failed and {summary.NotSent} not-sent items"
                        : $"{summary.Failed} failed items";
                    WriteVerbose($"Wrote {written} to dead-letter file: {resolvedDeadLetterPath}");
                }
            }

            // The file the caller asked for was opened and then could not be written. An error
            // record rather than a warning, because a warning is the one thing a caller can be
            // running without: at -WarningAction SilentlyContinue, or Ignore, a run that lost
            // the whole record of what to retry said nothing about it at all.
            //
            // First of the errors this run writes, ahead of the chunk failure and the item
            // errors. Under -ErrorAction Stop the run ends here, and that is the intent - the
            // item errors it ends before are the very lines the file was going to hold, so a
            // caller told about them and not about the missing file would go looking for a
            // file that is not there. The withheld-body warnings and the fold in the outcome
            // warning still run for a caller who is not stopping.
            //
            // A run with nothing to write cannot get here: the loop above only touches the
            // handle for a failed or not-sent item, so a failure means there were lines and
            // some of them are gone. An unusable path is answered before the batch instead,
            // where nothing has been applied and there is nothing to be told to retry.
            if (deadLetterWriteFailure != null)
            {
                WriteError(new ErrorRecord(
                    new IOException(
                        $"Failed to write dead-letter file '{resolvedDeadLetterPath}': "
                        + deadLetterWriteFailure.Message, deadLetterWriteFailure),
                    "DeadLetterWriteFailed", ErrorCategory.WriteError, resolvedDeadLetterPath));
            }

            // A chunk's POST failed while other chunks were being applied. Their results are
            // above; this says why the rest never went, and NotSent names them. After the
            // dead-letter write, like the item errors below, so -ErrorAction Stop cannot cut
            // the file short.
            if (batchResult.ChunkFailure != null)
            {
                // The id stays BatchChunkFailed; the category and wrapping come from the
                // failure itself - a throttled chunk is LimitsExceeded, an open circuit
                // ResourceUnavailable with the guidance text, not NotSpecified.
                var (_, category, report) = MgxErrorPresentation.PresentItemFailure(
                    batchResult.ChunkFailure, "BatchChunkFailed", CircuitBreakerMessage);
                WriteError(new ErrorRecord(report, "BatchChunkFailed", category,
                    $"{summary.Failed} of {summary.Total} operations failed, "
                    + $"{summary.NotSent} were not sent"));
            }

            // Emit errors for failed items (enables -ErrorAction Stop, populates $Error).
            // After the dead-letter write, so -ErrorAction Stop cannot cut the file short.
            for (int i = 0; i < results.Count; i++)
            {
                var (_, item) = results[i];
                if (item.Status == GraphBatchClient.NotSentStatus)
                {
                    var skipped = submitted[i];
                    WriteError(new ErrorRecord(
                        new InvalidOperationException(
                            $"{skipped.Method} {skipped.Url} was not sent: another chunk of the batch failed."),
                        "BatchItemNotSent", ErrorCategory.NotSpecified, skipped.Url));
                }
                else if (item.Status >= 400)
                {
                    var input = submitted[i];
                    var graphMessage = TryExtractBatchErrorMessage(item);
                    var errorMessage = graphMessage != null
                        ? $"{input.Method} {input.Url}: {graphMessage}"
                        : $"HTTP {item.Status} for {input.Method} {input.Url}";
                    var itemError = new InvalidOperationException(errorMessage);
                    WriteError(new ErrorRecord(itemError, "BatchItemError",
                        MapStatusToCategory((HttpStatusCode)item.Status), input.Url));
                }
            }

            WriteBatchTelemetry(telemetry, summary, withheldBodies.Count, deadLetterWriteFailure?.Message);

            // Last of everything this run owes for the batch: WriteWarning throws under
            // -WarningAction Stop, and a warning that ends the run here must not be able to
            // cost the caller the item errors above or the telemetry WriteBatchTelemetry just
            // wrote - both already ran by the time one of these can throw.
            foreach (var withheld in withheldBodies)
                WriteWarning(withheld);
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
            // Only reached with the handle still open where the run did not get to its own
            // write - a batch that threw, a cancellation, a stop. Guarded, because a throw from
            // a finally would replace the failure the run is already carrying.
            if (deadLetterWriter != null)
            {
                deadLetterLengthAtClose = DeadLetterLength(deadLetterStream);
                try { deadLetterWriter.Dispose(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }

            // Same reasoning as the removal after the write, for the same file on the paths
            // that never reach it: an empty file this run made is a record of nothing. Under
            // the same three conditions, the length included - a run that came through the
            // write and left the file standing because something had been appended to it does
            // not get a second, unconditional go at it here.
            if (deadLetterFileThisRunCreated != null && deadLetterLines == 0
                && deadLetterLengthAtClose == 0)
                TryRemoveEmptyDeadLetterFile(deadLetterFileThisRunCreated);

            // Drain verbose messages even on exception so retry/throttle history is visible
            DrainClientMessages();
            batchClient?.DrainVerboseMessages();
            base.EndProcessing();
        }
    }

    /// <summary>
    /// What the dead-letter file holds, as this run's own handle reports it - so a line a second
    /// run appended beside this one's is counted. Null where the handle cannot say, which is not
    /// the same answer as zero: the removal below turns on the length, and a length nothing
    /// measured leaves the file where it is.
    /// </summary>
    private static long? DeadLetterLength(FileStream? stream)
    {
        try { return stream?.Length; }
        catch (Exception ex) when (ex is IOException or NotSupportedException
                                     or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Remove a dead-letter file this run created and put nothing in - while it is still empty
    /// and no second run has it open. Silent either way: an empty file holds nothing a caller
    /// has to be told about, and a path that will not give it up is a path the next run will
    /// simply append to.
    /// <para>
    /// The claim is what makes the removal honest. The file is opened for append under
    /// <see cref="DeadLetterHoldShare"/>, which admits a second run appending to the same path -
    /// the shape a caller gets from running batches in parallel over one -DeadLetterPath - and
    /// unlink honors no lock, so "this run created it and wrote no lines" is not on its own a
    /// statement about what stands in the file by the end. FileShare.None asks for the entry
    /// exclusively: a sibling still holding it for append refuses this open and the file is
    /// left, and under a claim that succeeds the length is read once more, so a sibling that
    /// wrote and closed between the two readings is not deleted either. Only then is the path
    /// unlinked, under the claim, in the order <see cref="ScratchName.Discard"/> uses - so no
    /// instant exists in which this run has decided on the name and let go of it.
    /// </para>
    /// </summary>
    /// <summary>
    /// What a second opener of the dead-letter file may do while this run holds it, which is not
    /// the same question on the two platforms because the lock is not the same lock. The
    /// -DeadLetterPath help promises a file a second run adds to rather than replaces, and two
    /// batches over one path is what running them in parallel gives, so a sibling appending to
    /// it has to get in - and the removal below has to be refused for as long as one is there.
    /// <para>
    /// .NET on Unix backs FileShare with an advisory flock: FileShare.None takes LOCK_EX and
    /// every other mode takes LOCK_SH, and LOCK_SH refuses no second writer. FileShare.Read is
    /// therefore what admits the sibling while still refusing the LOCK_EX the removal's claim
    /// asks for - FileShare.ReadWrite would take no lock at all, and the claim would succeed
    /// over a sibling's open file and delete it on a length read before that sibling had
    /// written.
    /// </para>
    /// <para>
    /// Windows checks the sharing both ways round: a second opener's access has to be one this
    /// handle's share mode admits, and this handle's access has to be one the second opener's
    /// share mode admits. FileShare.Read admits a reader and no appender at all, so a second
    /// batch over one -DeadLetterPath could not open the file and wrote none of its dead letters
    /// - the shape the help promises, refused on the platform. FileShare.ReadWrite lets the
    /// sibling in, and the removal's claim is refused all the same: it asks FileShare.None,
    /// which admits none of the writing this handle is already doing.
    /// </para>
    /// </summary>
    private static FileShare DeadLetterHoldShare =>
        OperatingSystem.IsWindows() ? FileShare.ReadWrite : FileShare.Read;

    private static void TryRemoveEmptyDeadLetterFile(string path)
    {
        FileStream? claim = null;
        try
        {
            claim = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (claim.Length != 0) return;
            ScratchName.Discard(path, claim);
            claim = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { claim?.Dispose(); }
    }

    /// <summary>
    /// Parse pipeline input into a BatchInput. Supports:
    /// - String: use as URL with shared -Method/-Body parameters
    /// - Hashtable or PSObject with a Url member: use per-item Url/Method/Body
    /// </summary>
    internal BatchInput? ParsePipelineInput(object item)
    {
        var value = UnwrapPSObject(item);

        if (value is string url)
        {
            return new BatchInput(url, Method, Body);
        }

        // Structured batch input: hashtable (including this cmdlet's own output) or PSCustomObject
        if (value is IDictionary or PSObject)
        {
            var urlValue = TryGetMember(value, "Url")?.ToString();
            if (urlValue != null)
            {
                var method = (TryGetMember(value, "Method")?.ToString() ?? Method).ToUpperInvariant();
                if (method is not ("GET" or "POST" or "PATCH" or "PUT" or "DELETE"))
                {
                    WriteWarning($"Skipping invalid HTTP method '{method}' for URL: {urlValue}");
                    return null;
                }
                var body = TryGetMember(value, "Body");
                return new BatchInput(urlValue, method, body);
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
    /// "1 request" or "n requests". The gate names a count that can be one - what a batch will
    /// send is what the validation and the guard left of it - and "1 requests" is a sentence
    /// about a number rather than about the batch.
    /// </summary>
    private static string Requests(int count) => count == 1 ? "1 request" : $"{count} requests";

    /// <summary>What stands in the dead-letter file for a value it must not carry.</summary>
    internal const string RedactionMarker = "***REDACTED***";

    /// <summary>
    /// The one provider -DeadLetterPath may name. Matched on the implementing type rather than
    /// on the provider's name, which a module is free to reuse.
    /// </summary>
    private const string FileSystemProviderType = "Microsoft.PowerShell.Commands.FileSystemProvider";

    /// <summary>
    /// Where a body carries the redaction marker, as a string value or inside a property name,
    /// or null when it carries none. Every depth, and an array's elements as well as an
    /// object's properties: the writer puts the marker wherever the walk found a credential,
    /// and a keyCredentials array is where it usually finds one. The path is what the caller
    /// needs - it says what the file took, and so what has to be supplied again.
    ///
    /// A name and a value are read the same way, as substrings, because the writer leaves the
    /// marker inside text as often as alone. A pre-authenticated URL is cut at its capability
    /// parameter and what stands in the file is the URL up to that parameter with the marker
    /// after it - whether the URL was a field's value, an element of an array, or the field
    /// NAME itself, which is what the cut on a key leaves. Reading a value for equality saw
    /// only the cuts that took a whole value, so a line holding a cut uploadUri,
    /// azureStorageUri or @microsoft.graph.downloadUrl read as a body that could be sent, and
    /// sending it puts the literal marker where the signature belongs.
    ///
    /// A caller's own text carrying the marker is refused with the rest. The file's cut form is
    /// a prefix plus the marker, so nothing in the text separates the two, and refusing a body
    /// no file wrote costs the caller a rename; sending one the file did write costs a request
    /// the tenant cannot honor and a credential the caller still thinks it supplied.
    /// </summary>
    internal static string? FindRedactionMarker(JsonElement element, string path = "")
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString()!.Contains(RedactionMarker, StringComparison.Ordinal)
                    ? (path.Length == 0 ? "the whole body" : path)
                    : null;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var here = path.Length == 0 ? property.Name : $"{path}.{property.Name}";

                    // Before the value, so that a cut key whose own value says nothing is
                    // still reported as the key: what the cut leaves is the URL up to the
                    // capability parameter with the marker after it, and a cut that collided
                    // carries a #2, #3... suffix past that. Either way the name itself is the
                    // path returned, so the record names the key as written.
                    if (property.Name.Contains(RedactionMarker, StringComparison.Ordinal))
                        return here;

                    var found = FindRedactionMarker(property.Value, here);
                    if (found != null) return found;
                }
                return null;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    var found = FindRedactionMarker(item, $"{path}[{index++}]");
                    if (found != null) return found;
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// True when a field name or URL path segment carries one of the credential fragments. The
    /// fragments are SensitiveNames', and so are the two value rules beside them: a property
    /// whose NAME carries one of the fragments, a property whose name carries "downloadurl", and
    /// a string value that is a URL carrying one of the capability parameters. That class carries
    /// the reasoning for each.
    ///
    /// This file and a -Debug trace agree on the value rule for the form a body writes a URL in,
    /// since a URL is text either way, and on the two name rules only where the value beside the
    /// named key is itself a quoted string - a trace regex for a name rule matches a key and its value together, and
    /// cannot match past a value that is not a string. This file does not stop there: the value
    /// beside a matched key is cleared whatever it holds, so a number, a bool, an array or an
    /// object beside a password or a downloadUrl field is missing from the dead-letter line and
    /// still stands whole in -Debug output.
    ///
    /// Two things only the file has, because a trace has nothing to apply them to. The URL rule
    /// below reads the request's own path and takes the whole Body; the marker also stands for a
    /// body the walk could not read. The tracer sees a batch envelope as text - one item's url
    /// is a string beside its body, not a request whose body the tracer knows - so an
    /// uploadSecret body still reaches -Debug output whole, and nothing here changes that.
    /// </summary>
    private static bool IsSensitiveName(string name) => SensitiveNames.Matches(name);

    /// <summary>
    /// True when the request's own path names the secret its body carries - resetPassword,
    /// changePassword, uploadSecret, synchronization/secrets, addKey, vppTokens. Those bodies
    /// hold the credential under a field name that says nothing about it, `k` or `value`, which
    /// no name rule can reach; the caller of this decides on the whole Body instead.
    ///
    /// The path and nothing else. For an http or https URL that is what the parser gives after
    /// the authority, so the host is out of it: a tenant that reaches Graph through a host
    /// named keyvault.example.com is describing the machine, not the body, and losing every
    /// body under it costs the file the half it is read for. Everything else is cut at the
    /// first ? or #, a query string carrying filters rather than bodies.
    ///
    /// The scheme is checked because a parse alone does not say the string was absolute. On
    /// Unix a path is a legal file URI, so "/users/u1?$select=keyCredentials" parses as
    /// absolute with the query folded into an escaped AbsolutePath - the segment then reads as
    /// keyCredentials and the whole body goes, on a $select. On Windows the same string does
    /// not parse, and one relative URL would have been judged two ways on two hosts.
    ///
    /// Every segment is read decoded, because Graph reads the path decoded and the two forms
    /// are one request: .../microsoft.graph.reset%50assword is a password reset. The parser
    /// unescapes an unreserved character itself, so only the relative form - the one the batch
    /// envelope actually carries - could hide a name behind an escape.
    /// </summary>
    internal static bool UrlNamesSecret(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;

        string path;
        // System.Uri, not the cmdlet's -Uri parameter, which shadows the type name here.
        if (System.Uri.TryCreate(url, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == System.Uri.UriSchemeHttp || absolute.Scheme == System.Uri.UriSchemeHttps))
        {
            path = absolute.AbsolutePath;
        }
        else
        {
            path = url;
            var cut = path.IndexOfAny(['?', '#']);
            if (cut >= 0) path = path[..cut];
        }

        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsSensitiveName(DecodeSegment(segment))) return true;
        }
        return false;
    }

    /// <summary>
    /// A path segment as Graph reads it. An unpaired or invalid escape is not a segment anyone
    /// meant, so the raw text stands rather than the call failing on it.
    /// </summary>
    private static string DecodeSegment(string segment)
    {
        if (!segment.Contains('%')) return segment;
        try { return System.Uri.UnescapeDataString(segment); }
        catch (System.UriFormatException) { return segment; }
    }

    /// <summary>
    /// Redact every field whose name carries a credential token from a JSON body before writing
    /// it to the dead-letter file. Modifies the node in-place and returns the body to write,
    /// which is the same node except where a rule took the root itself: a node has no way to
    /// replace itself in a parent it does not have.
    ///
    /// Every node, at every depth: an object's properties, an array's elements whatever they
    /// are, and the root. A body is whatever the caller built, so descending only into the
    /// shapes one was expected to have leaves the rest unread - a password inside an array that
    /// is itself inside an array, {"settings":[[{"password":"..."}]]}, reached the file whole,
    /// and a body that is one bare string, a pre-authenticated URL and nothing else, has no
    /// property and no element to descend into at all and reached it whole too.
    ///
    /// A name usually decides, and takes the whole value with it. The other thing read rather
    /// than matched by name is a pre-authenticated URL, a credential with no name to give it
    /// away: an Intune azureStorageUri and a @microsoft.graph.downloadUrl fetch the bytes on the
    /// strength of the URL alone. That read applies to a property's value and, separately, to
    /// its name - a capability URL used as a key is what a -Debug trace already redacts, since
    /// it matches any quoted string in the text, key or value alike. Either way the cut keeps
    /// everything up to the capability parameter, so the line still says which host and which
    /// kind of URL failed - and where the key was the URL, the value beside it goes whole, the
    /// way a name that says credential takes its value.
    /// </summary>
    internal static JsonNode? RedactSensitiveFields(JsonNode? node)
    {
        // Before the branches below, because neither of them reaches a root that is one value.
        // The rule is the value rule, the one the trace and the file agree on outright: a body
        // that is a bare capability URL is the same credential in the same text whether it
        // stands under a property name or alone.
        if (RedactedUrlValue(node) is { } rootValue)
            return rootValue;

        if (node is JsonArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                if (RedactedUrlValue(arr[i]) is { } value)
                    arr[i] = value;
                else
                    RedactSensitiveFields(arr[i]);
            }
            return node;
        }
        if (node is not JsonObject obj) return node;
        foreach (var key in obj.Select(p => p.Key).ToArray())
        {
            // The capability-URL cut on the key runs before the fragment check below, not
            // after: "guestaccesstoken", "authkey" and a passwordreset path all contain a
            // TokenList fragment ("token", "key", "password") as plain text, so testing the
            // fragment first took that branch and left the key - secret included - untouched.
            // JsonObject has no rename and no positional insert, so the cut removes the
            // property and re-adds it under the cut name, which moves it to the end: re-adding
            // is the only way to change a key. Two keys that cut to the same string -
            // "?sig=AAA" and "?sig=BBB" both cut to "?sig=***REDACTED***" - would silently
            // collapse to one entry through that re-add, the second overwriting the first, so a
            // colliding cut name is suffixed #2, #3... instead.
            //
            // The value goes with the key, whatever it holds. A pre-authenticated URL used as a
            // field name is a credential, so whatever a body files under one is the thing that
            // URL was minted for. Deciding that on a fragment in the key's text let the secret
            // choose: "?sig=abc" carries none and kept its value, "?sig=abcsecretdef" carries
            // "secret" inside the signature and cleared it, and the two keys name the same kind
            // of URL. The whole-value rules meet here - a name that says credential takes its
            // value, and so does a name that IS one.
            if (SensitiveNames.RedactCapabilityUrl(key, RedactionMarker) is { } redactedKey)
            {
                obj.Remove(key);
                obj[UniqueKey(obj, redactedKey)] = RedactionMarker;
                continue;
            }

            if (IsSensitiveName(key) || SensitiveNames.NamesDownloadUrl(key))
            {
                obj[key] = RedactionMarker;
                continue;
            }

            if (RedactedUrlValue(obj[key]) is { } redactedValue)
                obj[key] = redactedValue;
            else
                RedactSensitiveFields(obj[key]);
        }
        return node;
    }

    /// <summary>
    /// <paramref name="candidate"/>, or that name suffixed #2, #3... until one names no property
    /// <paramref name="obj"/> already has. The order met: <paramref name="obj"/> reflects every
    /// key this walk has already assigned, so the first collision on a given cut string gets
    /// #2 and the next collision on THAT gets #3.
    /// </summary>
    private static string UniqueKey(JsonObject obj, string candidate)
    {
        if (!obj.ContainsKey(candidate)) return candidate;
        for (var n = 2; ; n++)
        {
            var next = $"{candidate}#{n}";
            if (!obj.ContainsKey(next)) return next;
        }
    }

    /// <summary>
    /// The value rule of the two the trace and the file share: a string that is a URL carrying a
    /// capability parameter, cut at that parameter. Null for every other node, which is what
    /// leaves the rest of a body readable.
    /// </summary>
    private static string? RedactedUrlValue(JsonNode? node) =>
        node is JsonValue leaf && leaf.TryGetValue<string>(out var text)
            ? SensitiveNames.RedactCapabilityUrl(text, RedactionMarker)
            : null;

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

    /// <summary>
    /// What the run did, computed once so that every line stating it states the same thing.
    /// </summary>
    /// <param name="Total">Operations submitted.</param>
    /// <param name="Succeeded">Answered with a status in [200,400).</param>
    /// <param name="Failed">Answered with a status of 400 or above, refusals included.</param>
    /// <param name="NotSent">Never POSTed, so certainly not applied.</param>
    /// <param name="BatchLevelRetries">Items the batch-level retry pass put on the wire.</param>
    /// <param name="Pass">What became of that pass.</param>
    private sealed record BatchRunSummary(
        int Total, int Succeeded, int Failed, int NotSent, int BatchLevelRetries, RetryPass Pass)
    {
        internal static BatchRunSummary Of(
            IReadOnlyList<(BatchOperation Operation, GraphBatchResponseItem Response)> results,
            BatchTelemetry telemetry,
            bool stopped)
        {
            // The not-sent count comes from the results rather than from telemetry, which
            // counts what was answered. A refused item carries a status and an unsent one does
            // not, so the results are what say which is which.
            var notSent = results.Count(r => r.Response.Status == GraphBatchClient.NotSentStatus);

            // Which of the two ways a stopped run stopped is decided by what the pass put on
            // the wire, not by the stop itself. The count is taken as each of the pass's chunks
            // is sent, so nonzero means the pass went out; a run stopped by a chunk failure
            // before the pass never reaches the pass at all, and leaves it at zero.
            var pass = !stopped ? RetryPass.RanClean
                : telemetry.BatchLevelRetries > 0 ? RetryPass.RanRefused
                : RetryPass.Withheld;

            return new BatchRunSummary(telemetry.TotalRequests, telemetry.Succeeded,
                telemetry.Failed, notSent, telemetry.BatchLevelRetries, pass);
        }
    }

    /// <summary>What became of the batch-level retry pass, which decides what the warning
    /// may claim about the attempts an item had.</summary>
    private enum RetryPass
    {
        /// <summary>Nothing stopped the run: every item had the attempts it qualified for,
        /// whether or not any of them needed the pass.</summary>
        RanClean,

        /// <summary>The pass went out and its own POST was refused. What it carried before
        /// that may have been applied on the server.</summary>
        RanRefused,

        /// <summary>A chunk failed before the pass, so it was skipped for every candidate and
        /// nothing of it reached the wire.</summary>
        Withheld,
    }

    /// <summary>
    /// Fold what the batch met on the wire into the session view Get-MgxTelemetry reads. Called
    /// as soon as the batch returns, ahead of everything this run writes about it: none of
    /// those numbers depends on how the run ends, and all of them are lost if it ends early.
    /// </summary>
    private static void RecordSessionTelemetry(BatchTelemetry telemetry)
    {
        // Propagate per-item 429 counts to session telemetry
        if (telemetry.ThrottleEncounters > 0)
            MgxTelemetryCollector.Current.RecordBatchItemThrottles(telemetry.ThrottleEncounters);

        // Propagate item-retry delay time so Get-MgxTelemetry's RetryDelayMs reflects
        // batch retry waits (they previously existed only in per-call BatchTelemetry)
        if (telemetry.TotalRetryDelayMs > 0)
            MgxTelemetryCollector.Current.RecordBatchRetryDelay(telemetry.TotalRetryDelayMs);
    }

    private void WriteBatchTelemetry(
        BatchTelemetry telemetry, BatchRunSummary summary, int withheldBodyCount, string? deadLetterWriteFailure)
    {
        // Always emit verbose summary with timing breakdown
        var elapsedSec = telemetry.TotalElapsedMs / 1000.0;
        var throughput = telemetry.TotalElapsedMs > 0 ? summary.Total / elapsedSec : 0;
        var notSentPart = summary.NotSent > 0 ? $", {summary.NotSent} not sent" : string.Empty;
        var line = $"Batch: {summary.Succeeded} succeeded, {summary.Failed} failed{notSentPart} out of {summary.Total} requests in {elapsedSec:F1}s ({throughput:F1}/sec).";
        if (telemetry.ItemRetries > 0)
            line += $" Item retries: {telemetry.ItemRetries}.";
        if (telemetry.ThrottleEncounters > 0)
            line += $" Throttle (429) encounters: {telemetry.ThrottleEncounters}.";
        if (summary.BatchLevelRetries > 0)
            line += $" Batch-level retries: {summary.BatchLevelRetries}.";
        if (telemetry.TotalRetryDelayMs > 0)
            line += $" Time in retry delays: {telemetry.TotalRetryDelayMs / 1000.0:F1}s.";
        WriteVerbose(line);

        // The one line a caller running without -Verbose sees at all, so it says both what
        // failed and what never went out - and does not credit the run with retries it withheld,
        // nor deny it the ones it sent.
        if (summary.Failed > 0 || summary.NotSent > 0)
        {
            // One sentence, so the base is built without its period and the period is added
            // once at the end. The clauses below are semicolon-joined continuations of it, and
            // a base that had already closed with a period left them reading as ".; 1 body was
            // withheld from the dead-letter file" with nothing closing the sentence after them.
            var outcome = summary.NotSent > 0
                ? $"{summary.Failed} of {summary.Total} batch items failed and {summary.NotSent} were not sent"
                : $"{summary.Failed} of {summary.Total} batch items failed";

            // Folded in here, ahead of the retry-pass sentence, rather than left to a dedicated
            // warning of its own after WriteBatchTelemetry returns: this outcome warning is the
            // first WriteWarning call a failed batch makes, so it is the one a run under
            // -WarningAction Stop is most likely to reach and stop on, and item failures are
            // exactly when a body can go unread or the dead-letter file can fail to open. A
            // caller stopped right here still learns both instead of learning neither.
            if (withheldBodyCount > 0)
            {
                outcome += withheldBodyCount == 1
                    ? "; 1 body was withheld from the dead-letter file"
                    : $"; {withheldBodyCount} bodies were withheld from the dead-letter file";
            }
            if (deadLetterWriteFailure != null)
                outcome += $"; the dead-letter file could not be written: {Reason(deadLetterWriteFailure)}";
            outcome += ".";

            // "after all retry attempts" holds only of a run that ran them, and "withheld" only
            // of a pass that never left. Between them is the pass that went out, was answered,
            // and was refused on a later chunk: the items it carried may have been applied, and
            // a caller told the pass never ran has no reason to check before resubmitting them.
            var retries = summary.Pass switch
            {
                RetryPass.Withheld =>
                    " A chunk failed, so the run stopped sending and the retry pass was withheld.",
                RetryPass.RanRefused =>
                    " The retry pass was sent and then refused, so the run stopped sending.",
                _ => " They failed after all retry attempts.",
            };
            WriteWarning(outcome + retries + " Check $Error for details on each item.");
        }
    }

    /// <summary>
    /// A failure's reason as a sentence quoting it carries it: its first line, and one trailing
    /// period off that line. The quoting sentence closes with a period of its own, and a
    /// platform message ends in one - "...is denied.", "...missing low surrogate." - so keeping
    /// both put "..denied.." mid-line. Only one is trimmed: a reason that genuinely ends in an
    /// ellipsis keeps what is left of it.
    /// </summary>
    private static string Reason(string text)
    {
        var line = FirstLine(text);
        return line.EndsWith('.') ? line[..^1] : line;
    }

    /// <summary>The first line of a possibly multi-line exception message, so a reason folded
    /// into the one-line outcome warning stays one line.</summary>
    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }

    internal sealed record BatchInput(string Url, string Method, object? Body);
}
