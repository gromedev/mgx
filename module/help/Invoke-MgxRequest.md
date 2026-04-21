---
external help file: Mgx.Cmdlets.dll-Help.xml
Module Name: Mgx
online version: https://github.com/gromedev/mgx/blob/main/module/help/Invoke-MgxRequest.md
schema: 2.0.0
---

# Invoke-MgxRequest

## SYNOPSIS
General-purpose resilient client for any Microsoft Graph endpoint.

## SYNTAX

### Direct (Default)
```
Invoke-MgxRequest [-Uri] <String> [-Method <String>] [-Body <Object>] [-Property <String[]>]
 [-ExpandProperty <String[]>] [-ConsistencyLevel <String>] [-Headers <Hashtable>] [-ApiVersion <String>] [-Raw]
 [-Filter <String>] [-Sort <String[]>] [-Search <String>] [-Skip <Int32>] [-All] [-Top <Int32>]
 [-PageSize <Int32>] [-CountVariable <String>] [-CheckpointPath <String>] [-NoPageSize]
 [-ProgressAction <ActionPreference>] [-WhatIf] [-Confirm] [<CommonParameters>]
```

### Pipeline
```
Invoke-MgxRequest [-Uri] <String> [-Method <String>] [-Body <Object>] [-Property <String[]>]
 [-ExpandProperty <String[]>] [-ConsistencyLevel <String>] [-Headers <Hashtable>] [-ApiVersion <String>] [-Raw]
 [-Filter <String>] [-Sort <String[]>] [-Search <String>] [-Skip <Int32>] [-All] [-Top <Int32>]
 [-PageSize <Int32>] [-CountVariable <String>] [-CheckpointPath <String>] [-NoPageSize] [-InputObject <String>]
 [-Concurrency <Int32>] [-SkipNotFound] [-SkipForbidden] [-ProgressAction <ActionPreference>] [-WhatIf]
 [-Confirm] [<CommonParameters>]
```

## DESCRIPTION
Invoke-MgxRequest is a general-purpose resilient client for any Microsoft Graph endpoint. It supports streaming pagination, fan-out concurrency, write operations (POST, PATCH, PUT, DELETE), and checkpoint/resume.

Results are returned as PSObjects with properties matching the Graph API JSON response. DateTime strings are automatically parsed to DateTimeOffset, and @odata.type is preserved as an ODataType property.

For bulk writes involving more than 10 items, consider using Invoke-MgxBatchRequest instead, which is 3-4x faster due to fewer HTTP round-trips.

## EXAMPLES

### Example 1: Get the first 5 users
```powershell
Invoke-MgxRequest /users -Top 5
```

Returns up to 5 user objects from Microsoft Graph.

### Example 2: Get all users with streaming pagination
```powershell
Invoke-MgxRequest /users -All
```

Streams all users from Microsoft Graph, automatically following @odata.nextLink pages. Results are emitted to the pipeline as they arrive, keeping memory usage constant.

### Example 3: Filter and select properties
```powershell
Invoke-MgxRequest /users -Filter "department eq 'Engineering'" -Property id, displayName, mail -All
```

Returns all users in the Engineering department, selecting only the specified properties.

### Example 4: Fan-out to resolve multiple IDs
```powershell
@("id1", "id2", "id3") | Invoke-MgxRequest '/users/{id}'
```

Pipes IDs into the URI template. Each ID replaces {id} and requests run concurrently (default concurrency: 5). Each result includes an _MgxSourceId property with the original input value.

### Example 5: Create a new user
```powershell
Invoke-MgxRequest /users -Method POST -Body @{
    displayName = "New User"
    mailNickname = "newuser"
    userPrincipalName = "newuser@contoso.com"
    accountEnabled = $true
    passwordProfile = @{ password = "P@ssw0rd!" }
}
```

Creates a new user. Write operations support -WhatIf and -Confirm.

### Example 6: Bulk update via fan-out
```powershell
$ids | Invoke-MgxRequest '/users/{id}' -Method PATCH -Body @{ department = "Engineering" }
```

Updates the department for multiple users concurrently.

### Example 7: Search with ConsistencyLevel
```powershell
Invoke-MgxRequest /users -Search "displayName:John" -ConsistencyLevel eventual -Top 10
```

Uses Graph advanced query capabilities. The -ConsistencyLevel parameter is required when using -Search.

### Example 8: Use the beta endpoint
```powershell
Invoke-MgxRequest /users -ApiVersion beta -Top 10
```

Queries the Microsoft Graph beta endpoint instead of v1.0.

### Example 9: Purview audit log search (async pattern)
```powershell
$search = Invoke-MgxRequest /security/auditLog/queries -Method POST -Body @{
    displayName         = "Last 24h sign-ins"
    filterStartDateTime = (Get-Date).AddDays(-1).ToString("o")
    filterEndDateTime   = (Get-Date).ToString("o")
    operationFilters    = @("userSignedIn")
}

# Poll until the search completes or fails (typically 5-15 minutes)
do {
    Start-Sleep -Seconds 10
    $status = Invoke-MgxRequest "/security/auditLog/queries/$($search.id)"
    if ($status.status -eq 'failed') { throw "Audit search failed" }
} while ($status.status -ne 'succeeded')

$records = Invoke-MgxRequest "/security/auditLog/queries/$($search.id)/records" -All
```

Purview audit log queries are asynchronous. POST creates the search, then poll until complete. The loop checks for failure status to avoid hanging indefinitely. Mgx resilience handles transient errors automatically. Requires AuditLog.Read.All scope.

### Example 10: Hierarchical traversal (Sites → Drives → Items)
```powershell
Invoke-MgxRequest '/sites?search=*' -All -Property id |
    Invoke-MgxRequest '/sites/{id}/drives' -Concurrency 10 -Property id |
    Invoke-MgxRequest '/drives/{id}/root/children' -Concurrency 10 -SkipForbidden
```

Three-level fan-out across SharePoint sites, drives, and root items. `/sites` requires `search=*` to enumerate all sites. The {id} template is resolved per pipeline input. -SkipForbidden silently skips sites where the app lacks read access. -Concurrency 10 runs up to 10 parallel requests per stage.

### Example 11: Bulk license assignment
```powershell
$sku = Invoke-MgxRequest /subscribedSkus | Where-Object skuPartNumber -eq 'ENTERPRISEPACK'
if (-not $sku) { throw "ENTERPRISEPACK SKU not found in tenant" }

Import-Csv ./users.csv |
    Select-Object -ExpandProperty UserPrincipalName |
    Invoke-MgxRequest '/users/{id}/assignLicense' -Method POST -Body @{
        addLicenses    = @(@{ skuId = $sku.skuId })
        removeLicenses = @()
    } -Concurrency 10
```

Assigns E3 licenses to users listed in a CSV. The {id} template resolves each UPN from the pipeline. Fan-out at concurrency 10 with automatic retry on throttling. Graph caps write throughput at ~20 operations per second per tenant.

## PARAMETERS

### -All
Automatically follow all @odata.nextLink pages and stream all results. Without this switch, only the first page is returned. Source: [Paging Microsoft Graph data in your app](https://learn.microsoft.com/en-us/graph/paging)

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
Graph API version to use. Default: v1.0. Use "beta" for preview endpoints.

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

### -Body
Request body for write operations (POST, PATCH, PUT). Accepts a hashtable or PSObject, which is serialized to JSON.

```yaml
Type: Object
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -CheckpointPath
Path to a JSON checkpoint file for resumable pagination. On interruption (Ctrl+C), progress is saved. On re-run with the same checkpoint path, pagination resumes from where it left off. The checkpoint file is deleted on successful completion.

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

### -Concurrency
Maximum number of concurrent requests during fan-out operations. Only applies when piping input with a {id} template URI. Range: 1-128. Default: 5.

```yaml
Type: Int32
Parameter Sets: Pipeline
Aliases:

Required: False
Position: Named
Default value: 5
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
Sets the ConsistencyLevel header on the request. Required when using -Search or advanced query capabilities ($count in $filter). Typically set to "eventual". Source: [Advanced query capabilities on Microsoft Entra ID objects](https://learn.microsoft.com/en-us/graph/aad-advanced-queries)

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

### -CountVariable
Name of a variable to store the total item count (@odata.count) from the response. The variable is created in the caller's scope.

```yaml
Type: String
Parameter Sets: (All)
Aliases: CV

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -ExpandProperty
OData $expand properties to include in the response. Retrieves related entities inline (e.g., "manager", "memberOf").

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
OData $filter expression to apply server-side filtering (e.g., "accountEnabled eq true").

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
Additional HTTP headers to include on the request as a hashtable (e.g., @{ "Prefer" = "outlook.body-content-type=text" }).

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

### -InputObject
Pipeline input for fan-out operations. Each value replaces the {id} placeholder in the URI template. Accepts string values or objects with an Id property.

```yaml
Type: String
Parameter Sets: Pipeline
Aliases: Id

Required: False
Position: Named
Default value: None
Accept pipeline input: True (ByPropertyName, ByValue)
Accept wildcard characters: False
```

### -Method
HTTP method for the request. Default: GET. Write methods (POST, PATCH, PUT, DELETE) trigger ShouldProcess confirmation.

```yaml
Type: String
Parameter Sets: (All)
Aliases:
Accepted values: GET, POST, PATCH, PUT, DELETE

Required: False
Position: Named
Default value: GET
Accept pipeline input: False
Accept wildcard characters: False
```

### -NoPageSize
Do not append $top to the request URL. Use this for endpoints that do not support the $top query parameter.

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

### -PageSize
Number of items to request per page. Range: 1-999. Default: 999. This sets the $top parameter on each page request (not the total result count).

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
OData $select properties to include in the response. Reduces payload size by requesting only the specified fields.

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

### -Raw
Return raw JSON strings instead of PSObjects. Useful for piping to ConvertFrom-Json or writing to files.

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

### -Search
OData $search expression for keyword search (e.g., "displayName:John"). Requires -ConsistencyLevel eventual.

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
Number of items to skip before returning results. Maps to the OData $skip parameter.

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

### -SkipForbidden
During fan-out operations, silently skip items that return HTTP 403 Forbidden instead of writing an error.

```yaml
Type: SwitchParameter
Parameter Sets: Pipeline
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -SkipNotFound
During fan-out operations, silently skip items that return HTTP 404 Not Found instead of writing an error.

```yaml
Type: SwitchParameter
Parameter Sets: Pipeline
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -Sort
OData $orderby expressions for server-side sorting (e.g., "displayName desc").

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
Maximum total number of items to return. Unlike -PageSize (which controls per-page size), -Top caps the total result set.

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
Microsoft Graph API endpoint. Accepts relative paths (e.g., /users) or absolute URLs (e.g., https://graph.microsoft.com/v1.0/users). For fan-out, use {id} as a placeholder (e.g., /users/{id}).

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

### System.String
Pipeline input for fan-out operations. Each string value replaces {id} in the URI template.

## OUTPUTS

### System.Management.Automation.PSObject
Graph API response objects with properties matching the JSON fields.

### System.String
When -Raw is specified, raw JSON strings are returned instead.

## NOTES
All requests are routed through a resilient HTTP client with retry, circuit breaker, rate limiting, and timeout policies. Configure these via Set-MgxOption.

Requires an active Microsoft Graph connection via Connect-MgGraph.

When to use Invoke-MgxBatchRequest instead: Fan-out (piping IDs to Invoke-MgxRequest) sends one HTTP request per item. Batching (Invoke-MgxBatchRequest) sends one HTTP request per 20 items. For bulk write operations (POST, PATCH, DELETE) over ~10 items, batching reduces HTTP round-trips and wall-clock time by 3-20x. Use fan-out for per-item URI templates (/users/{id}/memberOf), streaming results, or read-heavy operations where throttling is unlikely.

## RELATED LINKS
[Invoke-MgxBatchRequest](Invoke-MgxBatchRequest.md)
[Export-MgxCollection](Export-MgxCollection.md)
[Set-MgxOption](Set-MgxOption.md)
