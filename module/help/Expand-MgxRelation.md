---
external help file: Mgx.Cmdlets.dll-Help.xml
Module Name: Mgx
online version: https://github.com/gromedev/mgx/blob/main/module/help/Expand-MgxRelation.md
schema: 2.0.0
---

# Expand-MgxRelation

## SYNOPSIS
Enrich Graph objects with related data via concurrent fan-out.

## SYNTAX

```
Expand-MgxRelation [-Uri] <String> [-As] <String> -InputObject <PSObject> [-Flatten] [-IdProperty <String>]
 [-Concurrency <Int32>] [-SkipNotFound] [-SkipForbidden] [-ConsistencyLevel <String>] [-Headers <Hashtable>]
 [-ApiVersion <String>] [-Top <Int32>] [<CommonParameters>]
```

## DESCRIPTION
Expand-MgxRelation enriches pipeline objects with related data from Microsoft Graph. It buffers input objects, extracts their IDs, fans out concurrent requests to a template URI, and attaches the results as a new property on each object.

Automatically detects whether the relation endpoint returns a collection (e.g., /users/{id}/licenseDetails with a "value" array) or a singleton (e.g., /users/{id}/manager returning a flat object). Use -Flatten to unwrap singleton results from their array wrapper.

For bulk writes or non-enrichment scenarios, use Invoke-MgxRequest or Invoke-MgxBatchRequest instead.

## EXAMPLES

### Example 1: Expand license details for users
```powershell
Invoke-MgxRequest /users -Top 10 |
    Expand-MgxRelation -Uri '/users/{id}/licenseDetails' -As Licenses -SkipNotFound |
    Select-Object displayName, id, Licenses
```

Fetches license details for 10 users concurrently and attaches them as a Licenses property. Users without license data get $null for the Licenses property. -SkipNotFound suppresses 404 errors silently.

### Example 2: Expand a singleton relation with -Flatten
```powershell
Invoke-MgxRequest /users -All |
    Expand-MgxRelation -Uri '/users/{id}/manager' -As Manager -Flatten -SkipNotFound |
    Select-Object displayName, @{N='ManagerName'; E={$_.Manager.displayName}}
```

The /manager endpoint returns a single object, not a collection. -Flatten unwraps it from the array so Manager is a PSObject (not a one-element array). Users without a manager get $null.

### Example 3: Chain multiple expansions
```powershell
Invoke-MgxRequest /users -Top 20 |
    Expand-MgxRelation -Uri '/users/{id}/licenseDetails' -As Licenses -SkipNotFound |
    Expand-MgxRelation -Uri '/users/{id}/manager' -As Manager -Flatten -SkipNotFound |
    Select-Object displayName, @{N='LicenseCount'; E={$_.Licenses.Count}}, @{N='Manager'; E={$_.Manager.displayName}}
```

Each Expand-MgxRelation stage buffers its input, fans out, and passes enriched objects to the next stage. All original properties are preserved through the chain.

### Example 4: Limit per-relation items with -Top
```powershell
Invoke-MgxRequest /groups -All |
    Expand-MgxRelation -Uri '/groups/{id}/members' -As Members -Top 5 -SkipForbidden |
    Select-Object displayName, @{N='MemberCount'; E={$_.Members.Count}}
```

Fetches at most 5 members per group. Without -Top, large groups would return all members (potentially thousands). The $top parameter is sent to Graph to minimize bandwidth.

### Example 5: Use a custom ID property
```powershell
$report = Invoke-MgxRequest /users -All -Property id, displayName |
    Select-Object @{N='userId'; E={$_.id}}, displayName
$report | Expand-MgxRelation -Uri '/users/{id}/licenseDetails' -As Licenses -IdProperty userId -SkipNotFound
```

Uses the userId property instead of the default id for {id} template substitution. Useful when your input objects store the identifier under a different property name.

### Example 6: Expand with beta endpoint and consistency level
```powershell
Invoke-MgxRequest /users -Top 5 |
    Expand-MgxRelation -Uri '/users/{id}/authentication/methods' -As AuthMethods -ApiVersion beta -ConsistencyLevel eventual -SkipNotFound -SkipForbidden
```

Fetches authentication methods (beta-only endpoint) with eventual consistency for advanced query support.

## PARAMETERS

### -InputObject
The object to enrich with related data. Accepts any PSObject from the pipeline. Must have a property matching -IdProperty (default: id) whose value is substituted into the -Uri template.

```yaml
Type: PSObject
Parameter Sets: (All)
Aliases:

Required: True
Position: Named
Default value: None
Accept pipeline input: True (ByValue)
Accept wildcard characters: False
```

### -Uri
Template URI with an {id} placeholder. The placeholder is replaced with each input object's ID value (URL-encoded). Must be a relative Graph API path.

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

### -As
Property name to attach the relation results under on each output object. If the input object already has a property with this name, it is replaced.

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: True
Position: 1
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -Flatten
Unwrap single-value relations. When the endpoint returns exactly one item (or a singleton object like /manager), the result is attached as a single PSObject instead of a one-element array. If multiple items are returned, a warning is emitted and the array is returned as-is.

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

### -IdProperty
Which property on the input object to use for {id} substitution. Objects missing this property emit a non-terminating error and are output with $null for the relation.

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: id
Accept pipeline input: False
Accept wildcard characters: False
```

### -Concurrency
Maximum number of concurrent HTTP requests during fan-out. Higher values increase throughput but may trigger Graph API throttling.

```yaml
Type: Int32
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: 5
Accept pipeline input: False
Accept wildcard characters: False
```

### -SkipNotFound
Suppress 404 Not Found errors. Useful when some entities lack the relation (e.g., users without a manager). Skipped entities get $null for the relation property.

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

### -SkipForbidden
Suppress 403 Forbidden errors. Useful when permissions vary per entity (e.g., guest users with restricted access).

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

### -ConsistencyLevel
Consistency level header for advanced queries. Set to "eventual" when the relation endpoint requires it (e.g., endpoints using $search, $filter on certain properties, or $count). Source: [Advanced query capabilities on Microsoft Entra ID objects](https://learn.microsoft.com/en-us/graph/aad-advanced-queries)

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
Additional HTTP headers to send with each request. Keys and values are converted to strings.

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

### -ApiVersion
Graph API version to use. Some relation endpoints are only available in beta.

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: v1.0
Accept pipeline input: False
Accept wildcard characters: False
```

### -Top
Maximum number of items to return per relation. Sent as $top to Graph and enforced client-side. Without this parameter, all items are returned (potentially thousands for large collections like group members).

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

### CommonParameters
This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable, -InformationAction, -InformationVariable, -OutVariable, -OutBuffer, -PipelineVariable, -Verbose, -WarningAction, and -WarningVariable. For more information, see [about_CommonParameters](http://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

### System.Management.Automation.PSObject
Any PSObject with a property matching -IdProperty (default: id).

## OUTPUTS

### System.Management.Automation.PSObject
The input object with an additional property (named by -As) containing the relation data. Collection endpoints produce an array of PSObjects. Singleton endpoints produce a single PSObject when -Flatten is used, or a one-element array otherwise.

## NOTES
This cmdlet buffers all pipeline input before issuing requests. Use upstream filtering (-Top, -Filter) on the source cmdlet rather than downstream Select-Object -First to control the number of HTTP requests.

Duplicate IDs in the input are deduplicated: only one HTTP request is made per unique ID, and all objects sharing that ID receive the same result.

## RELATED LINKS
[Invoke-MgxRequest](Invoke-MgxRequest.md)
[Invoke-MgxBatchRequest](Invoke-MgxBatchRequest.md)
