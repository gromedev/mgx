# Benchmark 10: does adaptive pacing pay for itself against a service that throttles?
#
# Benchmark 06 answers "what does pacing cost" but structurally cannot answer "what does it
# buy": its mock injects 429s on a fixed schedule keyed on entity id, so the throttle rate is
# independent of send rate and backing off has no upside by construction. Real Graph throttles
# HARDER the faster you push, which is the whole premise of pacing.
#
# The first version of this benchmark did not answer it either, and looked like it had. It drove
# 8,000 reads at concurrency 128, spent 8,009 RU at ~180 RU/s, and recorded zero throttle retries
# in BOTH arms. Benchmark 13 explains why: this tenant hands out ~23,000 RU of burst allowance
# before the first refusal and keeps serving ~730 RU/s while refusing, so that run was a third of
# the burst at a quarter of the rate. It could not have been throttled. A clean result there says
# nothing about pacing - it says the workload was small.
#
# So this version states its admissibility, takes the thresholds from 13's recorded result rather
# than a constant someone has to remember to update, and records whether the run actually entered
# the throttled regime. If the unpaced arm was never refused there was nothing for pacing to
# avoid, and the comparison is reported as inconclusive rather than as a win for whichever arm
# happened to finish first.
#
# Reaching the regime needs more in flight than one pipeline holds: Invoke-MgxRequest caps
# -Concurrency at 128, and 128 in flight measured ~180 req/s - a quarter of the rate this tenant
# keeps serving at while refusing. Rate is the lever, and volume cannot substitute for it. The
# work is therefore split across N pipelines in parallel runspaces of ONE process. The pacer, the
# telemetry collector and the HTTP client are all process-wide statics, so those runspaces share
# a single pacer - which is the point: one workload bucket, N producers, the shape a real fan-out
# has. The roof is the transport's: 20 pooled connections multiplexing HTTP/2 streams hold
# roughly 2,000 requests in flight, and the 12 x 128 default sits under it.
#
# The ids handed to a pipeline have to be distinct. Invoke-MgxRequest deduplicates the ids it is
# piped, by design, so a slice that repeats an id issues one request for it - and a -Count above
# what the pool can cover was inert: the arm reported the volume it was asked for and issued a
# fraction of it. Each pipeline now draws IdsPerPipeline distinct ids off the shared pool, from a
# rotated start so the pipelines are not all on the same object at once, and the arm refuses to
# run if any slice holds a duplicate. Requests issued is then the number of ids handed out, and a
# -Count above Pipelines x IdPool is reported as clamped rather than quietly dropped.
#
# -Plan prints that shape and the admissibility arithmetic against 13's thresholds and exits
# without connecting, which is how a parameter set is checked before spending an hour on it.
#
# Requests are /users/{id}?$select=id, 1 RU each, the same shape 13 calibrated the thresholds
# with, so request counts and unit counts are the same number.
#
# Reads only: throttling is not write-specific, so this needs no write consent.
#
# CmdletBinding so an argument this script does not define is refused rather than collected into
# $args and ignored: a run that silently drops the parameter it was given is the same class of
# wrong answer as one that silently drops the requests it was given.
[CmdletBinding()]
param(
    [ValidateSet('both', 'paced', 'unpaced')] [string] $Mode = 'both',
    # 12 x 5,000: 60,000 units at a projected ~2,160 RU/s, which is ~10s inside the seeded
    # tenant's 23,000 RU allowance and ~51s past it at the 731 RU/s it serves while refusing.
    [ValidateRange(1, [int]::MaxValue)] [int] $Count = 60000,
    [ValidateRange(1, 512)] [int] $Pipelines = 12,
    [ValidateRange(1, 128)] [int] $Concurrency = 128,
    [ValidateRange(1, [int]::MaxValue)] [int] $IdPool = 5000,
    [int] $CooldownSeconds = 300,
    [switch] $Plan
)

. "$PSScriptRoot/common.ps1"

function Get-ThrottleThreshold {
    <#
        The bar this benchmark has to clear, measured rather than assumed. Benchmark 13 holds a
        fixed send rate until the tenant refuses and records both numbers that matter: the burst
        allowance (units served before the first 429) and the rate the tenant keeps serving at
        once it is refusing. Reading them back here means a tenant with a different budget
        recalibrates by running 13, not by editing a constant in this file.

        Without a recorded 13, fall back to the documented 8,000 RU per 10s for tenants above
        500 users - and say which one is in force, because a threshold that came from a document
        and one that came from this tenant deserve different confidence.
    #>
    $file = Join-Path $PSScriptRoot 'results/13-resource-unit-rate.json'
    if (Test-Path $file) {
        $rounds = @(Get-Content $file -Raw | ConvertFrom-Json | ForEach-Object {
            $_.Result.PSObject.Properties | Where-Object { $_.Name -match '^rate\d+$' } |
                ForEach-Object { $_.Value }
        })
        $refused = @($rounds | Where-Object { $null -ne $_.First429Seconds -and $_.ServedRateAfter429 })
        if ($refused) {
            return @{
                BurstRu  = [int](($refused | Measure-Object RuBeforeFirst429 -Maximum).Maximum)
                Sustained= [int](($refused | Measure-Object ServedRateAfter429 -Minimum).Minimum)
                Source   = 'benchmark 13, this tenant'
            }
        }
    }
    @{ BurstRu = 8000; Sustained = 800; Source = 'documented default (8,000 RU / 10s)' }
}

$threshold = Get-ThrottleThreshold
$shape = Get-BenchPacingLoadShape -Count $Count -Pipelines $Pipelines -Concurrency $Concurrency `
    -IdPool $IdPool -BurstRu $threshold.BurstRu -SustainedRuPerSecond $threshold.Sustained

function Show-LoadShape {
    <#
        What each arm will issue, and whether that can reach the regime - printed before
        anything connects, because the answer is arithmetic and does not need a tenant.
    #>
    Write-Host ("throttling threshold from {0}: burst {1} RU, sustained {2} RU/s" -f `
        $threshold.Source, $threshold.BurstRu, $threshold.Sustained)
    Write-Host '=== LOAD SHAPE (per arm) ==='
    Write-Host ("  distinct ids per pipeline {0,10}   (pool of {1}, shared across pipelines)" -f `
        $shape.IdsPerPipeline, $shape.IdPool)
    Write-Host ("  pipelines                 {0,10}   x concurrency {1} = {2} requests in flight" -f `
        $shape.Pipelines, $shape.Concurrency, $shape.InFlight)
    Write-Host ("  requests issued           {0,10}   = resource units (1 RU each)" -f $shape.TotalRequests)
    if ($shape.Clamped) {
        Write-Host ("  CLAMPED: -Count {0} exceeds {1} pipelines x {2} pool ids; the arm issues {3}." -f `
            $shape.Requested, $shape.Pipelines, $shape.IdPool, $shape.TotalRequests) -ForegroundColor Yellow
    }
    Write-Host ("  projected draw            {0,10} RU/s (from 180 RU/s per pipeline at 128)" -f `
        $shape.ExpectedRuPerSecond)
    Write-Host ("  time inside the allowance {0,10}s  then {1}s past it at {2} RU/s" -f `
        $shape.BurstSeconds, $shape.ThrottledSeconds, $shape.SustainedRuPerSecond)
    Write-Host ''
    if ($shape.Admissible) {
        Write-Host ("ADMISSIBLE: {0} RU/s over the {1} RU/s ceiling, and {2} of {3} estimated seconds spent past the allowance." -f `
            $shape.ExpectedRuPerSecond, $shape.SustainedRuPerSecond, $shape.ThrottledSeconds, $shape.EstimatedSeconds)
    }
    else {
        Write-Host 'INADMISSIBLE: this parameter set cannot reach the throttled regime.' -ForegroundColor Yellow
        foreach ($reason in $shape.Reasons) { Write-Host "  $reason" -ForegroundColor Yellow }
    }
}

if ($Plan) {
    Show-LoadShape
    Write-Host ''
    Write-Host '-Plan: nothing was run and no tenant was contacted.'
    return
}

if ($Mode -eq 'both') {
    Show-LoadShape
    Write-Host ''

    $resultFile = Join-Path $PSScriptRoot 'results/10-pacing-under-real-throttling.json'
    $arms = [ordered]@{}
    # The arms' own entries, kept whole beside the figures read out of them: this process starts
    # the two rounds and reads their files, and never imports the local module or connects to
    # anything, so the identity of the runs being compared is only in what they recorded.
    $armEntries = @()
    $roundIndex = 0
    foreach ($m in 'paced', 'unpaced') {
        $roundIndex++
        if ($roundIndex -gt 1) {
            # The bucket refills at roughly the sustained rate, so a full burst allowance is back
            # in ~30s. 300s is an order of magnitude over that, which is the margin worth paying
            # to be sure arm two is not measuring arm one's drained budget.
            Write-Host "cooldown ${CooldownSeconds}s (let the resource-unit budget refill)..."
            Start-Sleep -Seconds $CooldownSeconds
        }
        Write-Host "=== round: $m ===" -ForegroundColor Cyan
        # Start-Process rather than capturing stdout: each arm runs for minutes, and a round
        # whose output only appears once it has finished is a round you cannot tell from a hang.
        # The arm's own JSON record carries everything compared below, so nothing needs parsing
        # out of the console.
        $before = @(if (Test-Path $resultFile) { Get-Content $resultFile -Raw | ConvertFrom-Json }).Count
        $p = Start-Process pwsh -PassThru -Wait -NoNewWindow -ArgumentList `
            '-NoProfile', '-File', $PSCommandPath, '-Mode', $m, '-Count', $Count, `
            '-Pipelines', $Pipelines, '-Concurrency', $Concurrency, '-IdPool', $IdPool
        if ($p.ExitCode -ne 0) { throw "round '$m' failed (exit $($p.ExitCode))" }

        $entries = @(Get-Content $resultFile -Raw | ConvertFrom-Json)
        $entry = @($entries | Select-Object -Skip $before | Where-Object { $_.Result.Mode -eq $m } | Select-Object -Last 1)[0]
        if (-not $entry) { throw "round '$m' recorded no result in $resultFile" }
        $armEntries += $entry
        $r = $entry.Result
        $arms[$m] = @{
            Seconds         = [math]::Round($r.WallMs / 1000, 1)
            Ok              = [int]$r.Ok
            Failed          = [int]$r.Failed
            ThrottleRetries = [int]$r.ThrottleRetries
            RetryDelayMs    = [long]$r.RetryDelayMs
            PacingWaitMs    = [long]$r.PacingWaitMs
            ResourceUnits   = [long]$r.ResourceUnits
            RuPerSecond     = if ($r.WallMs -gt 0) { [math]::Round($r.ResourceUnits / ($r.WallMs / 1000), 1) } else { 0 }
            ReachedRegime   = [int]$r.ThrottleRetries -gt 0
        }
    }

    Write-Host ''
    Write-Host ("=== ADAPTIVE PACING AGAINST REAL THROTTLING ({0} reads, {1} x {2} in flight) ===" -f `
        $shape.TotalRequests, $shape.Pipelines, $shape.Concurrency)
    Write-Host ('{0,-28} {1,8} {2,8} {3,8} {4,10} {5,12} {6,10}' -f `
        'Arm', 'Wall', 'Ok', 'Failed', '429 retries', 'RetryDelay', 'RU/s')
    foreach ($k in 'paced', 'unpaced') {
        $a = $arms[$k]
        $label = if ($k -eq 'paced') { 'Adaptive pacing on (default)' } else { '-NoAdaptivePacing' }
        Write-Host ('{0,-28} {1,7:F1}s {2,8} {3,8} {4,10} {5,11:F1}s {6,10:F1}' -f `
            $label, $a.Seconds, $a.Ok, $a.Failed, $a.ThrottleRetries, ($a.RetryDelayMs / 1000), $a.RuPerSecond)
    }

    # Admissibility, stated per arm rather than inferred from the wall times: what each arm
    # actually drew against the rate this tenant was measured to keep serving at, and whether it
    # was ever refused. A paced arm that is never refused is the outcome pacing exists to
    # produce; an UNPACED arm that is never refused means the load never got there.
    Write-Host ''
    Write-Host ('=== ADMISSIBILITY (thresholds from {0}) ===' -f $threshold.Source)
    foreach ($k in 'paced', 'unpaced') {
        $a = $arms[$k]
        Write-Host ('  {0,-8} {1,8} RU at {2,7:F1} RU/s ({3,5:P0} of the {4} RU/s ceiling), {5} throttle retries - {6}' -f `
            $k, $a.ResourceUnits, $a.RuPerSecond, ($a.RuPerSecond / $threshold.Sustained),
            $threshold.Sustained, $a.ThrottleRetries,
            $(if ($a.ReachedRegime) { 'reached the throttled regime' } else { 'never refused' }))
    }

    # The verdict, and the conditions under which there is not one. An unpaced arm that was never
    # refused did not reach the regime this benchmark exists to measure, and the wall times above
    # it are then a comparison of two unthrottled runs - which is benchmark 14's question, already
    # answered there. Saying so is the difference between no result and a wrong one.
    Write-Host ''
    $conclusive = $arms.unpaced.ReachedRegime
    if (-not $conclusive) {
        Write-Host 'INCONCLUSIVE: the unpaced arm was never throttled, so there was nothing for pacing to avoid.' -ForegroundColor Yellow
        Write-Host ("  It spent {0} RU at {1} RU/s; the tenant serves {2} RU/s while refusing and allows {3} RU of burst." -f `
            $arms.unpaced.ResourceUnits, $arms.unpaced.RuPerSecond, $threshold.Sustained, $threshold.BurstRu) -ForegroundColor Yellow
        Write-Host '  Raise -Pipelines (offer a higher rate) and -Count with it, then re-run; -Plan checks a set first.' -ForegroundColor Yellow
    }
    else {
        $delta = $arms.unpaced.Seconds - $arms.paced.Seconds
        $verdict = if ($delta -gt 0) { "pacing finished {0:F1}s sooner" -f $delta }
                   elseif ($delta -lt 0) { "pacing finished {0:F1}s later" -f (-$delta) }
                   else { 'both arms finished level' }
        Write-Host ("Unpaced took {0} throttle retries and {1:F1}s honoring Retry-After; {2}." -f `
            $arms.unpaced.ThrottleRetries, ($arms.unpaced.RetryDelayMs / 1000), $verdict)
        if ($arms.unpaced.Failed -gt $arms.paced.Failed) {
            Write-Host ("Unpaced also lost {0} request(s) that pacing completed." -f `
                ($arms.unpaced.Failed - $arms.paced.Failed))
        }
    }

    Write-BenchResult -Benchmark '10-pacing-under-real-throttling' `
        -Identity (Get-BenchEntryIdentity -Entries $armEntries) -Result ([pscustomobject]@{
        Count       = $Count
        Pipelines   = $Pipelines
        Concurrency = $Concurrency
        Threshold   = $threshold
        # The load the arms were actually asked for and the arithmetic that said it could reach
        # the regime, recorded beside the verdict: a comparison is only readable against the
        # shape that produced it.
        Shape       = $shape
        Conclusive  = $conclusive
        Arms        = $arms
    })
    Write-Host 'both rounds complete - see results/10-pacing-under-real-throttling.json'
    return
}

# --- single arm ---------------------------------------------------------------------------

Show-LoadShape
Write-Host ''

Import-MgxLocal
Connect-MgxBenchmark

# A bounded id pool, shared: the tenant does not need 60,000 users for the workload to draw
# 60,000 units, because each pipeline may issue the whole pool once. A fixed pre-materialized
# list keeps the work bounded with no loop that can run away.
Write-Host "collecting up to $IdPool ids..."
$pool = Get-BenchUserIds -Count $IdPool
Write-Host "  got $($pool.Count) ids"

# The pool can come back short - a tenant with fewer users than -IdPool, or fewer seeded ones -
# and the shape is then not the one printed above. Recompute it against what the tenant gave.
if ($pool.Count -lt $IdPool) {
    $shape = Get-BenchPacingLoadShape -Count $Count -Pipelines $Pipelines -Concurrency $Concurrency `
        -IdPool $pool.Count -BurstRu $threshold.BurstRu -SustainedRuPerSecond $threshold.Sustained
    Write-Host ("  pool short of -IdPool $IdPool - the shape this arm will issue:") -ForegroundColor Yellow
    Show-LoadShape
    Write-Host ''
}

# The token-bucket limiter would cap us well under the RU budget and we would never reach the
# throttling regime this benchmark exists to measure. Lift it; the pacer (or its absence) is the
# variable under test. Both settings are process-wide statics, so the parallel runspaces below
# inherit them without setting them again.
Set-MgxOption -NoRateLimit
if ($Mode -eq 'unpaced') {
    Set-MgxOption -NoAdaptivePacing
    Write-Host '  adaptive pacing DISABLED'
} else {
    Write-Host '  adaptive pacing ON (2.1 default)'
}

# Distinct ids per pipeline, and the slicer refuses rather than hand back a slice that repeats
# one: Invoke-MgxRequest deduplicates its pipeline ids, so a repeat is a request this arm would
# report and never send - which is how the published figures came to describe a regime the load
# never entered.
$script:slices = Get-BenchPipelineSlice -Pool $pool -Pipelines $Pipelines -IdsPerPipeline $shape.IdsPerPipeline
Write-Host ("  $Pipelines pipelines x $($shape.IdsPerPipeline) distinct ids = $($shape.TotalRequests) requests")

$script:commonPath = Join-Path $PSScriptRoot 'common.ps1'
$script:armConcurrency = $Concurrency
$script:modulePath = (Get-Module Mgx).Path

$result = Measure-BenchPass -Name "reads $Mode" -Script {
    $tallies = $script:slices | ForEach-Object -Parallel {
        # A fresh runspace has no modules, but the assembly - and with it the pacer, the
        # telemetry collector and the HTTP client - loads once per PROCESS. Importing here
        # attaches this runspace to that shared state rather than creating a second copy, and
        # Connect-MgxBenchmark returns immediately because the Graph session is static too;
        # each runspace saying so is the confirmation that they share one pacer.
        #
        # Deliberately not Import-MgxLocal: it imports -Force, and eight runspaces force-loading
        # the same binary module at once is a race for no gain. The parent already imported it,
        # which fixed the Users-before-Mgx assembly order this only has to preserve.
        . $using:commonPath
        if (-not (Get-Module Microsoft.Graph.Users)) { Import-Module Microsoft.Graph.Users }
        Import-Module $using:modulePath
        Connect-MgxBenchmark | Out-Null
        $slice = $_
        $items = @($slice | Invoke-MgxRequest '/users/{id}' -Property id `
                      -Concurrency $using:armConcurrency -ErrorVariable errs -ErrorAction SilentlyContinue)
        [pscustomobject]@{ Ok = $items.Count; Failed = @($errs).Count }
    } -ThrottleLimit $Pipelines

    @{
        ok     = (@($tallies) | Measure-Object Ok -Sum).Sum
        failed = (@($tallies) | Measure-Object Failed -Sum).Sum
    }
}

$t = Get-MgxTelemetry
$wallSeconds = $result.ElapsedMs / 1000
Write-Host ''
Write-Host ("=== $Mode ===")
Write-Host ("  wall              {0,8:F1}s" -f $wallSeconds)
Write-Host ("  completed         {0,8}  failed {1}" -f $result.Output.ok, $result.Output.failed)
Write-Host ("  throttle retries  {0,8}" -f $t.ThrottleRetries)
Write-Host ("  retry delay       {0,8:F1}s  (time spent honoring Retry-After)" -f ($t.RetryDelayMs / 1000))
Write-Host ("  pacing wait       {0,8:F1}s  over {1} activations" -f ($t.AdaptivePacingWaitMs / 1000), $t.AdaptivePacingActivations)
Write-Host ("  resource units    {0,8}   ({1:F1} RU/s)" -f $t.ResourceUnitsConsumed, ($t.ResourceUnitsConsumed / $wallSeconds))
Write-Host ("  pacing state      {0}" -f $t.PacingState)

# The admissibility check for this arm on its own, in the same terms the plan stated: what it
# drew against the rate the tenant was measured to keep serving at, and whether it was refused.
$drawn = if ($wallSeconds -gt 0) { $t.ResourceUnitsConsumed / $wallSeconds } else { 0 }
Write-Host ("  admissibility     {0,8:F0} RU/s of the {1} RU/s ceiling, {2} refusal(s) - {3}" -f `
    $drawn, $threshold.Sustained, $t.ThrottleRetries,
    $(if ($t.ThrottleRetries -gt 0) { 'reached the throttled regime' } else { 'never refused' }))
Write-Host ("  issued            {0,8} of {1} planned" -f $result.Output.ok, $shape.TotalRequests)

if ($Mode -eq 'unpaced' -and $t.ThrottleRetries -eq 0) {
    Write-Host ''
    Write-Host ("  NOTE: no refusal at {0:F0} RU/s over {1} units, against a {2} RU/s ceiling and {3} RU of" -f `
        $drawn, $t.ResourceUnitsConsumed, $threshold.Sustained, $threshold.BurstRu) -ForegroundColor Yellow
    Write-Host ("  burst. This arm did not reach the throttled regime, so it sets no baseline for the") -ForegroundColor Yellow
    Write-Host ("  paced arm to beat. Raise -Pipelines; -Plan checks a set without spending a run on it.") -ForegroundColor Yellow
}

# WallMs is read back by Write-BenchResult for the RU rate: telemetry's own elapsed total is the
# SUM of per-request durations and would understate the budget draw by the concurrency factor.
# ThrottleRetries, RetryDelayMs and ResourceUnits are duplicated out of telemetry into the
# result itself: the parent process reads this record back to compare the arms, and it never
# loads Mgx, so it has no telemetry of its own to read them from.
Write-BenchResult -Benchmark '10-pacing-under-real-throttling' -Result ([pscustomobject]@{
    Mode            = $Mode
    Count           = $Count
    Pipelines       = $Pipelines
    Concurrency     = $Concurrency
    # The shape this arm issued and the verdict on whether it reached the regime, so the entry
    # is readable on its own rather than only through the summary the parent writes.
    Shape           = $shape
    ReachedRegime   = $t.ThrottleRetries -gt 0
    WallMs          = $result.ElapsedMs
    Ok              = $result.Output.ok
    Failed          = $result.Output.failed
    ThrottleRetries = $t.ThrottleRetries
    RetryDelayMs    = $t.RetryDelayMs
    PacingWaitMs    = $t.AdaptivePacingWaitMs
    ResourceUnits   = $t.ResourceUnitsConsumed
}) -WallMs ([long]$result.ElapsedMs)
