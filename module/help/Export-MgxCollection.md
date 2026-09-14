---
external help file: Mgx.Cmdlets.dll-Help.xml
Module Name: Mgx
online version: https://github.com/gromedev/mgx/blob/main/module/help/Export-MgxCollection.md
schema: 2.0.0
---

# Export-MgxCollection

## SYNOPSIS
Stream paginated Graph API results directly to a JSONL file.

## SYNTAX

```
Export-MgxCollection [-Uri] <String> -OutputFile <String> [-Property <String[]>] [-Filter <String>]
 [-ExpandProperty <String[]>] [-Search <String>] [-Sort <String[]>] [-Skip <Int32>] [-Top <Int32>] [-All]
 [-PageSize <Int32>] [-ConsistencyLevel <String>] [-Headers <Hashtable>] [-ApiVersion <String>]
 [-CheckpointPath <String>] [-NoPageSize] [-ProgressAction <ActionPreference>] [-WhatIf] [-Confirm]
 [<CommonParameters>]
```

## DESCRIPTION
Export-MgxCollection streams paginated Microsoft Graph API results directly to a JSONL (JSON Lines) file. One JSON object per line, no object conversion, minimal memory pressure regardless of collection size.

Supports checkpoint/resume: specify -CheckpointPath to save progress at page boundaries and mid-page (every 500 items). On interruption - Ctrl+C, a crash, or a killed process - re-running the same command resumes from the last checkpoint; an interrupted first run's partial temp file is recovered and trimmed to exactly the checkpointed item count, so the output never contains duplicates or torn lines. The checkpoint file is deleted on successful completion.

Downstream note: Graph pagination can transiently serve an overlapping page (observed in the wild as one extra page of items versus @odata.count). Mgx warns when the returned count overshoots the reported count; if the JSONL feeds another system, deduplicate on the id field as a matter of hygiene.

Returns a summary PSObject with ItemCount and OutputFile properties.

## EXAMPLES

### Example 1: Export all users to JSONL
```powershell
Export-MgxCollection /users -OutputFile ./users.jsonl -All
```

Streams all users to a JSONL file with constant memory usage.

### Example 2: Export with checkpoint/resume
```powershell
Export-MgxCollection /auditLogs/signIns -OutputFile ./signins.jsonl -CheckpointPath ./signins-cp.json -All
```

Exports sign-in logs with checkpoint support. If interrupted, re-running the same command resumes from the last saved position.

### Example 3: Export filtered results
```powershell
Export-MgxCollection /users -OutputFile ./engineers.jsonl -Filter "department eq 'Engineering'" -Property id, displayName, mail -All
```

Exports only Engineering department users with selected fields.

## PARAMETERS

### -All
Follow all @odata.nextLink pages and export the entire collection. Source: [Paging Microsoft Graph data in your app](https://learn.microsoft.com/en-us/graph/paging)

```yaml
Type: SwitchParameter
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

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
Path to a JSON checkpoint file for resumable exports. Deleted on successful completion. A run stops - with nothing written to the output, the checkpoint or a temp - when the temp file the checkpoint names, or the output it would append to or replace, is open in another export or cannot be opened for writing (a permission, a directory or a pipe at the path, a link that cannot be followed, a path too long for the file system, a socket, a directory above it this account cannot search), or when the copy it stages before promoting a temp fails, in which case both files are left as found: the rows it counts are in that file and this checkpoint is the only thing that counts them, and exporting anyway would write over both. This holds whether the run would append, promote a temp, or export from the beginning after its temp vanished. The copy a promotion stages is written beside the output under a name the run creates itself, and the name is cleared first: a link, a pipe, a socket, a copy left by an interrupted promotion or a copy this account cannot open standing there is deleted and the removal reported as a warning, while a directory or a copy another run holds stops the run instead. A -WhatIf pass removes nothing: where something stands at that name it says what the run would remove there and leaves it, and where the name is clear it makes the same create at no length before taking it back - so a preview stops where the run would and deletes nothing on the way. Wait for that export to finish, give this run its own -OutputFile and -CheckpointPath, or grant write access to that file - or to the directory holding it, where that is what the run names - and run again. A resumed or promoting run holds the output open for as long as it is appending, which is what stops a second run over the same command line; a run recovering a checkpoint older than 2.1 opens the output as it always has. On Linux and macOS a PowerShell reader of a held file fails with a sharing violation until the run ends, while tail and cat read it as before.

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

### -Confirm
Prompts you for confirmation before running the cmdlet.

```yaml
Type: SwitchParameter
Parameter Sets: (All)
Aliases: cf

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -ConsistencyLevel
Sets the ConsistencyLevel header. Required when using -Search. Source: [Advanced query capabilities on Microsoft Entra ID objects](https://learn.microsoft.com/en-us/graph/aad-advanced-queries)

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

### -ExpandProperty
OData $expand properties to include related entities.

```yaml
Type: String[]
Parameter Sets: (All)
Aliases: Expand

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -Filter
OData $filter expression for server-side filtering.

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

### -Headers
Additional HTTP headers as a hashtable.

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

### -NoPageSize
Do not append $top to the request URL.

```yaml
Type: SwitchParameter
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -OutputFile
Path to the output JSONL file. One JSON object per line.

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

### -PageSize
Items per page. Range: 1-999. Default: 999.

```yaml
Type: Int32
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: 999
Accept pipeline input: False
Accept wildcard characters: False
```

### -Property
OData $select properties. Reduces payload size.

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

### -Search
OData $search expression. Requires -ConsistencyLevel eventual.

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

### -Skip
Number of items to skip.

```yaml
Type: Int32
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: 0
Accept pipeline input: False
Accept wildcard characters: False
```

### -Sort
OData $orderby expressions for sorting.

```yaml
Type: String[]
Parameter Sets: (All)
Aliases: OrderBy

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -Top
Maximum total items to export.

```yaml
Type: Int32
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: 0
Accept pipeline input: False
Accept wildcard characters: False
```

### -Uri
Microsoft Graph API collection endpoint (e.g., /users, /auditLogs/signIns).

```yaml
Type: String
Parameter Sets: (All)
Aliases: Resource

Required: True
Position: 0
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -WhatIf
Shows what would happen if the cmdlet runs. The cmdlet is not run.

```yaml
Type: SwitchParameter
Parameter Sets: (All)
Aliases: wi

Required: False
Position: Named
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
Summary object with ItemCount and OutputFile properties.

## NOTES
Checkpoint uses positional skip, which assumes the Graph API returns the same page content on re-fetch. If items were added or deleted between interruption and resume, positional skip may produce duplicates or miss items.

## RELATED LINKS
[Invoke-MgxRequest](Invoke-MgxRequest.md)
[Invoke-MgxBatchRequest](Invoke-MgxBatchRequest.md)
