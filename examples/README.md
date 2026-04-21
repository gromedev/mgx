# Mgx Examples

Runnable scripts covering common Microsoft Graph scenarios. Each script is self-contained and includes the required scopes in its header comment.

## Prerequisites

```powershell
Install-Module Mgx                       # once
Import-Module Mgx
Connect-MgGraph -Scopes "User.Read.All", "Group.Read.All"   # adjust scopes per script
```

## Scripts

### Users

| Script | Description | Scopes |
|--------|-------------|--------|
| [01-get-all-users.ps1](01-get-all-users.ps1) | Stream all users to the console | `User.Read.All` |
| [02-export-users-to-jsonl.ps1](02-export-users-to-jsonl.ps1) | Export all users to a JSONL file with checkpoint/resume | `User.Read.All` |
| [03-get-managers-fan-out.ps1](03-get-managers-fan-out.ps1) | Fetch every user's manager concurrently | `User.Read.All` |
| [07-enrich-users-with-manager.ps1](07-enrich-users-with-manager.ps1) | Attach manager as a property on each user object | `User.Read.All` |
| [13-disabled-accounts-report.ps1](13-disabled-accounts-report.ps1) | Report all disabled accounts | `User.Read.All` |
| [14-guest-users-report.ps1](14-guest-users-report.ps1) | Report all guest (external) accounts | `User.Read.All` |
| [17-bulk-delete-whatif.ps1](17-bulk-delete-whatif.ps1) | Bulk delete stale guests with `-WhatIf` preview | `User.ReadWrite.All` |
| [19-chained-relation-expansion.ps1](19-chained-relation-expansion.ps1) | Enrich users with manager + licenses in one pass | `User.Read.All` |

### Groups

| Script | Description | Scopes |
|--------|-------------|--------|
| [12-group-members-multipage.ps1](12-group-members-multipage.ps1) | Stream all members from all groups concurrently | `Group.Read.All`, `User.Read.All` |

### Devices & Apps

| Script | Description | Scopes |
|--------|-------------|--------|
| [15-stale-devices-report.ps1](15-stale-devices-report.ps1) | Report devices inactive for 90+ days | `Device.Read.All` |
| [16-app-secrets-expiry.ps1](16-app-secrets-expiry.ps1) | Find app secrets and certificates expiring within 30 days | `Application.Read.All` |
| [20-conditional-access-export.ps1](20-conditional-access-export.ps1) | Export all Conditional Access policies (beta endpoint) | `Policy.Read.All` |

### Audit Logs

| Script | Description | Scopes |
|--------|-------------|--------|
| [08-export-sign-in-logs.ps1](08-export-sign-in-logs.ps1) | Export sign-in logs to JSONL with checkpoint/resume | `AuditLog.Read.All` |

### Bulk Operations

| Script | Description | Scopes |
|--------|-------------|--------|
| [06-bulk-update.ps1](06-bulk-update.ps1) | PATCH multiple users via `$batch` (20 per HTTP call) | `User.ReadWrite.All` |
| [09-dead-letter-retry.ps1](09-dead-letter-retry.ps1) | Bulk create users with dead-letter tracking for failures | `User.ReadWrite.All` |
| [22-mixed-endpoint-batch.ps1](22-mixed-endpoint-batch.ps1) | Query users, groups, apps, and SKUs in one HTTP call | `User.Read.All`, `Group.Read.All`, `Application.Read.All` |

### Delta Sync

| Script | Description | Scopes |
|--------|-------------|--------|
| [05-delta-sync.ps1](05-delta-sync.ps1) | Full sync on first run, incremental changes thereafter | `User.Read.All` |

### Resilience & Configuration

| Script | Description | Scopes |
|--------|-------------|--------|
| [04-resilience-for-existing-scripts.ps1](04-resilience-for-existing-scripts.ps1) | Add retry/circuit breaker to existing SDK scripts (zero code changes) | `User.Read.All` |
| [21-resilience-status.ps1](21-resilience-status.ps1) | Check, enable, and disable SDK resilience injection | `User.Read.All` |
| [10-telemetry.ps1](10-telemetry.ps1) | View request counts, retries, and throttle events for the session | `User.Read.All` |
| [11-beta-endpoint.ps1](11-beta-endpoint.ps1) | Access beta endpoints without installing extra modules | `User.Read.All` |
| [18-tune-rate-limits.ps1](18-tune-rate-limits.ps1) | Tune rate limiter, retry count, and timeouts at runtime | `User.Read.All` |
| [23-benchmark-resilience.ps1](23-benchmark-resilience.ps1) | Benchmark: bare SDK vs MgxResilience vs Invoke-MgxRequest vs Export | `User.Read.All` |

## Cmdlet Coverage

Every Mgx cmdlet is demonstrated in at least one script:

| Cmdlet | Scripts |
|--------|---------|
| `Invoke-MgxRequest` | 01, 03, 12, 13, 14, 15, 17, 19, 20 |
| `Invoke-MgxBatchRequest` | 06, 09, 22 |
| `Export-MgxCollection` | 02, 08, 23 |
| `Expand-MgxRelation` | 07, 19 |
| `Sync-MgxDelta` | 05 |
| `Enable-MgxResilience` | 04, 21, 23 |
| `Disable-MgxResilience` | 21 |
| `Get-MgxResilience` | 21 |
| `Set-MgxOption` | 18 |
| `Get-MgxOption` | 18 |
| `Get-MgxTelemetry` | 10, 23 |
