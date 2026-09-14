# Benchmark 17: the pathological faults that act on the session rather than on the socket.
# No tenant required.
#
# 16's faults are properties of the server. These two are properties of the process the
# operation is running in, and they arrive while it is running:
#
#   auth      a second runspace calls Connect-MgGraph again, with a different access token
#             and a different tenant id, while a read is in flight. GraphSession.Instance is
#             a process-wide singleton, so the runspace boundary does not protect anything:
#             what the operation holds is replaced under it.
#   modules   a second runspace imports Az.Accounts and then PnP.PowerShell while a read is
#             in flight. Module load and assembly resolution are irreversible in a process,
#             and Az.Accounts brings its own System.Text.Json.
#
# This records what happens today. Recovering from a session replaced mid-operation is 2.3.0
# work, so there is no pass or fail here and nothing is asserted: the rows are the before
# picture that work will be measured against. Every row says "recorded".
#
# Contenders: mgx ($ids | Invoke-MgxRequest '/users/{id}', one cmdlet invocation that spans
# the perturbation) and the bare Graph SDK (Get-MgUser per id, a sequence of invocations).
# The naive Invoke-RestMethod loop is not a contender here: it holds no session and imports
# nothing, so neither perturbation has anything to act on.
#
# The read is a paced sequence of entity reads rather than a paged enumeration, for the same
# reason 16's abort scenario is one page: the mock speaks http, and Mgx refuses an
# @odata.nextLink that is not https, so a paged enumeration is not a shape both contenders
# can be measured on. What is needed here is an operation that is still running when the
# perturbation lands, and 200 reads paced at -PaceMs give that for both.
#
# The timeline is the consumer's: an item counts as received when the pipeline handed it
# over, which for the fan-out can lag its own HTTP by the width of the fan.
#
# Standalone: not in run.ps1.
param(
    [int] $N = 200,
    [int] $Port = 8787,
    # 200 reads at 100ms is a twenty-second operation, which leaves room for a perturbation
    # to land in the middle of it and for the read to carry on afterwards.
    [int] $PaceMs = 100,
    # Counted from the read's first item, not from the start of the child.
    [int] $PerturbAfterMs = 5000,
    [int] $StallSeconds = 180,
    # child-mode plumbing (internal)
    [ValidateSet('', 'auth', 'modules')] [string] $Scenario = '',
    [ValidateSet('', 'sdk', 'mgx')] [string] $Contender = '',
    [string] $Base,
    [string] $ResultFile,
    [string] $HeartbeatFile
)

. "$PSScriptRoot/common.ps1"

$labels = [ordered]@{
    sdk = 'Graph SDK'
    mgx = 'mgx'
}

# ---------------- child mode: one contender under one perturbation ----------------
if ($Scenario -and $Contender) {
    if ($Contender -eq 'sdk') { Import-Module Microsoft.Graph.Users } else { Import-MgxLocal }
    function Beat { Set-Content -Path $HeartbeatFile -Value ([datetime]::UtcNow.Ticks) }
    Beat
    Connect-BenchMockGraph -GraphEndpoint $Base

    $script:steps = 0
    function Step { $script:steps++; if ($script:steps % 10 -eq 0) { Beat } }

    function Get-ErrorFacts($record) {
        if ($null -eq $record) { return $null }
        [ordered]@{
            id        = [string]$record.FullyQualifiedErrorId
            category  = [string]$record.CategoryInfo.Category
            exception = $record.Exception.GetType().Name
            inner     = $(if ($record.Exception.InnerException) { $record.Exception.InnerException.GetType().Name } else { $null })
        }
    }

    function Get-SessionFacts {
        $context = Get-MgContext
        [ordered]@{
            tenantId = [string]$context.TenantId
            authType = [string]$context.AuthType
            scopes   = @($context.Scopes).Count
        }
    }

    # Every id, one read at a time, paced so the operation is still running when the
    # perturbation lands. Each item is stamped as the pipeline hands it over, which is what
    # makes "before, during and after" answerable afterwards.
    function Invoke-PacedRead {
        $received = [System.Collections.Generic.List[object]]::new()
        $first = $null
        $startedAt = [datetime]::UtcNow
        switch ($Contender) {
            'mgx' {
                $ids | Invoke-MgxRequest '/users/{id}' -ErrorAction Continue 2>&1 | ForEach-Object {
                    if ($_ -is [System.Management.Automation.ErrorRecord]) {
                        if (-not $first) { $first = $_ }
                    }
                    else {
                        $received.Add([pscustomobject]@{ id = [string]$_.id; at = [datetime]::UtcNow })
                        if (-not $sync.ReadStarted) { $sync.ReadStarted = [datetime]::UtcNow }
                    }
                    Step
                    Start-Sleep -Milliseconds $PaceMs
                }
            }
            default {
                foreach ($id in $ids) {
                    $user = Get-MgUser -UserId $id -ErrorAction SilentlyContinue -ErrorVariable ev
                    if ($user) {
                        $received.Add([pscustomobject]@{ id = [string]$user.Id; at = [datetime]::UtcNow })
                        if (-not $sync.ReadStarted) { $sync.ReadStarted = [datetime]::UtcNow }
                    }
                    elseif (-not $first -and $ev) { $first = $ev[0] }
                    Step
                    Start-Sleep -Milliseconds $PaceMs
                }
            }
        }
        @{ startedAt = $startedAt; endedAt = [datetime]::UtcNow; received = $received
           error = (Get-ErrorFacts $first) }
    }

    # The perturbation runs in a second runspace of THIS process, because that is what makes
    # it a perturbation: a child process would share neither the session singleton nor the
    # assemblies. It waits for the read's first item and then for -PerturbAfterMs, so it
    # lands the same distance into the read whichever contender is running - the two do not
    # take the same time to hand over their first item, and a delay counted from anywhere
    # else lands before the mgx arm has read anything.
    $perturbations = @{
        auth = @'
param($DelayMs, $Token, $TenantId, $AuthModule)
$log = [ordered]@{ action = 'Connect-MgGraph again, different access token and tenant'; steps = @() }
while (-not $sync.ReadStarted) { Start-Sleep -Milliseconds 50 }
Start-Sleep -Milliseconds $DelayMs
$log.startedAt = [datetime]::UtcNow
try {
    # By path, not by name. A new runspace has no modules, and the name resolves to the
    # newest Microsoft.Graph.Authentication installed - which is not necessarily the one
    # this process already loaded, and loading a second version of it throws instead of
    # perturbing anything. The path is the one the read is running against.
    Import-Module $AuthModule -ErrorAction Stop
    Connect-MgGraph -Environment MgxBench -NoWelcome -ErrorAction Stop `
        -AccessToken (ConvertTo-SecureString $Token -AsPlainText -Force)
    [Microsoft.Graph.PowerShell.Authentication.GraphSession]::Instance.AuthContext.TenantId = $TenantId
    $log.steps += "connected as $TenantId"
}
catch { $log.steps += "connect failed: $($_.Exception.Message)" }
$log.finishedAt = [datetime]::UtcNow
[pscustomobject]$log
'@
        modules = @'
param($DelayMs)
$log = [ordered]@{ action = 'Import-Module Az.Accounts, then PnP.PowerShell'; steps = @() }
while (-not $sync.ReadStarted) { Start-Sleep -Milliseconds 50 }
Start-Sleep -Milliseconds $DelayMs
$log.startedAt = [datetime]::UtcNow
foreach ($module in 'Az.Accounts', 'PnP.PowerShell') {
    try {
        Import-Module $module -ErrorAction Stop
        $log.steps += "$module imported at $([datetime]::UtcNow.ToString('HH:mm:ss.fff'))"
    }
    catch { $log.steps += "$module failed: $($_.Exception.Message)" }
}
$log.finishedAt = [datetime]::UtcNow
[pscustomobject]$log
'@
    }

    $ids = @(1..$N | ForEach-Object { "$_" })
    $sessionBefore = Get-SessionFacts

    # Shared across both runspaces of this process: the read stamps it when its first item
    # arrives, and the perturbation blocks on it.
    $sync = [hashtable]::Synchronized(@{ ReadStarted = $null })
    $runspace = [runspacefactory]::CreateRunspace()
    $runspace.Open()
    $runspace.SessionStateProxy.SetVariable('sync', $sync)
    $shell = [powershell]::Create()
    $shell.Runspace = $runspace
    $null = $shell.AddScript($perturbations[$Scenario])
    $null = if ($Scenario -eq 'auth') {
        $shell.AddParameters(@{ DelayMs = $PerturbAfterMs
                                Token = 'replacement-token-not-validated'
                                TenantId = 'gauntlet-replacement-tenant'
                                AuthModule = (Get-Module Microsoft.Graph.Authentication |
                                              Select-Object -First 1).Path })
    } else {
        $shell.AddParameters(@{ DelayMs = $PerturbAfterMs })
    }
    $handle = $shell.BeginInvoke()

    $pass = Measure-BenchPass -Name $labels[$Contender] -Script { Invoke-PacedRead }

    $perturbation = $null
    try { $perturbation = @($shell.EndInvoke($handle))[0] }
    catch { $perturbation = [pscustomobject]@{ action = 'perturbation runspace'; steps = @("threw: $($_.Exception.Message)") } }
    $runspaceErrors = @($shell.Streams.Error |
        ForEach-Object { '{0}: {1}' -f $_.CategoryInfo.Category, $_.Exception.Message } |
        Where-Object { $_ -notmatch '^\w+: *$' })
    if ($runspaceErrors.Count) {
        $perturbation | Add-Member -NotePropertyName runspaceErrors -NotePropertyValue $runspaceErrors -Force
    }
    $shell.Dispose(); $runspace.Close(); $runspace.Dispose()

    # What the operation got, against what the mock holds. The ids are the mock's own, so a
    # loss and a duplicate are both countable without a tenant.
    $out = $pass.Output
    $received = @($out.received)
    $distinct = @($received.id | Sort-Object -Unique)
    $windowStart = $perturbation.startedAt
    $windowEnd = $perturbation.finishedAt
    $summary = [ordered]@{
        items          = $received.Count
        distinctItems  = $distinct.Count
        duplicates     = $received.Count - $distinct.Count
        lost           = $N - $distinct.Count
        itemsBefore    = @($received | Where-Object { $windowStart -and $_.at -lt $windowStart }).Count
        itemsDuring    = @($received | Where-Object { $windowStart -and $windowEnd -and $_.at -ge $windowStart -and $_.at -le $windowEnd }).Count
        itemsAfter     = @($received | Where-Object { $windowEnd -and $_.at -gt $windowEnd }).Count
        firstId        = $(if ($received.Count) { $received[0].id } else { $null })
        lastId         = $(if ($received.Count) { $received[-1].id } else { $null })
        readStartedAt  = $out.startedAt.ToString('o')
        readEndedAt    = $out.endedAt.ToString('o')
        error          = $out.error
        sessionBefore  = $sessionBefore
        sessionAfter   = (Get-SessionFacts)
        perturbation   = $perturbation
        # The proof that this row measured anything: the perturbation has to have started
        # after the read did and before it ended.
        perturbedInFlight = [bool]($windowStart -and $windowStart -ge $out.startedAt -and $windowStart -le $out.endedAt)
    }
    # The per-item list is what the summary was derived from; it is not what anyone reads.
    $pass.Output = $summary

    # No teardown: the process exits here, and the parent takes the Graph environment out.
    # A session this scenario deliberately replaced is not worth disconnecting cleanly, and
    # a throw on the way out would lose the row.
    $pass | ConvertTo-Json -Depth 8 | Set-Content $ResultFile
    exit 0
}

# ---------------- parent mode ----------------
Import-MgxLocal

$server = Start-BenchMockServer -Port $Port
$base = $server.BaseUrl
$tmp = [System.IO.Path]::GetTempPath()
try {
    function Show-Record([string] $Id, $Row) {
        $o = $Row.Output
        if ($Row.Hung) {
            Write-Host ("{0,-8} {1,-11} HUNG (killed by watchdog) after {2:F1}s" -f $Id, $Row.Name, ($Row.ElapsedMs / 1000))
            return
        }
        if ($Row.Crashed) {
            # ElapsedMs here is watchdog poll noise, not a measurement, so it is left out
            # rather than printed as though it were a wall time.
            Write-Host ("{0,-8} {1,-11} EXITED {2} without a result" -f $Id, $Row.Name, $Row.ExitCode)
            return
        }
        $telemetry = if ($Row.MgxTelemetry) {
            'req {0}, retries {1}' -f $Row.MgxTelemetry.Requests,
                ($Row.MgxTelemetry.ThrottleRetries + $Row.MgxTelemetry.OtherRetries)
        } else { 'no telemetry' }
        Write-Host ("{0,-8} {1,-11} recorded {2,4} items ({3} before / {4} during / {5} after the perturbation), {6} duplicated, {7} lost, wall {8:F1}s, {9}" -f `
            $Id, $Row.Name, $o.items, $o.itemsBefore, $o.itemsDuring, $o.itemsAfter,
            $o.duplicates, $o.lost, ($Row.ElapsedMs / 1000), $telemetry)
        Write-Host ("{0,-8} {1,-11}   perturbation landed in flight: {2} - {3}" -f `
            $Id, $Row.Name, $o.perturbedInFlight, $o.perturbation.action)
        foreach ($step in @($o.perturbation.steps)) {
            Write-Host ("{0,-8} {1,-11}   step: {2}" -f $Id, $Row.Name, $step)
        }
        foreach ($runspaceError in @($o.perturbation.runspaceErrors | Where-Object { $_ })) {
            Write-Host ("{0,-8} {1,-11}   runspace error: {2}" -f $Id, $Row.Name, $runspaceError)
        }
        $surfaced = if ($o.error) { '{0} / {1}' -f $o.error.exception, $o.error.id } else { 'none' }
        Write-Host ("{0,-8} {1,-11}   error surfaced: {2}; session tenant {3} -> {4}" -f `
            $Id, $Row.Name, $surfaced, $o.sessionBefore.tenantId, $o.sessionAfter.tenantId)
    }

    # A rule whose window never opens governs the entity route without ever firing, which
    # takes the route off the mock's default 429/503 schedule: the perturbation is then the
    # only thing that can go wrong, and a lost item is the perturbation's doing.
    $plan = @{ rules = @(@{ route = '^/(v1\.0|beta)/users/'; kind = 'outage'; from = 1000000 }) }
    $planResponse = Invoke-RestMethod "$base/plan" -Method Post -ContentType 'application/json' `
        -Body (ConvertTo-Json -InputObject $plan -Depth 6 -Compress)
    # The server answers with the rule count it actually armed; a mismatch means the entity
    # route is not off the default schedule the way this whole file assumes it is.
    $postedRules = @($plan.rules).Count
    if ($planResponse.rules -ne $postedRules) {
        throw "plan setup posted $postedRules rule(s) but the server reports $($planResponse.rules)"
    }

    Write-Host ''
    Write-Host 'The naive Invoke-RestMethod loop is not a contender here: it holds no session and'
    Write-Host 'imports nothing, so neither perturbation has anything of its to act on.'

    $scenarios = [ordered]@{
        auth    = 'a second runspace connects again, with a different token and tenant, mid-read'
        modules = 'a second runspace imports Az.Accounts and PnP.PowerShell mid-read'
    }

    $results = [ordered]@{}
    foreach ($id in $scenarios.Keys) {
        Write-Host ''
        Write-Host ("### {0}: {1}" -f $id, $scenarios[$id])
        $results[$id] = [ordered]@{}
        foreach ($arm in $labels.Keys) {
            $null = Invoke-RestMethod "$base/reset"
            $row = Invoke-WatchdoggedContender -Name $labels[$arm] -ScriptPath $PSCommandPath `
                -ArgumentList @(
                    '-Scenario', $id, '-Contender', $arm, '-Base', $base,
                    '-N', $N, '-PaceMs', $PaceMs, '-PerturbAfterMs', $PerturbAfterMs,
                    '-ResultFile', (Join-Path $tmp "bench17-$id-$arm.json"),
                    '-HeartbeatFile', (Join-Path $tmp "bench17-$id-$arm.beat")) `
                -ResultFile (Join-Path $tmp "bench17-$id-$arm.json") `
                -HeartbeatFile (Join-Path $tmp "bench17-$id-$arm.beat") `
                -StallSeconds $StallSeconds
            $results[$id][$arm] = $row
            Show-Record $id $row
        }
    }

    Write-Host ''
    Write-Host ("=== PATHOLOGICAL ENVIRONMENT (N={0}, paced {1}ms, perturbed at {2}ms) ===" -f $N, $PaceMs, $PerturbAfterMs)
    foreach ($id in $results.Keys) {
        foreach ($arm in $results[$id].Keys) { Show-Record $id $results[$id][$arm] }
    }
    Write-BenchResult -Benchmark '17-pathological-environment' -Result $results
}
finally {
    Remove-BenchGraphEnvironment
    Stop-BenchMockServer $server
}
