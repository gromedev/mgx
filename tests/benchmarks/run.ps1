# Runs the Mgx benchmark suite. Each benchmark executes in a fresh pwsh process so
# Mgx's static state (rate limiter, adapted pacing, circuit breaker) never leaks
# between benchmarks. Order matters: read benchmarks run first; the two
# throttle-provoking write benchmarks (07, 04) run last with a cooldown, so they
# cannot poison the read numbers.
# Usage:
#   ./run.ps1                     # everything except the slow ones
#   ./run.ps1 -IncludeSlow        # everything, including 04 full baselines (~2h)
#   ./run.ps1 -Only 01,05         # just those benchmarks
#   ./run.ps1 -RecordBaseline     # pin what the run produced into baseline.json
#   ./run.ps1 -CompareBaseline    # read the run against the pinned baseline.json
param(
    [string[]] $Only,
    [switch] $IncludeSlow,
    [int] $CooldownSeconds = 300,
    [switch] $RecordBaseline,
    [switch] $CompareBaseline
)

$ErrorActionPreference = 'Stop'
# Export-BenchBaseline, Compare-BenchBaseline and the table of headline metrics they share live
# here. It is also what forces invariant culture, so the delta lines below print 12.5% and
# never 12,5%.
. (Join-Path $PSScriptRoot 'common.ps1')

$logDir = Join-Path $PSScriptRoot 'results/logs'
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }

# name, script, extra args, needsCooldownAfter
$suite = @(
    @{ id = '06'; script = '06-fault-gauntlet.ps1';  args = @();                 cooldown = $false },
    @{ id = '01'; script = '01-list-users.ps1';      args = @();                 cooldown = $false },
    @{ id = '05'; script = '05-memory-export.ps1';   args = @();                 cooldown = $false },
    @{ id = '02'; script = '02-fanout-lookup.ps1';   args = @();                 cooldown = $false },
    @{ id = '03'; script = '03-user-report.ps1';     args = @();                 cooldown = $false },
    @{ id = '08'; script = '08-delta-sync.ps1';      args = @();                 cooldown = $false },
    @{ id = '09'; script = '09-kill-resume.ps1';     args = @();                 cooldown = $false },
    @{ id = '07'; script = '07-adaptive-pacing.ps1'; args = @();                 cooldown = $true },
    @{ id = '04'; script = '04-batch-create.ps1';    args = $(if ($IncludeSlow) { @() } else { @('-SkipBaselines') }); cooldown = $true },
    # Last, and after a cooldown: it drives the tenant toward its resource-unit budget on
    # purpose, so it must not color any earlier benchmark's numbers.
    @{ id = '10'; script = '10-pacing-under-real-throttling.ps1'; args = @(); cooldown = $false }
)

$promoteOnly = @()
if ($Only) {
    # pwsh -File passes "05,02" as one literal string - split so both call styles work
    $Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
    $suite = @($suite | Where-Object { $Only -contains $_.id })

    # An id Get-BenchScenario knows the metrics for but that never runs inside this suite -
    # 18, a live-tenant scenario driven standalone through -DriveId/-DriveUri - is not the
    # same thing as an id nobody has ever heard of. Without -RecordBaseline it is refused by
    # name rather than folded into the generic "no benchmarks matched"; with it, it is
    # accepted and promoted below from its own latest recorded result, never run here.
    $matchedIds = @($suite | ForEach-Object { $_.id })
    $unmatched = @($Only | Where-Object { $matchedIds -notcontains $_ })
    if ($unmatched.Count -gt 0) {
        $standalone = @(Get-BenchScenario -Id $unmatched -WarningAction SilentlyContinue)
        $promoteOnly = @($standalone | ForEach-Object { $_.Id })
        if ($promoteOnly.Count -gt 0 -and -not $RecordBaseline) {
            $sentences = $standalone | ForEach-Object {
                "$($_.Id) is not in the suite; run tests/benchmarks/$($_.Script) directly, then promote it with -RecordBaseline -Only $($_.Id)."
            }
            throw ($sentences -join ' ')
        }
    }
}
if ($suite.Count -eq 0 -and $promoteOnly.Count -eq 0) { throw "no benchmarks matched -Only $($Only -join ',')" }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
# Both the promotion and the comparison read results/<benchmark>.json back, which is an
# append-only log: without a floor they would read the newest entry a benchmark ever wrote,
# and a benchmark that died before recording would be pinned or compared from a run that
# happened last month.
$runStart = Get-Date
$outcomes = @()
foreach ($b in $suite) {
    $log = Join-Path $logDir "$($b.id)-$stamp.log"
    Write-Host ''
    Write-Host ("### running {0} -> {1}" -f $b.script, $log)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot $b.script) @($b.args) 2>&1 | Tee-Object -FilePath $log
    $exit = $LASTEXITCODE
    $sw.Stop()
    $outcomes += [pscustomobject]@{ Benchmark = $b.id; ExitCode = $exit; Minutes = [math]::Round($sw.Elapsed.TotalMinutes, 1) }
    if ($exit -ne 0) { Write-Warning "$($b.script) exited $exit - continuing" }
    if ($b.cooldown) {
        Write-Host "cooldown ${CooldownSeconds}s before next benchmark..."
        Start-Sleep -Seconds $CooldownSeconds
    }
}

Write-Host ''
Write-Host '=== SUITE COMPLETE ==='
$outcomes | Format-Table -AutoSize
Write-Host "per-benchmark results in $(Join-Path $PSScriptRoot 'results')"

$ran = @($suite | ForEach-Object { $_.id })

if ($RecordBaseline) {
    # The seven scenarios the release baseline is made of. A full run also executes 03, 06 and
    # 07, which are read on their own merits rather than pinned - naming one with -Only pins it
    # anyway, which is how the no-tenant gauntlet gets into a baseline on a laptop.
    $baselineDefault = @('01', '02', '04', '05', '08', '09', '10')
    $promote = if ($Only) { $ran } else { @($ran | Where-Object { $baselineDefault -contains $_ }) }
    Write-Host ''
    Write-Host '=== RECORDING BASELINE ==='
    if ($promote.Count -eq 0 -and $promoteOnly.Count -eq 0) {
        Write-Warning "none of the benchmarks this run executed are baseline scenarios ($($baselineDefault -join ', ')) - nothing promoted."
    }
    else {
        if ($promote.Count -gt 0) { Export-BenchBaseline -Benchmark $promote -Since $runStart }
        # $promoteOnly never ran in this invocation, so there is no fresh entry to floor with
        # -Since $runStart - it promotes from whatever its own standalone run last recorded,
        # however long before this call that was.
        if ($promoteOnly.Count -gt 0) { Export-BenchBaseline -Benchmark $promoteOnly }
    }
}

if ($CompareBaseline) {
    # Reports, never gates: exit code is the suite's, not the comparison's.
    $null = Compare-BenchBaseline -Benchmark $ran -Since $runStart
}
