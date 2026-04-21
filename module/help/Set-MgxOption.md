---
external help file: Mgx.Cmdlets.dll-Help.xml
Module Name: Mgx
online version: https://github.com/gromedev/mgx/blob/main/module/help/Set-MgxOption.md
schema: 2.0.0
---

# Set-MgxOption

## SYNOPSIS
Configure resilience options for all Mgx cmdlets.

## SYNTAX

```
Set-MgxOption [-RateLimitBurst <Int32>] [-RateLimitPerSecond <Int32>] [-NoRateLimit]
 [-RateLimitQueueLimit <Int32>] [-MaxRetryAfterSeconds <Int32>] [-MaxRetryAttempts <Int32>]
 [-TotalTimeoutSeconds <Int32>] [-AttemptTimeoutSeconds <Int32>] [-CircuitBreakerDurationSeconds <Int32>]
 [-CircuitBreakerFailureRatio <Double>] [-CircuitBreakerMinThroughput <Int32>]
 [-CircuitBreakerSamplingDurationSeconds <Int32>] [-BatchItemsPerSecond <Int32>] [-Reset] [<CommonParameters>]
```

## DESCRIPTION
Set-MgxOption configures resilience and rate limiting options for all Mgx cmdlets. Only parameters explicitly passed are updated; unspecified values retain their current settings. Options take effect on the next cmdlet invocation.

Use -Reset to restore all options to their defaults.

## EXAMPLES

### Example 1: Increase total timeout for large batches
```powershell
Set-MgxOption -TotalTimeoutSeconds 600
```

Sets a 10-minute total timeout, useful for large batch operations.

### Example 2: Disable rate limiting
```powershell
Set-MgxOption -NoRateLimit
```

Disables the client-side rate limiter. Useful for testing, but not recommended for production.

### Example 3: Tune for large tenants
```powershell
Set-MgxOption -TotalTimeoutSeconds 600 -RateLimitBurst 300 -RateLimitPerSecond 50
```

Configures higher throughput settings suitable for tenants with 50k+ objects.

### Example 4: Reset all to defaults
```powershell
Set-MgxOption -Reset
```

Restores all options to their default values.

## PARAMETERS

### -BatchItemsPerSecond
Target throughput for batch item pacing in items/sec. Controls inter-chunk delay in sequential batch execution to avoid burst-and-stall against Graph's server-side write throttle. Set to 0 to disable pacing. Range: 0-1000. Default: 20.

```yaml
Type: Int32
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: 20
Accept pipeline input: False
Accept wildcard characters: False
```

### -AttemptTimeoutSeconds
Timeout per individual HTTP request attempt. Range: 1-300. Default: 30.

```yaml
Type: Int32
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: 30
Accept pipeline input: False
Accept wildcard characters: False
```

### -CircuitBreakerDurationSeconds
How long the circuit breaker stays open (rejecting requests) after tripping. Range: 1-300. Default: 15.

```yaml
Type: Int32
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: 15
Accept pipeline input: False
Accept wildcard characters: False
```

### -CircuitBreakerFailureRatio
Failure ratio threshold to trip the circuit breaker (0.01-1.0). Default: 0.1 (10%).

```yaml
Type: Double
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: 0.1
Accept pipeline input: False
Accept wildcard characters: False
```

### -CircuitBreakerMinThroughput
Minimum number of requests in the sampling window before the circuit breaker evaluates failure ratio. Range: 1-1000. Default: 40.

```yaml
Type: Int32
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: 40
Accept pipeline input: False
Accept wildcard characters: False
```

### -CircuitBreakerSamplingDurationSeconds
Duration of the circuit breaker's failure measurement window. Range: 5-300. Default: 30.

```yaml
Type: Int32
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: 30
Accept pipeline input: False
Accept wildcard characters: False
```

### -MaxRetryAfterSeconds
Maximum delay to honor from a server Retry-After header. If the server requests a longer delay, it is clamped to this value. Range: 1-600. Default: 120.

```yaml
Type: Int32
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: 120
Accept pipeline input: False
Accept wildcard characters: False
```

### -MaxRetryAttempts
Maximum number of retry attempts (not counting the initial attempt). Range: 1-50. Default: 7.

```yaml
Type: Int32
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: 7
Accept pipeline input: False
Accept wildcard characters: False
```

### -NoRateLimit
Disable the client-side rate limiter entirely.

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

### -RateLimitBurst
Maximum burst capacity for the token bucket rate limiter. Range: 1-10,000. Default: 200.

```yaml
Type: Int32
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: 200
Accept pipeline input: False
Accept wildcard characters: False
```

### -RateLimitPerSecond
Sustained token replenishment rate (requests per second). Range: 1-10,000. Default: 50.

```yaml
Type: Int32
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: 50
Accept pipeline input: False
Accept wildcard characters: False
```

### -RateLimitQueueLimit
Maximum number of requests to queue when rate limit is exceeded. Requests beyond this limit are rejected. Range: 0-100,000. Default: 500.

```yaml
Type: Int32
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: 500
Accept pipeline input: False
Accept wildcard characters: False
```

### -Reset
Restore all options to their default values.

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

### -TotalTimeoutSeconds
Total timeout across all retries for a single operation. Range: 1-3,600. Default: 300.

```yaml
Type: Int32
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: 300
Accept pipeline input: False
Accept wildcard characters: False
```

### CommonParameters
This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable, -InformationAction, -InformationVariable, -OutVariable, -OutBuffer, -PipelineVariable, -Verbose, -WarningAction, and -WarningVariable. For more information, see [about_CommonParameters](http://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

### None

## OUTPUTS

### None

## NOTES
Options are shared across all Mgx cmdlets in the current PowerShell session. Changes to rate limiter settings (Burst, PerSecond, NoRateLimit, QueueLimit) trigger a pipeline rebuild on the next cmdlet invocation.

Graph API server-side throttle limits:

Microsoft Graph enforces server-side write quotas that apply regardless of client-side settings. These limits use a token bucket algorithm and cannot be bypassed - Mgx handles them automatically via retry, but understanding them helps set realistic expectations for bulk operations.

**Identity and Access (users, groups, applications, service principals):**

| Scope | Write Limit | Sustained Rate |
|-------|-------------|---------------|
| App + Tenant | 3,000 requests / 2 min 30s | ~20/sec |
| Tenant (all apps) | 18,000 requests / 5 min | ~60/sec |
| App (all tenants) | 35,000 requests / 5 min | ~117/sec |

Write operations (POST, PATCH, PUT, DELETE) each cost 1 request. Batch requests (/$batch) count each item inside the batch individually against these limits - batching reduces HTTP overhead but does not increase write throughput.

Read operations use a separate ResourceUnit-based quota (5,000-8,000 RU/10s for large tenants) and are rarely throttled at scale.

**What this means in practice:**
- Sustained write throughput for a single app against one tenant caps at ~20 writes/sec
- At 100k objects, expect ~80 minutes minimum for creation regardless of client settings
- Throttle stalls (429 with Retry-After) occur in waves every ~5,000 writes as the token bucket drains and refills
- The default `-RateLimitPerSecond` of 50 is optimized for read-heavy and mixed workloads. For pure write workloads, the server-side cap is ~20 writes/sec regardless of client settings
- `-MaxRetryAfterSeconds` controls how long Mgx waits when throttled (default: 120s). Graph typically requests 150s; clamping lower causes more retry cycles. Source: [Microsoft Graph throttling guidance](https://learn.microsoft.com/en-us/graph/throttling)

Source: [Microsoft Graph service-specific throttling limits](https://learn.microsoft.com/en-us/graph/throttling-limits)

**Non-directory APIs have tighter rate limits:**

Some Graph workloads enforce significantly lower throttle ceilings than the Identity and Access limits above. The default `-RateLimitPerSecond 50` will cause immediate 429 responses against these APIs. Lower the client-side rate to stay under the server-side cap:

| Workload | Approximate Limit | Recommended Setting |
|----------|-------------------|---------------------|
| Identity Protection (risky users/events) | ~0.25 req/sec | -RateLimitPerSecond 1 |
| OneNote (pages, sections, notebooks) | ~2 req/sec | -RateLimitPerSecond 2 |
| Planner (tasks, plans, buckets) | ~4 req/sec | -RateLimitPerSecond 4 |
| Conditional Access (policies, named locations) | ~25 req/sec | -RateLimitPerSecond 20 |
| Security alerts (alerts, incidents) | Variable, lower than directory | -RateLimitPerSecond 10 |

Example: Set-MgxOption -RateLimitPerSecond 1 -RateLimitBurst 1, run Identity Protection queries, then Set-MgxOption -Reset to restore defaults.

These limits are per-app-per-tenant and vary by license tier. When working with multiple restricted APIs in one session, set the rate to match the most restrictive workload, or call Set-MgxOption -Reset between workloads.

## RELATED LINKS
[Get-MgxOption](Get-MgxOption.md)
