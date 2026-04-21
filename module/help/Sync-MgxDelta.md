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
Sync-MgxDelta [-Uri] <String> -DeltaPath <String> [-Property <String[]>] [-Filter <String>] [-Top <Int32>]
 [-OutputFile <String>] [-FullSync] [-ApiVersion <String>] [-Headers <Hashtable>]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION
Sync-MgxDelta retrieves incremental changes from Microsoft Graph delta endpoints. On the first run, it performs a full sync and saves a delta token. Subsequent runs retrieve only items that changed since the last sync.

Delta tokens are saved to the file specified by -DeltaPath and persist across successful completions. This is different from -CheckpointPath (used by Export-MgxCollection) which is ephemeral and deleted on success.

Items that no longer match the query appear with an `@removed` property containing `{"reason": "changed"}` (item moved out of scope or soft-deleted) or `{"reason": "deleted"}` (permanently deleted). Filter these with `Where-Object { -not $_.'@removed' }`.

Delta tokens expire after approximately 7 days for directory objects (users, groups, applications). When a token expires, Graph returns HTTP 410 Gone. Sync-MgxDelta handles this automatically by deleting the stale token and performing a full re-sync with a warning.

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

### System.Management.Automation.PSObject
Graph API objects with properties matching the JSON fields. Deleted items include an `@removed` property.

## NOTES
Delta queries follow all @odata.nextLink pages automatically (equivalent to -All on other cmdlets). The delta token is saved only after all pages are successfully retrieved.

Query parameters ($select, $filter) are encoded into the delta token on the first request. Do not re-specify them on subsequent runs; they are automatically applied from the saved token. If you change -Property between runs, Sync-MgxDelta detects the mismatch and performs a full re-sync.

Supported delta endpoints include: /users/delta, /groups/delta, /applications/delta, /servicePrincipals/delta, /devices/delta, /directoryRoles/delta, and many others. Source: [Use delta query to track changes in Microsoft Graph data](https://learn.microsoft.com/en-us/graph/delta-query-overview)

## RELATED LINKS
[Export-MgxCollection](Export-MgxCollection.md)
[Invoke-MgxRequest](Invoke-MgxRequest.md)
[Set-MgxOption](Set-MgxOption.md)
