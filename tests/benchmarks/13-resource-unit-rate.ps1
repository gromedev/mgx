# Benchmark 13: what resource-unit rate does the tenant actually sustain?
#
# The budget behaves like a token bucket, and the bucket starts full - so any measurement that
# does not outlast the initial burst allowance reads as a much higher sustained ceiling than
# the tenant has. This holds 1-RU requests (/users/{id}?$select=id) at a fixed send rate and
# records, per rate: when the first 429 arrived, how many units had been served by then, and
# what rate the tenant kept serving at once it was refusing. The burst allowance is the units
# served before the first refusal; the sustained ceiling is the served rate after it.
#
# A round that ends with no refusal proves sustainability ONLY if it spent well past the burst
# allowance; the report says so explicitly rather than leaving a short clean run to be read as
# a ceiling. That misreading is exactly how this suite once published 882 RU/s "sustained".
#
# Raw HttpClient, no retries, no pacing: the tenant is the subject, not the client. Each
# request counts once, and a 429 is a data point rather than something to recover from.
param(
    [int[]] $Rates = @(300, 700, 1200),
    [int] $Seconds = 90,
    [int] $CooldownSeconds = 120,
    [int] $MaxConnections = 512
)

. "$PSScriptRoot/common.ps1"
Import-MgxLocal
Connect-MgxBenchmark

# What the tenant is DOCUMENTED to allow, read before the first round measures what it actually
# allows. Recorded per round beside the measured numbers: the burst allowance and the sustained
# ceiling only mean something against the budget the tier promises, and the tier is banded on
# the user count, so the same two numbers off two tenants are two different findings.
$tenant = Get-BenchTenantFact
$documented = $tenant.Budget
if ($documented) {
    Write-Host ("tenant {0}: {1} users, documented tier {2} ({3}) = {4} RU/s ({5} RU / 10s)" -f `
        $tenant.TenantId, $documented.UserCount, $documented.Tier, $documented.Band,
        $documented.RuPerSecond, $documented.RuPer10Seconds)
}
else {
    Write-Host 'documented budget unknown (no user count) - rounds record it as null.'
}

# Enough ids to avoid hammering one object; requests cycle through them.
$ids = Get-BenchUserIds -Count 5000
Write-Host "using $($ids.Count) user ids; $($Rates.Count) round(s) of ${Seconds}s"

$results = [ordered]@{}
$roundIndex = 0
foreach ($rate in $Rates) {
    $roundIndex++
    if ($roundIndex -gt 1) {
        Write-Host "cooldown ${CooldownSeconds}s (let the resource-unit budget refill)..."
        Start-Sleep -Seconds $CooldownSeconds
    }

    $token = Get-BenchAppToken
    $handler = [System.Net.Http.SocketsHttpHandler]::new()
    $handler.MaxConnectionsPerServer = $MaxConnections
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(120)
    $client.DefaultRequestHeaders.Authorization =
        [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $token)

    Write-Host "holding ~$rate RU/s for ${Seconds}s..."
    $pending = [System.Collections.Generic.List[System.Threading.Tasks.Task[System.Net.Http.HttpResponseMessage]]]::new()
    $sent = 0; $ok = 0; $throttled = 0; $failed = 0
    $okBeforeFirst429 = 0; $okAfterFirst429 = 0
    $first429Seconds = $null
    $ruHeaderSample = $null

    $harvest = {
        $still = [System.Collections.Generic.List[System.Threading.Tasks.Task[System.Net.Http.HttpResponseMessage]]]::new()
        foreach ($t in $pending) {
            if (-not $t.IsCompleted) { $still.Add($t); continue }
            if ($t.Status -ne 'RanToCompletion') { $script:failed++; continue }
            $r = $t.Result
            $code = [int]$r.StatusCode
            if ($code -eq 429) {
                $script:throttled++
                if ($null -eq $script:first429Seconds) {
                    $script:first429Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1)
                }
            }
            elseif ($code -ge 200 -and $code -lt 300) {
                $script:ok++
                if ($null -eq $script:first429Seconds) { $script:okBeforeFirst429++ }
                else { $script:okAfterFirst429++ }
                if ($null -eq $script:ruHeaderSample) {
                    $v = $null
                    if ($r.Headers.TryGetValues('x-ms-resource-unit', [ref]$v)) {
                        $script:ruHeaderSample = @($v)[0]
                    }
                }
            }
            else { $script:failed++ }
            $r.Dispose()
        }
        $script:pending = $still
    }

    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
        # Cadence self-corrects: send whatever the elapsed time says should be out by now.
        $due = [int][math]::Min($rate * $Seconds, [math]::Floor($rate * $sw.Elapsed.TotalSeconds))
        while ($sent -lt $due) {
            $id = $ids[$sent % $ids.Count]
            $pending.Add($client.GetAsync("https://graph.microsoft.com/v1.0/users/$id`?`$select=id"))
            $sent++
        }
        & $harvest
        Start-Sleep -Milliseconds 50
    }
    $sendWindow = $sw.Elapsed.TotalSeconds

    # Let stragglers land; they still carry statuses that belong to this round.
    $deadline = [datetime]::UtcNow.AddSeconds(60)
    while ($pending.Count -gt 0 -and [datetime]::UtcNow -lt $deadline) {
        & $harvest
        Start-Sleep -Milliseconds 200
    }
    $failed += $pending.Count   # anything still hanging after 60s
    $sw.Stop()
    $client.Dispose()

    $achieved = [math]::Round($sent / $sendWindow, 0)
    $served = if ($null -ne $first429Seconds -and $sw.Elapsed.TotalSeconds -gt $first429Seconds) {
        [math]::Round($okAfterFirst429 / ($sw.Elapsed.TotalSeconds - $first429Seconds), 0)
    } else { $null }

    $results["rate$rate"] = @{
        TargetRate       = $rate
        AchievedRate     = $achieved
        Seconds          = [math]::Round($sendWindow, 1)
        Sent             = $sent
        Succeeded        = $ok
        Throttled        = $throttled
        Failed           = $failed
        First429Seconds  = $first429Seconds
        RuBeforeFirst429 = $okBeforeFirst429   # 1 RU each, so units == successes
        ServedRateAfter429 = $served
        RuHeaderSample   = $ruHeaderSample
        # The documented tier the two measured numbers above are to be read against.
        DocumentedBudgetRuPerSecond = $documented?.RuPerSecond
        DocumentedTier              = $documented?.Tier
        DocumentedTierBand          = $documented?.Band
    }

    if ($null -eq $first429Seconds) {
        Write-Host ("  no refusal in {0}s at ~{1} RU/s ({2} RU spent)." -f `
            [math]::Round($sendWindow), $achieved, $ok)
        Write-Host ("  NOTE: clean only proves sustainability if {0} RU is well past the burst allowance" -f $ok)
        Write-Host ("  a throttled round below reports that allowance as 'RU before first 429'.")
    }
    else {
        Write-Host ("  first 429 at {0}s; {1} RU served before it; ~{2} RU/s served while refusing." -f `
            $first429Seconds, $okBeforeFirst429, $served)
    }
}

# --- Report -------------------------------------------------------------------------------
Write-Host ''
Write-Host "=== RESOURCE-UNIT RATE (1-RU requests, no retry) ==="
if ($documented) {
    Write-Host ("documented for this tenant: tier {0}, {1}, {2} RU/s" -f `
        $documented.Tier, $documented.Band, $documented.RuPerSecond)
}
Write-Host ('{0,10} {1,10} {2,9} {3,11} {4,13} {5,15}' -f `
    'Target', 'Achieved', 'Sent', 'First 429', 'RU before it', 'Served after it')
foreach ($k in $results.Keys) {
    $r = $results[$k]
    Write-Host ('{0,8}/s {1,8}/s {2,9} {3,11} {4,13} {5,13}/s' -f `
        $r.TargetRate, $r.AchievedRate, $r.Sent,
        ($null -ne $r.First429Seconds ? "$($r.First429Seconds)s" : '-'),
        ($null -ne $r.First429Seconds ? $r.RuBeforeFirst429 : '-'),
        ($r.ServedRateAfter429 ?? '-'))
}

$results.rates = $Rates
$results.seconds = $Seconds
Write-BenchResult -Benchmark '13-resource-unit-rate' -Result $results
