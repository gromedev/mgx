# Benchmark 16: the pathological class. No tenant required.
#
# 06 measures the mock's default schedule - a fault an entity meets once or twice and then
# never again, which is what ordinary Graph throttling looks like. This one measures the
# faults that do not go away by themselves. Each is posted to the mock as a fault plan
# (POST /plan) before its contenders run, and cleared after:
#
#   storm       every request 429s with Retry-After for -StormSeconds, then the storm ends
#   outage      every attempt 503s, for as long as the scenario lasts; the contender has to
#               give up, and how long it takes to admit that is the measurement
#   abort       the collection's body is killed halfway through, connection dropped
#   visibility  a resource 404s for -VisibilitySeconds after it is first asked for and then
#               appears - the read-after-create shape, where nothing in either stack retries
#               a 404 and the caller is the one that has to poll
#
# The same four contenders as 06, in 06's order: a naive Invoke-RestMethod loop with no
# retry, the bare Graph SDK, the SDK with Enable-MgxResilience injected, and Mgx. Each runs
# in its own child pwsh under common.ps1's watchdog, which buys two things: a hang is killed
# and recorded as Hung instead of stalling the run (two of these scenarios exist to produce
# one), and "bare SDK" means a process Mgx was never loaded into, so its Telemetry is null
# because there is none rather than because it read zero. The server's /reset runs before
# every contender, so a storm or an outage lands on the same request ordinal for each of
# them, and its /stats delta is recorded per contender beside what the contender itself saw.
#
# The abort scenario asks for one large page rather than a long paged enumeration, because
# the mock speaks http and Mgx refuses to follow an @odata.nextLink that is not https
# (NextLinkValidator, an SSRF guard): a paged enumeration is a fault the Mgx arm would never
# reach, and a row reading "25 of 1000" against contenders that met the fault is worse than
# no row. One page every contender does read, cut in the middle, measures the same thing.
#
# Standalone: not in run.ps1, like 11-15. Next-link cycles are out of scope until the
# detection they exercise exists.
param(
    [int] $N = 200,
    [int] $Port = 8787,
    # Storm and visibility are wall-clock windows, so they bound their scenario whatever the
    # item count is. A window of 0 is not a short window, it is no window - the title would
    # read "throttled for 0s" or "404s for 0s" and the plan would have nothing to arm.
    [ValidateRange(1, [int]::MaxValue)] [int] $StormSeconds = 15,
    [ValidateRange(1, [int]::MaxValue)] [int] $VisibilitySeconds = 8,
    [int] $VisibilityBudgetSeconds = 40,
    [int] $PollMs = 500,
    # Under the outage every attempt fails and every failure costs a contender its whole
    # retry budget, so that scenario is measured on a handful of items rather than on N.
    [int] $OutageItems = 10,
    # One page big enough that half of it is a body worth dying in the middle of.
    [int] $PageSize = 1000,
    [int] $StallSeconds = 120,
    # child-mode plumbing (internal)
    [ValidateSet('', 'storm', 'outage', 'abort', 'visibility')] [string] $Scenario = '',
    [ValidateSet('', 'naive', 'sdk', 'sdkMgx', 'mgx')] [string] $Contender = '',
    [string] $Base,
    [string] $ResultFile,
    [string] $HeartbeatFile
)

. "$PSScriptRoot/common.ps1"

# Named for the stack under the call, not for the call: the entity scenarios ask for one
# user at a time and the abort scenario enumerates a collection, and the row is read on
# which stack answered.
$labels = [ordered]@{
    naive  = 'naive Invoke-RestMethod'
    sdk    = 'Graph SDK'
    sdkMgx = 'Graph SDK + MgxResilience'
    mgx    = 'mgx'
}
# The id the visibility scenario reads. Nothing else asks for it, so the plan can govern
# that one path and leave every other route on the default schedule.
$visibilityId = '999'

# ---------------- child mode: one contender against one scenario ----------------
if ($Scenario -and $Contender) {
    switch ($Contender) {
        'naive' { }                                  # no Graph module at all: it is a REST loop
        'sdk'   { Import-Module Microsoft.Graph.Users }
        default { Import-MgxLocal }
    }
    function Beat { Set-Content -Path $HeartbeatFile -Value ([datetime]::UtcNow.Ticks) }
    Beat
    if ($Contender -ne 'naive') { Connect-BenchMockGraph -GraphEndpoint $Base }
    if ($Contender -eq 'sdkMgx') { Enable-MgxResilience }

    # The heartbeat is touched every few items, and a failed item is an item: under the
    # outage a contender makes steady progress without producing a single object, and a
    # watchdog reading output alone would call that a hang.
    $script:steps = 0
    function Step { $script:steps++; if ($script:steps % 5 -eq 0) { Beat } }

    # The record's id and category, not its message: the message carries this run's port and
    # would differ between two runs of the same scenario.
    function Get-ErrorFacts($record) {
        if ($null -eq $record) { return $null }
        [ordered]@{
            id        = [string]$record.FullyQualifiedErrorId
            category  = [string]$record.CategoryInfo.Category
            exception = $record.Exception.GetType().Name
            inner     = $(if ($record.Exception.InnerException) { $record.Exception.InnerException.GetType().Name } else { $null })
        }
    }

    # One user at a time, the way each contender asks for one.
    function Invoke-EntityWork([string[]] $Items) {
        $ok = 0; $failed = 0; $first = $null
        switch ($Contender) {
            'naive' {
                foreach ($id in $Items) {
                    try { $null = Invoke-RestMethod "$Base/v1.0/users/$id" -TimeoutSec 30; $ok++ }
                    catch { $failed++; if (-not $first) { $first = $_ } }
                    Step
                }
            }
            'mgx' {
                # 2>&1 rather than -ErrorVariable: the error records reach the pipeline as
                # they happen, so a scenario where every item fails still beats.
                $Items | Invoke-MgxRequest '/users/{id}' -ErrorAction Continue 2>&1 | ForEach-Object {
                    if ($_ -is [System.Management.Automation.ErrorRecord]) {
                        $failed++; if (-not $first) { $first = $_ }
                    }
                    else { $ok++ }
                    Step
                }
            }
            default {
                foreach ($id in $Items) {
                    $u = Get-MgUser -UserId "$id" -ErrorAction SilentlyContinue -ErrorVariable ev
                    if ($u) { $ok++ } else { $failed++; if (-not $first -and $ev) { $first = $ev[0] } }
                    Step
                }
            }
        }
        @{ ok = $ok; failed = $failed; error = (Get-ErrorFacts $first) }
    }

    # The collection, page by page, until it ends or something cuts it off.
    function Invoke-ListWork {
        $ok = 0; $err = $null
        switch ($Contender) {
            'naive' {
                $uri = "$Base/v1.0/users"
                try {
                    while ($uri) {
                        $page = Invoke-RestMethod $uri -TimeoutSec 30
                        $ok += @($page.value).Count
                        Beat
                        $uri = $page.'@odata.nextLink'
                    }
                }
                catch { $err = $_ }
            }
            'mgx' {
                try { Invoke-MgxRequest '/users' -All -ErrorAction Stop | ForEach-Object { $ok++; Step } }
                catch { $err = $_ }
            }
            default {
                try { Get-MgUser -All -ErrorAction Stop | ForEach-Object { $ok++; Step } }
                catch { $err = $_ }
            }
        }
        @{ ok = $ok; failed = $(if ($err) { 1 } else { 0 }); error = (Get-ErrorFacts $err) }
    }

    # Read-after-create. No layer in any of these stacks retries a 404, so what is measured
    # is the caller's own polling: how many reads, and how long before the resource answers.
    function Invoke-VisibilityWork {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $attempts = 0; $visibleMs = $null; $first = $null
        while ($sw.Elapsed.TotalSeconds -lt $VisibilityBudgetSeconds) {
            $attempts++
            $got = switch ($Contender) {
                'naive' {
                    try { $null = Invoke-RestMethod "$Base/v1.0/users/$visibilityId" -TimeoutSec 30; $true }
                    catch { if (-not $first) { $first = $_ }; $false }
                }
                'mgx' {
                    $r = @($visibilityId | Invoke-MgxRequest '/users/{id}' -ErrorAction SilentlyContinue -ErrorVariable ev)
                    if (-not $first -and $ev) { $first = $ev[0] }
                    [bool]$r.Count
                }
                default {
                    $u = Get-MgUser -UserId $visibilityId -ErrorAction SilentlyContinue -ErrorVariable ev
                    if (-not $first -and $ev) { $first = $ev[0] }
                    [bool]$u
                }
            }
            if ($got) { $visibleMs = $sw.ElapsedMilliseconds; break }
            Start-Sleep -Milliseconds $PollMs
            Beat
        }
        # $visibleMs is truthy-tested nowhere below: 0 is a legitimate elapsed reading (visible
        # on the very first read) and PowerShell treats 0 as $false, which used to record a
        # resource visible at 0ms the same as one that never became visible at all.
        @{ ok = $(if ($null -ne $visibleMs) { 1 } else { 0 }); failed = $(if ($null -ne $visibleMs) { 0 } else { 1 })
           attempts = $attempts; visibleAfterMs = $visibleMs; error = (Get-ErrorFacts $first) }
    }

    $ids = @(1..$N | ForEach-Object { "$_" })
    $outageIds = @($ids | Select-Object -First $OutageItems)
    $pass = switch ($Scenario) {
        'storm'      { Measure-BenchPass -Name $labels[$Contender] -Script { Invoke-EntityWork $ids } }
        'outage'     { Measure-BenchPass -Name $labels[$Contender] -Script { Invoke-EntityWork $outageIds } }
        'abort'      { Measure-BenchPass -Name $labels[$Contender] -Script { Invoke-ListWork } }
        'visibility' { Measure-BenchPass -Name $labels[$Contender] -Script { Invoke-VisibilityWork } }
    }
    if ($Contender -eq 'sdkMgx') { Disable-MgxResilience }
    Disconnect-MgGraph -ErrorAction SilentlyContinue | Out-Null
    $pass | ConvertTo-Json -Depth 8 | Set-Content $ResultFile
    exit 0
}

# ---------------- parent mode ----------------
Import-MgxLocal

$server = Start-BenchMockServer -Port $Port
$base = $server.BaseUrl
$tmp = [System.IO.Path]::GetTempPath()
try {
    function Set-Plan([hashtable] $Document, [string] $Label) {
        $json = ConvertTo-Json -InputObject $Document -Depth 6 -Compress
        $posted = @($Document.rules).Count
        $response = Invoke-RestMethod "$base/plan" -Method Post -Body $json -ContentType 'application/json'
        # The server answers with the rule count it actually armed. A mismatch means this
        # scenario is about to run against a plan that is not the one just posted - worth
        # stopping for, since every row after it would print as a result against no fault.
        if ($response.rules -ne $posted) {
            throw "plan for '$Label' posted $posted rule(s) but the server reports $($response.rules)"
        }
    }
    function Reset-Server { $null = Invoke-RestMethod "$base/reset" }
    function Get-ServerStats { Invoke-RestMethod "$base/stats" }
    # What this contender cost the server, not what the server has counted since it started.
    function Get-StatsDelta($Before, $After) {
        $delta = [ordered]@{}
        foreach ($property in $After.PSObject.Properties) {
            $was = $Before.PSObject.Properties[$property.Name]
            $delta[$property.Name] = $property.Value - $(if ($was) { $was.Value } else { 0 })
        }
        [pscustomobject]$delta
    }
    function Show-Row([string] $Id, $Row) {
        $o = $Row.Output
        $telemetry = if ($Row.MgxTelemetry) {
            'req {0}, retries {1}' -f $Row.MgxTelemetry.Requests,
                ($Row.MgxTelemetry.ThrottleRetries + $Row.MgxTelemetry.OtherRetries)
        } else { 'no telemetry' }
        # A crashed contender's ElapsedMs is watchdog poll noise (up to 10s), not a
        # measurement - it never got the chance to be one - so the wall-time slot names the
        # exit instead of printing a number nobody asked for.
        $wall = if ($Row.Crashed) { "EXITED $($Row.ExitCode) without a result" } else { '{0:F1}s' -f ($Row.ElapsedMs / 1000) }
        $note = if ($Row.Hung) { 'HUNG (killed by watchdog)' }
                elseif ($null -ne $o.visibleAfterMs) { 'visible after {0:F1}s in {1} reads' -f ($o.visibleAfterMs / 1000), $o.attempts }
                elseif ($null -ne $o.attempts) { 'never visible in {0} reads' -f $o.attempts }
                elseif ($o.error) { '{0} / {1}' -f $o.error.exception, $o.error.id }
                else { '' }
        Write-Host ("{0,-10} {1,-26} completed {2,5} / failed {3,5}  wall {4,8}  {5,-20}  {6}" -f `
            $Id, $Row.Name, $o.ok, $o.failed, $wall, $telemetry, $note)
    }

    $scenarios = [ordered]@{
        storm = @{
            Title = "429 storm: every request throttled for ${StormSeconds}s, Retry-After 2, then clean ($N ids)"
            Plan  = @{ rules = @(@{ route = '^/(v1\.0|beta)/users'; kind = 'throttleStorm'
                                    scope = 'server'; seconds = $StormSeconds; retryAfter = 2 }) }
        }
        outage = @{
            Title = "never-recovering 5xx: every attempt 503, no window ($OutageItems ids)"
            Plan  = @{ rules = @(@{ route = '^/(v1\.0|beta)/users'; kind = 'outage'; retryAfter = 1 }) }
        }
        abort = @{
            Title = "connection death mid-body: one page of $PageSize users, killed halfway through the body"
            Plan  = @{ pages = 1; pageSize = $PageSize
                       rules = @(@{ route = '^/(v1\.0|beta)/users$'; kind = 'abort'
                                    from = 1; count = 1 }) }
        }
        visibility = @{
            Title = "delayed visibility: /users/$visibilityId 404s for ${VisibilitySeconds}s, then appears"
            Plan  = @{ rules = @(@{ route = ('^/(v1\.0|beta)/users/{0}$' -f $visibilityId)
                                    kind = 'delayedVisibility'; seconds = $VisibilitySeconds }) }
        }
    }

    $results = [ordered]@{}
    foreach ($id in $scenarios.Keys) {
        $definition = $scenarios[$id]
        Write-Host ''
        Write-Host ("### {0}: {1}" -f $id, $definition.Title)
        Set-Plan $definition.Plan $id
        $results[$id] = [ordered]@{}
        foreach ($arm in $labels.Keys) {
            Reset-Server
            $before = Get-ServerStats
            $row = Invoke-WatchdoggedContender -Name $labels[$arm] -ScriptPath $PSCommandPath `
                -ArgumentList @(
                    '-Scenario', $id, '-Contender', $arm, '-Base', $base,
                    '-N', $N, '-OutageItems', $OutageItems,
                    '-VisibilityBudgetSeconds', $VisibilityBudgetSeconds, '-PollMs', $PollMs,
                    '-ResultFile', (Join-Path $tmp "bench16-$id-$arm.json"),
                    '-HeartbeatFile', (Join-Path $tmp "bench16-$id-$arm.beat")) `
                -ResultFile (Join-Path $tmp "bench16-$id-$arm.json") `
                -HeartbeatFile (Join-Path $tmp "bench16-$id-$arm.beat") `
                -StallSeconds $StallSeconds
            $row | Add-Member -NotePropertyName ServerStats -NotePropertyValue (Get-StatsDelta $before (Get-ServerStats)) -Force
            $results[$id][$arm] = $row
            Show-Row $id $row
        }
        # Cleared between scenarios so the next one's plan is the only thing in force.
        Set-Plan @{ rules = @() } "$id (clear)"
    }

    Write-Host ''
    Write-Host ("=== PATHOLOGICAL GAUNTLET (N={0}) ===" -f $N)
    foreach ($id in $results.Keys) {
        foreach ($arm in $results[$id].Keys) { Show-Row $id $results[$id][$arm] }
    }
    Write-Host ''
    Write-Host 'server-side truth: the /stats delta each contender left behind'
    foreach ($id in $results.Keys) {
        foreach ($arm in $results[$id].Keys) {
            $s = $results[$id][$arm].ServerStats
            Write-Host ("{0,-10} {1,-26} served {2,5}  pages {3,4}  429 {4,4}  503 {5,4}  abort {6,3}  404 {7,4}  errors {8,3}" -f `
                $id, $labels[$arm], $s.served, $s.pagesServed, $s.throttleStorm, $s.outage,
                $s.abort, $s.delayedVisibility, $s.serverErrors)
        }
    }
    Write-BenchResult -Benchmark '16-pathological-gauntlet' -Result $results
}
finally {
    Remove-BenchGraphEnvironment
    Stop-BenchMockServer $server
}
