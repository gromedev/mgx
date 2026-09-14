---
external help file: Mgx.Cmdlets.dll-Help.xml
Module Name: Mgx
online version: https://github.com/gromedev/mgx/blob/main/module/help/Sync-MgxDelta.md
schema: 2.0.0
---

# Sync-MgxDelta

## SYNOPSIS
Incremental sync via Microsoft Graph delta queries.

## SYNTAX

```
Sync-MgxDelta [-Uri] <String> -DeltaPath <String> [-Property <String[]>] [-Filter <String>]
 [-Prefer <String[]>] [-Top <Int32>] [-OutputFile <String>] [-FullSync] [-Latest]
 [-CheckpointPath <String>] [-ApiVersion <String>] [-Headers <Hashtable>]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION
Sync-MgxDelta retrieves incremental changes from Microsoft Graph delta endpoints. On the first run, it performs a full sync and saves a delta token. Subsequent runs retrieve only items that changed since the last sync.

Delta tokens are saved to the file specified by -DeltaPath and persist across successful completions. -CheckpointPath is different: it is an ephemeral mid-run resume position, saved at page boundaries while an enumeration is in flight and deleted the moment it completes. Use both on long enumerations - a killed 3-hour drive sync resumes where it stopped instead of starting over.

Drive delta is fully supported: `/me/drive/root/delta`, `/drives/{id}/root/delta`, `/users/{id}/drive/root/delta`, and `/groups/{id}/drive/root/delta` all work, including drive-specific `-Prefer` behaviors and the drive form of `-Latest`.

Items that no longer match the query appear with an `@removed` property containing `{"reason": "changed"}` (item moved out of scope or soft-deleted) or `{"reason": "deleted"}` (permanently deleted). Filter these with `Where-Object { -not $_.'@removed' }`. The completion message counts them separately.

Delta tokens expire after approximately 7 days for directory objects (users, groups, applications). When a token expires, Graph returns HTTP 410 Gone. Sync-MgxDelta handles this automatically by deleting the stale token (and this run's own resume checkpoint) and performing a full re-sync with a warning.

Resume semantics are at-least-once: in pipeline mode the page in flight at a crash is re-emitted in full on resume; in JSONL mode mid-page checkpoints keep the window to at most 500 items. Deduplicate on `id` downstream if exact-once matters.

Source: [Use delta query to track changes in Microsoft Graph data](https://learn.microsoft.com/en-us/graph/delta-query-overview)

## EXAMPLES

### Example 1: Sync all users (first run = full sync)
```powershell
Sync-MgxDelta /users/delta -DeltaPath users.delta -Property displayName,mail,jobTitle
```

First run retrieves all users with selected properties and saves the delta token. Subsequent runs return only users whose displayName, mail, or jobTitle changed.

### Example 2: Incremental sync (subsequent runs)
```powershell
Sync-MgxDelta /users/delta -DeltaPath users.delta
```

Returns only users changed since the last sync. Do not re-specify -Property on subsequent runs; the selection is encoded in the saved token.

### Example 3: Export changes to JSONL
```powershell
Sync-MgxDelta /users/delta -DeltaPath users.delta -OutputFile user-changes.jsonl -Property displayName,mail
```

Writes changed items as JSONL (one JSON object per line) for ETL pipelines.

### Example 4: Force full re-sync
```powershell
Sync-MgxDelta /users/delta -DeltaPath users.delta -FullSync
```

Discards the saved delta token and performs a full sync. Use when you need to rebuild your local state.

### Example 5: Sync group changes including membership
```powershell
Sync-MgxDelta /groups/delta -DeltaPath groups.delta
```

Group delta responses include `members@delta` arrays showing member additions and removals. Access via `$group.'members@delta'`.

### Example 6: Scheduled sync in unattended script
```powershell
$results = Sync-MgxDelta /users/delta -DeltaPath C:\sync\users.delta -Property id,displayName,accountEnabled
$removed = $results | Where-Object { $_.'@removed' }
$changed = $results | Where-Object { -not $_.'@removed' }
Write-Host "$($changed.Count) changed, $($removed.Count) removed"
```

Suitable for scheduled tasks. If the delta token expires (>7 days since last run), Sync-MgxDelta automatically performs a full re-sync and warns.

### Example 7: Enumerate a whole drive, resumable
```powershell
Sync-MgxDelta /me/drive/root/delta -DeltaPath drive.delta -CheckpointPath drive.checkpoint -OutputFile drive.jsonl
```

First run enumerates the entire drive to JSONL. If the run is killed at any point, re-running the identical command resumes from the last checkpointed page instead of starting over. Subsequent runs after completion return only changed items.

### Example 8: Baseline from now, then track changes only
```powershell
Sync-MgxDelta /groups/delta -DeltaPath groups.delta -Latest   # instant baseline, no data
# ...later...
Sync-MgxDelta /groups/delta -DeltaPath groups.delta           # only what changed since
```

-Latest skips the initial full enumeration and stores a "from now" token. The form is chosen automatically: drive resources use `?token=latest`, directory and everything else `$deltatoken=latest`.

### Example 9: Drive delta with Prefer behaviors
```powershell
Sync-MgxDelta /me/drive/root/delta -DeltaPath drive.delta -Prefer deltashowremovedasdeleted
```

Removed drive items carry the `deleted` facet instead of appearing as bare deletions. Note: `deltaExcludeParent` is a standalone request header, not a Prefer token - pass it as `-Headers @{ deltaExcludeParent = "true" }`.

## PARAMETERS

### -ApiVersion
Graph API version. Default: v1.0. Use "beta" for preview endpoints.

```yaml
Type: String
Parameter Sets: (All)
Aliases:
Accepted values: v1.0, beta

Required: False
Position: Named
Default value: v1.0
Accept pipeline input: False
Accept wildcard characters: False
```

### -CheckpointPath
Path for the ephemeral mid-run resume checkpoint. Saved at page boundaries (and every 500 items in JSONL mode) while an enumeration is in flight; deleted on successful completion. Any event that invalidates the enumeration - HTTP 410 Gone, -FullSync, or a -Property/-Filter/-Prefer change - deletes it too. A completion and a 410 delete this run's own checkpoint; one they recognize as another sync's is left as it stands, for the sync that resumes from exactly it. Must differ from -DeltaPath and -OutputFile.

A run stops - with nothing written to the output, the checkpoint, a temp or the delta state - when the temp file the checkpoint names, or the output it would append to or replace, is open in another sync or cannot be opened for writing (a permission, a directory or a pipe at the path, a link that cannot be followed, a path too long for the file system, a socket, a directory above it this account cannot search), or when the copy it stages before promoting a temp fails, in which case both files are left as found: the changes it counts are in that file and this checkpoint is the only thing that counts them, and syncing anyway would write over both and advance the token past those changes. This holds whether the run would append, promote a temp, or re-enumerate from the last saved delta token after its temp vanished. The copy a promotion stages is written beside the output under a name the run creates itself, and the name is cleared first: a link, a pipe, a socket, a copy left by an interrupted promotion or a copy this account cannot open standing there is deleted and the removal reported as a warning, while a directory or a copy another run holds stops the run instead. Wait for that sync to finish, give this run its own -OutputFile and -CheckpointPath, or grant write access to that file - or to the directory holding it, where that is what the run names - and run again. A resumed or promoting run holds the output open for as long as it is appending, which is what stops a second run over the same command line; a run recovering a checkpoint older than 2.1 opens the output as it always has. On Linux and macOS a PowerShell reader of a held file fails with a sharing violation until the run ends, while tail and cat read it as before.

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -DeltaPath
Path to the delta state file. This file persists across successful completions and tracks the sync position. JSON format, human-readable.

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: True
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -Filter
OData $filter expression. Delta queries support very limited filtering (typically only `id eq 'value'`). Invalid filters will be rejected by Graph with a clear error message. Source: [Get incremental changes for users](https://learn.microsoft.com/en-us/graph/delta-query-users)

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -FullSync
Delete the existing delta state file and perform a full sync from scratch.

```yaml
Type: SwitchParameter
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: False
Accept pipeline input: False
Accept wildcard characters: False
```

### -Latest
Baseline without enumerating: request only the latest delta token ("sync from now"). Returns no data; the next run returns everything that changed since. Drive resources take `?token=latest`, directory and other resources `$deltatoken=latest` - the form is chosen automatically from the URI shape. Ignored with a warning when usable delta state already exists (delete it or use -FullSync to re-baseline).

```yaml
Type: SwitchParameter
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: False
Accept pipeline input: False
Accept wildcard characters: False
```

### -Prefer
Prefer-header tokens joined into a single Prefer header on every page request. Tab completion offers the documented drive delta tokens: `deltashowremovedasdeleted`, `deltatraversepermissiongaps`, `deltashowsharingchanges` (requires the other two and Sites.FullControl.All), and `hierarchicalsharing`. A change against the stored state forces a full re-sync, like -Property and -Filter. `deltaExcludeParent` is a standalone request header, not a Prefer token - pass it via -Headers.

```yaml
Type: String[]
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -Headers
Custom request headers applied to each page request.

```yaml
Type: Hashtable
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -OutputFile
Write output as JSONL (one JSON object per line) instead of pipeline objects. Useful for ETL pipelines and large datasets.

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -Property
Properties to include via $select. Specify on the first run only; the selection is encoded into the delta token. If changed on a subsequent run, the delta state is invalidated and a full re-sync is performed automatically.

```yaml
Type: String[]
Parameter Sets: (All)
Aliases: Select

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -Top
Page size hint. Controls how many items Graph returns per page.

```yaml
Type: Int32
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -Uri
Delta endpoint URI. Must be a delta-capable endpoint (e.g., /users/delta, /groups/delta, /applications/delta). Source: [Use delta query to track changes in Microsoft Graph data](https://learn.microsoft.com/en-us/graph/delta-query-overview)

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: True
Position: 0
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -ProgressAction
Determines how the cmdlet responds to progress updates.

```yaml
Type: ActionPreference
Parameter Sets: (All)
Aliases: proga

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### CommonParameters
This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable, -InformationAction, -InformationVariable, -OutVariable, -OutBuffer, -PipelineVariable, -Verbose, -WarningAction, and -WarningVariable. For more information, see [about_CommonParameters](http://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

### None

## OUTPUTS

### System.Collections.Hashtable
Graph API objects as case-insensitive hashtables with keys matching the JSON fields. Deleted items include an `@removed` key.

## NOTES
Delta queries follow all @odata.nextLink pages automatically (equivalent to -All on other cmdlets). The delta token is saved only after all pages are successfully retrieved.

Query parameters ($select, $filter) are encoded into the delta token on the first request. Do not re-specify them on subsequent runs; they are automatically applied from the saved token. If you change -Property between runs, Sync-MgxDelta detects the mismatch and performs a full re-sync.

Supported delta endpoints include: /users/delta, /groups/delta, /applications/delta, /servicePrincipals/delta, /devices/delta, /directoryRoles/delta, and many others. Source: [Use delta query to track changes in Microsoft Graph data](https://learn.microsoft.com/en-us/graph/delta-query-overview)

DUPLICATE OBJECTS ACROSS PAGES

Graph does not guarantee that an object appears only once in a delta response: it "can't ensure that entities are unified in a single response." Deduplicate by `id` before treating emitted objects as change events.

This is most visible on an initial full enumeration. Measured against a tenant with 15,779 groups, `/groups/delta` emitted 156,413 objects across all pages - a replay factor of roughly 10x - with every object accounted for by a repeat rather than a distinct group. Incremental rounds off an established token are typically clean, but nothing in the contract promises that, so the dedup belongs in the consumer either way.

Two consequences worth designing around:

- For a change feed, baseline with `-Latest` so the initial enumeration never happens. It records a sync-from-now token and returns nothing, and every later run reports only real changes.
- When counting, count distinct ids. An object that changed twice inside one window is one object; keeping the last occurrence gives its current state.

`-CheckpointPath` writes item counts as emitted, not deduplicated, so a resumed run's progress figures reflect the same replay.

Source: [Use delta query to track changes](https://learn.microsoft.com/en-us/graph/delta-query-overview#other-considerations)

## RELATED LINKS
[Export-MgxCollection](Export-MgxCollection.md)
[Invoke-MgxRequest](Invoke-MgxRequest.md)
[Set-MgxOption](Set-MgxOption.md)
