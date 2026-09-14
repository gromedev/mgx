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

        // -All says how far to page, -Top says how much to return. They answer different
        // questions, so -All no longer discards the cap: -Top is documented as the total
        // maximum, and Invoke-MgxRequest already honours it either way. Neither, and a single
        // page is the limit.
        int maxItems;
        bool defaultedToPageSize = false;
        if (Top > 0)
            maxItems = Top;
        else if (All.IsPresent)
            maxItems = 0; // unlimited
        else
        {
            maxItems = PageSize; // single page worth
            defaultedToPageSize = true;
        }

        // Track whether $count=true was auto-added (not user-requested).
        // If the endpoint rejects it with 400, retry without. Settled before the checkpoint is
        // looked at: which URL this run fetches is half of whether the checkpoint on disk is
        // about it, and none of this needs a Graph connection.
        bool countAutoAdded = !string.IsNullOrEmpty(Filter)
            && !ExistingQueryOptions(Uri).Contains("$count");
        bool includeAutoCount = countAutoAdded;
        bool suppressTop = false;

        // Which form of that URL the checkpoint on disk was written under, when it is this
        // export's. The loop below drops the auto-added $count on a bare 400 and the automatic
        // $top on a Request_UnsupportedQuery, so an export interrupted after a rejection
        // recorded a URL this run does not build until its own retry. Comparing the first form
        // alone called that checkpoint another export's: the reconcile recovered nothing, the
        // sweep below deleted the temp holding its items, and the retry - which does build that
        // form - matched with the append already decided from the output merely existing, so
        // the remainder of the enumeration was appended onto a previous export's file. Every
        // form goes to the same comparison, and it settles which URL this run resumes before
        // anything is promoted, trimmed or deleted.
        //
        // And decided without writing anything, like everything else above the gate: which
        // spellings of a file name the output's directory keeps apart is a question this run
        // would otherwise answer by creating a file there, and -WhatIf reports what a run would
        // do without moving the caller's directory's write time to do it. An entry already in
        // the directory answers it for free; only a directory holding nothing cannot, and there
        // the platform's own rule stands for this side of the gate while the run below asks the
        // volume itself. Said out loud, because that is the one case where the two can differ.
        if (cpPath != null && File.Exists(cpPath)
            && DirectoryCaseRuleNeedsProbe(Path.GetDirectoryName(outputPath)))
        {
            WriteVerbose(
                $"Nothing in '{Path.GetDirectoryName(outputPath)}' says whether it keeps two "
                + "spellings of a name apart, and nothing above the ShouldProcess gate writes a "
                + "file to ask it, so the checkpoint's file names are compared here under the "
                + $"{(PlatformCaseRule ? "case-sensitive" : "case-insensitive")} rule this "
                + "platform applies by default. A run that goes past the gate asks the directory "
                + "itself, so the two differ only on a volume whose rule is not that default.");
        }

        if (cpPath != null && File.Exists(cpPath)
            && AttemptFormOf(PaginationCheckpoint.Load(cpPath), outputPath, countAutoAdded,
                   mayWrite: false) is { } form)
        {
            includeAutoCount = form.IncludeCount;
            suppressTop = form.SuppressTop;
        }

        // ShouldProcess check (before requiring Graph connection, so -WhatIf works without auth).
        // The action it names is what recovery below would do, worked out without doing any of
        // it: recovery promotes, trims and deletes, and it used to run above this gate, so
        // -WhatIf rewrote the output and removed checkpoints on the way to reporting that it
        // would not. It stays above GetClient either way - "what would this do" is not a
        // question that should need a Graph connection.
        var wouldAppend = ReconcileCheckpoint(cpPath, outputPath, includeAutoCount, suppressTop, apply: false);
        if (!ShouldProcess(outputPath, wouldAppend ? "Append JSONL data" : "Export JSONL data"))
        {
            // A preview that ends here is the only one that has to say this. The pass below
            // reaches the same condition and ends the run on it, so a caller who is not
            // previewing reads it as the error it is.
            if (_wouldRemoveAtStagingName is { } wouldRemove) WriteWarning(wouldRemove);
            if (_wouldStopOnCheckpointFile is { } wouldStop) WriteWarning(wouldStop);
            return;
        }

        // Whether this run appends to the output rather than exporting into a fresh temp,
        // decided once, against every form of the URL the attempts below can build, and only
        // ever withdrawn afterwards. Recomputed from a checkpoint and an output merely
        // EXISTING, it came back true on the attempt after the reconcile had refused - and the
        // rest of the enumeration went onto whatever file was sitting at -OutputFile. A
        // refusal has to outlast the attempt that made it.
        var appendToOutput = ReconcileCheckpoint(cpPath, outputPath, includeAutoCount, suppressTop, apply: true);

        // What the reconcile just took, on a resumed run, is the output itself, and it is this
        // run's until the writing ends. Released here on every way out of the loop below,
        // including the one that never reaches it - GetClient on a session that is not connected
        // - and again from Dispose, which is what a pipeline stopped from outside reaches.
        using var writingEnds = ReleasingOutputWhenWritingEnds();

        // Init client after the gate (populates s_graphEndpoint for sovereign clouds)
        var client = GetClient();

        // Whether an attempt of this run has written a checkpoint of its own over
        // -CheckpointPath. A refusal leaves the file where it is and the very next page boundary
        // saves over the same path, so what the refusal was protecting is gone and what is there
        // is this run's - which is what the completion path has to know before it spares the
        // file on the refusal's account. Run-scoped, unlike the per-attempt flag of the same
        // reading inside the loop: that one answers which attempt saved the checkpoint now on
        // disk, and the temp it decides to keep needs exactly that.
        var tookOverCheckpoint = false;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                // The checkpoint can go between attempts - a previous one deleted it as stale,
                // or finished with it - so its existence is re-checked. Whose it is, is not
                // re-decided: that answer is above, and it can only be taken away here.
                appendToOutput = appendToOutput
                    && cpPath != null && File.Exists(cpPath) && File.Exists(outputPath);

                var url = BuildUrl(includeAutoCount, suppressTop);
                var headers = BuildHeaders();

                // Load checkpoint and compute resume state (consumer owns checkpoint lifecycle)
                ResumeState? resume = null;
                long resumedItemCount = 0;
                string currentFetchUrl = url;

                if (cpPath != null && appendToOutput)
                {
                    var checkpoint = PaginationCheckpoint.Load(cpPath);
                    if (checkpoint != null)
                    {
                        if (!DescribesThisExport(checkpoint, outputPath, includeAutoCount,
                                suppressTop, mayWrite: true))
                        {
                            // Ownership is re-decided on every load, not carried over from the
                            // check above: the checkpoint is a file, the run that owns it is
                            // still going, and it can be replaced between the two reads. A
                            // checkpoint that is not this export's is left where it is - the
                            // export it does belong to needs it - and this run exports fresh.
                            // Only ever this way round: a refusal stands for the rest of the
                            // run, and no later attempt grants it back from the two files
                            // being where they were.
                            //
                            // And said out loud. This is the only refusal that lands on a
                            // checkpoint already accepted, so a resume has been reported and is
                            // being taken back: the collection is enumerated from the first page
                            // again, and the pages collected under the resume are collected a
                            // second time. Withdrawn in silence, the recovery message was the
                            // last thing on any stream, and a run that had quietly started over
                            // was indistinguishable from one that resumed.
                            //
                            // Two readings reach here and only one of them is about whose the
                            // file is, which is where they part company: the run does the same
                            // thing either way - drops the resume and exports fresh - and what
                            // it leaves on disk afterwards is the opposite thing. Every form of
                            // the request this run can build goes to the same comparison the
                            // adoption above the loop makes, so a checkpoint that matches one
                            // of them is this export's own, saved under the form an endpoint
                            // that refused $top or the auto-added $count has since sent the
                            // loop round without. The verdict is about the ones that match none.
                            var ownership =
                                AttemptFormOf(checkpoint, outputPath, countAutoAdded,
                                    mayWrite: true) != null
                                    ? CheckpointOwnership.Mine
                                    : OwnershipOf(checkpoint, outputPath, includeAutoCount,
                                        suppressTop, mayWrite: true);
                            appendToOutput = false;
                            if (ownership == CheckpointOwnership.Mine)
                            {
                                // This export's own position, into the enumeration this run is
                                // about to make again and replace the output with. Nothing is
                                // held back on its account: the completion path deletes it as
                                // it deletes any checkpoint of this run's, and a temp it still
                                // names is this run's own for the sweep below to reclaim. Left
                                // standing instead, it is a position nothing comes back to, and
                                // the next run over the same command line reads it as its own -
                                // the finished output cut back to the byte count of an
                                // enumeration that has since been replaced, and the remainder
                                // appended from a nextLink counting items it no longer holds.
                                WriteWarning(RebuiltRequestWarning(cpPath, outputPath));
                            }
                            else
                            {
                                // Another export's, or one nothing beside this output
                                // corroborates. The export it belongs to resumes from exactly
                                // that, so for the rest of the run the sweep below leaves this
                                // checkpoint's temp where it is, and the completion path leaves
                                // the checkpoint itself.
                                _sparedCheckpoint = SparedCheckpoint.NotThisRuns;
                                _refusedCheckpointTemp = checkpoint.TempFile;
                                WriteWarning(ReplacedCheckpointWarning(cpPath, outputPath));
                            }
                        }
                        else if (checkpoint.NextLink == null)
                        {
                            // Completion marker: previous export finished, checkpoint is stale
                            WriteVerbose("Checkpoint indicates previous export completed. Deleting stale checkpoint.");
                            PaginationCheckpoint.Delete(cpPath);
                            appendToOutput = false;
                        }
                        else
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
                                appendToOutput = false;
                            }
                        }
                    }
                    else
                    {
                        // The unreadable checkpoint above could not be deleted either - it is
                        // locked, or the account cannot touch it. Appending to the output on
                        // the strength of a file nothing can read is what has to stop, not the
                        // export: write a fresh run to a temp and let it replace the output.
                        appendToOutput = false;
                    }
                }

                // For fresh exports (not resume), write to a temp file first.
                // This protects any pre-existing output file from truncation if
                // the Graph request fails on the first page.
                // Use GUID to prevent collision when multiple exports target the same file.
                if (!appendToOutput)
                {
                    // No resume is pending, so every "{output}.{guid}.tmp" on disk is an orphan.
                    // Leaving them is not inert: the pre-length adoption path picks the NEWEST
                    // match with only a line count to go on, so a survivor of an unrelated run is
                    // adoptable by some later crash's checkpoint.
                    //
                    // Except after a refusal, where one of them is not an orphan at all: a
                    // checkpoint describing it is on disk, left there deliberately a moment ago,
                    // and the items it counts are in that temp. Sweeping it took the one file
                    // that made the checkpoint worth keeping, so the export it belongs to came
                    // back to a position pointing at nothing and re-enumerated from the first
                    // page - the cost the refusal was written to avoid. The sweep is all or
                    // nothing over this output's temps, so a run that has just refused leaves
                    // them to the next run that has not. A run that could not claim the temp
                    // its checkpoint names never gets here to decide: it stops above the client.
                    //
                    // And once more on a retry, where the checkpoint standing over the temp is
                    // this run's own. An endpoint that refuses the auto-added $count, or $top,
                    // sends the attempt loop round again after pages have already been
                    // collected and checkpointed, and the attempt that died kept its temp for
                    // exactly that checkpoint to resume from. Sweeping here deleted it while
                    // the checkpoint naming it was still on disk - the one combination neither
                    // this policy nor the retry's intends - and the next invocation found a
                    // position pointing at nothing and exported from the first page.
                    if (RefusedTempIsOnDisk(_refusedCheckpointTemp, outputPath))
                    {
                        WriteVerbose(
                            $"Left the temp files beside '{outputPath}' alone: '{_refusedCheckpointTemp}' "
                            + "holds the items of the checkpoint this run refused.");
                    }
                    else if (_sparedCheckpoint is SparedCheckpoint.NotThisRuns
                             && _refusedCheckpointTemp == null)
                    {
                        // The refused checkpoint names no temp, which is not the same as there
                        // being none: a checkpoint written before the name was recorded stands
                        // for a temp beside this output all the same, and there is nothing here
                        // that says which of them it is. Keyed on the temp name alone, the
                        // refusal spared the checkpoint and the sweep took the one file it was
                        // pointing at - so the export it belongs to came back to a position over
                        // nothing and re-enumerated from the first page, the exact cost the
                        // refusal is written to avoid, under a warning that had just promised
                        // the files would be left as they are. So the whole sweep goes with the
                        // refusal, and the orphans it would have taken are left to the next run
                        // over this output that has not refused anything.
                        WriteVerbose(
                            $"Left the temp files beside '{outputPath}' alone: one of them may "
                            + "hold the items of the checkpoint this run refused.");
                    }
                    else if (_keptTempPath != null && File.Exists(_keptTempPath))
                    {
                        WriteVerbose(
                            $"Left the temp files beside '{outputPath}' alone: "
                            + $"'{Path.GetFileName(_keptTempPath)}' holds the items of this run's own "
                            + "checkpoint, which a previous attempt left on disk.");
                    }
                    else
                    {
                        DeleteStaleTemps(outputPath);
                    }
                }
                // The output is let go the moment this attempt stops appending to it. Every way
                // appendToOutput is withdrawn - the checkpoint or the output gone between
                // attempts, an ownership verdict landing on a reload, a 410 sending the loop
                // round for a full pass - ends with this run replacing the output from a temp
                // instead, and a handle of its own on the destination is what that move fails
                // against. An attempt that still appends keeps the hold it was given.
                if (!appendToOutput) ReleaseOutputHold();

                var writePath = appendToOutput ? outputPath : $"{outputPath}.{Guid.NewGuid():N}.tmp";
                // What the checkpoint sites below should say about WHERE the counted items are.
                // A resumed run appends to the output itself, so it has no temp to name.
                string? checkpointTempFile = appendToOutput ? null : Path.GetFileName(writePath);
                // Whether the checkpoint on disk is one THIS attempt saved. A checkpoint an
                // earlier attempt left names an earlier temp, and the two are not
                // interchangeable when it comes to deciding what a file on disk is still for.
                var savedOwnCheckpoint = false;
                long? checkpointDataLength = null;
                long itemCount = 0;
                // Seeded from the resume skip, not 0. PageIterator drops the skipped items before
                // this loop ever sees them, so a counter starting at 0 records only the NEWLY
                // written items of a resumed first page. A mid-page checkpoint saved there then
                // claims fewer items of that page than the output holds, and the next resume skips
                // too few and writes the difference twice.
                int pageItemsWritten = resume?.SkipOnFirstPage ?? 0;
                long totalWritten = resumedItemCount;
                long? reportedODataCount = null;

                // A temp a failed attempt of this run kept is kept on one condition: a
                // checkpoint counting its items is on disk. Saving a checkpoint here is what
                // ends that - the file just written describes this attempt's temp, or the
                // output on a resumed run, and never the earlier one - so from that moment
                // nothing on disk refers to it, no recovery can reach it, and the sweep at the
                // top of the next attempt is still being held off on its behalf. Left where it
                // was, it outlived the export that made it: a partial copy of the caller's
                // data sitting beside the finished file, waiting for some later run's sweep.
                //
                // Never the file this attempt is writing into, which on a resumed run is the
                // output itself. Best effort otherwise: a temp something else holds open is
                // not worth failing an export over.
                void ReleaseKeptTemp()
                {
                    var kept = _keptTempPath;
                    if (kept == null
                        || string.Equals(kept, writePath, StringComparison.OrdinalIgnoreCase))
                        return;
                    _keptTempPath = null;
                    try { if (File.Exists(kept)) File.Delete(kept); } catch { }
                }

                try
                {
                    // The reconcile's own handle on the output, where this run resumed into it.
                    // Never a second open of that file while the hold stands: on Unix the hold's
                    // LOCK_EX refuses one from this process as readily as from another, and on
                    // Windows its write access does. The pre-length adoption route appends
                    // without a hold and opens its own, as it always has.
                    using (var writer = appendToOutput && HeldOutput is { } held
                               ? held.AppendingWriter()
                               : new StreamWriter(writePath, appendToOutput))
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
                                        checkpointDataLength = writer.BaseStream.Position;
                                        new PaginationCheckpoint
                                        {
                                            Resource = url,
                                            NextLink = info.NextPageUrl,
                                            ItemsCollected = totalWritten,
                                            PageItemsAlreadyWritten = 0,
                                            TempFile = checkpointTempFile,
                                            OutputFile = Path.GetFullPath(outputPath),
                                            DataLength = checkpointDataLength
                                        }.Save(cpPath);
                                        savedOwnCheckpoint = true;
                                        tookOverCheckpoint = true;
                                        ReleaseKeptTemp();
                                    }
                                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                                    {
                                        // Buffered rather than written here. This callback runs
                                        // on whichever thread the iterator resumed on, and a
                                        // page that yields nothing resumes on the thread pool -
                                        // so a WriteWarning from here throws
                                        // PSInvalidOperationException, and the disk problem it
                                        // was reporting ends the export instead of being
                                        // reported. The drains write it from the pipeline
                                        // thread, on the same channel the client's own warnings
                                        // take.
                                        client.EnqueueWarning(
                                            $"Checkpoint save failed (page boundary): {ex.Message}");
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
                                            checkpointDataLength = writer.BaseStream.Position;
                                            new PaginationCheckpoint
                                            {
                                                Resource = url,
                                                NextLink = currentFetchUrl,
                                                ItemsCollected = totalWritten,
                                                PageItemsAlreadyWritten = pageItemsWritten,
                                                TempFile = checkpointTempFile,
                                                OutputFile = Path.GetFullPath(outputPath),
                                                DataLength = checkpointDataLength
                                            }.Save(cpPath);
                                            savedOwnCheckpoint = true;
                                            tookOverCheckpoint = true;
                                            ReleaseKeptTemp();
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

                    // Writer closed. Promote the temp, and only then let go of the checkpoint.
                    //
                    // The move is the one step that can still fail with every page already
                    // fetched: the output held open by another process, a read-only
                    // destination, a share that dropped between the last write and the rename.
                    // Deleting the checkpoint first made that failure destroy the run - the
                    // catch below read "no checkpoint" as "nothing to resume", deleted the temp
                    // holding every item, and the next invocation enumerated from the first
                    // page. This way round a failed move leaves the checkpoint and the temp on
                    // disk exactly as an interruption does, and the next run promotes the temp
                    // and resumes from the last page the checkpoint recorded.
                    if (!appendToOutput)
                    {
                        File.Move(writePath, outputPath, overwrite: true);
                    }

                    // The items are in the output now, so the position describing them is
                    // spent. A crash in the gap costs a re-enumeration: the checkpoint names a
                    // temp that is no longer there, which recovery reads as unusable.
                    //
                    // Spent for this export, and the file on disk is only this export's once no
                    // refusal left it standing and a save of its own has landed on the path. A
                    // run that refused what it found and then finished without ever saving one,
                    // everything it had fitting in a single page, deleted the file it had warned
                    // it would leave alone, orphaning the temp it had just spared the sweep and
                    // sending the export that needs both back to page one.
                    //
                    // One of the three readings spares it: the ownership verdict's, where
                    // whatever wrote the file resumes from exactly it. Not a checkpoint refused
                    // for a request the endpoint made this run rebuild - that one records a
                    // position into an enumeration this run has just replaced and nothing more,
                    // and left standing beside the finished output it is what the next run over
                    // the same command line reads as its own and promotes a superseded temp
                    // over a completed file from. And not a checkpoint whose temp, or whose
                    // output, this run could not claim, which reaches no door at all: that run
                    // stopped at the reconcile, above the client, having written nothing.
                    //
                    // tookOverCheckpoint is the flag, and savedOwnCheckpoint has nothing left
                    // to add: it answers for the attempt finishing here, and every site that
                    // sets it sets the run-scoped one beside it - which also covers the attempt
                    // a query the endpoint refused sent round again with a boundary checkpoint
                    // of this run's already over the path.
                    if (_sparedCheckpoint is SparedCheckpoint.NotThisRuns && !tookOverCheckpoint)
                    {
                        WriteVerbose(SparedCheckpointVerbose("export completed"));
                    }
                    else if (cpPath != null)
                    {
                        PaginationCheckpoint.Delete(cpPath);
                    }

                    // And with it goes the last thing that could have named a temp an earlier
                    // attempt kept - the attempt that finished need never have saved a
                    // checkpoint of its own to release it, if everything it had left fitted in
                    // one page. Only here, on the way out of a run that promoted: a promotion
                    // that failed leaves the checkpoint and the temp for the next run to resume
                    // from, and both are still exactly what that run needs.
                    ReleaseKeptTemp();
                }
                catch (Exception attemptEx)
                {
                    if (!appendToOutput)
                    {
                        // User cancellation of a checkpointed fresh run: promote the temp
                        // file (the using block already flushed it on unwind) and save a
                        // checkpoint matching its exact content, so the printed resume
                        // hint is true for first runs too. Previously the temp was
                        // deleted here and the next run declared the checkpoint stale.
                        var cancelled = attemptEx is OperationCanceledException
                            && CancellationToken.IsCancellationRequested;
                        var promoted = false;
                        if (cancelled && cpPath != null && itemCount > 0)
                        {
                            try
                            {
                                // Move first, then record. A checkpoint that named the output
                                // before the move existed would describe a file that is not
                                // there yet, and a move that then failed would leave it saying
                                // so. The length is the temp's, taken before the move, because
                                // it is the same bytes under a different name afterwards.
                                var promotedLength = new FileInfo(writePath).Length;
                                File.Move(writePath, outputPath, overwrite: true);
                                promoted = true;
                                new PaginationCheckpoint
                                {
                                    Resource = url,
                                    NextLink = currentFetchUrl,
                                    ItemsCollected = totalWritten,
                                    PageItemsAlreadyWritten = pageItemsWritten,
                                    TempFile = null,
                                    OutputFile = Path.GetFullPath(outputPath),
                                    DataLength = promotedLength
                                }.Save(cpPath);
                                ReleaseKeptTemp();
                            }
                            catch (Exception promoteEx) when (promoteEx is IOException or UnauthorizedAccessException)
                            {
                                // Promotion is best-effort; fall back to the old cleanup.
                            }
                        }
                        if (!promoted)
                        {
                            // A surviving checkpoint counts items that exist only in this temp:
                            // every checkpoint site flushes the writer before recording the
                            // position, so the temp always holds at least what it promises.
                            // Deleting it made the next run's recovery find the checkpoint
                            // naming a missing file and start the export over - resume worked
                            // after a kill or a Ctrl-C but never after a handled error, which
                            // is the common way a long export dies. Keep the temp for the next
                            // run to promote; it is deleted by promotion or by the stale-temp
                            // sweep once the checkpoint is gone.
                            //
                            // The checkpoint counting them has to be one this attempt saved.
                            // Any other is an earlier attempt's, naming an earlier temp, and
                            // this attempt's items are then counted by nothing: keeping the
                            // file left a partial page no recovery can reach, and the newest
                            // temp beside an output is what the pre-length adoption path picks
                            // up on a line count alone.
                            var resumable = cpPath != null && File.Exists(cpPath) && savedOwnCheckpoint;
                            if (resumable)
                            {
                                // Named for the sweep above, which the next attempt reaches
                                // before this checkpoint has been resumed from or replaced.
                                _keptTempPath = writePath;
                            }
                            else
                            {
                                // Whatever is named stays named: the checkpoint on disk is
                                // still the earlier attempt's, and so is the temp it counts.
                                try { if (File.Exists(writePath)) File.Delete(writePath); } catch { }
                            }
                        }
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
            catch (System.Text.Json.JsonException ex)
            {
                // A page body that does not parse. Items already exported stay in the file;
                // report the failure instead of ending the pipeline with a raw exception.
                DrainClientMessages();
                WriteError(new ErrorRecord(
                    new InvalidOperationException($"A response page declared JSON but does not parse: {ex.Message}", ex),
                    "MalformedJsonResponse", ErrorCategory.InvalidData, Uri));
                return;
            }
            catch (GraphServiceException ex) when (IsCountRejection(ex, includeAutoCount && countAutoAdded))
            {
                DrainClientMessages();
                WriteVerbose(CountRejectedVerbose);
                includeAutoCount = false;
                continue;
            }
            catch (GraphServiceException ex) when (IsTopRejection(ex, suppressTop, NoPageSize.IsPresent))
            {
                DrainClientMessages();
                WriteVerbose(TopRejectedVerbose);
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
            catch (Exception)
            {
                // Unexpected exception types skip the drains above; the buffered verbose
                // and warning messages are the context that explains the failure.
                DrainClientMessages();
                throw;
            }
        }
    }

    private bool _warnedDeferredOptions;

    private string BuildUrl(bool includeCount, bool suppressTop = false)
    {
        var url = BuildListUrl(
            VersionedBaseUrl, Uri,
            new ODataListParams(NoPageSize.IsPresent || suppressTop, Top, PageSize, Filter,
                Property, Sort, Search, Skip, ExpandProperty,
                IncludeCount: includeCount),
            out var deferred);
        if (deferred.Count > 0 && !_warnedDeferredOptions)
        {
            _warnedDeferredOptions = true;
            WriteWarning(DescribeDeferredOptions(deferred));
        }
        return url;
    }

    private Dictionary<string, string>? BuildHeaders() =>
        BuildRequestHeaders(ConsistencyLevel, Headers);

    /// <summary>
    /// What a caller can act on when the checkpoint on disk turns out to be another export's:
    /// which two files disagree, what this run does instead, and that a -CheckpointPath belongs
    /// to one export. Refusing it is the whole response - the file is left alone, and so is any
    /// staging file beside it, because the export that wrote it resumes from exactly those.
    /// </summary>
    private static string ForeignCheckpointWarning(string checkpointPath, string outputPath) =>
        $"The resume checkpoint at '{checkpointPath}' belongs to a different export, so it is "
        + $"left as it is and '{outputPath}' is exported from the beginning. Two exports sharing "
        + "one -CheckpointPath overwrite each other's resume position; give each its own.";

    /// <summary>
    /// What a caller can act on when the request was rebuilt under this export's own feet: the
    /// endpoint refused a query option part-way through, the loop went round without it, and
    /// the position on disk counts a URL this run has stopped building. It is this export's
    /// own, and the resume it was announced for is being taken back, so the sentence has to say
    /// both - the collection is enumerated from the first page again, and the file goes the way
    /// any of this run's own positions go once the output is complete.
    /// </summary>
    private static string RebuiltRequestWarning(string checkpointPath, string outputPath) =>
        $"The resume checkpoint at '{checkpointPath}' records this export under a form of the "
        + "request the endpoint has since refused, so the resume is dropped and "
        + $"'{outputPath}' is exported from the beginning; the checkpoint is this run's own and "
        + "goes with the completed export.";

    /// <summary>
    /// And what they can act on when a resume already announced turns out to be another run's
    /// position: the file at -CheckpointPath was replaced between the reconcile's read and this
    /// one, so what is there now describes an output or an enumeration that is not this one.
    /// Both files are left where they are, because whatever wrote them resumes from exactly
    /// that.
    /// </summary>
    private static string ReplacedCheckpointWarning(string checkpointPath, string outputPath) =>
        $"The resume checkpoint at '{checkpointPath}' no longer describes this export - another "
        + $"run replaced the file - so the resume is dropped and '{outputPath}' is exported from "
        + "the beginning; the checkpoint and the temp it names are left as they are.";

    /// <summary>
    /// What a caller can act on when a checkpoint that records no output file is refused: it
    /// was written before the output was recorded, nothing beside this one's output stands for
    /// it any more, and this run exports from the beginning. Naming a different export there
    /// sends the caller looking for a second run over the same -CheckpointPath, and there is
    /// none to find - a temp that has since been removed and an output that has since been
    /// replaced reach the same refusal.
    /// </summary>
    private static string UncorroboratedCheckpointWarning(string checkpointPath, string outputPath) =>
        $"The resume checkpoint at '{checkpointPath}' records no output file, and the files "
        + $"beside '{outputPath}' no longer corroborate it, so it is left as it is and "
        + $"'{outputPath}' is exported from the beginning; nothing is lost.";

    /// <summary>
    /// How the run ends when a file its own checkpoint stands for is open in another export, or
    /// cannot be opened read-write by this account. Two files reach this, and the reading is one:
    /// the rows the checkpoint counts are in that file and in no other, the checkpoint is the
    /// only thing on disk that counts them, and this export has no pass to make that leaves the
    /// pair intact - it saves its own position over the checkpoint at the first page boundary,
    /// after which the completion door deletes what it finds there as its own. So it says what
    /// it found, what it did not do, and the two ways out, and every file is exactly where it was.
    ///
    /// Which file decides what going on would cost, and for an output refusal
    /// <see cref="CheckpointFileRefusal.Route"/> decides it further: a checkpoint naming a temp
    /// that is still there holds the rows in that temp, and going on promotes them, replacing
    /// the output outright; one naming a temp that has vanished holds them nowhere this run can
    /// reach, and going on enumerates the collection again and replaces the output with the
    /// result; and one naming no temp at all was written by a run appending straight into the
    /// output, so the rows are in the output itself and going on cuts it back under its writer -
    /// whose next write then lands past the hole that offset leaves. All three reach the same
    /// claim on the same file, because a rename and a cut are both closed to a run that does not
    /// hold it.
    ///
    /// Not phrased as a fault in the held case: nothing has gone wrong with either file, two
    /// exports were pointed at one -OutputFile, and the caller has only to decide which of them
    /// they meant. The unopenable case has no second export to go and find at all, and the
    /// sentence written for the other one pointed the reader at a run that was never there
    /// instead of at the open's own reason - which is named now, in <see cref="UnopenableReason
    /// .Reason"/>, rather than assumed to be a permission: a symlink loop and an over-long path
    /// reach it too, and granting write access fixes neither.
    ///
    /// The closing sentence is the caller's: a run that is stopping says so, and a -WhatIf pass
    /// over the same two files says what would happen instead.
    /// </summary>
    private static string CheckpointFileStopMessage(CheckpointFileRefusal refusal,
        string checkpointPath, string outputPath, PaginationCheckpoint checkpoint, string closing)
    {
        // What going on would cost past a held temp, which is the output beside it - named only
        // where there is one. A checkpoint naming a temp is reached with nothing at -OutputFile
        // at all, and a run that said it would replace that file sent the caller to look at
        // something no export had written yet.
        var goingOn = File.Exists(outputPath)
            ? $"Going on would replace '{outputPath}' and save this run's position over the "
              + "checkpoint that counts those items."
            : "Going on would save this run's position over the checkpoint that counts those "
              + "items.";

        // The output is unopenable rather than held. The three routes agree past this point -
        // the code beyond this check falls through to the same place a file that never held the
        // recorded items reaches, which deletes the checkpoint and replaces the output at the
        // completion that follows - so only the first clause, naming where the items are now,
        // and the recovery word in the advice change per route.
        string UnopenableOutputSentence()
        {
            var reason = refusal.Reason!.Value;
            var openFailed = $"'{outputPath}' could not be opened for writing: {reason.Reason}.";
            var recovers = refusal.Route switch
            {
                CheckpointStopRoute.Promoting => " to recover them",
                CheckpointStopRoute.FreshAfterVanishedTemp => "",
                _ => " to resume from it",
            };
            // Four ways out, because the causes do not share one. A permission on the file is
            // granted; a permission on the directory holding it is granted there, and the file
            // this sentence names is one the caller cannot reach to change or remove; a
            // directory at the path is removed or pointed away from, and no grant would have
            // made it writable at an offset; and a loop, an over-long path, a socket or a pipe
            // is none of those - what this run can honestly say about them is what failed.
            var advice = reason.IsDirectory
                ? $"Remove it (or point -OutputFile elsewhere) and run again{recovers}, or "
                  + "remove the checkpoint to export afresh."
                : reason.IsUnsearchableParent
                    ? $"Grant access to that directory and run again{recovers}, or remove the "
                      + "checkpoint to export afresh."
                    : reason.IsPermission
                        ? $"Grant write access to that file and run again{recovers}, or remove "
                          + "it and the checkpoint to export afresh."
                        : $"Fix that and run again{recovers}, or remove it and the checkpoint to "
                          + "export afresh.";

            return refusal.Route switch
            {
                CheckpointStopRoute.Promoting =>
                    $"{openFailed} The resume checkpoint at '{checkpointPath}' records "
                    + $"{checkpoint.ItemsCollected} items into the temp '{checkpoint.TempFile}'; "
                    + $"this run cannot promote them into '{outputPath}'. Going on would delete "
                    + $"the checkpoint that counts them and replace that file. {closing} {advice}",

                CheckpointStopRoute.FreshAfterVanishedTemp =>
                    $"{openFailed} The resume checkpoint at '{checkpointPath}' recorded "
                    + $"{checkpoint.ItemsCollected} items into a temp that is gone; this run "
                    + $"cannot export a fresh copy into '{outputPath}' either. Going on would "
                    + $"delete the checkpoint that counts them and replace that file. {closing} "
                    + $"{advice}",

                _ =>
                    $"{openFailed} The resume checkpoint at '{checkpointPath}' records "
                    + $"{checkpoint.ItemsCollected} items into it, so this run can neither "
                    + "resume into it nor say whether those items are still there. Going on "
                    + "would delete the checkpoint that counts them and replace that file. "
                    + $"{closing} {advice}",
            };
        }

        return (refusal.File, refusal.Held) switch
        {
            (CheckpointFile.NamedTemp, true) =>
                "Another export is still writing the temp file the resume checkpoint at "
                + $"'{checkpointPath}' names, so the {checkpoint.ItemsCollected} items it records "
                + $"are that run's. {goingOn} {closing} Wait for that export to finish, or give "
                + "this one its own -OutputFile and -CheckpointPath.",

            (CheckpointFile.NamedTemp, false) =>
                $"The temp file '{checkpoint.TempFile}' that the resume checkpoint at "
                + $"'{checkpointPath}' names cannot be opened for writing by this account - the "
                + "open was refused on permissions, not on sharing - so the "
                + $"{checkpoint.ItemsCollected} items it records cannot be recovered here. "
                + $"{goingOn} {closing} Grant write access to that file and run again to recover "
                + "them, or remove it and the checkpoint to export afresh.",

            (CheckpointFile.Output, true) => refusal.Route switch
            {
                CheckpointStopRoute.Promoting =>
                    $"Another export is still writing '{outputPath}'. The resume checkpoint at "
                    + $"'{checkpointPath}' records {checkpoint.ItemsCollected} items into the "
                    + $"temp '{checkpoint.TempFile}'; going on would replace '{outputPath}' with "
                    + "them under the run that holds it, and save this run's position over the "
                    + $"checkpoint that counts those items. {closing} Wait for that export to "
                    + "finish, or give this one its own -OutputFile and -CheckpointPath.",

                CheckpointStopRoute.FreshAfterVanishedTemp =>
                    $"Another export is still writing '{outputPath}'. The resume checkpoint at "
                    + $"'{checkpointPath}' recorded {checkpoint.ItemsCollected} items into a "
                    + "temp that is gone; going on would export from the beginning and replace "
                    + $"'{outputPath}' under the run that holds it, and save this run's position "
                    + $"over the checkpoint that counts those items. {closing} Wait for that "
                    + "export to finish, or give this one its own -OutputFile and "
                    + "-CheckpointPath.",

                _ =>
                    $"Another export is still writing '{outputPath}', which the resume "
                    + $"checkpoint at '{checkpointPath}' records {checkpoint.ItemsCollected} "
                    + "items into. Going on would cut that file back under it and save this "
                    + "run's position over the checkpoint that counts those items. "
                    + $"{closing} Wait for that export to finish, or give this one its own "
                    + "-OutputFile and -CheckpointPath.",
            },

            _ => UnopenableOutputSentence(),
        };
    }

    /// <summary>
    /// The record the run ends on, which is that sentence in the present tense under an id and a
    /// category naming which file the claim failed on and which of the two ways it failed.
    /// </summary>
    private static ErrorRecord CheckpointFileStop(CheckpointFileRefusal refusal,
        string checkpointPath, string outputPath, PaginationCheckpoint checkpoint) =>
        new(new InvalidOperationException(CheckpointFileStopMessage(refusal, checkpointPath,
                outputPath, checkpoint, RunStopsHere)),
            (refusal.File, refusal.Held) switch
            {
                (CheckpointFile.NamedTemp, true) => "CheckpointTempHeld",
                (CheckpointFile.NamedTemp, false) => "CheckpointTempUnopenable",
                (CheckpointFile.Output, true) => "CheckpointOutputHeld",
                _ => "CheckpointOutputUnopenable",
            },
            // ResourceBusy for a file another run has, which is what a caller waits out. Past
            // that the category follows the open's own reason rather than assuming a
            // permission: PermissionDenied is a mode this account can be granted, and a symlink
            // loop, an over-long path, a socket, a FIFO or a directory at the path is none -
            // InvalidOperation says so, and a refusal that carries no reason at all is the
            // temp's, whose own sentence names permissions.
            refusal.Held
                ? ErrorCategory.ResourceBusy
                : refusal.Reason is { IsPermission: false }
                    ? ErrorCategory.InvalidOperation
                    : ErrorCategory.PermissionDenied,
            checkpointPath);

    /// <summary>
    /// How the run ends when the copy a promotion stages beside the output could not be written,
    /// or could not be moved onto the output once it was. Nothing here is a file that has gone
    /// missing, and reading it as one was what deleted the checkpoint: every item it counts is in
    /// the temp it names, whole and claimable, and the run that comes back stages the same bytes
    /// over the same output. So the sentence says where those items are, what failed and where,
    /// and that both files are as they were found - and the run stops rather than export past a
    /// position it would then save over.
    /// </summary>
    /// <param name="tempName">The temp those items are in. The checkpoint's own record of it
    /// where it has one, and the file adoption picked where the checkpoint predates that field:
    /// either way it is the file the reader has to keep, and a sentence that read it off the
    /// checkpoint named nothing at all on the adoption route.</param>
    /// <param name="removedKind">What the sweep took off the staging name on the way here, where
    /// it took anything: the run deleted an entry and then stopped, and a reader who is told only
    /// that the staging failed goes looking for a file that is no longer there. Named in the
    /// stop rather than warned about separately, because a warning written before a terminating
    /// error is what -WarningAction Stop ends the run on instead of this sentence.</param>
    private static string CheckpointStagingStopMessage(StagingFailure staging,
        string checkpointPath, string outputPath, PaginationCheckpoint checkpoint, string tempName,
        string? removedKind, string closing) =>
        $"The {checkpoint.ItemsCollected} items the resume checkpoint at '{checkpointPath}' "
        + $"records are in '{tempName}', whole, but staging them into '{outputPath}' "
        + $"failed at '{staging.Path}': {staging.Reason}"
        + (removedKind != null
            ? $", after removing what stood at the staging name: {removedKind}"
            : "")
        + $". {closing} fix that and run again to "
        + "recover them, or remove the temp and the checkpoint to export afresh.";

    /// <inheritdoc cref="CheckpointStagingStopMessage"/>
    /// <summary>
    /// The record the run ends on. A write that did not happen, under the checkpoint whose items
    /// it was for: not a claim refused on either of the two files, which is what the ids and
    /// categories beside this one are about.
    /// </summary>
    private static ErrorRecord CheckpointStagingStop(StagingFailure staging, string checkpointPath,
        string outputPath, PaginationCheckpoint checkpoint, string tempName,
        string? removedKind) =>
        new(new InvalidOperationException(CheckpointStagingStopMessage(staging, checkpointPath,
                outputPath, checkpoint, tempName, removedKind, NothingWasChanged)),
            "CheckpointStagingFailed", ErrorCategory.WriteError, checkpointPath);

    /// <summary>
    /// The form of this export's URL a checkpoint on disk was written under - whether the
    /// auto-added $count was on it and whether the automatic $top was - or null when no form of
    /// it is this export's. The attempt loop reaches each of these in turn, dropping $count on a
    /// bare 400 and $top on a Request_UnsupportedQuery, and a checkpoint records whichever URL
    /// was current when it was saved. Every form is put to the same comparison the loop makes,
    /// so a URL the loop can legitimately build is recognized as this export's and one it cannot
    /// is still refused. Ordered as the loop reaches them, so an unambiguous match is the one
    /// this run would have built first.
    /// </summary>
    private (bool IncludeCount, bool SuppressTop)? AttemptFormOf(
        PaginationCheckpoint? checkpoint, string outputPath, bool countAutoAdded, bool mayWrite)
    {
        if (checkpoint == null) return null;
        bool[] counts = countAutoAdded ? [true, false] : [false];
        bool[] tops = [false, true];
        foreach (var includeCount in counts)
        {
            foreach (var suppressTop in tops)
            {
                if (DescribesThisExport(checkpoint, outputPath, includeCount, suppressTop, mayWrite))
                    return (includeCount, suppressTop);
            }
        }
        return null;
    }

    /// <summary>
    /// True when a checkpoint on disk is about the export this run is making. The reconcile
    /// below promotes and trims files before any other part of the checkpoint is looked at, and
    /// a byte offset applies to whatever file it is handed - so a -CheckpointPath shared with a
    /// second export cut a file that checkpoint knows nothing about, mid-line, and the resumed
    /// pages were appended onto the torn byte. Two things have to agree: the output the writing
    /// run named - the whole path, since two exports to "users.jsonl" in different directories
    /// agree on the name and on nothing else, and on checkpoints too old to record one, the
    /// files on disk standing in for it - and the resource, path and query both. The query is
    /// half of what a resource is: -Top, -Filter and -Select change which items come back and
    /// in what order, so a checkpoint recorded under one of them counts a different enumeration
    /// than the one this run is making. Leaving it out put the exact comparison after the
    /// promoting and trimming instead of before it, which is not a comparison that can refuse.
    /// </summary>
    private bool DescribesThisExport(PaginationCheckpoint checkpoint, string outputPath,
        bool includeCount, bool suppressTop, bool mayWrite)
        => OwnershipOf(checkpoint, outputPath, includeCount, suppressTop, mayWrite)
            == CheckpointOwnership.Mine;

    /// <summary>
    /// Which reading refused the checkpoint this run found at -CheckpointPath, or null where
    /// nothing refused it. Set wherever a refusal that leaves the file standing is reported and
    /// never cleared - an attempt rebuilds the request URL, so a checkpoint refused under one
    /// form of it can compare equal under another, and that is not evidence of anything.
    ///
    /// Read on the way out of a run that completed, where the delete would otherwise take a
    /// position this run had warned it would leave alone. One of the three refusals records a
    /// reading here: the ownership verdict, where whatever wrote the file resumes from exactly
    /// it. A checkpoint refused because the endpoint made this run rebuild the request records
    /// none - it is a position into an enumeration this run replaces and nothing else, so it
    /// goes with the completed export and the temp it names is this run's own to sweep. And a
    /// checkpoint whose temp, or whose output, this run could not claim never reaches a door:
    /// that run stops at the reconcile, with nothing enumerated and nothing written.
    ///
    /// Not something <see cref="_refusedCheckpointTemp"/> can answer either: a checkpoint
    /// written by a run that was appending to its output names no temp at all, and refusing
    /// that one counts the same.
    /// </summary>
    private SparedCheckpoint? _sparedCheckpoint;

    /// <summary>
    /// The temp file a checkpoint this run refused still points at, or null. Refusing is the
    /// whole response: the checkpoint is left where it is because the export that wrote it
    /// resumes from exactly that, and the items it counted are in the temp it names.
    /// <para>
    /// Null is two readings, and <see cref="_sparedCheckpoint"/> tells them apart: nothing was
    /// refused, or what was refused records no temp name. The second stands for a temp all the
    /// same - a checkpoint written before the name was recorded says only that the items are
    /// somewhere beside this output - so the sweep reads the pair rather than this alone.
    /// </para>
    /// </summary>
    private string? _refusedCheckpointTemp;

    /// <summary>
    /// What a run would stop on - a temp it could not claim, or the output it would append to -
    /// worked out by the preview pass above the ShouldProcess gate, or null where nothing would
    /// stop it. The gate names one of two actions on -OutputFile and "nothing at all" is
    /// neither, so the sentence goes out beside it - and only on the -WhatIf side, where a real
    /// run has already ended on the same condition by the time the gate is past.
    /// </summary>
    private string? _wouldStopOnCheckpointFile;

    /// <summary>
    /// What a run would take off the staging name before it staged its copy there, worked out by
    /// the preview pass and reported beside the gate; null where the name is clear. The pass
    /// used to clear the name itself, which made its deletions as real as a run's: a caller who
    /// ran -WhatIf to find out what would be touched had the link, the pipe or the leftover copy
    /// at that name deleted in order to be told that nothing would be. It is named now and left
    /// where it is, and the create test that follows a clear name is skipped for it - nothing
    /// standing at the name can be tested without taking it off.
    /// </summary>
    private string? _wouldRemoveAtStagingName;

    /// <summary>
    /// The temp a failed attempt of this run left on disk for a checkpoint of its own, or null.
    /// The attempt loop comes back round on a query the endpoint refused, and the sweep it
    /// reaches on the way is the one thing that can take that temp away while the checkpoint
    /// naming it is still there.
    ///
    /// It says nothing once that checkpoint has been replaced by one naming another file, or
    /// deleted by the run that finished: the temp is then a partial copy of the caller's data
    /// that nothing refers to, and it is deleted where the checkpoint changes rather than left
    /// beside the output for a later run's sweep.
    /// </summary>
    private string? _keptTempPath;

    /// <summary>
    /// Whether that temp is a file the stale-temp sweep would reach. The name comes off a
    /// checkpoint, which is untrusted once it is on disk, and nothing here opens it - all it
    /// decides is whether the sweep runs at all, so a name of some other shape is answered no
    /// rather than refused: the sweep cannot delete it either, and skipping the sweep for it
    /// would leave real orphans behind for nothing.
    ///
    /// "Some other shape" is the sweep's own test of a name and not a looser one kept here.
    /// Matching on the prefix and the suffix alone answers yes for "users.jsonl.{guid}.tmp"
    /// beside an output named "users" - a file the sweep passes over, because the glob's '*'
    /// spans dots and the sweep's own filter does not - so a checkpoint left by the run next
    /// door suppressed this output's sweep and its real orphans stayed on disk, which is the
    /// case this comment says is answered no.
    /// </summary>
    private static bool RefusedTempIsOnDisk(string? tempFile, string outputPath)
    {
        if (tempFile == null || tempFile != Path.GetFileName(tempFile)) return false;
        var dir = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(dir)) return false;
        if (!IsRunTempName(dir, Path.GetFileName(outputPath), tempFile, mayWrite: true))
            return false;
        return File.Exists(Path.Combine(dir, tempFile));
    }

    /// <summary>Whose the checkpoint on disk is, and where it is not this export's, why not.</summary>
    private enum CheckpointOwnership
    {
        /// <summary>This export's.</summary>
        Mine,

        /// <summary>Another export's: it records another output, or another enumeration.</summary>
        AnotherExports,

        /// <summary>
        /// Records no output file, and the files beside this one no longer stand for it. Which
        /// export wrote it cannot be told from here, and telling the caller it was another one
        /// is a diagnosis they can go and check and find nothing behind.
        /// </summary>
        Uncorroborated,
    }

    /// <summary>
    /// Which of the three the checkpoint is. A checkpoint recording neither an output nor a
    /// length has nothing here to be weighed: RecordedOutputMatches needs a length to hold a
    /// file against, and answering "not yours" for want of one put the whole pre-2.1.0 shape
    /// behind a refusal, where the stale-temp sweep deleted the items it was pointing at. That
    /// shape is decided on the evidence it does have - a temp carrying this output's own name,
    /// beside an output that is not there - by the reconcile, which refuses it wherever an
    /// output exists. A recorded resource that is not a URL says nothing that can be matched
    /// and is refused with the enumerations that do not match.
    /// </summary>
    private CheckpointOwnership OwnershipOf(PaginationCheckpoint checkpoint, string outputPath,
        bool includeCount, bool suppressTop, bool mayWrite)
    {
        if (RecordsAFileToWeigh(checkpoint)
            && !RecordedOutputMatches(checkpoint.OutputFile, checkpoint.TempFile,
                   checkpoint.DataLength, outputPath, mayWrite))
        {
            return checkpoint.OutputFile != null
                ? CheckpointOwnership.AnotherExports
                : CheckpointOwnership.Uncorroborated;
        }

        return SameResourceIdentity(ResourceIdentity(checkpoint.Resource),
                   ResourceIdentity(BuildUrl(includeCount, suppressTop)))
            ? CheckpointOwnership.Mine
            : CheckpointOwnership.AnotherExports;
    }

    /// <summary>
    /// Put the files into the state the checkpoint claims, or leave them alone, and answer
    /// whether the run that follows appends to the output rather than exporting into a fresh
    /// temp. With <paramref name="apply"/> false nothing is written, deleted or warned about:
    /// the ShouldProcess gate has to name the action before the run is allowed to take any, and
    /// -WhatIf has to leave every file it found exactly as it found it - and leave the directory
    /// holding them as it found it too, which is why the flag also decides whether the case rule
    /// of that directory may be probed or has to be assumed.
    ///
    /// A checkpoint records which file its items were written to and how many bytes of that
    /// file they occupy, which makes three cases decidable instead of guessed.
    ///
    /// A temp is named, so the interrupted run was fresh and its items are in that temp while
    /// the output still holds a PREVIOUS export. An export is a snapshot and a fresh run that
    /// finishes moves its temp over the output, so recovery promotes the temp the same way -
    /// appending would leave the previous export's rows in front of this one's.
    ///
    /// None is named, so the run was appending to the output and its items are already there,
    /// past the recorded length only if it wrote more after its last save. Cutting back to that
    /// length is what stops those from being written twice.
    ///
    /// Neither is recorded, so the checkpoint predates this and is handled as it was before.
    ///
    /// When the counted items turn out to be in no file, nothing has been promoted and no token
    /// has moved, so starting over costs a pass and loses nothing, while resuming past them
    /// would drop them from the output for good.
    /// </summary>
    private bool ReconcileCheckpoint(string? checkpointPath, string outputPath,
        bool includeCount, bool suppressTop, bool apply)
    {
        if (checkpointPath == null || !File.Exists(checkpointPath)) return false;

        var checkpoint = PaginationCheckpoint.Load(checkpointPath);
        if (checkpoint == null)
        {
            // Load answers null for a checkpoint that does not deserialize and for one that
            // cannot be read at all. Either way it says nothing about how much of the output is
            // already there, and the run below decides to append from the file merely EXISTING -
            // which appended a second complete export onto the first. A checkpoint that cannot
            // be read is no checkpoint.
            //
            // It is left where it is. The loop below forces a fresh export whenever the load
            // answers null, so deleting the file changes nothing this run does - and "cannot be
            // read" covers a lock and a denying ACL as well as a torn file, so it threw away a
            // position that the next run, or another account, could still have resumed from.
            if (apply)
            {
                WriteWarning(
                    "The resume checkpoint could not be read, so it cannot say what the output already holds. "
                    + "Exporting again from the beginning; nothing is lost.");
            }
            return false;
        }

        var ownership = OwnershipOf(checkpoint, outputPath, includeCount, suppressTop, apply);
        if (ownership != CheckpointOwnership.Mine)
        {
            if (apply)
            {
                // What the refusal leaves has to survive this same run - the sweep a few
                // lines on, and the delete the completion path ends with.
                _sparedCheckpoint = SparedCheckpoint.NotThisRuns;
                _refusedCheckpointTemp = checkpoint.TempFile;
                WriteWarning(ownership == CheckpointOwnership.AnotherExports
                    ? ForeignCheckpointWarning(checkpointPath, outputPath)
                    : UncorroboratedCheckpointWarning(checkpointPath, outputPath));
            }
            return false;
        }

        if (checkpoint.NextLink == null)
        {
            // Completion marker. With the output beside it the loop below deletes it and
            // exports fresh; without one it describes nothing at all.
            if (!File.Exists(outputPath) && apply)
            {
                WriteWarning("Checkpoint found but output file is missing. Deleting stale checkpoint and starting fresh.");
                PaginationCheckpoint.Delete(checkpointPath);
            }
            return false;
        }

        if (checkpoint.DataLength is not { } dataLength)
        {
            // Written before any of this was recorded. Adoption then has only a line count and
            // the newest matching temp to go on, which is safe to attempt only when there is no
            // output it could be merged into - exactly the case this path used to be limited to.
            if (!File.Exists(outputPath))
            {
                string? adoptedTemp;
                StagingFailure? adoptStaging;
                StagingRemoval? adoptSwept;
                var adopted = apply
                    ? TryAdoptOrphanedTemp(outputPath, checkpoint.ItemsCollected,
                        out adoptedTemp, out adoptStaging, out adoptSwept)
                    : CanAdoptOrphanedTemp(outputPath, checkpoint.ItemsCollected,
                        out adoptedTemp, out adoptStaging, out adoptSwept);

                // What the adoption took off its own staging name before it created one there,
                // or - on the preview, which takes nothing off it - what a run would. The
                // applying pass records the deletion here, where it happened, and warns about it
                // below once this run's own outcome for the recovery is known: a warning is a
                // terminating error under -WarningAction Stop, so written here it ended the run
                // with the entry deleted, the items still in the temp and nothing recovered.
                if (adoptSwept is { } adoptRemoval)
                {
                    if (apply) WriteVerbose(adoptRemoval.Sentence);
                    else _wouldRemoveAtStagingName = adoptRemoval.Sentence;
                }

                // The copy could not be staged beside the output. Every item the checkpoint
                // counts is in the temp this names, whole, so this is not the route below for a
                // checkpoint whose output has gone: that one deletes the position counting them
                // and exports afresh, and reached from here it did so with the temp holding
                // every one.
                if (adoptStaging is { } adoptFailure)
                {
                    if (apply)
                    {
                        ThrowTerminatingError(CheckpointStagingStop(adoptFailure, checkpointPath,
                            outputPath, checkpoint, adoptedTemp!, adoptSwept?.Kind));
                        return false;
                    }

                    // The preview pass reaches the same name and may not throw. What a real run
                    // would do there is stop, which is not one of the two actions the gate names.
                    _wouldStopOnCheckpointFile = CheckpointStagingStopMessage(adoptFailure,
                        checkpointPath, outputPath, checkpoint, adoptedTemp!, null,
                        NothingWouldBeChanged);
                    return false;
                }

                if (adopted)
                {
                    if (apply)
                    {
                        WriteWarning($"Recovered {checkpoint.ItemsCollected} items from an interrupted export's temp file. Resuming from checkpoint.");
                        // And what the sweep took off the staging name, now that what this run
                        // did about the checkpoint is on the stream in front of it. Under
                        // -WarningAction Stop the run ends here, with the items recovered into
                        // the output and the position they resume from still on disk.
                        if (adoptSwept is { } removed) WriteWarning(removed.Sentence);
                    }
                    return true;
                }

                if (apply)
                {
                    WriteWarning("Checkpoint found but output file is missing. Deleting stale checkpoint and starting fresh.");
                    PaginationCheckpoint.Delete(checkpointPath);
                    if (adoptSwept is { } removedAnyway) WriteWarning(removedAnyway.Sentence);
                }
                return false;
            }

            // The output exists and the checkpoint cannot say whether its items are in it. Both
            // shapes are possible from a release that recorded neither field: a run that was
            // appending, whose items ARE there, and a fresh run killed mid-flight, whose items
            // are in a temp while the output still holds a PREVIOUS export. Resuming assumed the
            // first, so upgrading with the second on disk appended the remainder of the
            // enumeration onto the earlier export - a 100,000-row file coming back with 163,037.
            // Undecidable means start over: an export re-runs from the first page and replaces
            // the output, which costs a pass and cannot leave a wrong file behind.
            if (apply)
            {
                WriteWarning(
                    "The resume checkpoint does not record which file the interrupted export's items are in. "
                    + "Exporting again from the beginning; nothing is lost.");
                PaginationCheckpoint.Delete(checkpointPath);
            }
            return false;
        }

        if (checkpoint.TempFile != null)
        {
            TempPromotion promotion;
            UnopenableReason? promotionReason;
            StagingFailure? promotionStaging = null;
            StagingRemoval? promotionSwept;
            if (apply)
                promotion = TryPromoteNamedTemp(outputPath, checkpoint.TempFile, dataLength,
                    out promotionReason, out promotionStaging, out promotionSwept);
            else
                promotion = CanPromoteNamedTemp(outputPath, checkpoint.TempFile, dataLength,
                    out promotionReason, out promotionStaging, out promotionSwept);

            // What the promotion took off its own staging name before it created one there, or -
            // on the preview, which takes nothing off it - what a run would. The applying pass
            // records the deletion here, where it happened, and warns about it below once this
            // run's own outcome for the recovery is known: a warning is a terminating error
            // under -WarningAction Stop, so written here it ended the run with the entry
            // deleted, the items still in the temp and nothing recovered.
            if (promotionSwept is { } promotionRemoval)
            {
                if (apply) WriteVerbose(promotionRemoval.Sentence);
                else _wouldRemoveAtStagingName = promotionRemoval.Sentence;
            }

            // The copy could not be staged, or could not be moved once it was. Both files are
            // where they were found and every item the checkpoint counts is still in the temp it
            // names, so this is not the route below for a temp whose items are in no file: that
            // one deletes the position counting them and exports afresh over the output, and
            // reached from here it did so with the temp holding all of them.
            if (promotion == TempPromotion.StagingFailed)
            {
                if (apply)
                {
                    ThrowTerminatingError(CheckpointStagingStop(promotionStaging!.Value,
                        checkpointPath, outputPath, checkpoint, checkpoint.TempFile,
                        promotionSwept?.Kind));
                    return false;
                }

                // The preview pass reaches the same name and may not throw. What a real run
                // would do there is stop, which is not one of the two actions the gate names.
                _wouldStopOnCheckpointFile = CheckpointStagingStopMessage(promotionStaging!.Value,
                    checkpointPath, outputPath, checkpoint, checkpoint.TempFile, null,
                    NothingWouldBeChanged);
                return false;
            }

            // The output the promotion would land in is another run's, or is not a file this
            // account can write at the recorded offset. The same stop the branch below reaches,
            // about the same file for the same reason: the run that has the output is appending
            // to it, and this one would be replacing it under them.
            //
            // Asked for before the move, so nothing was replaced - and the temp, with the
            // checkpoint still naming it, is exactly as it was found, since the temp is unlinked
            // only where the output was kept. So the position and the file it counts are the
            // pair they were, and the run that comes back promotes the same rows to the same
            // length over the same output.
            if (promotion is TempPromotion.OutputHeld or TempPromotion.OutputUnopenable)
            {
                var promotedOutputRefusal = new CheckpointFileRefusal(CheckpointFile.Output,
                    promotion == TempPromotion.OutputHeld
                        ? ClaimRefusal.AnotherRunHasIt
                        : ClaimRefusal.CannotBeOpened,
                    CheckpointStopRoute.Promoting, promotionReason);
                if (apply)
                {
                    ThrowTerminatingError(CheckpointFileStop(promotedOutputRefusal, checkpointPath,
                        outputPath, checkpoint));
                    return false;
                }

                // The preview pass asks the same question of the same file and may not throw:
                // it reports what a run would do and does none of it. What it would do is stop,
                // which is not one of the two actions the ShouldProcess gate names, so the gate
                // writes this sentence beside the one it does name.
                _wouldStopOnCheckpointFile = CheckpointFileStopMessage(promotedOutputRefusal,
                    checkpointPath, outputPath, checkpoint, RunWouldStopHere);
                return false;
            }

            if (promotion == TempPromotion.Taken)
            {
                if (apply)
                {
                    // Those items are the output now, and the output is this run's - the
                    // promotion kept it through the move. Repoint the checkpoint at it before
                    // anything else can fail, so a second interruption cannot promote the same
                    // temp again - and before the warning below, which under -WarningAction Stop
                    // is where the run ends: the position on disk has to be the one that
                    // resumes from what is now in the output.
                    RepointCheckpointAtHeldOutput(checkpoint, checkpointPath, outputPath,
                        HeldOutput!);
                    WriteWarning($"Recovered {checkpoint.ItemsCollected} items from an interrupted export's temp file. Resuming from checkpoint.");
                    // And what the sweep took off the staging name, now that what this run did
                    // about the checkpoint is on the stream in front of it.
                    if (promotionSwept is { } removed) WriteWarning(removed.Sentence);
                }
                return true;
            }

            // The temp is there and whole, and another run has it open - so the export this
            // checkpoint belongs to is not interrupted at all, it is collecting into that file
            // right now. Nothing here is a recovery: the items are that run's, the checkpoint is
            // the position it comes back to, and this run exports from the beginning into a temp
            // of its own. Deleting the checkpoint below would be the same mistake as unlinking
            // the temp, one file over.
            //
            // Or the claim failed because this account cannot open the file read-write, which
            // leaves both files in the same place and has no second export behind it. The
            // refusal is the same; only what it can honestly say about the cause differs.
            var namedTempRefusal = new CheckpointFileRefusal(CheckpointFile.NamedTemp,
                RefusalForNamedTemp(outputPath, checkpoint.TempFile, dataLength, apply));
            if (namedTempRefusal.Refuses)
            {
                // The run ends here, before the client is asked for, before the sweep a few
                // lines on, before a temp of this run's own exists and before anything is
                // written to -OutputFile or -CheckpointPath.
                //
                // Leaving the two files alone and exporting anyway was not enough, because
                // exporting IS what takes them: the first page boundary saves this run's own
                // position over the same path, the completion door then deletes what it finds
                // there as its own, and the holder's temp is an orphan the next sweep takes
                // with the rows still in it. The property the caller wants - this run proceeds
                // and the other run's work survives - is not available at any price, since
                // proceeding means replacing the output the checkpoint records and saving over
                // the checkpoint itself. So this run does not proceed.
                //
                // The rows it is protecting are in that temp and in no other file, and the
                // checkpoint is the only thing on disk that counts them: a holder still running
                // deletes it itself on the way out, a holder that dies keeps its temp for
                // exactly as long as a checkpoint counting it is there, and where the claim
                // failed on permissions instead it is the position write access has to be
                // granted for.
                if (apply)
                {
                    ThrowTerminatingError(CheckpointFileStop(namedTempRefusal, checkpointPath,
                        outputPath, checkpoint));
                    return false;
                }

                // The preview pass reaches the same two files and may not throw: -WhatIf reports
                // what a run would do and does none of it. What it would do is stop, which is
                // not one of the two actions the ShouldProcess gate below names, so the gate
                // writes this sentence beside the one it does name.
                _wouldStopOnCheckpointFile = CheckpointFileStopMessage(namedTempRefusal,
                    checkpointPath, outputPath, checkpoint, RunWouldStopHere);
                return false;
            }

            // The temp is not there, or is shorter than the length recorded for it - and the
            // first of those is what a promotion leaves for the instant between its unlink and
            // the save that repoints the position at the output. A second run over the same
            // command line, released with the first, read that instant and said those items were
            // not on disk, deleted the position counting them, exported from the beginning and
            // moved its own temp over the output at the end - which was the promoting run's
            // output, held and being appended to. So the output is asked about before any of
            // that, and a promoter holding it ends this run where the branch below ends it.
            //
            // Only the refusals are read. An output nothing holds is not evidence that a
            // promotion landed in it, whatever it measures: this checkpoint records its items
            // into the temp, and a previous export's file standing at the path measures exactly
            // what a promotion would have left about as often as a promoted one does - two rows
            // of one shape being two rows of another shape's length. So that case takes the route
            // it always took, which replaces the whole file rather than cutting it back to an
            // offset counted in a different one.
            var vanishedTempRefusal = new CheckpointFileRefusal(CheckpointFile.Output,
                RefusalForStandingOutput(outputPath, out var vanishedTempReason),
                CheckpointStopRoute.FreshAfterVanishedTemp, vanishedTempReason);
            if (vanishedTempRefusal.Refuses)
            {
                if (apply)
                {
                    ThrowTerminatingError(CheckpointFileStop(vanishedTempRefusal, checkpointPath,
                        outputPath, checkpoint));
                    return false;
                }

                _wouldStopOnCheckpointFile = CheckpointFileStopMessage(vanishedTempRefusal,
                    checkpointPath, outputPath, checkpoint, RunWouldStopHere);
                return false;
            }

            if (apply)
            {
                WriteWarning(
                    $"The interrupted export's temp file is missing or incomplete, so the {checkpoint.ItemsCollected} items it "
                    + "recorded are not on disk. Exporting again from the beginning; nothing is lost.");
                PaginationCheckpoint.Delete(checkpointPath);
                // And what the sweep took off the staging name, after what this run did about
                // the checkpoint.
                if (promotionSwept is { } removedAnyway) WriteWarning(removedAnyway.Sentence);
            }
            return false;
        }

        // The checkpoint names no temp, so the run that wrote it was appending straight into the
        // output: the items it counts are in that file, and cutting it back to the recorded
        // length is what stops the ones written after the last save from being written twice.
        //
        // Which is the same claim on the output that the branch above makes on a temp, and for
        // the same reason. A run that has resumed once holds -OutputFile open with the rows in
        // it, and its checkpoint names no temp for a second run over the same command line to
        // refuse it by - so that run judged the checkpoint its own, cut the live file back to
        // its own offset and appended, and the holder's next write landed past the hole.
        //
        // So the claim is not let go. The handle it opens is the handle this run writes through:
        // the cut below is a SetLength on it, the writer downstream appends onto it, and it is
        // released where the writing ends. Asked for and released here, a page-fetch before the
        // writer took an open of its own, it left a window a second run walked straight through
        // - both runs past the same claim, both cutting, both appending.
        CheckpointOutput taken;
        UnopenableReason? takenReason;
        if (apply) taken = TakeOutputForCheckpoint(outputPath, dataLength, out takenReason);
        else taken = WouldTakeOutputForCheckpoint(outputPath, dataLength, out takenReason);
        if (taken == CheckpointOutput.Taken) return true;

        var outputRefusal = new CheckpointFileRefusal(CheckpointFile.Output, RefusalFrom(taken),
            CheckpointStopRoute.Appending, takenReason);
        if (outputRefusal.Refuses)
        {
            // Where the temp branch ends the run, before the client, the sweep and the first
            // page - and on the preview pass, where it says the same thing in the future tense.
            if (apply)
            {
                ThrowTerminatingError(CheckpointFileStop(outputRefusal, checkpointPath,
                    outputPath, checkpoint));
                return false;
            }

            _wouldStopOnCheckpointFile = CheckpointFileStopMessage(outputRefusal, checkpointPath,
                outputPath, checkpoint, RunWouldStopHere);
            return false;
        }

        // Not the file the checkpoint describes - absent, or shorter than the length recorded -
        // so the items it counts are in no file and there is nothing to append to.
        if (apply)
        {
            WriteWarning(
                $"'{outputPath}' no longer holds the {checkpoint.ItemsCollected} items the resume checkpoint records. "
                + "Exporting again from the beginning; nothing is lost.");
            PaginationCheckpoint.Delete(checkpointPath);
        }

        return false;
    }
}
