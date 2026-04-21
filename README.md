# Mgx

SDK cmdlets like `Get-MgUser -All` buffer every page in memory, have no retry, no rate limiting, and time out on large tenants. Raw `Invoke-RestMethod` is faster but you build your own pagination, retry, and batching. Mgx gives you the speed of raw REST with resilience you'd never build yourself, using your existing `Connect-MgGraph` auth.

## Quick start

```powershell
Install-Module Mgx
Connect-MgGraph -Scopes "User.Read.All"
Invoke-MgxRequest /users -All -Property displayName,mail
```

## What it does

- **Speed** — streaming pagination, concurrent fan-out, batched writes (up to 20 per HTTP call)
- **Resilience** — proactive rate limiting, retry with exponential backoff, circuit breaker, per-request and total timeouts
- **Operations** — JSONL export with checkpoint/resume, delta sync with automatic token management, SDK resilience injection

## Benchmarks

Entra ID tenant.

| Operation | Mgx | SDK (`Get-MgUser`) | `Invoke-RestMethod` |
|-----------|-----|-----|---------------------|
| List 37k users | 16s | 59s | 18s |
| List 110k users | 38s | 196s | 49s |
| Look up 5,000 users by ID | 115s | 635s | 961s |
| User report (1k users + groups + apps) | 57s | 270s | ~370s |
| Create 10,000 users | 502s (batch) | ~2,500s (2 timeouts) | ~3,333s |
| Export 37k to JSONL | -34MB | n/a | +118MB |
| Delete 87k from 300k tenant | 4.8h, 0 failures | — | — |

Memory column is process memory delta. Mgx streams to disk as pages arrive and GC reclaims as it goes (-34MB). RestMethod buffers everything in memory then writes (+118MB). On a 200k-user tenant that's ~600MB.

Rate limiter keeps requests under Graph's throttle ceiling proactively — zero 429s across all read and export operations at every scale tested (1k to 110k). Memory stays flat across repeated enumerations (19.7MB average per 10k-user round, no leak).

## Examples

```powershell
# All users, streamed
Invoke-MgxRequest /users -All -Property displayName,mail,department

# Filter
Invoke-MgxRequest /users -Filter "department eq 'Engineering'" -Property displayName,mail

# Single user
Invoke-MgxRequest "/users/$userId"

# Pipe straight to CSV
Invoke-MgxRequest /users -All -Property displayName,mail,department | Export-Csv users.csv

# JSONL export with checkpoint/resume (survives Ctrl+C)
Export-MgxCollection /auditLogs/signIns -OutputFile ./signins.jsonl -CheckpointPath ./cp.json -All

# Beta endpoints, no extra module
Invoke-MgxRequest /users -ApiVersion beta -Top 10

# Add resilience to existing SDK scripts
Enable-MgxResilience
Get-MgUser -All                  # now has retry, circuit breaker, rate limiting
Disable-MgxResilience            # back to normal
```

Bulk operations:

```powershell
# Fan-out: look up multiple users concurrently
@("id1", "id2", "id3") | Invoke-MgxRequest '/users/{id}'

# Enrich users with their manager
Invoke-MgxRequest /users -Top 50 | Expand-MgxRelation '/users/{id}/manager' -As Manager -Flatten

# Batch: 20 requests per HTTP call
@("/users/id1", "/users/id2") | Invoke-MgxBatchRequest -Method PATCH -Body @{ department = "HR" }

# Delta sync: full pull first time, only changes after
Sync-MgxDelta /users/delta -DeltaPath ./delta.json -Property displayName,mail
```

23 more examples in [`examples/`](examples/).

## Microsoft.Graph Comparison

| Microsoft.Graph | Mgx |
|---|---|
| `$ids \| ForEach-Object { Get-MgUser -UserId $_ }` | `$ids \| Invoke-MgxRequest '/users/{id}'` |
| `$ids \| ForEach-Object { Update-MgUser -UserId $_ ... }` | `$urls \| Invoke-MgxBatchRequest -Method PATCH -Body @{...}` |
| `$all = Get-MgUser -All; $all \| Export-Csv users.csv` | `Export-MgxCollection /users -OutputFile users.jsonl` |
| No retry, no circuit breaker, no rate limiting | `Enable-MgxResilience` |
| `Install-Module Microsoft.Graph.Beta.*` | `-ApiVersion beta` |

## Cmdlets

| Cmdlet | Purpose |
|--------|---------|
| `Invoke-MgxRequest` | Any Graph endpoint, with retry and rate limiting |
| `Invoke-MgxBatchRequest` | Multiple requests in a single HTTP call (up to 20) |
| `Export-MgxCollection` | Paginated results → JSONL, with checkpoint/resume |
| `Expand-MgxRelation` | Concurrent fan-out to resolve related objects |
| `Sync-MgxDelta` | Delta queries with automatic token management |
| `Set-MgxOption` / `Get-MgxOption` | Configure rate limiting, retry, circuit breaker, timeouts |
| `Enable-MgxResilience` / `Disable-MgxResilience` | Inject resilience into the Microsoft.Graph SDK |
| `Get-MgxResilience` | Check whether resilience injection is active |
| `Get-MgxTelemetry` | Request counts, retries, throttling, timing |

`Get-Help <cmdlet> -Full` for parameter details.

## Resilience stack

Every HTTP call goes through four [Polly 8.x](https://github.com/App-vNext/Polly) layers:

| Layer | Behavior |
|-------|----------|
| **Rate limiter** | Token bucket (200 burst / 50 per sec) — stays under Graph's limits before you get throttled |
| **Retry** | Up to 7 attempts, exponential backoff + jitter, honors `Retry-After`. Covers 429, 500, 502, 503, 504 |
| **Circuit breaker** | Opens after 10% failure rate, half-open probe after 15s |
| **Timeout** | 30s per request, 300s total across retries |

All tunable via `Set-MgxOption`. Client errors (400, 401, 403, 404) are never retried. POST only retries on 429 to avoid duplicates.

## Requirements

- PowerShell 7.5+
- [Microsoft.Graph.Authentication](https://www.powershellgallery.com/packages/Microsoft.Graph.Authentication) 2.10.0+

## Building

```powershell
./build.ps1
```

Dependencies ([Microsoft.Graph.Core](https://www.nuget.org/packages/Microsoft.Graph.Core) 3.2.5, [Polly.Core](https://www.nuget.org/packages/Polly.Core) 8.6.6, [System.Threading.RateLimiting](https://www.nuget.org/packages/System.Threading.RateLimiting) 9.0.0) load in a custom Assembly Load Context so they don't conflict with other modules.

## License

[MIT](LICENSE)
