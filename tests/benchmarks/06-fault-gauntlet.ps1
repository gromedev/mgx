# Benchmark 06: resilience under controlled fault injection. No tenant required.
#
# Spins up mock-graph-server.ps1 (deterministic 429/503 schedule keyed on entity id),
# then fetches the same N users with four contenders:
#   1. naive      - Invoke-RestMethod loop, no retry (what quick scripts actually do)
#   2. sdk        - Get-MgUser sequential (includes the SDK's own built-in retry handler)
#   3. sdk+mgx    - Get-MgUser sequential with Enable-MgxResilience injected
#   4. mgx        - ids | Invoke-MgxRequest '/users/{id}' (fan-out, full resilience stack)
# The server's /reset is called between contenders so each faces the identical schedule.
# Fault profile: 15% of ids throttle once (429, Retry-After 1s); 3% fail twice with 503.
#
# The server also takes a programmable fault plan over POST /plan. 06 posts none, so what
# it measures is the default schedule above and nothing else; a scenario wanting a plan
# gets its own script rather than growing this one.
param(
    [int] $N = 1000,
    [int] $Port = 8787
)

. "$PSScriptRoot/common.ps1"
Import-MgxLocal
Import-Module Microsoft.Graph.Users

# Strings, not integers. Invoke-MgxRequest resolves a piped id as either a bare string or
# an 'id' member; an int is neither, so every record raised MissingPipelineId, -ErrorAction
# SilentlyContinue swallowed all of them, and the fan-out contender died at EndProcessing
# claiming no pipeline input. Real Graph ids are strings, so this matches actual usage.
$ids = @(1..$N | ForEach-Object { "$_" })

# --- Start mock server as a child process, on a port nothing else is holding ---
$server = Start-BenchMockServer -Port $Port
$base = $server.BaseUrl
try {
    # --- Point the whole Graph stack at the mock ---
    # This run's port, every run: the environment is written into the user profile, so one
    # left standing from a previous run names a port nothing is listening on now.
    Connect-BenchMockGraph -GraphEndpoint $base

    function Reset-Server { $null = Invoke-RestMethod "$base/reset" }
    function Get-ServerStats { Invoke-RestMethod "$base/stats" }
    function Show-Contender($r) {
        Write-Host ("{0,-28} completed {1,5} / failed {2,4}  wall {3,7:F1}s" -f `
            $r.Name, $r.Output.ok, $r.Output.failed, ($r.ElapsedMs / 1000))
    }

    $results = [ordered]@{}

    # --- 1. naive: no retry at all ---
    Reset-Server
    $results.naive = Measure-BenchPass -Name 'naive Invoke-RestMethod' -Script {
        $ok = 0; $failed = 0
        foreach ($id in $ids) {
            try { $null = Invoke-RestMethod "$base/v1.0/users/$id" -TimeoutSec 10; $ok++ }
            catch { $failed++ }
        }
        @{ ok = $ok; failed = $failed; serverStats = (Get-ServerStats) }
    }

    Show-Contender $results.naive

    # --- 2. bare SDK (its built-in retry handler included) ---
    Reset-Server
    $results.sdk = Measure-BenchPass -Name 'Get-MgUser sequential' -Script {
        $ok = 0; $failed = 0
        foreach ($id in $ids) {
            $u = Get-MgUser -UserId "$id" -ErrorAction SilentlyContinue
            if ($u) { $ok++ } else { $failed++ }
        }
        @{ ok = $ok; failed = $failed; serverStats = (Get-ServerStats) }
    }

    Show-Contender $results.sdk

    # --- 3. SDK + Enable-MgxResilience ---
    Reset-Server
    Enable-MgxResilience
    $results.sdkMgx = Measure-BenchPass -Name 'Get-MgUser + MgxResilience' -Script {
        $ok = 0; $failed = 0
        foreach ($id in $ids) {
            $u = Get-MgUser -UserId "$id" -ErrorAction SilentlyContinue
            if ($u) { $ok++ } else { $failed++ }
        }
        @{ ok = $ok; failed = $failed; serverStats = (Get-ServerStats) }
    }
    Disable-MgxResilience
    Show-Contender $results.sdkMgx

    # --- 4. Mgx fan-out ---
    Reset-Server
    $results.mgx = Measure-BenchPass -Name 'Invoke-MgxRequest fan-out' -Script {
        $items = @($ids | Invoke-MgxRequest '/users/{id}' -ErrorVariable mgxErrs -ErrorAction SilentlyContinue)
        @{ ok = $items.Count; failed = $mgxErrs.Count; serverStats = (Get-ServerStats) }
    }
    Show-Contender $results.mgx

    # --- Report ---
    Write-Host ''
    Write-Host ("=== FAULT GAUNTLET (N={0}; 15% single-429, 3% double-503) ===" -f $N)
    foreach ($k in $results.Keys) {
        $r = $results[$k]
        Write-Host ("{0,-28} completed {1,5} / failed {2,4}  wall {3,7:F1}s" -f `
            $r.Name, $r.Output.ok, $r.Output.failed, ($r.ElapsedMs / 1000))
    }
    Write-BenchResult -Benchmark '06-fault-gauntlet' -Result $results
}
finally {
    Disconnect-MgGraph -ErrorAction SilentlyContinue | Out-Null
    Remove-BenchGraphEnvironment
    Stop-BenchMockServer $server
}
