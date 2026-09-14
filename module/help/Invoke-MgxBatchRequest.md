---
external help file: Mgx.Cmdlets.dll-Help.xml
Module Name: Mgx
online version: https://github.com/gromedev/mgx/blob/main/module/help/Invoke-MgxBatchRequest.md
schema: 2.0.0
---

# Invoke-MgxBatchRequest

## SYNOPSIS
Bundle multiple Graph API requests into /$batch calls.

## SYNTAX

```
Invoke-MgxBatchRequest [-Uri] <Object[]> [-Method <String>] [-Body <Object>] [-ConsistencyLevel <String>]
 [-Headers <Hashtable>] [-ThrottlePriority <String>] [-ApiVersion <String>] [-DeadLetterPath <String>]
 [-ProgressAction <ActionPreference>] [-WhatIf] [-Confirm] [<CommonParameters>]
```

## DESCRIPTION
Invoke-MgxBatchRequest bundles multiple Microsoft Graph API requests into /$batch calls, sending up to 20 requests per HTTP round-trip (the Graph API maximum). This is 3-4x faster than individual requests for bulk operations.

Supports GET, POST, PATCH, PUT, and DELETE methods with optional request bodies. Auto-chunks input into 20-request batches.

Source: [Combine multiple HTTP requests using JSON batching](https://learn.microsoft.com/en-us/graph/json-batching)

Pipeline input can be string URLs (for GET, or combined with -Method/-Body for the same operation on all) or hashtables or PSCustomObjects with Url, Method, and Body members for per-item control.

Failed items are surfaced as PowerShell ErrorRecords. Use -ErrorAction Stop to halt on the first failure, or inspect $Error after completion.

## EXAMPLES

### Example 1: Batch GET multiple users
```powershell
@("/users/id1", "/users/id2", "/users/id3") | Invoke-MgxBatchRequest
```

Retrieves three users in a single HTTP round-trip.

### Example 2: Batch POST to create multiple entities
```powershell
$requests = 1..100 | ForEach-Object {
    [PSCustomObject]@{
        Url = "/users"
        Method = "POST"
        Body = @{
            displayName = "User $_"
            mailNickname = "user$_"
            userPrincipalName = "user$_@contoso.com"
            accountEnabled = $true
            passwordProfile = @{ password = "P@ss$(Get-Random)!" }
        }
    }
}
$requests | Invoke-MgxBatchRequest
```

Creates 100 users in 5 batches of 20. Each result includes Url, Status, and Body properties.

### Example 3: Batch PATCH with shared body
```powershell
@("/users/id1", "/users/id2") | Invoke-MgxBatchRequest -Method PATCH -Body @{ department = "HR" }
```

Updates the department for two users in a single batch call.

### Example 4: Batch DELETE multiple entities
```powershell
# Delete all decommissioned users in batches of 20
$users = Invoke-MgxRequest /users -Filter "department eq 'Decommissioned'" -All -Property id
$users | ForEach-Object { "/users/$($_.id)" } | Invoke-MgxBatchRequest -Method DELETE
# For large-scale deletes (1,000+), chunk and report progress:
# $urls = $users | ForEach-Object { "/users/$($_.id)" }
# for ($i = 0; $i -lt $urls.Count; $i += 1000) {
#     $urls[$i..([math]::Min($i+999, $urls.Count-1))] |
#         Invoke-MgxBatchRequest -Method DELETE -ErrorAction SilentlyContinue
# }
```

Deletes all matching users in batches of 20. Failed items (e.g., 404 for already-deleted) are emitted as ErrorRecords. Use `-ErrorAction SilentlyContinue` to suppress expected 404s during cleanup.

### Example 5: Deprioritize background cleanup under throttling
```powershell
$staleGroups | ForEach-Object { "/groups/$($_.id)" } |
    Invoke-MgxBatchRequest -Method DELETE -ThrottlePriority Low -ErrorAction SilentlyContinue
```

Sets `x-ms-throttle-priority: Low` on each batch item, telling Graph to throttle these requests first if the tenant is under pressure. Useful for background cleanup jobs that shouldn't compete with interactive workloads.

### Example 6: Custom per-item headers
```powershell
@("/users/id1", "/users/id2") | Invoke-MgxBatchRequest -Headers @{
    "Prefer" = "outlook.body-content-type=text"
}
```

Passes custom headers to each individual batch item. Headers are merged with -ConsistencyLevel (if specified).

### Example 7: Search with ConsistencyLevel
```powershell
@('/users?$search="displayName:John"', '/groups?$search="displayName:Sales"') |
    Invoke-MgxBatchRequest -ConsistencyLevel eventual
```

Batches search queries that require the ConsistencyLevel header.

### Example 8: Capture failures to a dead-letter file
```powershell
1..1000 | ForEach-Object {
    [PSCustomObject]@{ Url = "/users"; Method = "POST"; Body = @{ displayName = "User-$_"; mailNickname = "user$_"; userPrincipalName = "user$_@contoso.com"; passwordProfile = @{ password = "P@ss$(Get-Random -Minimum 10000)!" } } }
} | Invoke-MgxBatchRequest -DeadLetterPath ./failed-users.jsonl

# The redacted password field, as failed-users.jsonl holds it:
# "passwordProfile":"***REDACTED***"
```

Creates 1000 users via batch. Anything the server answered with a status >= 400 is appended to the JSONL dead-letter file, and so is anything that was never sent, which carries status 0; each line holds Timestamp, Url, Method, Status and Body, then Error where the item's own response carried an error message - a never-sent item has no response to read one from, and neither does an item of a refused chunk the server did not answer, so those lines end at Body. The Body is redacted by field name, by field value and by URL: a field whose name carries password, secret, credential, key, token, assertion, passphrase or connectionstring is replaced by the marker shown above, which is what passwordProfile gets here; a value that is a pre-authenticated URL is cut after its capability parameter, the way a -Debug trace cuts it; and a request whose own path names the secret it carries - resetPassword, uploadSecret, addKey - has its whole Body replaced by that marker instead, as does an item whose body could not be read for redaction.

### Example 9: Pipeline usage at scale (recommended pattern)
```powershell
# Correct: pipe all items into one call
$urls = 1..10000 | ForEach-Object { "/users/$_" }
$urls | Invoke-MgxBatchRequest

# Avoid: calling in a tight loop - slower and can cause threading errors at 200+ iterations
foreach ($url in $urls) { $url | Invoke-MgxBatchRequest }
```

Always pipe all items into a single Invoke-MgxBatchRequest call. This is both faster (one pipeline setup) and avoids PowerShell pipeline threading issues at high invocation rates.

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

### -Body
Request body for all requests when piping string URLs. Ignored when pipeline input carries its own Body member.

Bodies follow the same serialization contract as Invoke-MgxRequest -Body. An item whose body serialization refuses a value (a SecureString, NaN) fails on its own, like an item whose body is not valid JSON; the rest of the batch is sent. So does an item whose body the redaction check cannot read: a raw-string body carrying an unpaired surrogate escape - `{"displayName":"Jos\ud83d"}` - is a document the JSON parser accepts and the check cannot read a string out of, and the check is what decides whether a body may be sent, so that item is refused naming the reason and the rest of the batch is sent.

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

### -DeadLetterPath
Path to a JSONL file where the batch's failed and never-sent items are appended: anything the server answered with a status >= 400, the items of a chunk whose own POST failed, which carry that failure's status, and the items of chunks that were never POSTed, which carry status 0. Nothing the server confirmed is ever written, so the file holds only work that is still outstanding. Each line holds Timestamp, Url, Method and Status, then Body where the request had one, with sensitive fields redacted, then Error where the item's own response carried an error message: a never-sent item has no response to read one from, and neither does an item of a refused chunk the server did not answer, so their lines end at Body. It is a record of what to retry, not a request you can replay directly: the redaction that keeps passwords out of the file also means a redacted Body is no longer the body that was sent. Any field whose name carries password, secret, credential, key, token, assertion, passphrase or connectionstring is replaced by a redaction marker, and so is any field whose name carries downloadUrl, because a pre-authenticated URL fetches the file with no token of its own. A value that is a URL carrying a capability parameter - `sig`, `tempauth`, `guestaccesstoken`, `authkey`, `X-Amz-Signature` - is cut after that parameter and the rest replaced by the marker, the way a -Debug trace cuts it, so the host and the parameter name still say which URL failed; an ordinary link is left whole, because `@odata.nextLink` and a `webUrl` are half of what the line is read for. Where the URL itself names the secret - resetPassword, uploadSecret, addKey, synchronization/secrets - the whole Body is that marker, because those bodies hold the credential under a name that says nothing. A body the redaction cannot walk at all - one naming the same property twice is such a body - gets the marker whole for the same reason, and the run writes a warning naming the item whose body was withheld. What stands in the file is:

```
***REDACTED***
```

Read it to decide what to resubmit, and supply the sensitive fields again yourself. Re-piping the file - `Get-Content dead.jsonl | ConvertFrom-Json | Invoke-MgxBatchRequest` - resends the lines that carry no marker, and refuses the ones that do as an error record before the first chunk leaves: a Body that is the marker whole is not JSON, and a Body carrying it in a field parses but is no longer the body that was sent, so that one is refused naming the field. Any body carrying that marker text is refused the same way before the first chunk leaves, whether it came from the file or not. Under -ErrorAction Stop - which this cmdlet's description recommends - the pipeline then ends with nothing sent at all: re-pipe at the default error preference, or filter the marker lines out first. A refused item carries that status whether its POST reached the server and went unanswered or never left at all - an open circuit, a rate limiter with no permit for it - and the status does not separate the two. Read it as the first: the write may have been applied, so resending one is a decision about duplicates rather than a free retry. Only status 0 says nothing was sent.

The file is opened before the first request goes out and held open for the run, so a path this run cannot write - a directory standing at it, a parent directory that is not there, a file this account may not write - is refused with nothing sent at all, and the error says so: fix the path and run the same command again. It has to be a file system path with something in it. `Env:\X` resolves to a bare name relative to nothing and an empty path resolves to the working directory, so both are refused rather than written somewhere nobody named. A run with no failed and no never-sent items has no lines for the file, and one it created for them is removed again, so a batch that succeeded whole leaves nothing behind; a file that was already there holds an earlier run's outstanding work and is left as it was, and so is one a second run appending to the same path has put a line in.

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
ConsistencyLevel header added to each individual batch item. Required when any batch item URL contains $search (Graph advanced query capabilities). Takes precedence over the same key in -Headers. Source: [Advanced query capabilities on Microsoft Entra ID objects](https://learn.microsoft.com/en-us/graph/aad-advanced-queries)

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
Custom headers applied to each individual batch item. Accepts a hashtable of key-value pairs. Merged with -ConsistencyLevel and -ThrottlePriority (dedicated parameters take precedence over matching keys in -Headers).

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

### -ThrottlePriority
Throttle priority hint for Graph API. Graph uses this to decide which requests to throttle first under pressure. Valid values: Low, Normal, High.

Sets `x-ms-throttle-priority` header on each batch item. Use `Low` for background jobs that should yield to interactive workloads.

```yaml
Type: String
Parameter Sets: (All)
Aliases:
Accepted values: Low, Normal, High

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -Method
HTTP method for all requests when piping string URLs. Default: GET. Ignored when pipeline input carries its own Method member.

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

### -Uri
Graph API URLs to batch. Accepts absolute URLs (https://graph.microsoft.com/v1.0/users/id) or relative URLs (/users/id). Also accepts hashtables or PSCustomObjects with Url, Method, and Body members for per-item control.

```yaml
Type: Object[]
Parameter Sets: (All)
Aliases: Url

Required: True
Position: 0
Default value: None
Accept pipeline input: True (ByValue)
Accept wildcard characters: False
```

### -WhatIf
Shows what would happen if the cmdlet runs. The cmdlet is not run. The gate covers reads as well as writes: an all-GET batch is described and not sent, because even a read batch spends resource units, can be throttled, and emits objects into the pipeline. Invoke-MgxRequest makes the other choice and sends its reads under -WhatIf.

The count it names is what would be sent. Bodies are validated and checked for the redaction marker before the gate, so an item either of those refuses is left out of that count and named beside it - `PATCH 1 request via $batch; 2 refused` - and each refusal is written as the error record it is, under -WhatIf as without it: a refusal is not an action the gate governs.

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

### System.Object[]
String URLs, or hashtables or PSCustomObjects with Url, Method, and Body members.

## OUTPUTS

### System.Collections.Hashtable
Per-request results with Url, Method, Status, and Body keys. Status says what became of the operation: an item the server answered keeps the status it answered with; an item whose chunk was refused carries the refusal's status (>= 400), which covers both a POST that went out unanswered and one an open circuit or a rate limiter stopped before it left, so read it as a write that may have been applied; only an item in a chunk that was never POSTed gets status 0, and only those carry NotSent - another chunk of the batch failed, not necessarily one before it, since chunks can run in parallel. Body here is the RESPONSE body - for a failure that is the error envelope, not the request - so piping results straight back resubmits the wrong thing. Use Url and Method to rebuild the requests you want to retry.

## NOTES
Each batch item is retried individually on 429 (throttled) or 5xx errors (for idempotent methods). POST requests only retry on 429 because POST is non-idempotent - retrying a failed POST on 5xx could create duplicates if the server processed the request before the error. This matches the Kiota SDK retry behavior. Source: [Microsoft Graph error responses and resource types](https://learn.microsoft.com/en-us/graph/errors)

Items that exhaust per-chunk retries get one additional batch-level retry pass, except after a chunk failure: the run has stopped sending, and that pass is a send. It is skipped for every item it would have picked up, including items in chunks that were POSTed and answered in full, and they are handed back with the status the server gave them rather than resent.

Use -Verbose to see retry counts, throttle encounters, and timing.

Batching reduces HTTP round-trips (20 operations per request instead of 1), but Graph counts each item inside a batch individually against the server-side write quota (3,000 writes / 2.5 min per app+tenant). Sustained write throughput caps at ~20/sec regardless of batching. See [Set-MgxOption](Set-MgxOption.md) for the full throttle limits table and tuning guidance. Source: [Microsoft Graph service-specific throttling limits](https://learn.microsoft.com/en-us/graph/throttling-limits)

For best performance and stability, always pipe all items into a single Invoke-MgxBatchRequest call rather than calling it in a loop. At 200+ rapid invocations per second, PowerShell's internal pipeline thread safety can race between cmdlet instances. Piping all items into one call avoids this entirely and is significantly faster.

## RELATED LINKS
[Invoke-MgxRequest](Invoke-MgxRequest.md)
[Set-MgxOption](Set-MgxOption.md)
