# Mgx

High-performance, resilient Microsoft Graph access for PowerShell. 

```powershell
Install-Module Mgx
```

`Mgx` is a PowerShell client for Microsoft Graph built for long-running and high-volume workloads. It handles the parts that become difficult when a script makes thousands of requests: pagination, concurrency, throttling, retries, dead connections, memory usage, and recovery from interruption.

It adds concurrent fan-out, streaming pagination, batching, adaptive rate limiting, retry and connection recovery, checkpointed exports, delta synchronization, content downloads, and telemetry.

Mgx can use the authentication context established by `Connect-MgGraph`, so it does not require a separate authentication system.

```powershell
Install-Module Microsoft.Graph.Authentication
Install-Module Mgx

Connect-MgGraph -Scopes "User.Read.All"

Invoke-MgxRequest /users -All -Property displayName,mail
```

## What Mgx adds

- **Concurrent fan-out** for workloads with many independent requests
- **Streaming pagination** without buffering entire collections in memory
- **Batch requests** with up to 20 sub-requests per HTTP call
- **Adaptive rate limiting** that learns the usable rate for each workload
- **Retries, circuit breaking, timeouts, and connection recovery**
- **Checkpointed JSONL exports** with crash-safe resume
- **Delta synchronization** with persisted state and mid-run recovery
- **Whole-file and ranged content downloads**
- **Telemetry** for HTTP timing, throttling, retries, pacing, and resource units
- **SDK resilience integration** through `Enable-MgxResilience`

The difference matters most when Graph stops behaving like a simple request/response API and starts behaving like a distributed system with quotas, transient failures, long-running connections, and partial progress.

## Examples

```powershell
# Enumerate all users, streamed
Invoke-MgxRequest /users -All -Property displayName,mail,department

# Filter
Invoke-MgxRequest /users `
    -Filter "department eq 'Engineering'" `
    -Property displayName,mail

# Single request
Invoke-MgxRequest "/users/$userId"

# Concurrent fan-out
@("id1", "id2", "id3") |
    Invoke-MgxRequest '/users/{id}'

# Expand related objects in parallel
Invoke-MgxRequest /users -Top 50 |
    Expand-MgxRelation '/users/{id}/manager' -As Manager -Flatten

# Batch up to 20 requests per HTTP call
@("/users/id1", "/users/id2") |
    Invoke-MgxBatchRequest -Method PATCH -Body @{ department = "HR" }

# Stream a collection to JSONL with checkpoint/resume
Export-MgxCollection /auditLogs/signIns `
    -OutputFile ./signins.jsonl `
    -CheckpointPath ./signins.cp `
    -All

# Stateful delta synchronization
Sync-MgxDelta /users/delta `
    -DeltaPath ./users.delta.json `
    -Property displayName,mail

# Resumable delta export
Sync-MgxDelta /me/drive/root/delta `
    -DeltaPath ./drive.json `
    -CheckpointPath ./drive.cp `
    -OutputFile ./drive.jsonl

# Read only the first 256 KiB of each file
Invoke-MgxRequest "/me/drive/items/$folderId/children" -All |
    Where-Object { $_.file } |
    Get-MgxContent -First 262144

# Add Mgx resilience to existing Microsoft Graph SDK code
Enable-MgxResilience
Get-MgUser -All
Disable-MgxResilience
```

See [`examples/`](examples/) for additional examples.

## Microsoft.Graph comparison

Mgx is not intended to replace the Microsoft Graph SDK's typed cmdlets for ordinary interactive use. It targets workloads where request orchestration, throughput, and recovery become the dominant problems.

| Common operation | Microsoft.Graph | Mgx |
| --- | --- | --- |
| Bulk lookups | `ForEach-Object { Get-MgUser -UserId $_ }` | `Invoke-MgxRequest '/users/{id}'` |
| Bulk updates | Individual SDK calls | `Invoke-MgxBatchRequest` |
| Large exports | Buffer with `Get-MgUser -All` | `Export-MgxCollection` |
| Fault protection | Script-specific | Built in |
| Adaptive pacing | Not provided | Built in |
| Dead connection recovery | Default HTTP behavior | Body-read timeouts + connection recycling |
| Telemetry | Caller-managed | `Get-MgxTelemetry` |
| Beta endpoints | `Microsoft.Graph.Beta` | `-ApiVersion beta` |

`Enable-MgxResilience` can add Mgx's resilience handling to existing SDK scripts without replacing the SDK's own request pipeline.

## Benchmarks

### Performance

Benchmarks were run against an Entra ID test tenant containing 100,000 users and 15,779 groups using PowerShell 7.6.4 (.NET 10), app-only authentication, and the Microsoft Graph SDK.

| Operation | Mgx | SDK (`Get-MgUser`) | Raw REST | Speedup vs SDK |
| --- | ---: | ---: | ---: | ---: |
| **List 100,000 users** | **47.1s** | 53.2s | 58.5s | 1.1× |
| **Look up 5,000 users by ID** | **98.8s** | 521.0s | 985.3s | **5.3×** |
| **User report** *(1k users + groups)* | **23.9s** | 107.9s | 205.8s | **4.5×** |
| **Full delta enumeration** *(130,233 items)* | **145.6s** | — | — | — |

Sequential enumeration has little concurrency to exploit, so Mgx is close to a tuned SDK. Workloads with many independent requests benefit much more from concurrency and batching.

The SDK enumeration result above uses `-PageSize 999`, matching the benchmark. At its default page size, the same enumeration takes roughly 2.5–3× as long.

### Resilience under throttling

A second client was used to hold the application's resource-unit budget near its limit while all 100,000 users were enumerated.

| Contender | Retrieved | Missing | Duplicates | Wall time |
| --- | ---: | ---: | ---: | ---: |
| `Invoke-MgxRequest -All` | **100,000 / 100,000** | 0 | 0 | 715.2s |
| `Get-MgUser -All` | **100,000 / 100,000** | 0 | 0 | 729.9s |
| `Invoke-RestMethod` + fixed retry | 8,000 / 100,000 | 92,000 | 0 | 120.0s |
| `Invoke-RestMethod`, no retry | 1,700 / 100,000 | 98,300 | 0 | 5.5s |

The raw REST loops finish sooner because they stop making useful progress once the budget is exhausted. Mgx and the SDK take longer because they continue until the enumeration is complete.

Without the competing load, all four clients returned exactly 100,000 users. The difference above is caused by throttling, not pagination.

### Memory efficiency

Exporting 100,000 users to JSONL:

| | Mgx `Export-MgxCollection` | Mgx request → file | REST buffer → write |
| --- | ---: | ---: | ---: |
| Wall time | 53.0s | 47.0s | 64.0s |
| Peak working set | 305MB | **265MB** | **555MB** |
| Managed heap delta | +19.8MB | **+5.0MB** | **+415.7MB** |

Streaming directly to a file keeps managed heap growth close to zero relative to a buffer-then-write implementation, and `Export-MgxCollection` persists progress so an interrupted export can resume without duplicating objects.

### Adaptive pacing

Adaptive pacing is enabled by default. It starts conservatively, increases the allowed rate as requests succeed, and backs off when throttling indicates that a workload is approaching a limit.

| Workload | Pacing on | `-NoAdaptivePacing` |
| --- | ---: | ---: |
| 8,000 concurrent reads | 39.4s | 40.3s |
| 50 lookups, concurrency 8, cold session | 4.4s | 1.0s |

The cold-start cost is most visible on very small workloads. For interactive workloads where the tenant is not being pushed hard, disable it with:

```powershell
Set-MgxOption -NoAdaptivePacing
```

## Resilience model

Mgx combines request-level resilience with workload-level pacing.

The request pipeline provides:

1. **Rate limiting** — token bucket with burst capacity and a configurable refill rate
2. **Retries** — exponential backoff with jitter for transient failures, including `429`, `500`, `502`, `503`, and `504`, while honoring `Retry-After`
3. **Circuit breaking** — stops repeatedly failing workloads and allows controlled half-open recovery
4. **Timeouts** — per-request and cumulative limits across retries
5. **Connection recycling** — periodically replaces long-lived HTTP connections so dead sockets do not remain stuck indefinitely

Adaptive pacing sits above those request-level strategies and learns a separate usable rate for each workload. A throttled Directory workload therefore does not unnecessarily slow an unrelated workload such as Drive.

`Enable-MgxResilience` does not replace the SDK's own retry handler. It adds handling for failure modes outside that handler's scope, including body-read timeouts and periodic connection recycling.

## Resource units and throttling

Microsoft Graph directory workloads use resource-unit budgets. The cost depends on query shape rather than on which client issued the request.

Mgx reads `x-ms-resource-unit` and exposes the accumulated values through `Get-MgxTelemetry`. It uses throttling signals to pace requests before the workload repeatedly hits the limit. Observed latency is reported alongside them but is not a pacing input.

Examples measured against the test tenant:

| Query shape | Resource units |
| --- | ---: |
| `transitiveMembers?$top=5` | 4 |
| `transitiveMembers?$top=5&$select=id` | **3** |
| `groups/{id}` | 1 |

Resource units are not reduced by changing clients. Mgx makes them visible and uses them when deciding how aggressively to issue requests.

See [`examples/resilience-and-telemetry/resource-unit-budgeting.ps1`](examples/resilience-and-telemetry/resource-unit-budgeting.ps1) and [`tests/benchmarks/13-resource-unit-rate.ps1`](tests/benchmarks/13-resource-unit-rate.ps1) for the measurement code.

## Delta synchronization

Graph delta enumeration can return the same object more than once during a single run, including combinations of live objects and `@removed` tombstones. Mgx does not silently deduplicate the stream because doing so requires a caller policy for conflicting occurrences and retaining every ID seen during the enumeration.

`Sync-MgxDelta` handles delta-link persistence and checkpointed recovery while preserving the stream returned by Graph. Apply deduplication downstream when the application needs a specific conflict policy.

## Output shape

Graph-data cmdlets emit case-insensitive `Hashtable`s, matching the shape returned by `Invoke-MgGraphRequest`:

```powershell
$user = Invoke-MgxRequest "/users/$id"

$user.displayName
$user['@odata.type']
```

`@odata.type` is preserved. Other `@odata.*` transport metadata is stripped, including `@odata.etag`. Use `-Raw` when the original payload or `If-Match` tag is required.

Keys have no defined order. Pin output columns explicitly when order matters:

```powershell
Invoke-MgxRequest /users -All |
    Select-Object displayName,mail |
    Export-Csv users.csv
```

Use `-Raw` when you need the original JSON object:

```powershell
Invoke-MgxRequest /users -All -Raw |
    ConvertFrom-Json
```

## Cmdlets

| Cmdlet | Purpose |
| --- | --- |
| `Invoke-MgxRequest` | Execute Graph requests with streaming, concurrency, retries, and rate limiting |
| `Invoke-MgxBatchRequest` | Send up to 20 sub-requests in one Graph batch |
| `Export-MgxCollection` | Stream paginated results to JSONL with checkpointing |
| `Expand-MgxRelation` | Perform concurrent fan-out lookups for related objects |
| `Sync-MgxDelta` | Manage delta state and checkpointed synchronization |
| `Get-MgxContent` | Download whole files or ranged content |
| `Set-MgxOption` / `Get-MgxOption` | Configure limits, retry counts, timeouts, and resilience thresholds |
| `Enable-MgxResilience` / `Disable-MgxResilience` | Add or remove Mgx resilience from Microsoft.Graph SDK calls |
| `Get-MgxTelemetry` | Report HTTP timing, retries, throttling, pacing, and resource-unit usage |

## Requirements

- **PowerShell:** 7.4+
- **Authentication:** `Microsoft.Graph.Authentication` 2.10.0+ for token acquisition

The authentication module is intentionally not a declared dependency because Mgx can operate with the authentication context already established by `Connect-MgGraph`.

## Build

```powershell
./build.ps1
```

Install `platyPS` before building: `build.ps1` regenerates the compiled help and refuses to build without it.

`Microsoft.Graph.Core`, `Polly.Core`, and `System.Threading.RateLimiting` are loaded into an isolated **Assembly Load Context** to avoid assembly-version conflicts with other PowerShell modules.

## License

[MIT](LICENSE)
