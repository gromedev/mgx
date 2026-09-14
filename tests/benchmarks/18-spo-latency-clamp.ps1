# Benchmark 18: does SPO clamp a drive workload by latency instead of by 429?
#
# After heavy traffic a SharePoint-backed drive has been seen to serve the same GET five times
# slower or worse for about an hour, with no 429 anywhere in the run and no throttle header on
# any response. Nothing in the pacer reacts to that: the AIMD cap is armed by a 429, the
# proximity damping by x-ms-throttle-limit-percentage, and a clamp shows neither. The pacer
# already keeps a slow EMA of drive latency and reports the ratio in Get-MgxTelemetry's
# PacingState - telemetry only, deliberately not a pacing input (AdaptiveRequestPacer
# RecordLatency). Whether it should become one is a measurement, and this is the measurement.
#
# So the recorded metric is NOT throughput. It is the latency distribution over the window,
# per five-minute bucket, beside the throttle signals present or absent on each call: the
# resource unit, the throttle-limit percentage, the throttle scope, Retry-After, and whether
# any RateLimit-* header appeared at all. A clamp is a run whose late buckets sit several times
# the early ones on p90 while the throttle-signal columns stay empty. The absence of the
# signals is half the finding, which is why every call records them as absent rather than the
# summary reporting only the ones that showed up.
#
# What is recorded per call, and from where. Two figures, never conflated:
#   - ElapsedMs is the HTTP round trip, read from the request tracer's own response line
#     ("[Mgx] Response: 200 OK in 41 ms"). This is the latency the clamp is about.
#   - CallElapsedMs is the whole Invoke-MgxRequest call, which includes the pacer's proactive
#     wait and any Retry-After sleep. PacingWaitMs, from the pacer's verbose line, says how
#     much of the gap is mgx's own. Reading a paced call's wall time as SPO latency would
#     manufacture a clamp out of the client's own back-off.
# The headers come from the same trace: the tracer keeps Retry-After, x-ms-resource-unit and
# every RateLimit-* and x-ms-throttle-* header, which is exactly the set this measurement
# needs. It is on when $DebugPreference is Continue, which the workers set - the -Debug switch
# itself asks ShouldContinue and would stall a headless run. No raw HttpClient probe is needed
# and none is taken: the subject is mgx under pacing, not a bare socket, and a parallel probe
# would be a different request on a different connection answered under a different queue.
# Get-MgxTelemetry supplies the run-level counters (resource units, throttle retries, pacing
# wait, PacingState with the drive bucket's latency ratio), sampled once per bucket - those are
# process-wide totals, so they describe the window and never one call.
#
# Read-only throughout: GET only, against the drive the operator names. There is no default
# drive, because a default is how a benchmark ends up pointed at production. The load itself is
# heavy by design - $Concurrency workers issuing back-to-back single-item GETs for $Minutes -
# and heavy read traffic against a drive is what the clamp is reported to follow, so it draws
# on the tenant's budget and may leave the drive slow for other callers for the following hour.
# That is the point of the experiment and the reason it wants an uncontended machine, a window
# longer than the hour the clamp is said to last, and a tenant nobody is working in.
#
# Rows checkpoint to results/18-spo-latency-clamp.rows.jsonl as they arrive, flushed per line,
# so a run killed at minute 50 leaves the fifty minutes it collected. The summary is written
# only by a completed window: a partial summary in the results log would be promoted as a
# baseline by the next -RecordBaseline, and half a window is not a distribution.
#
# The short smoke, which must run end to end before a long run is worth starting:
#   ./18-spo-latency-clamp.ps1 -DriveId <driveId> -Minutes 2 -Concurrency 2 `
#       -PrecedingActivity 'smoke, drive idle'
# It writes rows and a summary like the long form, on the same paths.
param(
    # The drive to measure. One of the two, and no default: -DriveId b!xxxx, or the same drive
    # as a URI in the /drives/<id> shape. Anything else is refused rather than guessed at.
    [string] $DriveId,
    [string] $DriveUri,
    # Longer than the hour the clamp is reported to hold, so a default run outlasts it.
    [int] $Minutes = 75,
    [int] $Concurrency = 8,
    [int] $BucketMinutes = 5,
    # How many item ids to cycle through. One item repeatedly would measure that item's cache.
    [int] $ItemPoolSize = 200,
    # What the drive had just been through, in the operator's words ("2h export at c=64",
    # "idle overnight"). The distribution means nothing without it, so it is recorded verbatim.
    [string] $PrecedingActivity = ''
)

. "$PSScriptRoot/common.ps1"

# Get-ClampPercentile and the rest of the summary - buckets, totals, refusals-by-kind, the
# headline - live in common.ps1, dot-sourced above: they read the checkpointed rows file back
# rather than trust a live accumulator, and a Pester test calls the identical functions over a
# synthetic rows.jsonl. See the comment above Get-ClampSummary in common.ps1.

# --- Preconditions: refuse, never measure nothing quietly -----------------------------------

if ($Minutes -le 0)       { throw "18-spo-latency-clamp: -Minutes is $Minutes; a window has to be at least a minute long." }
if ($Concurrency -le 0)   { throw "18-spo-latency-clamp: -Concurrency is $Concurrency; a paced load needs at least one worker." }
if ($BucketMinutes -le 0) { throw "18-spo-latency-clamp: -BucketMinutes is $BucketMinutes; a bucket has to span at least a minute." }
if ($ItemPoolSize -le 0)  { throw "18-spo-latency-clamp: -ItemPoolSize is $ItemPoolSize; the load needs at least one item to read." }

$drivePath = $null
if ($DriveId) {
    if ($DriveId -notmatch '^[^/?#\s]+$') {
        throw "18-spo-latency-clamp: -DriveId '$DriveId' is not a drive id. Pass the id alone, or pass the whole thing as -DriveUri /drives/<id>."
    }
    $drivePath = "/drives/$DriveId"
}
if ($DriveUri) {
    if ($DriveUri -notmatch '^/drives/[^/?#\s]+$') {
        throw "18-spo-latency-clamp: -DriveUri '$DriveUri' is not in the /drives/<id> shape."
    }
    if ($drivePath -and $drivePath -ne $DriveUri) {
        throw "18-spo-latency-clamp: -DriveId and -DriveUri name different drives ('$drivePath' and '$DriveUri'). Pass one."
    }
    $drivePath = $DriveUri
}
if (-not $drivePath) {
    throw '18-spo-latency-clamp: name the drive to measure - -DriveId <id> or -DriveUri /drives/<id>. There is no default: a default drive is how a read benchmark ends up pointed at production.'
}

Import-MgxLocal
Connect-MgxBenchmark

# One probe GET. Every reason this run cannot happen is named here, before anything is
# measured: a drive that does not answer, a tenant with no SharePoint at all, a session whose
# token carries no Files/Sites read.
$probe = $null
$probeSw = [Diagnostics.Stopwatch]::StartNew()
try {
    $probe = Invoke-MgxRequest -Uri $drivePath -Property id, driveType, name -ErrorAction Stop
}
catch {
    $ex = $_.Exception
    $status = 0
    if ($null -ne $ex.StatusCode) { try { $status = [int]$ex.StatusCode } catch { $status = 0 } }
    $text = "$($ex.Message)"

    # The documented shape of a tenant with no SharePoint: every sites/drives call answers
    # BadRequest, and only the message says why. No permission grant changes it.
    if ($text -match 'SPO license') {
        throw "18-spo-latency-clamp: the tenant reports no SPO license, so $drivePath cannot be measured on it - '$text'. This benchmark needs a tenant with SharePoint; no role grant substitutes for the license."
    }
    if ($status -eq 401 -or $status -eq 403) {
        throw "18-spo-latency-clamp: the session cannot read $drivePath (HTTP $status - '$text'). Connect with Files.Read.All or Sites.Read.All and try again."
    }
    throw "18-spo-latency-clamp: $drivePath did not answer a probe GET$(if ($status) { " (HTTP $status)" }) - '$text'. Nothing was measured."
}
$probeSw.Stop()
if (-not $probe -or -not $probe.id) {
    throw "18-spo-latency-clamp: $drivePath answered a probe GET without a drive id, so there is no drive there to measure."
}
Write-Host ("drive {0} ({1}) answered a probe in {2} ms" -f $probe.id, ($probe.driveType ?? 'unknown type'), $probeSw.ElapsedMilliseconds)

# The item pool. One item read over and over measures a cache; a pool the size of a page does
# not. A drive with no children at root is still measurable - /root is a drive item too - so
# an empty listing narrows the load rather than refusing it.
$items = @()
try {
    $items = @(Invoke-MgxRequest -Uri "$drivePath/root/children" -Property id -Top $ItemPoolSize -ErrorAction Stop |
        ForEach-Object { $_.id } | Where-Object { $_ })
}
catch {
    Write-Warning "listing $drivePath/root/children failed ('$($_.Exception.Message)') - the load falls back to /root."
}
$targets = if ($items.Count -gt 0) {
    @($items | Select-Object -First $ItemPoolSize | ForEach-Object { [pscustomobject]@{ Label = 'item'; Uri = "$drivePath/items/$_" } })
} else {
    @([pscustomobject]@{ Label = 'root'; Uri = "$drivePath/root" })
}
Write-Host ("load reads {0} target(s) in rotation, {1} worker(s), for {2} minute(s)" -f $targets.Count, $Concurrency, $Minutes)
if ($PrecedingActivity) { Write-Host "preceding activity, as stated: $PrecedingActivity" }
else { Write-Warning 'no -PrecedingActivity given; the distribution is uninterpretable without knowing what the drive had just been through.' }

# --- The load -------------------------------------------------------------------------------

$resultsDir = Join-Path $PSScriptRoot 'results'
if (-not (Test-Path $resultsDir)) { New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null }
$rowsPath = Join-Path $resultsDir '18-spo-latency-clamp.rows.jsonl'

$modulePath = Join-Path $PSScriptRoot '../../module/mgx.psd1'
if (-not (Test-Path $modulePath)) { $modulePath = 'Mgx' }
$startedAt = Get-Date
$deadline = [datetime]::UtcNow.AddMinutes($Minutes)

# A rolling figure for the live progress line only - the real summary is read back from
# $rowsPath by Get-ClampSummary once the load below finishes, never from this list.
$liveLatencies = [System.Collections.Generic.List[double]]::new()
$attempts = 0
$pacingSamples = @()
$lastBucket = -1

# AutoFlush, append: the file is the checkpoint. A run that dies at minute 50 has minute 50 on
# disk, not a buffer that never reached it.
$writer = [System.IO.StreamWriter]::new($rowsPath, $true, [System.Text.UTF8Encoding]::new($false))
$writer.AutoFlush = $true
$loadSw = [Diagnostics.Stopwatch]::StartNew()
try {
    1..$Concurrency | ForEach-Object -ThrottleLimit $Concurrency -Parallel {
        $worker = $_
        $deadline = $using:deadline
        $targets = $using:targets
        $modulePath = $using:modulePath

        # Staggered: eight runspaces importing the same binary module in the same millisecond is
        # a race nobody needs, and the ramp is seconds against a window of an hour or more.
        Start-Sleep -Milliseconds (($worker - 1) * 400)
        $ErrorActionPreference = 'Stop'
        Import-Module $modulePath -ErrorAction Stop
        # The request tracer, which is where the response headers come from, is on whenever
        # DebugPreference is not silent. The -Debug switch asks ShouldContinue on a compiled
        # cmdlet and would block a headless run; the preference does not.
        $DebugPreference = 'Continue'
        $VerbosePreference = 'Continue'

        $index = 0
        while ([datetime]::UtcNow -lt $deadline) {
            $target = $targets[$index % $targets.Count]
            $index++
            $sentAt = (Get-Date).ToString('o')
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $streams = $null
            $failure = $null
            try {
                $streams = Invoke-MgxRequest -Uri $target.Uri -Property id -ErrorAction Stop 5>&1 4>&1
            }
            catch {
                $failure = "$($_.Exception.Message)"
            }
            $sw.Stop()

            # One row per attempt on the wire: a call retried past a 429 met the service twice,
            # and the second meeting has its own latency and its own headers.
            $traces = @()
            $pacingWaitMs = $null
            foreach ($record in @($streams)) {
                if ($record -is [System.Management.Automation.VerboseRecord]) {
                    if ("$($record.Message)" -match 'Adaptive pacing: waited (\d+)ms') {
                        $pacingWaitMs = [int]$Matches[1]
                    }
                    continue
                }
                if ($record -isnot [System.Management.Automation.DebugRecord]) { continue }
                $lines = "$($record.Message)" -split "`r?`n"
                if ($lines[0] -notmatch '^\[Mgx\] Response: (\d+) .* in (\d+) ms') { continue }
                $trace = [ordered]@{
                    Status                  = [int]$Matches[1]
                    ElapsedMs               = [int]$Matches[2]
                    ResourceUnit            = $null
                    ThrottleLimitPercentage = $null
                    ThrottleScope           = $null
                    RetryAfter              = $null
                    RateLimitHeader         = $false
                }
                foreach ($line in $lines) {
                    # The body follows the header block and is not indented; a four-space indent
                    # is a header and nothing else is.
                    if ($line -match '^  Body \(') { break }
                    if ($line -notmatch '^    ([^:]+):\s*(.*)$') { continue }
                    $name = $Matches[1].Trim(); $value = $Matches[2].Trim()
                    switch -Regex ($name) {
                        '^(?i)x-ms-resource-unit$'            { $trace.ResourceUnit = $value }
                        '^(?i)x-ms-throttle-limit-percentage$' { $trace.ThrottleLimitPercentage = $value }
                        '^(?i)x-ms-throttle-scope$'           { $trace.ThrottleScope = $value }
                        '^(?i)Retry-After$'                   { $trace.RetryAfter = $value }
                        '^(?i)RateLimit-'                     { $trace.RateLimitHeader = $true }
                    }
                }
                $traces += , $trace
            }

            if ($traces.Count -eq 0) {
                # No response reached the tracer: a transport failure, or a refusal raised before
                # the request went out. Recorded, never dropped - a window with a hole in it is
                # not a distribution, and a silent drop is how one gets a hole.
                [pscustomobject]@{
                    Timestamp = $sentAt; Worker = $worker; Label = $target.Label; Uri = $target.Uri
                    Attempt = 1; Attempts = 1; Status = 0; ElapsedMs = $null
                    CallElapsedMs = [int]$sw.ElapsedMilliseconds; PacingWaitMs = $pacingWaitMs
                    ResourceUnit = $null; ThrottleLimitPercentage = $null; ThrottleScope = $null
                    RetryAfter = $null; RateLimitHeader = $false
                    Error = $(if ($failure) { $failure } else { 'no response traced' })
                }
                continue
            }

            $n = 0
            foreach ($trace in $traces) {
                $n++
                [pscustomobject]@{
                    Timestamp = $sentAt; Worker = $worker; Label = $target.Label; Uri = $target.Uri
                    Attempt = $n; Attempts = $traces.Count; Status = $trace.Status; ElapsedMs = $trace.ElapsedMs
                    CallElapsedMs = [int]$sw.ElapsedMilliseconds; PacingWaitMs = $pacingWaitMs
                    ResourceUnit = $trace.ResourceUnit
                    ThrottleLimitPercentage = $trace.ThrottleLimitPercentage
                    ThrottleScope = $trace.ThrottleScope
                    RetryAfter = $trace.RetryAfter
                    RateLimitHeader = $trace.RateLimitHeader
                    Error = $(if ($failure -and $n -eq $traces.Count) { $failure } else { $null })
                }
            }
        }
    } | ForEach-Object {
        $row = $_
        $writer.WriteLine((ConvertTo-Json -InputObject $row -Compress -Depth 4))
        $attempts++
        if ($null -ne $row.ElapsedMs -and $row.Status -ge 200 -and $row.Status -lt 300) {
            $liveLatencies.Add([double]$row.ElapsedMs)
        }

        # Clamped with a comparison, not [math]::Max: Max(0, <double>) binds the int overload
        # and rounds the minute away before the floor sees it, which halves the first bucket
        # and invents a last one.
        $minutesIn = ((Get-Date $row.Timestamp) - $startedAt).TotalMinutes
        if ($minutesIn -lt 0) { $minutesIn = 0 }
        $index = [int][math]::Floor($minutesIn / $BucketMinutes)

        # Once per bucket, not once per row: these are process-wide totals, and the point of
        # sampling them on the clock is to see the drive bucket's latency ratio move. Forward
        # only: rows arrive from every worker at once and their timestamps interleave across a
        # boundary, so a late row from the previous bucket must not re-sample. The p90 printed
        # here is $liveLatencies' rolling figure, for this progress line only - the summary
        # Write-BenchResult records below is read back from $rowsPath, not from it.
        if ($index -gt $lastBucket) {
            $lastBucket = $index
            $t = $null
            try { $t = Get-MgxTelemetry -ErrorAction Stop } catch { $t = $null }
            if ($t) {
                $pacingSamples += [pscustomobject]@{
                    AtMinute               = $index * $BucketMinutes
                    Requests               = $t.Requests
                    ResourceUnits          = $t.ResourceUnitsConsumed
                    ThrottleRetries        = $t.ThrottleRetries
                    PacingWaitMs           = $t.AdaptivePacingWaitMs
                    PacingActivations      = $t.AdaptivePacingActivations
                    LastThrottlePercentage = $t.LastThrottlePercentage
                    DrivePacingState       = @($t.PacingState -split ';' | Where-Object { $_ -match '^\s*drive:' } |
                                               ForEach-Object { $_.Trim() })[0]
                }
            }
            Write-Host ("  minute {0,4}: {1} attempt(s) so far, p90 {2} ms" -f `
                ($index * $BucketMinutes), $attempts, (Get-ClampPercentile -Values $liveLatencies -Percentile 90))
        }
    }
}
finally {
    $writer.Flush()
    $writer.Dispose()
    $loadSw.Stop()
}
Write-Host ("rows written to {0} ({1} attempt row(s))" -f $rowsPath, $attempts)

# --- Report ---------------------------------------------------------------------------------
#
# Read back from $rowsPath rather than carried over from the loop above: the checkpoint is the
# one artifact a killed run still has, and a summary built from anything else can disagree with
# what re-running Get-ClampSummary against the same file would say. It is also what the Pester
# cases over this benchmark call, so the table below and the assertions over it read the same
# computation, not two implementations of it.

$endedAt = Get-Date
$wallMs = [long]$loadSw.ElapsedMilliseconds

Write-Host ''
Write-Host '=== SPO LATENCY CLAMP (paced drive reads, read-only) ==='
Write-Host ("drive {0}, {1} worker(s), {2} target(s)" -f $drivePath, $Concurrency, $targets.Count)
# Both clocks, always: a Stopwatch that lost time to a suspended machine only shows up next to
# the wall clock it should agree with, never in either figure alone.
Write-Host ("window measured: {0:F1} minute(s) wall clock (StartedAt to EndedAt), {1:F1} minute(s) stopwatch" -f `
    ($endedAt - $startedAt).TotalMinutes, ($wallMs / 60000.0))
Write-Host ("preceding activity: {0}" -f $(if ($PrecedingActivity) { $PrecedingActivity } else { 'NOT STATED' }))

$summary = Get-ClampSummary -RowsPath $rowsPath -StartedAt $startedAt -EndedAt $endedAt `
    -WallMs $wallMs -Minutes $Minutes -BucketMinutes $BucketMinutes

if ($summary.Refused) {
    # Named in $summary.RefusalReason, printed verbatim: rows are already safe on disk (written
    # above, independent of this check), but a distribution computed across a gap the machine
    # was not awake for is not a distribution, and nothing is appended to the results log for a
    # later -RecordBaseline to pick up.
    Write-Host $summary.RefusalReason
    exit 1
}

$bucketRows = $summary.Buckets
$overall = $summary.Overall

Write-Host ('{0,7} {1,8} {2,8} {3,8} {4,8} {5,6} {6,7} {7,7} {8,7} {9,7} {10,7}' -f `
    'minute', 'calls', 'p50', 'p90', 'p99', '429s', 'errors', 'pct', 'scope', 'retry', 'ratelim')
foreach ($b in $bucketRows) {
    $mark = if ($b.Note) { "  <- $($b.Note)" } else { '' }
    Write-Host ('{0,7} {1,8} {2,7}ms {3,7}ms {4,7}ms {5,6} {6,7} {7,7} {8,7} {9,7} {10,7}{11}' -f `
        $b.FromMinute, $b.Calls, ($b.P50ElapsedMs ?? '-'), ($b.P90ElapsedMs ?? '-'), ($b.P99ElapsedMs ?? '-'),
        $b.Throttled429, $b.Errors, $b.ThrottlePercentageSeen, $b.ThrottleScopeSeen, $b.RetryAfterSeen, $b.RateLimitSeen, $mark)
}
Write-Host ''
Write-Host $summary.Headline.Sentence
if ($overall.Refusals.Count -gt 0) {
    Write-Host 'refusals by kind:'
    foreach ($kind in $overall.Refusals.Keys) {
        Write-Host ("  {0}: {1}" -f $kind, $overall.Refusals[$kind])
    }
}
if ($overall.Throttled429 -eq 0 -and $overall.ThrottlePercentageSeen -eq 0 -and $overall.ThrottleScopeSeen -eq 0 -and $overall.RateLimitSeen -eq 0) {
    Write-Host 'no 429 and no throttle-signal header anywhere in the window: whatever the latency did, nothing in the response said so.'
}
else {
    Write-Host ("throttle signals present: {0} 429(s), {1} percentage header(s), {2} scope header(s), {3} RateLimit-* header(s)" -f `
        $overall.Throttled429, $overall.ThrottlePercentageSeen, $overall.ThrottleScopeSeen, $overall.RateLimitSeen)
}

$telemetry = $null
try { $telemetry = Get-MgxTelemetry -ErrorAction Stop } catch { $telemetry = $null }
# Labeled, and never the headline above: PacingState is the pacer's one most-recent latency
# sample, which is exactly the figure that used to be read as this benchmark's verdict.
if ($telemetry?.PacingState) { Write-Host "last pacer sample: $($telemetry.PacingState)" }
foreach ($sample in $pacingSamples) {
    if ($sample.DrivePacingState) { Write-Host ("  minute {0,4}: {1}" -f $sample.AtMinute, $sample.DrivePacingState) }
}

$result = [ordered]@{
    DriveUri          = $drivePath
    DriveType         = $probe.driveType
    Minutes           = $Minutes
    Concurrency       = $Concurrency
    BucketMinutes     = $BucketMinutes
    Targets           = $targets.Count
    StartedAt         = $startedAt.ToString('o')
    EndedAt           = $endedAt.ToString('o')
    PrecedingActivity = $PrecedingActivity
    # Recorded, not assumed: an unpaced run measures a different thing and must not be read
    # against a paced baseline without the difference being visible in the entry.
    AdaptivePacing    = -not (Get-MgxOption).NoAdaptivePacing
    RowsFile          = '18-spo-latency-clamp.rows.jsonl'
    ProbeElapsedMs    = [int]$probeSw.ElapsedMilliseconds
    Overall           = $overall
    # The verdict, taken from the buckets - see Get-ClampHeadline in common.ps1. PacingState
    # below is the pacer's own last snapshot, kept for diagnosis and never read as this.
    Headline          = $summary.Headline.Sentence
    Buckets           = $bucketRows
    PacingSamples     = $pacingSamples
    PacingState       = $telemetry?.PacingState
    WallMs            = $wallMs
}

Write-BenchResult -Benchmark '18-spo-latency-clamp' -Result $result -WallMs $wallMs
