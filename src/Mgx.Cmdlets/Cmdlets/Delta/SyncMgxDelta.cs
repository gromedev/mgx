using System.Collections;
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
/// -CheckpointPath adds mid-run crash resume: the enumeration position is saved at page
/// boundaries (and mid-page in JSONL mode), so a killed sync continues where it stopped
/// instead of re-enumerating from scratch. Resume is at-least-once: in pipeline mode the
/// page in flight at the crash is re-emitted in full.
/// </summary>
[Cmdlet(VerbsData.Sync, "MgxDelta")]
[OutputType(typeof(Hashtable))]
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

    /// <summary>
    /// Prefer-header tokens joined into a single Prefer header (drive delta behaviors such as
    /// deltashowremovedasdeleted). A change against the stored state forces a full re-sync,
    /// like -Property and -Filter. Note: deltaExcludeParent is a standalone request header,
    /// not a Prefer token - pass it via -Headers.
    /// </summary>
    [Parameter]
    [ArgumentCompleter(typeof(DeltaPreferCompleter))]
    public string[]? Prefer { get; set; }

    [Parameter]
    [ValidateRange(1, 999)]
    public int Top { get; set; }

    [Parameter]
    public string? OutputFile { get; set; }

    [Parameter]
    public SwitchParameter FullSync { get; set; }

    /// <summary>
    /// Baseline without enumerating: request only the latest delta token ("sync from now").
    /// Drive resources take ?token=latest; directory and other resources take
    /// $deltatoken=latest - the form is chosen automatically from the URI shape.
    /// Ignored (with a warning) when usable delta state already exists.
    /// </summary>
    [Parameter]
    public SwitchParameter Latest { get; set; }

    /// <summary>
    /// Path for the ephemeral mid-run resume checkpoint. Deleted on successful completion;
    /// any event that invalidates the enumeration (410 Gone, -FullSync, a -Property/-Filter/
    /// -Prefer change) deletes it too, so a stale position can never be resumed.
    /// </summary>
    [Parameter]
    public string? CheckpointPath { get; set; }

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
    /// Also used for Prefer tokens - the same normalization semantics apply.
    /// </summary>
    private static string NormalizeSelect(string? s) =>
        string.IsNullOrEmpty(s) ? "" : string.Join(",",
            s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

    protected override void BeginProcessing()
    {
        // Reject absolute URLs (relative paths only)
        if (Uri.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            Uri.TrimStart().StartsWith("http://", StringComparison.OrdinalIgnoreCase))
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
        string? resolvedOutputPath = null;
        if (OutputFile != null)
        {
            resolvedOutputPath = GetUnresolvedProviderPathFromPSPath(OutputFile);
            if (string.Equals(resolvedDeltaPath, resolvedOutputPath, StringComparison.OrdinalIgnoreCase))
            {
                ThrowTerminatingError(new ErrorRecord(
                    new ArgumentException("-DeltaPath and -OutputFile cannot be the same file."),
                    "DeltaPathOutputFileCollision", ErrorCategory.InvalidArgument, null));
                return;
            }
            // Named for what it is. Both probes are the same call, and one noun for both told a
            // caller whose -OutputFile directory was not writable that they could not write to
            // the delta state path - naming a file at -DeltaPath that had just passed its own.
            DeltaState.ValidateWriteAccess(resolvedOutputPath, "output path");
        }

        // The checkpoint must not collide with either state file: sharing a path would
        // corrupt both the position and the data it describes.
        if (CheckpointPath != null)
        {
            var resolvedCheckpointPath = GetUnresolvedProviderPathFromPSPath(CheckpointPath);
            if (string.Equals(resolvedCheckpointPath, resolvedDeltaPath, StringComparison.OrdinalIgnoreCase))
            {
                ThrowTerminatingError(new ErrorRecord(
                    new ArgumentException("-CheckpointPath and -DeltaPath must be different files."),
                    "CheckpointDeltaPathCollision", ErrorCategory.InvalidArgument, CheckpointPath));
                return;
            }
            if (resolvedOutputPath != null
                && string.Equals(resolvedCheckpointPath, resolvedOutputPath, StringComparison.OrdinalIgnoreCase))
            {
                ThrowTerminatingError(new ErrorRecord(
                    new ArgumentException("-CheckpointPath and -OutputFile must be different files."),
                    "CheckpointOutputCollision", ErrorCategory.InvalidArgument, CheckpointPath));
                return;
            }
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
        var resolvedCheckpointPath = CheckpointPath != null
            ? GetUnresolvedProviderPathFromPSPath(CheckpointPath)
            : null;

        // Handle -FullSync: delete existing delta state and any resume checkpoint - the
        // position it describes belongs to the enumeration being discarded.
        if (FullSync.IsPresent)
        {
            if (File.Exists(resolvedDeltaPath))
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
            DeleteCheckpoint(resolvedCheckpointPath, "full sync requested");
        }

        // Normalize $select and Prefer for order-independent comparison. The effective
        // select is what will go on the wire: a $select already in -Uri wins over
        // -Property (the builder defers to it, with a warning), and the state must
        // record and compare the wire value or every later run trips the consistency
        // check against a select that was never sent.
        var normalizedSelect = NormalizeSelect(
            GetQueryOptionValue(Uri, "$select")
            ?? (Property != null ? string.Join(",", Property) : null));
        var normalizedPrefer = NormalizeSelect(Prefer != null ? string.Join(",", Prefer) : null);
        var currentFilter = Filter;
        string requestUrl;

        // LoadWithResult distinguishes "not found" from "corrupt".
        // The endpoint-independent state checks run BEFORE GetClient() so their
        // errors surface without requiring a Graph connection; the checks that
        // compare against the session's endpoint run after it.
        var (existingState, loadResult) = DeltaState.LoadWithResult(resolvedDeltaPath);
        // -Latest means "baseline from now, return nothing". That is right for a first run and
        // catastrophic after a state invalidation: the user is told a full re-sync is starting,
        // gets zero items, and a fresh baseline token is persisted - so every change since the
        // last successful sync is dropped permanently. The guard that warns "-Latest ignored"
        // lives in the resume branch, which an invalidated state never reaches. Track it here
        // and clear it wherever state is discarded.
        var honourLatest = Latest.IsPresent;

        // A live resume checkpoint is not a fresh run either. Without delta state the guards
        // below never fire, so -Latest was honored on top of an interrupted enumeration: the
        // checkpoint is dropped a moment later as "a different enumeration" (the token=latest
        // suffix changes requestUrl), the items the crashed run collected stay stranded in its
        // temp, and an empty page still saves a from-now token - so everything before this
        // moment is permanently unreachable. -FullSync deletes the checkpoint above, so
        // "-FullSync -Latest" still re-baselines from now, which is what the warning below
        // tells people to use.
        var hasResumableCheckpoint = resolvedCheckpointPath != null && File.Exists(resolvedCheckpointPath);
        if (honourLatest && hasResumableCheckpoint)
            honourLatest = false;

        if (loadResult == DeltaLoadResult.Corrupt)
        {
            WriteWarning($"Delta state file '{DeltaPath}' is corrupt. Starting full sync.");
            // A corrupt state means the previous position is unknown, which is exactly when
            // baselining from now would hide the most: everything since the last good sync.
            honourLatest = false;
            // And the resume checkpoint goes with the position it counted into, the way it does
            // at every other door out of the delta state. What it records is the delta-link
            // spelling this run no longer builds, so leaving it standing bought a refusal of the
            // run's own earlier position: a warning naming a second sync that does not exist,
            // and a stale-temp sweep held off, over a temp that outlives the run by one because
            // nothing counts it any more.
            DeleteCheckpoint(resolvedCheckpointPath, "delta state corrupt");
            // Nothing downstream may still believe it is there. The -Latest guard below reads
            // this to decide which of two sentences to write, and one of them tells the caller
            // to delete a checkpoint that has just gone.
            hasResumableCheckpoint = false;
        }

        if (existingState != null)
        {
            // The deltaLink is absolute and carries its own version, so a run that omits
            // -ApiVersion silently keeps syncing whichever version built the state - the
            // caller believes they are on the default and are not. Empty means a pre-2.0.1
            // state file: unknown, not mismatched, so upgrades are not broken by this check.
            if (!string.IsNullOrEmpty(existingState.ApiVersion)
                && !string.Equals(existingState.ApiVersion, ApiVersion, StringComparison.OrdinalIgnoreCase))
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException(
                        $"Delta state was created against Graph {existingState.ApiVersion} "
                        + $"but this run requests {ApiVersion}. Re-run with "
                        + $"-ApiVersion {existingState.ApiVersion}, or use -FullSync to rebuild "
                        + $"against {ApiVersion}."),
                    "DeltaApiVersionMismatch", ErrorCategory.InvalidOperation, null));
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
            // State written before the wire-value change recorded the -Property form even
            // when -Uri carried the $select. If the stored value matches THAT form for
            // the same invocation, nothing actually changed on the wire - accept it, and
            // the state is rewritten in the new form on save.
            var legacySelect = NormalizeSelect(Property != null ? string.Join(",", Property) : null);
            var selectUnchanged =
                string.Equals(storedSelect, normalizedSelect, StringComparison.OrdinalIgnoreCase)
                || string.Equals(storedSelect, legacySelect, StringComparison.OrdinalIgnoreCase);
            if (!selectUnchanged)
            {
                WriteWarning(
                    "Property selection changed since last sync "
                    + $"(was: '{existingState.Select ?? "(all)"}', now: '{(string.IsNullOrEmpty(normalizedSelect) ? "(all)" : normalizedSelect)}')."
                    + " Starting full re-sync to capture all selected properties.");
                if (!DeltaState.Delete(resolvedDeltaPath))
                    WriteVerbose($"Could not delete old delta state at '{DeltaPath}' (file may be locked). It will be overwritten.");
                DeleteCheckpoint(resolvedCheckpointPath, "property selection changed");
                hasResumableCheckpoint = false;  // the -Latest warning below must not name it
                existingState = null;
                honourLatest = false;  // a discarded state is not a fresh run
            }

            // Detect Prefer change between runs: the tokens shape what the enumeration
            // returns (removed facets, sharing annotations), so mixing states is unsound.
            if (existingState != null)
            {
                var storedPrefer = NormalizeSelect(existingState.Prefer);
                if (!string.Equals(storedPrefer, normalizedPrefer, StringComparison.OrdinalIgnoreCase))
                {
                    WriteWarning(
                        "Prefer headers changed since last sync "
                        + $"(was: '{(string.IsNullOrEmpty(storedPrefer) ? "(none)" : storedPrefer)}', now: '{(string.IsNullOrEmpty(normalizedPrefer) ? "(none)" : normalizedPrefer)}')."
                        + " Starting full re-sync.");
                    if (!DeltaState.Delete(resolvedDeltaPath))
                        WriteVerbose($"Could not delete old delta state at '{DeltaPath}' (file may be locked). It will be overwritten.");
                    DeleteCheckpoint(resolvedCheckpointPath, "Prefer headers changed");
                    hasResumableCheckpoint = false;  // the -Latest warning below must not name it
                    existingState = null;
                    honourLatest = false;  // a discarded state is not a fresh run
                }
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
                DeleteCheckpoint(resolvedCheckpointPath, "filter changed");
                hasResumableCheckpoint = false;  // the -Latest warning below must not name it
                existingState = null;
                honourLatest = false;  // a discarded state is not a fresh run
            }
        }

        // GetClient() sits between the two validation halves on purpose. It runs after the
        // state-file checks above so their errors surface without a Graph connection, and
        // before everything below because it is the only thing that refreshes s_graphEndpoint
        // from the session: on the first call of a session the endpoint comparison and the
        // request URL would otherwise be built against the default endpoint instead of the
        // connected one. Invoke-MgxRequest sequences GetClient() first for the same reason.
        var client = GetClient();

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
            // Compare paths to paths. NormalizePath keeps any query, while AbsolutePath never
            // has one, so "/users/delta?$select=id" - the shape Microsoft's delta docs show -
            // guaranteed a mismatch: run 1 saved state, run 2 died with a SecurityError accusing
            // that state file of tampering. A trailing slash failed identically.
            var expectedPath = NormalizePath(Uri).Split('?')[0].TrimEnd('/');
            if (System.Uri.TryCreate(validated, UriKind.Absolute, out var parsedDelta)
                && !parsedDelta.AbsolutePath.TrimEnd('/')
                        .Contains(expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                ThrowTerminatingError(new ErrorRecord(
                    new InvalidOperationException(
                        $"Delta state URL path does not match expected resource '{expectedPath}'. "
                        + "The delta state file may have been tampered with. Use -FullSync to start fresh."),
                    "DeltaLinkPathMismatch", ErrorCategory.SecurityError, null));
                return;
            }

            if (Latest.IsPresent)
            {
                WriteWarning(
                    $"-Latest ignored: usable delta state already exists at '{DeltaPath}'. "
                    + "Delete it or use -FullSync to re-baseline from now.");
            }

            requestUrl = validated;
            WriteVerbose($"Resuming delta sync from {existingState.LastSync:u} ({existingState.ItemCount} items in previous sync).");
        }
        else
        {
            requestUrl = BuildListUrl(VersionedBaseUrl, Uri,
                new ODataListParams(false, Top, Top > 0 ? Top : 999, Filter, Property, null, null, 0, null),
                out var deferred);
            if (deferred.Count > 0)
                WriteWarning(DescribeDeferredOptions(deferred));

            if (Latest.IsPresent && !honourLatest)
            {
                WriteWarning(hasResumableCheckpoint
                    ? "-Latest ignored: a resume checkpoint exists, so an interrupted enumeration "
                      + "is still in progress. Baselining from now would abandon what it collected "
                      + "and drop every change before now. Delete the checkpoint, or use -FullSync "
                      + "to re-baseline."
                    : "-Latest ignored: the previous delta state was discarded, so this run must "
                      + "enumerate to rebuild it. Baselining from now would silently drop every "
                      + "change since the last successful sync.");
            }
            else if (honourLatest)
            {
                // "Sync from now": returns an empty page plus a deltaLink; the existing
                // empty-page-still-saves-token path persists the baseline. The token form
                // differs by service: OneDrive/SharePoint take token=latest, directory and
                // everything else $deltatoken=latest.
                var tokenParam = AdaptivePacing.Classify(Uri) == WorkloadBucket.Drive
                    ? "token=latest"
                    : "$deltatoken=latest";
                requestUrl += (requestUrl.Contains('?') ? "&" : "?") + tokenParam;
                WriteVerbose($"No existing delta state. Requesting latest delta token only ({tokenParam}).");
            }
            else
            {
                WriteVerbose("No existing delta state. Performing full initial sync.");
            }
        }

        ExecuteDeltaSync(client, requestUrl, resolvedDeltaPath, resolvedOutputPath,
            resolvedCheckpointPath, normalizedSelect, normalizedPrefer, currentFilter, sw);
    }

    /// <summary>
    /// The Graph API version a deltaLink was issued by, read from the link itself, or null when
    /// it cannot be read.
    /// </summary>
    private static string? ApiVersionOfLink(string? deltaLink)
    {
        if (string.IsNullOrEmpty(deltaLink)) return null;
        if (!System.Uri.TryCreate(deltaLink, UriKind.Absolute, out var u)) return null;
        var first = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.Equals(first, "v1.0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(first, "beta", StringComparison.OrdinalIgnoreCase)
            ? first
            : null;
    }

    private void DeleteCheckpoint(string? checkpointPath, string reason)
    {
        if (checkpointPath == null || !File.Exists(checkpointPath)) return;
        if (PaginationCheckpoint.Delete(checkpointPath))
            WriteVerbose($"Deleted resume checkpoint ({reason}).");
        else
            WriteWarning($"Could not delete resume checkpoint at '{checkpointPath}' ({reason}). " +
                "Delete it manually before the next run.");
    }


    /// <summary>
    /// Whose the checkpoint on disk is, and where it is not this sync's, why not. Three things
    /// have to agree before it is this run's.
    ///
    /// The output mode: a checkpoint that names an output file, names a temp of one, or records
    /// a byte length was written by a sync collecting into a file, and its position counts items
    /// that went there rather than down the pipeline. Resuming a pipeline sync from it emits
    /// neither those items nor anything before them and then saves a delta token over the lot.
    /// The length carries as much of that as the names do: a file-mode run records no temp once
    /// it is appending, and none when a cancellation promoted the one it had, so a release that
    /// did not yet record the output left file-mode checkpoints naming neither - and every one
    /// of them was adopted here. A length is measured on a file, and a pipeline run has none to
    /// measure. The reverse is refused on the same grounds.
    ///
    /// The output itself, on file-mode runs: two syncs of the same -Uri build the same resource,
    /// so that comparison says nothing about WHICH file a recorded length was counted in, and
    /// applied to another sync's it cut that file to this one's offset, mid-line.
    ///
    /// The resource, endpoint-independently and however -Uri was typed: what was enumerated, not
    /// where Graph was reached, and not which spelling reached it - Graph answers "/users/delta"
    /// and "/Users/delta" from one collection, so a case the caller typed differently between
    /// two runs is the same enumeration and refusing it costs the run its own position.
    ///
    /// Nothing here decides differently than the bare predicate this replaced; what a refusal
    /// can then say is what changes. A checkpoint recording no output was believed for as long
    /// as the files beside it corroborated it, so losing that corroboration - a temp since
    /// removed, an output since replaced - is one sync and no second run anywhere, and it is
    /// the shape every release before this one wrote.
    ///
    /// Which is why the corroboration is asked for only where there is something to corroborate:
    /// <see cref="MgxCmdletBase.RecordsAFileToWeigh"/> is the export's own guard, and without it
    /// a checkpoint recording neither an output nor a length was refused for want of a
    /// measurement - the whole pre-2.1 file-mode shape, recovered by the export and refused
    /// here, with -Latest suppressed by the file it left standing for good. That shape reaches
    /// the resource comparison below and, where the enumeration is this run's, the recovery the
    /// export makes of it.
    /// </summary>
    private CheckpointOwnership OwnershipOf(PaginationCheckpoint checkpoint, string? outputPath,
        string requestUrl)
    {
        if (outputPath == null)
        {
            if (checkpoint.OutputFile != null || checkpoint.TempFile != null
                || checkpoint.DataLength != null)
            {
                return CheckpointOwnership.AnotherSyncs;
            }
        }
        else if (RecordsAFileToWeigh(checkpoint)
                 && !RecordedOutputMatches(checkpoint.OutputFile, checkpoint.TempFile,
                        checkpoint.DataLength, outputPath, mayWrite: true))
        {
            return checkpoint.OutputFile != null
                ? CheckpointOwnership.AnotherSyncs
                : CheckpointOwnership.Uncorroborated;
        }

        return SameResourceIdentity(ResourceIdentity(checkpoint.Resource),
                   ResourceIdentity(requestUrl))
            ? CheckpointOwnership.Mine
            : CheckpointOwnership.AnotherSyncs;
    }

    /// <summary>Which of the three a checkpoint on disk is.</summary>
    private enum CheckpointOwnership
    {
        /// <summary>This sync's.</summary>
        Mine,

        /// <summary>
        /// Another sync's: it records another output, another enumeration, or the other
        /// output mode.
        /// </summary>
        AnotherSyncs,

        /// <summary>
        /// Records no output file, and the files beside this one no longer stand for it. Which
        /// sync wrote it cannot be told from here, and telling the caller it was another one is
        /// a diagnosis they can go and check and find nothing behind.
        /// </summary>
        Uncorroborated,
    }

    /// <summary>
    /// Whether the temp a refused checkpoint names is a file the stale-temp sweep would reach.
    /// The name comes off a checkpoint, which is untrusted once it is on disk, and nothing here
    /// opens it - all it decides is whether the sweep runs at all, so a name of some other shape
    /// is answered no rather than refused: the sweep cannot delete it either, and skipping the
    /// sweep for it would leave real orphans behind for nothing.
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

    /// <summary>
    /// What a caller can act on when the checkpoint on disk records an enumeration this run
    /// cannot resume: what is known about the file, what this run does instead, and that a
    /// -CheckpointPath belongs to one enumeration. Refusing it is the whole response - the file
    /// is left alone, and so is any staging file beside it, because whatever wrote them may
    /// still resume from exactly those.
    ///
    /// Which sync wrote it is not something this can tell, and asserting a second one sent the
    /// caller looking for a run that in the commonest shape here does not exist: a checkpoint
    /// saved during a delta enumeration, found by the same command line after the state
    /// recording that delta was lost - corrupt, deleted by hand, a -DeltaPath since moved - so
    /// the run builds a full-sync URL and refuses its own earlier position. There is no second
    /// sync to find, and giving each its own -CheckpointPath is what the caller already did.
    /// Both readings are offered and neither is asserted; what this run does about it is the
    /// same either way.
    /// </summary>
    private static string ForeignCheckpointWarning(string checkpointPath, string? outputPath, string deltaPath) =>
        $"The resume checkpoint at '{checkpointPath}' records an enumeration this run cannot "
        + $"resume from, so it is left as it is and this run enumerates from what '{deltaPath}' "
        + "records"
        + (outputPath != null ? $", into '{outputPath}'" : "")
        + ". It is either another sync's, sharing this -CheckpointPath, or this sync's own from "
        + "a pass it no longer makes - a delta enumeration whose state has since been lost "
        + "leaves exactly this. Two syncs sharing one -CheckpointPath overwrite each other's "
        + "resume position; give each its own.";

    /// <summary>
    /// What a caller can act on when a checkpoint that records no output file is refused: it
    /// was written before the output was recorded, nothing beside this run's output stands for
    /// it any more, and this run enumerates from the delta token instead. Naming a different
    /// sync there sends the caller looking for a second run over the same -CheckpointPath, and
    /// there is none to find - a temp that has since been removed and an output that has since
    /// been replaced reach the same refusal.
    /// </summary>
    private static string UncorroboratedCheckpointWarning(
        string checkpointPath, string outputPath, string deltaPath) =>
        $"The resume checkpoint at '{checkpointPath}' records no output file, and the files "
        + $"beside '{outputPath}' no longer corroborate it, so it is left as it is and this run "
        + $"enumerates from the delta token in '{deltaPath}', into '{outputPath}'; no changes "
        + "are lost.";

    /// <summary>
    /// What this run tells the caller about a checkpoint it will not resume from, or null when
    /// it is this sync's and there is nothing to tell. Uncorroborated is reachable only from
    /// the branch that has an output to name; a pipeline run refuses on the output mode, which
    /// is another sync's checkpoint however little else is known about it.
    /// </summary>
    private string? RefusalFor(PaginationCheckpoint checkpoint, string? outputPath,
        string requestUrl, string checkpointPath, string deltaPath)
        => OwnershipOf(checkpoint, outputPath, requestUrl) switch
        {
            CheckpointOwnership.Mine => null,
            CheckpointOwnership.Uncorroborated when outputPath != null =>
                UncorroboratedCheckpointWarning(checkpointPath, outputPath, deltaPath),
            _ => ForeignCheckpointWarning(checkpointPath, outputPath, deltaPath),
        };

    /// <summary>
    /// How the run ends when a file its own checkpoint stands for is held open by another sync, or
    /// cannot be opened read-write by this account. Two files reach this, and the reading is one:
    /// the changes the checkpoint counts are in that file and in no other, the checkpoint is the
    /// only thing on disk that counts them, and this sync has no enumeration to make that leaves
    /// the pair intact - it would save its own position over the checkpoint at the first page
    /// boundary and move the delta token past changes held in that file alone. So it says what it
    /// found, what it did not do, and the two ways out, and every file is exactly where it was.
    ///
    /// Which file decides what going on would cost, and for an output refusal
    /// <see cref="CheckpointFileRefusal.Route"/> decides it further: a checkpoint naming a temp
    /// that is still there holds the changes in that temp, and going on promotes them, replacing
    /// the output outright; one naming a temp that has vanished holds them nowhere this run can
    /// reach, and going on re-enumerates from the last saved delta token and replaces the output
    /// with the result; and one naming no temp at all was written by a run appending straight
    /// into the output, so the changes are in the output itself and going on cuts it back under
    /// its writer - whose next write then lands past the hole that offset leaves.
    ///
    /// Which of the two readings decides only the cause, the advice, and the id and category
    /// under them. A held file has a second sync behind it that will either finish, and delete
    /// this checkpoint itself, or die and leave it for its own re-run; an unopenable one has no
    /// second sync anywhere, only a file whose own open failed for a reason named in
    /// <see cref="UnopenableReason.Reason"/> - a permission this account can be granted, or a
    /// symlink loop, an over-long path or a pipe that granting one does nothing for.
    /// </summary>
    private static string CheckpointFileStopMessage(CheckpointFileRefusal refusal,
        string checkpointPath, string outputPath, PaginationCheckpoint checkpoint)
    {
        // What going on would cost past a held temp, which is the output beside it - named only
        // where there is one. A checkpoint naming a temp is reached with nothing at -OutputFile
        // at all, and a run that said it would replace that file sent the caller to look at
        // something no sync had written yet.
        var goingOn = File.Exists(outputPath)
            ? $"Going on would replace '{outputPath}', save this run's position over the "
              + "checkpoint that counts those items, and advance the delta token past them."
            : "Going on would save this run's position over the checkpoint that counts those "
              + "items, and advance the delta token past them.";

        // The output is unopenable rather than held. The three routes agree past this point -
        // the code beyond this check falls through to the same place a file that never held the
        // recorded items reaches, which deletes the checkpoint, replaces the output and advances
        // the token at the completion that follows - so only the first clause, naming where the
        // items are now, and the recovery word in the advice change per route.
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
                  + "remove the checkpoint to sync afresh."
                : reason.IsUnsearchableParent
                    ? $"Grant access to that directory and run again{recovers}, or remove the "
                      + "checkpoint to sync afresh."
                    : reason.IsPermission
                        ? $"Grant write access to that file and run again{recovers}, or remove "
                          + "it and the checkpoint to sync afresh."
                        : $"Fix that and run again{recovers}, or remove it and the checkpoint to "
                          + "sync afresh.";

            return refusal.Route switch
            {
                CheckpointStopRoute.Promoting =>
                    $"{openFailed} The resume checkpoint at '{checkpointPath}' records "
                    + $"{checkpoint.ItemsCollected} items into the temp '{checkpoint.TempFile}'; "
                    + $"this run cannot promote them into '{outputPath}'. Going on would delete "
                    + "the checkpoint that counts them, replace that file and advance the delta "
                    + $"token past them. {RunStopsHere} {advice}",

                CheckpointStopRoute.FreshAfterVanishedTemp =>
                    $"{openFailed} The resume checkpoint at '{checkpointPath}' recorded "
                    + $"{checkpoint.ItemsCollected} items into a temp that is gone; this run "
                    + $"cannot write a fresh copy into '{outputPath}' either. Going on would "
                    + "delete the checkpoint that counts them, replace that file and advance the "
                    + $"delta token past them. {RunStopsHere} {advice}",

                _ =>
                    $"{openFailed} The resume checkpoint at '{checkpointPath}' records "
                    + $"{checkpoint.ItemsCollected} items into it, so this run can neither "
                    + "resume into it nor say whether those items are still there. Going on "
                    + "would delete the checkpoint that counts them, replace that file and "
                    + $"advance the delta token past them. {RunStopsHere} {advice}",
            };
        }

        return (refusal.File, refusal.Held) switch
        {
            (CheckpointFile.NamedTemp, true) =>
                "Another sync is still writing the temp file the resume checkpoint at "
                + $"'{checkpointPath}' names, so the {checkpoint.ItemsCollected} items it records "
                + $"are that run's. {goingOn} {RunStopsHere} Wait for that sync to finish, or "
                + "give this one its own -OutputFile and -CheckpointPath.",

            (CheckpointFile.NamedTemp, false) =>
                $"The temp file '{checkpoint.TempFile}' that the resume checkpoint at "
                + $"'{checkpointPath}' names cannot be opened for writing by this account - the "
                + "open was refused on permissions, not on sharing - so the "
                + $"{checkpoint.ItemsCollected} items it records cannot be recovered here. "
                + $"{goingOn} {RunStopsHere} Grant write access to that file and run again to "
                + "recover them, or remove it and the checkpoint to sync afresh.",

            (CheckpointFile.Output, true) => refusal.Route switch
            {
                CheckpointStopRoute.Promoting =>
                    $"Another sync is still writing '{outputPath}'. The resume checkpoint at "
                    + $"'{checkpointPath}' records {checkpoint.ItemsCollected} items into the "
                    + $"temp '{checkpoint.TempFile}'; going on would replace '{outputPath}' with "
                    + "them under the run that holds it, save this run's position over the "
                    + "checkpoint that counts those items, and advance the delta token past "
                    + $"them. {RunStopsHere} Wait for that sync to finish, or give this one its "
                    + "own -OutputFile and -CheckpointPath.",

                CheckpointStopRoute.FreshAfterVanishedTemp =>
                    $"Another sync is still writing '{outputPath}'. The resume checkpoint at "
                    + $"'{checkpointPath}' recorded {checkpoint.ItemsCollected} items into a "
                    + "temp that is gone; going on would re-enumerate from the last saved delta "
                    + $"token and replace '{outputPath}' under the run that holds it, save this "
                    + "run's position over the checkpoint that counts those items, and advance "
                    + $"the delta token past them. {RunStopsHere} Wait for that sync to finish, "
                    + "or give this one its own -OutputFile and -CheckpointPath.",

                _ =>
                    $"Another sync is still writing '{outputPath}', which the resume checkpoint "
                    + $"at '{checkpointPath}' records {checkpoint.ItemsCollected} items into. "
                    + "Going on would cut that file back under it, save this run's position over "
                    + "the checkpoint that counts those items, and advance the delta token past "
                    + $"them. {RunStopsHere} Wait for that sync to finish, or give this one its "
                    + "own -OutputFile and -CheckpointPath.",
            },

            _ => UnopenableOutputSentence(),
        };
    }

    /// <inheritdoc cref="CheckpointFileStopMessage"/>
    private static ErrorRecord CheckpointFileStop(CheckpointFileRefusal refusal,
        string checkpointPath, string outputPath, PaginationCheckpoint checkpoint) =>
        new(new InvalidOperationException(CheckpointFileStopMessage(refusal, checkpointPath,
                outputPath, checkpoint)),
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
    /// missing, and reading it as one was what deleted the checkpoint and moved the delta token
    /// past changes held in the temp: every item it counts is in that temp, whole and claimable,
    /// and the run that comes back stages the same bytes over the same output. So the sentence
    /// says where those changes are, what failed and where, and that both files are as they were
    /// found - and the run stops rather than sync past a position it would then save over.
    /// </summary>
    /// <param name="tempName">The temp those changes are in. The checkpoint's own record of it
    /// where it has one, and the file adoption picked where the checkpoint predates that field:
    /// either way it is the file the reader has to keep, and a sentence that read it off the
    /// checkpoint named nothing at all on the adoption route.</param>
    /// <param name="removedKind">What the sweep took off the staging name on the way here, where
    /// it took anything: the run deleted an entry and then stopped, and a reader who is told only
    /// that the staging failed goes looking for a file that is no longer there. Named in the
    /// stop rather than warned about separately, because a warning written before a terminating
    /// error is what -WarningAction Stop ends the run on instead of this sentence.</param>
    private static string CheckpointStagingStopMessage(StagingFailure staging,
        string checkpointPath, string outputPath, PaginationCheckpoint checkpoint,
        string tempName, string? removedKind) =>
        $"The {checkpoint.ItemsCollected} items the resume checkpoint at '{checkpointPath}' "
        + $"records are in '{tempName}', whole, but staging them into '{outputPath}' "
        + $"failed at '{staging.Path}': {staging.Reason}"
        + (removedKind != null
            ? $", after removing what stood at the staging name: {removedKind}"
            : "")
        + $". {NothingWasChanged} fix that and run "
        + "again to recover them, or remove the temp and the checkpoint to re-enumerate from the "
        + "last saved delta token.";

    /// <inheritdoc cref="CheckpointStagingStopMessage"/>
    private static ErrorRecord CheckpointStagingStop(StagingFailure staging, string checkpointPath,
        string outputPath, PaginationCheckpoint checkpoint, string tempName,
        string? removedKind) =>
        new(new InvalidOperationException(CheckpointStagingStopMessage(staging, checkpointPath,
                outputPath, checkpoint, tempName, removedKind)),
            "CheckpointStagingFailed", ErrorCategory.WriteError, checkpointPath);

    /// <summary>
    /// What a reconcile leaves for the caller to do. Three of the four say do not resume, and
    /// they are not the same answer: one is a checkpoint a live sync is still using, on which
    /// the run ends with both files where they were; another is a promotion whose copy could
    /// not be written or moved, on which the run also ends with both files where they were;
    /// and the last is a checkpoint describing items that are in no file, which is deleted
    /// wherever this run is able to delete it.
    /// </summary>
    private enum CheckpointRecovery
    {
        /// <summary>
        /// The files hold what the checkpoint says they hold - promoted, trimmed or already
        /// so - and the run resumes from the position it records.
        /// </summary>
        Resumable,

        /// <summary>
        /// Another sync is writing one of the files this checkpoint stands for - the temp it
        /// names, or the output it appends to - or this account cannot open it read-write.
        /// Nothing here is this run's to recover or to remove, and there is no enumeration it
        /// can make that does not destroy what those files hold, so the caller ends the run on
        /// it. Which file, and which of the two ways the claim failed, comes back beside this,
        /// because between them they decide what the caller can honestly tell the reader to do
        /// about it.
        /// </summary>
        Refused,

        /// <summary>
        /// The copy the promotion stages beside the output could not be written, or could not be
        /// moved onto the output once it was. The temp holds every change the checkpoint counts
        /// and the checkpoint still names it, so nothing here is recoverable by re-enumerating
        /// and nothing is this run's to remove: the caller ends the run on it, naming the write
        /// that failed and where. Told from Discarded because that one deletes the position
        /// counting those changes and moves the token past them.
        /// </summary>
        StagingFailed,

        /// <summary>
        /// The items the checkpoint counts are in no file this run can reach, so there is
        /// nothing to resume from. The delete that goes with it is attempted and may fail; the
        /// answer holds either way.
        /// </summary>
        Discarded,
    }

    /// <summary>
    /// Put the files into the state the checkpoint claims, or delete the checkpoint. A
    /// checkpoint records which file its items were written to and how many bytes of that file
    /// they occupy, which makes three cases decidable instead of guessed.
    ///
    /// A temp is named, so the interrupted run was fresh and its items are in that temp while
    /// the output still holds the PREVIOUS sync's rows. Those rows were the previous run's
    /// result, already replaced by every path that completes - a fresh run that finishes moves
    /// its temp over the output, and a cancellation promotes the same way - so recovery
    /// promotes too. Appending instead would put rows the caller has already consumed back in
    /// front of this sync's.
    ///
    /// None is named, so the run was appending to the output and its items are already there,
    /// past the recorded length only if it wrote more after its last save. Cutting back to that
    /// length is what stops those from being written twice.
    ///
    /// Neither is recorded, so the checkpoint cannot say which file its items are in, and an
    /// appending run's checkpoint cannot be told from a fresh one's. When no output exists the
    /// ambiguity is harmless - everything the run wrote is in its temp - but against an
    /// existing output the only recovery that cannot lose or repeat items is re-enumerating.
    ///
    /// When the counted items turn out to be in no file, the delta link has not moved, so
    /// re-enumerating costs time and loses nothing while resuming past them loses them for good.
    ///
    /// A file of the checkpoint's that another run has open is none of those: the files are
    /// exactly what the checkpoint says they are, and the sync they belong to has not been
    /// interrupted. Its items are that run's, and the checkpoint is the position it comes back
    /// to, so neither is this run's to touch - and there is nothing left for this run to do
    /// either. Resuming would leave those items out of the output while the delta token advanced
    /// past them; re-enumerating would replace the output, save this run's position over the
    /// checkpoint that counts them, and move the token anyway. Refused comes back, and the caller
    /// ends the run there. The named temp is one such file; the output a checkpoint naming no
    /// temp records its items into is the other, and there the run that holds it is one that has
    /// already resumed - so a second run over the same command line finds nothing in the
    /// checkpoint to refuse it by and has to be stopped by the claim instead.
    ///
    /// Whether the checkpoint may be resumed from is the answer, and not the file left on disk.
    /// A bail-out that deletes the checkpoint it has just given up on can fail to delete it - a
    /// checkpoint directory this account may read but not unlink from is answered false and
    /// leaves the file - so a caller reading the refusal off File.Exists read the one thing a
    /// failed delete gets wrong, resumed from the nextLink the warning had promised to
    /// re-enumerate past, and advanced the delta token over items that are in no file at all.
    /// Discarded binds either way; the file that outlives it is given up on again next run.
    /// </summary>
    private CheckpointRecovery ReconcileCheckpointWithFiles(string checkpointPath,
        string outputPath, PaginationCheckpoint checkpoint, out CheckpointFileRefusal refused,
        out StagingFailure? staging, out string? stagingTemp, out string? stagingRemoved)
    {
        refused = CheckpointFileRefusal.None;
        staging = null;
        stagingTemp = null;
        stagingRemoved = null;
        if (checkpoint.DataLength is not { } dataLength)
        {
            if (!File.Exists(outputPath))
            {
                var adopted = TryAdoptOrphanedTemp(outputPath, checkpoint.ItemsCollected,
                    out stagingTemp, out staging, out var adoptSwept);

                // What the adoption took off its own staging name before it created one there.
                // The name is nobody's to keep - a link, a pipe or a copy left by an interrupted
                // promotion standing at it is gone - so the run says which it was. Recorded here
                // and warned about once this run's own outcome for the recovery is known: a
                // warning is a terminating error under -WarningAction Stop, so written here it
                // ended the run with the entry deleted, the changes still in the temp and
                // nothing recovered.
                stagingRemoved = adoptSwept?.Kind;
                if (adoptSwept is { } adoptRemoval) WriteVerbose(adoptRemoval.Sentence);

                if (adopted)
                {
                    WriteWarning($"Recovered {checkpoint.ItemsCollected} items from an interrupted sync's temp file. Resuming from checkpoint.");
                    if (adoptSwept is { } removed) WriteWarning(removed.Sentence);
                    return CheckpointRecovery.Resumable;
                }

                // The copy could not be staged beside the output. Every change the checkpoint
                // counts is in the temp this names, whole, so the delete below is not the answer:
                // it would take the position counting them away and move the token past every
                // one. What came off the name goes into the stop the caller writes, since a
                // warning in front of a terminating error is what -WarningAction Stop ends on.
                if (staging != null) return CheckpointRecovery.StagingFailed;

                WriteWarning("Checkpoint found but output file is missing. Deleting stale checkpoint and starting fresh.");
                PaginationCheckpoint.Delete(checkpointPath);
                if (adoptSwept is { } removedAnyway) WriteWarning(removedAnyway.Sentence);
                return CheckpointRecovery.Discarded;
            }

            WriteWarning(
                "The resume checkpoint does not record which file the interrupted sync's items are in. "
                + "Re-enumerating from the last saved delta token; no changes are lost.");
            PaginationCheckpoint.Delete(checkpointPath);
            return CheckpointRecovery.Discarded;
        }

        if (checkpoint.TempFile != null)
        {
            stagingTemp = checkpoint.TempFile;
            var promotion = TryPromoteNamedTemp(outputPath, checkpoint.TempFile, dataLength,
                out var promotionReason, out staging, out var swept);

            // What the promotion took off its own staging name before it created one there. The
            // name is nobody's to keep - a link, a pipe or a copy left by an interrupted
            // promotion standing at it is gone - so the run says which it was. Recorded here and
            // warned about once this run's own outcome for the recovery is known: a warning is a
            // terminating error under -WarningAction Stop, so written here it ended the run with
            // the entry deleted, the changes still in the temp and nothing recovered.
            stagingRemoved = swept?.Kind;
            if (swept is { } promotionRemoval) WriteVerbose(promotionRemoval.Sentence);

            // The copy could not be staged, or could not be moved once it was. Both files are
            // where they were found and every change the checkpoint counts is still in the temp
            // it names, so this is not the route below for a temp whose changes are in no file:
            // that one deletes the position counting them, re-enumerates over the output and
            // moves the token past them, and reached from here it did so with the temp holding
            // every one.
            if (promotion == TempPromotion.StagingFailed) return CheckpointRecovery.StagingFailed;

            // The output the promotion would land in is another run's, or is not a file this
            // account can write at the recorded offset. The same stop the branch below reaches,
            // about the same file for the same reason: the run that has the output is appending
            // to it, and this one would be replacing it under them and moving the token past
            // what it wrote.
            //
            // Asked for before the move, so nothing was replaced - and the temp, with the
            // checkpoint still naming it, is exactly as it was found, since the temp is unlinked
            // only where the output was kept. So the position and the file it counts are the
            // pair they were, and the run that comes back promotes the same changes to the same
            // length over the same output.
            if (promotion is TempPromotion.OutputHeld or TempPromotion.OutputUnopenable)
            {
                refused = new CheckpointFileRefusal(CheckpointFile.Output,
                    promotion == TempPromotion.OutputHeld
                        ? ClaimRefusal.AnotherRunHasIt
                        : ClaimRefusal.CannotBeOpened,
                    CheckpointStopRoute.Promoting, promotionReason);
                return CheckpointRecovery.Refused;
            }

            if (promotion == TempPromotion.Taken)
            {
                // Those changes are the output now, and the output is this run's - the promotion
                // kept it through the move. Repoint the checkpoint at it immediately, so a second
                // interruption cannot promote the same temp a second time - and before the
                // warnings below, which under -WarningAction Stop are where the run ends: the
                // position on disk has to be the one that resumes from what is now in the output.
                RepointCheckpointAtHeldOutput(checkpoint, checkpointPath, outputPath, HeldOutput!);
                WriteWarning($"Recovered {checkpoint.ItemsCollected} items from an interrupted sync's temp file. Resuming from checkpoint.");
                if (swept is { } removed) WriteWarning(removed.Sentence);
                return CheckpointRecovery.Resumable;
            }

            // The temp is there and whole, and another run has it open - so the sync this
            // checkpoint belongs to is not interrupted at all, it is collecting into that file
            // right now. Its items are that run's, the checkpoint is the position it comes back
            // to, and this run has nothing here to recover: deleting the checkpoint below, or
            // resuming from it, would be the same mistake as unlinking the temp, one file over.
            //
            // Or the claim failed because this account cannot open the file read-write, which
            // leaves both files in the same place and has no second sync behind it. The refusal
            // is the same; only what it can honestly say about the cause differs.
            //
            // Refused says nothing here, because the sentence a caller acts on is about the run
            // and not about the reconcile: this sync cannot go on without replacing the output
            // beside these two files, saving its own position over the checkpoint that counts
            // their rows, and moving the delta token past them. It ends instead, and the site
            // that ends it is the one that can say so before anything has happened.
            refused = new CheckpointFileRefusal(CheckpointFile.NamedTemp,
                RefusalForNamedTemp(outputPath, checkpoint.TempFile, dataLength, mayWrite: true));
            if (refused.Refuses) return CheckpointRecovery.Refused;

            // The temp is not there, or is shorter than the length recorded for it - and the
            // first of those is what a promotion leaves for the instant between its unlink and
            // the save that repoints the position at the output. A second run over the same
            // command line, released with the first, read that instant and said those changes
            // were not on disk, deleted the position counting them, re-enumerated over the
            // output the promoter was holding, and moved the delta token past both runs' work.
            // So the output is asked about before any of that, and a promoter holding it ends
            // this run where the branch below ends it.
            //
            // Only the refusals are read. An output nothing holds is not evidence that a
            // promotion landed in it, whatever it measures: this checkpoint records its changes
            // into the temp, and a previous sync's file standing at the path measures exactly
            // what a promotion would have left about as often as a promoted one does - two rows
            // of one shape being two rows of another shape's length. So that case takes the route
            // it always took, which replaces the whole file rather than cutting it back to an
            // offset counted in a different one.
            refused = new CheckpointFileRefusal(CheckpointFile.Output,
                RefusalForStandingOutput(outputPath, out var vanishedTempReason),
                CheckpointStopRoute.FreshAfterVanishedTemp, vanishedTempReason);
            if (refused.Refuses) return CheckpointRecovery.Refused;

            WriteWarning(
                $"The interrupted sync's temp file is missing or incomplete, so the {checkpoint.ItemsCollected} items it "
                + "recorded are not on disk. Re-enumerating from the last saved delta token; no changes are lost.");
            PaginationCheckpoint.Delete(checkpointPath);
            // And what the sweep took off the staging name, after what this run did about the
            // checkpoint.
            if (swept is { } removedAnyway) WriteWarning(removedAnyway.Sentence);
            return CheckpointRecovery.Discarded;
        }

        // No temp is named, so the run that wrote this checkpoint was appending straight into the
        // output and its items are in that file. The claim above, one file over: a sync that has
        // resumed once holds -OutputFile open with those changes in it, and leaves a checkpoint
        // with no temp for a second run over the same command line to refuse it by - so that run
        // judged the checkpoint its own, cut the live file back to the recorded offset, appended,
        // and moved the token past changes the output no longer held.
        //
        // So the claim is not let go. The handle it opens is the handle this run writes through:
        // the cut is a SetLength on it, the writer further down appends onto it, and it is
        // released where the writing ends. Let go here, a page-fetch before the writer took an
        // open of its own, it left a window a second run walked straight through.
        var taken = TakeOutputForCheckpoint(outputPath, dataLength, out var takenReason);
        if (taken == CheckpointOutput.Taken) return CheckpointRecovery.Resumable;

        refused = new CheckpointFileRefusal(CheckpointFile.Output, RefusalFrom(taken),
            CheckpointStopRoute.Appending, takenReason);
        if (refused.Refuses) return CheckpointRecovery.Refused;

        // Not the file the checkpoint describes - absent, or shorter than the length recorded -
        // so the changes it counts are in no file and there is nothing to append to.
        WriteWarning(
            $"'{outputPath}' no longer holds the {checkpoint.ItemsCollected} items the resume checkpoint records. "
            + "Re-enumerating from the last saved delta token; no changes are lost.");
        PaginationCheckpoint.Delete(checkpointPath);
        return CheckpointRecovery.Discarded;
    }

    private void ExecuteDeltaSync(
        ResilientGraphClient client,
        string requestUrl,
        string deltaPath,
        string? outputPath,
        string? checkpointPath,
        string? select,
        string? prefer,
        string? filter,
        Stopwatch sw)
    {
        // What the reconcile below takes, on a resumed run, is the output itself, and it is this
        // run's until the writing ends. Released on every way out of the loop, and again from
        // Dispose, which is what a pipeline stopped from outside reaches.
        using var writingEnds = ReleasingOutputWhenWritingEnds();

        bool isFullResync = false;
        // Whether this run has given up on the checkpoint on disk, and the temp that checkpoint
        // points at. Both outlive the attempt that refused, because the 410 door below sends the
        // loop round again: an attempt that has forgotten the refusal resumes from the position
        // it was told to leave alone and sweeps away the temp holding its items, seconds after
        // warning that neither would be touched. A refusal is still decided again on every load
        // - the checkpoint is a file, and the sync that owns it is still going - but only ever
        // this way round, and never granted back: the retry rebuilds the request URL, so a
        // checkpoint refused against the delta link can compare equal to the full re-sync one,
        // and that is not evidence of anything.
        var refusedCheckpoint = false;
        // The temp that checkpoint named, or null - which is both "nothing refused" and "what
        // was refused names no temp". sparedCheckpoint below tells those apart, and the sweep
        // reads the pair: a checkpoint written before the name was recorded stands for a temp
        // beside this output without saying which one, so its refusal holds the sweep off over
        // all of them.
        string? refusedTemp = null;
        // And which reading refused it, which is what the two doors that would otherwise delete
        // the file - the completion path and the 410 - decide on. One reading a sync can carry
        // this far: the ownership verdict's, where whatever wrote the file resumes from exactly
        // that, so deleting it makes the warning that has just promised both files false. A
        // checkpoint whose temp, or whose output, this run could not claim reaches no door,
        // because the run that found that out ended there. The cost of sparing is that the file
        // suppresses the next -Latest and holds this output's stale-temp sweep off while it
        // stands.
        SparedCheckpoint? sparedCheckpoint = null;
        // The temp an attempt of this run kept when it died, because a checkpoint counting its
        // items was on disk. Nothing else records it: the sweep at the top of the next attempt
        // is the only thing that reclaims a temp, and a refusal holds that off over every temp
        // beside this output - so when the checkpoint stops naming the file, by being replaced
        // with the next attempt's or deleted by the run that completed, this is what says which
        // one is now a partial copy of the caller's changes that nothing refers to.
        string? keptTemp = null;
        // Whether an attempt of this run has written a checkpoint of its own over
        // -CheckpointPath. The refusal above leaves the file where it is and then the very next
        // page boundary saves over the same path, so what the refusal was protecting is gone
        // and what is there is this run's - which is the other thing the two doors have to know
        // before they spare the file on the refusal's account. Run-scoped, unlike the
        // per-attempt flag of the same reading further down: that one answers which attempt
        // saved the checkpoint now on disk, and the keep it feeds needs exactly that.
        var tookOverCheckpoint = false;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var headers = BuildRequestHeaders(null, Headers);
                if (Prefer is { Length: > 0 })
                {
                    // Dedicated parameter wins over a Prefer key in -Headers (matches the
                    // ConsistencyLevel convention in BuildRequestHeaders).
                    headers ??= new Dictionary<string, string>();
                    headers["Prefer"] = string.Join(",", Prefer);
                }

                // --- resume from checkpoint, when one exists for THIS enumeration ---
                ResumeState? resume = null;
                long resumedItemCount = 0;
                var currentFetchUrl = requestUrl;
                var appendOutput = false;

                // Refusing is the whole response: the checkpoint is left where it is because
                // the sync that wrote it resumes from exactly that, and the items it counted
                // are in the temp it names. A refusal already made is not revisited - this run
                // has nothing to resume from either way, and the two files stay out of its
                // reach for the rest of it.
                if (checkpointPath != null && File.Exists(checkpointPath) && !refusedCheckpoint)
                {
                    // Ownership first, in both output modes. A sync writing to the pipeline used
                    // to skip this block entirely, so a checkpoint from a JSONL sync resumed it
                    // at that sync's nextLink: the pages before it went to the file and never to
                    // the pipeline, and the delta token was saved over them on success, which
                    // puts those changes permanently out of reach.
                    var orphanCp = PaginationCheckpoint.Load(checkpointPath);
                    if (orphanCp != null
                        && RefusalFor(orphanCp, outputPath, requestUrl, checkpointPath, deltaPath)
                            is { } refusal)
                    {
                        // What the refusal leaves has to survive the sweep in this same run,
                        // the 410 retry that runs it a second time, and the delete either door
                        // ends with.
                        refusedCheckpoint = true;
                        sparedCheckpoint = SparedCheckpoint.NotThisRuns;
                        refusedTemp = orphanCp.TempFile;
                        WriteWarning(refusal);
                    }
                    else
                    {
                        // JSONL crash: the checkpoint survives but the output was never promoted
                        // from its temp file. Promote the temp (trimmed to the checkpointed
                        // length) so resume appends to real data instead of declaring staleness.
                        // Without this the resume restarts at checkpoint.NextLink and the crashed
                        // run's items, sitting only in the temp, are never emitted - while the
                        // delta token advances past them on success.
                        //
                        // A load that answers null reaches none of it. "Checkpoint found but
                        // output file is missing" is a reading of the checkpoint's contents, and
                        // a checkpoint nothing could read has none to go on: the delete below is
                        // for a file that says a sync completed, not for one this run failed to
                        // open. That case is answered where it is decided, a few lines down.

                        // Whether the reconcile left a position worth reloading. Not the same
                        // question as whether the file is still there: a bail-out deletes the
                        // checkpoint it gave up on, that delete fails wherever the account cannot
                        // unlink from the checkpoint's directory, and the file it leaves is what
                        // File.Exists then answered yes to - so the run reloaded the position it
                        // had just warned it would re-enumerate past, appended this sync's rows
                        // onto the previous sync's, and saved a token past items held in no file.
                        // A checkpoint that outlives being given up on is given up on again next
                        // run. Resumable where there was nothing to reconcile: a pipeline sync
                        // has no output to put into any state, and a checkpoint that could not be
                        // read is answered on its own terms below.
                        var recovery = CheckpointRecovery.Resumable;
                        if (outputPath != null && orphanCp != null)
                        {
                            if (orphanCp.NextLink != null)
                            {
                                recovery = ReconcileCheckpointWithFiles(
                                    checkpointPath, outputPath, orphanCp,
                                    out var refused, out var staging, out var stagingTemp,
                                    out var stagingRemoved);

                                // The write that would have put the checkpoint's changes at the
                                // output path failed, and both files are where they were found.
                                // Read as a temp that had gone - which is what the catch around
                                // the promotion made of it - this run said those changes were on
                                // no disk, deleted the position counting them, re-enumerated over
                                // the output and moved the delta token past every one of them,
                                // with the temp holding them all the while.
                                if (recovery == CheckpointRecovery.StagingFailed)
                                {
                                    ThrowTerminatingError(CheckpointStagingStop(
                                        staging!.Value, checkpointPath, outputPath, orphanCp,
                                        stagingTemp!, stagingRemoved));
                                    return;
                                }

                                // A file of this checkpoint's that this run could not claim ends
                                // the run, here, before it has enumerated a page, written a byte
                                // of output, saved a checkpoint, swept a temp or touched the
                                // delta state. Every other answer this sync could give away the
                                // two files.
                                //
                                // The rows the checkpoint counts are in that file and in no
                                // other, and the checkpoint is the only thing on disk that
                                // counts them: a holder still running deletes it itself on the
                                // way out, a holder that dies keeps its temp for exactly as
                                // long as a checkpoint counting it is there, and where the
                                // claim failed on permissions instead it is the position write
                                // access has to be granted for. Going on takes that away by
                                // going on: the sweep at the top of this attempt reclaims the
                                // temp the moment its holder is gone, the first page boundary
                                // saves this run's own position over the same path, the
                                // completion door then deletes what it finds there as its own,
                                // and a page carrying a deltaLink moves the token past changes
                                // held in that file alone. Resuming is worse again - the items
                                // are in a file this run cannot have, so they are left out of
                                // the output while the token advances past them - and where the
                                // file is the output itself, resuming is a cut taken out from
                                // under a live writer, whose next write lands past the hole.
                                //
                                // Which leaves nothing this sync can do with the -OutputFile
                                // and -CheckpointPath it was given, and saying so is the whole
                                // of what it does.
                                if (recovery == CheckpointRecovery.Refused)
                                {
                                    ThrowTerminatingError(CheckpointFileStop(
                                        refused, checkpointPath, outputPath, orphanCp));
                                    return;
                                }
                            }
                            else if (!File.Exists(outputPath))
                            {
                                WriteWarning("Checkpoint found but output file is missing. Deleting stale checkpoint and starting fresh.");
                                PaginationCheckpoint.Delete(checkpointPath);
                            }
                        }

                        // The refusal above is a refusal of this checkpoint, not of one
                        // read of it: reloading and resuming from the file just left alone is
                        // the resume it was made to stop. And a checkpoint given up on is not
                        // resumed from either, however the delete that went with it fared.
                        if (recovery == CheckpointRecovery.Resumable
                            && !refusedCheckpoint && File.Exists(checkpointPath))
                        {
                            var checkpoint = PaginationCheckpoint.Load(checkpointPath);
                            if (checkpoint == null)
                            {
                                // Left where it is. Load answers null for a file torn by a crash
                                // and for one that is locked or that this account cannot open,
                                // and this run does the same thing either way - resume stays
                                // null, so the sync re-enumerates from the delta token. Deleting
                                // it changed nothing here and destroyed a position the next run,
                                // or another account, could still have resumed from.
                                WriteWarning(
                                    "The resume checkpoint could not be read, so it cannot say how far the "
                                    + "interrupted sync got. Re-enumerating from the last saved delta token; "
                                    + "no changes are lost.");
                            }
                            else if (checkpoint.NextLink == null)
                            {
                                // A completion marker is this run's to remove, and the reading is
                                // the file's own: the sync that wrote it finished. Leaving it is
                                // not free - -Latest is suppressed by the checkpoint file merely
                                // existing - so a marker that outlives its sync costs the next
                                // -Latest run the baseline it asked for.
                                WriteVerbose("Checkpoint indicates the previous sync completed. Deleting stale checkpoint.");
                                PaginationCheckpoint.Delete(checkpointPath);
                            }
                            else if (RefusalFor(checkpoint, outputPath, requestUrl,
                                         checkpointPath, deltaPath) is { } reloadedRefusal)
                            {
                                // Decided again on this load rather than carried over: the
                                // checkpoint is a file, and the sync that owns it is still going.
                                refusedCheckpoint = true;
                                sparedCheckpoint = SparedCheckpoint.NotThisRuns;
                                refusedTemp = checkpoint.TempFile;
                                WriteWarning(reloadedRefusal);
                            }
                            else
                            {
                                // SSRF validation: the checkpoint nextLink is untrusted (a file on disk)
                                var expectedHost = new System.Uri(requestUrl);
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
                                    appendOutput = outputPath != null && File.Exists(outputPath);
                                    WriteVerbose($"Resuming delta enumeration from checkpoint: {resumedItemCount} items already processed"
                                        + (checkpoint.PageItemsAlreadyWritten > 0
                                            ? $", skipping {checkpoint.PageItemsAlreadyWritten} items on first page."
                                            : "."));
                                }
                                else
                                {
                                    WriteWarning("Checkpoint nextLink failed validation. Deleting checkpoint and starting fresh.");
                                    PaginationCheckpoint.Delete(checkpointPath);
                                }
                            }
                        }
                    }
                }

                var iterator = new PageIterator(client);
                string? capturedDeltaLink = null;
                long itemCount = 0;
                long removedCount = 0;
                long totalProcessed = resumedItemCount;
                // Seeded from the resume skip, not 0. PageIterator drops the skipped items before
                // the consumer ever sees them (PageIterator.cs: "if (isFirstPage && skippedOnPage
                // < skipOnFirstPage) continue;"), so a counter starting at 0 records only the
                // NEWLY written items of the first resumed page. A mid-page checkpoint there then
                // claimed fewer items of that page than the output actually held, and the next
                // resume skipped too few and re-emitted the difference - up to a page's worth of
                // duplicate lines, which is exactly what the comment below says cannot happen.
                int pageItemsWritten = resume?.SkipOnFirstPage ?? 0;

                // What the next two checkpoint sites should say about WHERE the counted items
                // are. Set once the writer exists; null on the pipeline path, which has no file.
                string? checkpointTempFile = null;
                long? checkpointDataLength = null;
                // Whether the checkpoint on disk is one THIS attempt saved. A checkpoint an
                // earlier attempt left names an earlier temp, and the two are not
                // interchangeable when it comes to deciding what a file on disk is still for.
                var savedOwnCheckpoint = false;

                // A temp a failed attempt of this run kept is kept on one condition: a
                // checkpoint counting its items is on disk. Saving a checkpoint here is what
                // ends that - the file just written names this attempt's temp, or the output on
                // a resumed run, and never the earlier one - so from that moment nothing on
                // disk refers to it, no recovery can reach it, and the sweep is still being
                // held off on behalf of the refusal that carried it this far. Left where it
                // was, it outlived the sync that made it: a partial copy of the caller's
                // changes beside the finished output, waiting for some later run's sweep.
                //
                // Never the temp the checkpoint now names, which is this attempt's own. Best
                // effort otherwise: a file something else holds open is not worth failing a
                // finished sync over.
                void ReleaseKeptTemp()
                {
                    var kept = keptTemp;
                    if (kept == null
                        || string.Equals(Path.GetFileName(kept), checkpointTempFile,
                            StringComparison.OrdinalIgnoreCase))
                        return;
                    keptTemp = null;
                    try { if (File.Exists(kept)) File.Delete(kept); } catch { }
                }

                void OnPageComplete(PageCompletedInfo info)
                {
                    if (info.NextPageUrl != null)
                        currentFetchUrl = info.NextPageUrl;
                    pageItemsWritten = 0;
                }

                void SaveBoundaryCheckpoint(PageCompletedInfo info)
                {
                    if (checkpointPath == null || info.NextPageUrl == null) return;
                    try
                    {
                        new PaginationCheckpoint
                        {
                            Resource = requestUrl,
                            NextLink = info.NextPageUrl,
                            ItemsCollected = totalProcessed,
                            PageItemsAlreadyWritten = 0,
                            TempFile = checkpointTempFile,
                            OutputFile = outputPath != null ? Path.GetFullPath(outputPath) : null,
                            DataLength = checkpointDataLength
                        }.Save(checkpointPath);
                        savedOwnCheckpoint = true;
                        tookOverCheckpoint = true;
                        ReleaseKeptTemp();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Buffered rather than written here. This runs from the page-boundary
                        // callback, on whichever thread the iterator resumed on, and a page that
                        // yields nothing resumes on the thread pool - so a WriteWarning from
                        // here throws PSInvalidOperationException, and the disk problem it was
                        // reporting ends the sync instead of being reported. A delta enumeration
                        // meets that shape most: the empty-page limit is raised to 1000 for it
                        // precisely because delta endpoints return runs of empty pages behind
                        // nextLinks. The drains write it from the pipeline thread, on the same
                        // channel the client's own warnings take.
                        client.EnqueueWarning(
                            $"Checkpoint save failed (page boundary): {ex.Message}");
                    }
                }

                if (outputPath != null)
                {
                    // JSONL output mode. Fresh runs write to a temp file and promote on
                    // success; checkpointed resumes append to the already-promoted output.
                    if (!appendOutput)
                    {
                        // Nothing is being resumed, so no temp on disk describes recoverable
                        // work. Anything left over was orphaned - by -FullSync, by a -Property
                        // /-Filter/-Prefer change, by a checkpoint from a different enumeration,
                        // or by an adoption that declined a torn temp - and orphans are not
                        // inert: TryAdoptOrphanedTemp picks the NEWEST file matching
                        // outputPath + ".*.tmp" with nothing but a line count to go on, so a
                        // survivor from an unrelated enumeration is adoptable by some later
                        // crash's checkpoint, and one success makes those rows permanent.
                        // Sweeping here is what keeps "a temp exists only while a checkpoint
                        // describing it exists" true across runs.
                        //
                        // Which is exactly why a refusal is the one case it is wrong in: the
                        // checkpoint describing that temp IS on disk, left there deliberately a
                        // moment ago, and sweeping the temp took the one file that made it worth
                        // keeping - so the sync it belongs to came back to a position pointing at
                        // nothing and re-enumerated from the start. The sweep is all or nothing
                        // over this output's temps, so a run that has just refused leaves them to
                        // the next run that has not.
                        if (RefusedTempIsOnDisk(refusedTemp, outputPath))
                        {
                            WriteVerbose(
                                $"Left the temp files beside '{outputPath}' alone: '{refusedTemp}' "
                                + "holds the items of the checkpoint this run refused.");
                        }
                        else if (sparedCheckpoint is SparedCheckpoint.NotThisRuns
                                 && refusedTemp == null)
                        {
                            // The refused checkpoint names no temp, which is not the same as
                            // there being none: a checkpoint written before the name was
                            // recorded stands for a temp beside this output all the same, and
                            // nothing here says which of them it is. Keyed on the temp name
                            // alone, the refusal spared the checkpoint and the sweep took the
                            // one file it was pointing at, so the sync it belongs to came back
                            // to a position over nothing - seconds after a warning promising
                            // both files would be left as they are. The whole sweep goes with
                            // the refusal instead, and the orphans it would have taken are left
                            // to the next run over this output that has not refused anything.
                            WriteVerbose(
                                $"Left the temp files beside '{outputPath}' alone: one of them "
                                + "may hold the items of the checkpoint this run refused.");
                        }
                        else
                        {
                            DeleteStaleTemps(outputPath);
                        }
                    }
                    // The output is let go the moment this attempt stops appending to it. Every
                    // way appendOutput is withdrawn - a checkpoint given up on after the
                    // reconcile, a nextLink that fails validation, a 410 sending the loop round
                    // for a full re-sync - ends with this run replacing the output from a temp
                    // instead, and a handle of its own on the destination is what that move
                    // fails against.
                    if (!appendOutput) ReleaseOutputHold();

                    var writePath = appendOutput ? outputPath : $"{outputPath}.{Guid.NewGuid():N}.tmp";
                    // A resumed run appends to the output itself, so there is no temp to name.
                    checkpointTempFile = appendOutput ? null : Path.GetFileName(writePath);
                    try
                    {
                        // The reconcile's own handle on the output, where this run resumed into
                        // it. Never a second open of that file while the hold stands: on Unix
                        // the hold's LOCK_EX refuses one from this process as readily as from
                        // another, and on Windows its write access does.
                        using (var writer = appendOutput && HeldOutput is { } held
                                   ? held.AppendingWriter()
                                   : new StreamWriter(writePath, appendOutput))
                        {
                            var enumerable = iterator.StreamAllWithCountAsync(
                                requestUrl,
                                maxItems: 0,
                                onCount: null,
                                headers: headers,
                                resume: resume,
                                onPageComplete: info =>
                                {
                                    writer.Flush();
                                    checkpointDataLength = writer.BaseStream.Position;
                                    SaveBoundaryCheckpoint(info);
                                    OnPageComplete(info);
                                },
                                onDeltaLink: dl => capturedDeltaLink = dl,
                                cancellationToken: CancellationToken);

                            var enumerator = enumerable.GetAsyncEnumerator(CancellationToken);
                            try
                            {
                                while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                                {
                                    writer.WriteLine(enumerator.Current.GetRawText());
                                    // TryGetProperty throws on anything that is not an object,
                                    // and the item is whatever the service put in "value".
                                    if (enumerator.Current.ValueKind == JsonValueKind.Object
                                        && enumerator.Current.TryGetProperty("@removed", out _))
                                        removedCount++;
                                    itemCount++;
                                    pageItemsWritten++;
                                    totalProcessed++;
                                    DrainClientMessages();

                                    if (totalProcessed % 500 == 0)
                                    {
                                        writer.Flush();
                                        checkpointDataLength = writer.BaseStream.Position;
                                        // Mid-page checkpoint: tracks items written from the
                                        // current page so crash resume skips them (no dupes).
                                        if (checkpointPath != null)
                                        {
                                            try
                                            {
                                                new PaginationCheckpoint
                                                {
                                                    Resource = requestUrl,
                                                    NextLink = currentFetchUrl,
                                                    ItemsCollected = totalProcessed,
                                                    PageItemsAlreadyWritten = pageItemsWritten,
                                                    TempFile = checkpointTempFile,
                                                    OutputFile = outputPath != null ? Path.GetFullPath(outputPath) : null,
                                                    DataLength = checkpointDataLength
                                                }.Save(checkpointPath);
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
                        if (!appendOutput)
                            File.Move(writePath, outputPath, overwrite: true);
                    }
                    catch (Exception attemptEx)
                    {
                        if (!appendOutput)
                        {
                            // User cancellation of a checkpointed fresh run: promote the temp
                            // file (the using block already flushed it on unwind) and save a
                            // checkpoint matching its exact content, so resume works on first
                            // runs too. Otherwise clean the temp up as before.
                            var cancelled = attemptEx is OperationCanceledException
                                && CancellationToken.IsCancellationRequested;
                            var promoted = false;
                            if (cancelled && checkpointPath != null && itemCount > 0)
                            {
                                try
                                {
                                    // Promote first: once the move lands the items are in the
                                    // output, so that is what the checkpoint must point at. If
                                    // the save then fails, the previous checkpoint still names
                                    // a temp that no longer exists, which reads as unusable and
                                    // costs a re-enumeration rather than a wrong resume.
                                    var promotedLength = new FileInfo(writePath).Length;
                                    File.Move(writePath, outputPath, overwrite: true);
                                    promoted = true;
                                    new PaginationCheckpoint
                                    {
                                        Resource = requestUrl,
                                        NextLink = currentFetchUrl,
                                        ItemsCollected = totalProcessed,
                                        PageItemsAlreadyWritten = pageItemsWritten,
                                        TempFile = null,
                                        OutputFile = Path.GetFullPath(outputPath),
                                        DataLength = promotedLength
                                    }.Save(checkpointPath);
                                    ReleaseKeptTemp();
                                }
                                catch (Exception promoteEx) when (promoteEx is IOException or UnauthorizedAccessException)
                                {
                                    // Promotion is best-effort; fall back to the old cleanup.
                                }
                            }
                            if (!promoted)
                            {
                                // A surviving checkpoint describes items that exist ONLY in this
                                // temp: SaveBoundaryCheckpoint flushes the writer before recording
                                // the position, so the temp always holds at least ItemsCollected.
                                // Deleting it leaves the checkpoint pointing past data that is
                                // nowhere - and the next run then finds a checkpoint, an output
                                // and no temp, which is the routine "nothing to promote" state, so
                                // it resumes in APPEND mode against an output that never received
                                // these pages and the delta token advances past them.
                                // Keep the temp for the next run to promote. It is deleted by
                                // promotion, by a later fresh run's own failure once the checkpoint
                                // is gone, or on the missing-output path below.
                                //
                                // The checkpoint counting them has to be one this attempt saved.
                                // Any other is an earlier attempt's, naming an earlier temp, and
                                // this attempt's items are then counted by nothing: keeping the
                                // file left a page no recovery can reach, and the newest temp
                                // beside an output is what the pre-length adoption path picks up
                                // on a line count alone.
                                var resumable = checkpointPath != null && File.Exists(checkpointPath)
                                    && savedOwnCheckpoint;
                                if (resumable)
                                {
                                    // Named, because nothing else on disk will be: the sweep the
                                    // next attempt reaches is held off by the refusal, not by
                                    // this, and the checkpoint that makes the file worth keeping
                                    // is replaced or deleted without a word about it.
                                    keptTemp = writePath;
                                }
                                else
                                {
                                    try { if (File.Exists(writePath)) File.Delete(writePath); } catch { }
                                }
                            }
                        }
                        throw;
                    }
                }
                else
                {
                    // Pipeline output mode. Checkpoints save at page boundaries only:
                    // emitted objects cannot be un-emitted, so resume re-emits the page in
                    // flight at the crash (at-least-once, documented).
                    var enumerable = iterator.StreamAllWithCountAsync(
                        requestUrl,
                        maxItems: 0,
                        onCount: null,
                        headers: headers,
                        resume: resume,
                        onPageComplete: info =>
                        {
                            SaveBoundaryCheckpoint(info);
                            OnPageComplete(info);
                        },
                        onDeltaLink: dl => capturedDeltaLink = dl,
                        cancellationToken: CancellationToken);

                    var enumerator = enumerable.GetAsyncEnumerator(CancellationToken);
                    try
                    {
                        while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                        {
                            if (enumerator.Current.ValueKind == JsonValueKind.Object
                                && enumerator.Current.TryGetProperty("@removed", out _))
                                removedCount++;
                            var ht = JsonToHashtable(enumerator.Current);
                            WriteObject(ht);
                            itemCount++;
                            pageItemsWritten++;
                            totalProcessed++;
                            DrainClientMessages();
                        }
                    }
                    finally
                    {
                        enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                }

                // Success: the checkpoint's job is done - delete it BEFORE saving delta
                // state, so a crash between the two leaves a fresh incremental (correct)
                // rather than a resumable position into a completed enumeration (wrong).
                //
                // "Its job" is this run's job, and the file on disk is only this run's once no
                // refusal left it standing and a save of its own has landed on the path. A run
                // that refused the checkpoint and then finished without ever saving one -
                // everything it had fitting in a single page, or a 410 arriving before the
                // first boundary - was deleting the file it had warned it would leave alone,
                // orphaning the temp it had just spared the sweep and sending the sync that
                // needs both back to page one.
                //
                // One reading spares it: the ownership verdict's, where whatever wrote the
                // file resumes from exactly it. It costs the next -Latest its baseline and
                // holds this output's stale-temp sweep off, and that is cheaper than deleting
                // a position another run is coming back to. A checkpoint whose temp, or whose
                // output, this run could not claim never reaches here at all - that run stopped
                // at the reconcile, having written nothing - so this door is only ever answering
                // for a run that had an enumeration of its own to finish.
                //
                // The 410 door reads the same thing for the same reason. savedOwnCheckpoint
                // answers only for the attempt that is finishing here and is not asked: every
                // site that sets it sets the run-scoped flag beside it, which also covers the
                // attempt a query the endpoint refused sent round again with a boundary
                // checkpoint of this run's already over the path.
                if (sparedCheckpoint is SparedCheckpoint.NotThisRuns && !tookOverCheckpoint)
                {
                    WriteVerbose(SparedCheckpointVerbose("sync completed"));
                }
                else
                {
                    DeleteCheckpoint(checkpointPath, "sync completed");
                }

                // And with it goes the last thing that could have named a temp an earlier
                // attempt kept - the attempt that finished need never have saved a checkpoint of
                // its own to release it, if everything it had left fitted in one page. Only
                // here, on the way out of a run that completed: an attempt that dies leaves the
                // checkpoint and the temp it names for the next run to resume from.
                ReleaseKeptTemp();

                // Save delta state ONLY after successful completion (Architect P0).
                // Zero-item responses still save the token (Adversarial P0).
                if (capturedDeltaLink != null)
                {
                    new DeltaState
                    {
                        DeltaLink = capturedDeltaLink,
                        Select = select, // Normalized value for stable future comparisons
                        Filter = filter,
                        Prefer = prefer, // Normalized, like Select
                        Resource = Uri,
                        ItemCount = totalProcessed,
                        GraphEndpoint = s_graphEndpoint,
                        // The version the LINK carries, not the one that was asked for. A state
                        // file written before this field existed has none, so the mismatch check
                        // is skipped and the run proceeds - against whatever version the stored
                        // deltaLink names, which may not be the requested one. Stamping the
                        // request there recorded a version the token was never issued by, and
                        // every later run then refused with advice pointing the wrong way.
                        ApiVersion = ApiVersionOfLink(capturedDeltaLink) ?? ApiVersion
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
                    $"Delta sync complete: {itemCount} items"
                    + (removedCount > 0 ? $" ({removedCount} removed)" : "")
                    + $" in {sw.Elapsed.TotalSeconds:F1}s"
                    + (resumedItemCount > 0 ? $" (resumed after {resumedItemCount})" : "")
                    + (isFullResync ? " (full re-sync after 410 Gone)" : "")
                    + (outputPath != null ? $". Output: {outputPath}" : "."));

                return;
            }
            catch (GraphServiceException ex) when (
                attempt == 0
                && ex.StatusCode == HttpStatusCode.Gone)
            {
                // 410 Gone: delta token expired (>7 days for directory objects).
                // Delete delta state AND any checkpoint of this run's - both describe the dead
                // enumeration - and restart with full sync.
                // Second attempt builds fresh URL (no delta token), so 410 won't recur.
                DrainClientMessages();
                if (!DeltaState.Delete(deltaPath))
                    WriteVerbose("Could not delete expired delta state (file may be locked). It will be overwritten.");
                // The output this run took at the reconcile goes with the position it was taken
                // for, which is the position the 410 has just declared dead. The retry starts
                // over: it collects into a temp of its own and promotes that at the end, so it
                // has no use for a handle opened at an offset counted in the enumeration behind
                // it - and carried into the retry, that handle is one the retry's own reconcile
                // asks about and is refused by. Measured: where the checkpoint delete below
                // fails and the retry reads the same position again, the claim answered held and
                // the run ended telling the caller to wait for a second sync that was this one.
                ReleaseOutputHold();
                // The refusal has to still hold over the file that is actually there. A refused
                // checkpoint is spared here because some run comes back to exactly it - but the
                // first page boundary of this run saves over the same path, and from then on
                // the refused file is gone and the position on disk is this run's own. Sparing
                // that one keeps a position into the enumeration the 410 has just declared
                // dead: the retry re-enumerates in full, so the resource it records matches
                // nothing this command line builds again, and every later run refuses it in
                // turn - warning about a sync that was never there and holding off its
                // stale-temp sweep on that account. It goes the way any other position into a
                // dead enumeration goes.
                //
                // A checkpoint whose temp, or whose output, this run could not claim does not
                // reach this door either: a run that finds that out at the reconcile has not
                // sent a request yet, so it cannot be the run a 410 answered.
                if (sparedCheckpoint is SparedCheckpoint.NotThisRuns && !tookOverCheckpoint)
                {
                    // The token that expired is this run's; what the refusal left standing is
                    // not. It was left there moments ago for a sync that resumes from exactly
                    // it, and an expired token of this one's is no reason to take it away right
                    // after the warning said both files would be left where they were.
                    WriteVerbose(
                        SparedCheckpointVerbose("delta token expired (410 Gone)"));
                }
                else
                {
                    DeleteCheckpoint(checkpointPath, "delta token expired (410 Gone)");
                }
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
                var resumeHint = checkpointPath != null
                    ? $" Resume with: Sync-MgxDelta '{Uri}' -DeltaPath '{DeltaPath}' -CheckpointPath '{CheckpointPath}'"
                      + (OutputFile != null ? $" -OutputFile '{OutputFile}'" : "")
                    : " Use -CheckpointPath to enable mid-run resume.";
                WriteWarning($"Delta sync cancelled.{resumeHint}");
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
            catch (UnauthorizedAccessException ex)
            {
                // Not an IOException, so the catch above never saw it. It is what Windows raises
                // for a denying ACL, a read-only file, or an -OutputFile naming a directory, and
                // leaving it out made every one of those an unhandled error there while the same
                // failure was a clean error record on Unix. Export-MgxCollection already reports
                // it this way.
                DrainClientMessages();
                WriteError(new ErrorRecord(ex, "AccessDenied",
                    ErrorCategory.PermissionDenied, OutputFile));
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
