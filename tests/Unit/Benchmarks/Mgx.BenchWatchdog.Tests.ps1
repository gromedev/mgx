#Requires -Modules Pester

<#
    Invoke-WatchdoggedContender in tests/benchmarks/common.ps1.

    Each case spawns a real child pwsh process, so these run at the watchdog's own 10-second
    poll granularity rather than instantly - the same cost 16 and 17 already pay to use it.
#>

BeforeAll {
    $script:BenchRoot = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'benchmarks'

    # common.ps1 pins the thread to invariant culture; not something to leave behind in a
    # test run.
    $script:CultureBefore = [System.Threading.Thread]::CurrentThread.CurrentCulture
    . (Join-Path $script:BenchRoot 'common.ps1')
}

AfterAll {
    [System.Threading.Thread]::CurrentThread.CurrentCulture = $script:CultureBefore
}

Describe 'Invoke-WatchdoggedContender' {
    BeforeEach {
        $script:ResultFile = Join-Path $TestDrive 'result.json'
        $script:HeartbeatFile = Join-Path $TestDrive 'heartbeat'
        Remove-Item -Path $script:ResultFile, $script:HeartbeatFile -ErrorAction SilentlyContinue
    }

    It 'records a Crashed row naming the exit code when the child dies before its first heartbeat' {
        # An import or connect failure inside the child's first poll: it exits before ever
        # touching $HeartbeatFile, which is what used to fall back to $p.StartTime and read
        # back null once the child had already exited - and used to come back Hung, which is
        # not what happened: nothing here waited for progress that never came, the child quit
        # on its own within the first poll.
        $child = Join-Path $TestDrive 'dies-early.ps1'
        Set-Content -Path $child -Value 'exit 3'

        $row = Invoke-WatchdoggedContender -Name 'dies-early' -ScriptPath $child `
            -ResultFile $script:ResultFile -HeartbeatFile $script:HeartbeatFile -StallSeconds 60

        $row.Hung | Should -BeFalse
        $row.Crashed | Should -BeTrue
        $row.ExitCode | Should -Be 3
        $row.Output.note | Should -BeLike '*exited 3*'
    }

    It 'reports Hung = false and Crashed = false for a child that heartbeats and then completes' {
        $child = Join-Path $TestDrive 'completes.ps1'
        Set-Content -Path $child -Value @'
param([string] $HeartbeatFile, [string] $ResultFile)
Set-Content -Path $HeartbeatFile -Value ([datetime]::UtcNow.Ticks)
[pscustomobject]@{ Name = 'completes'; ElapsedMs = 1 } | ConvertTo-Json | Set-Content -Path $ResultFile
exit 0
'@

        $row = Invoke-WatchdoggedContender -Name 'completes' -ScriptPath $child `
            -ArgumentList @('-HeartbeatFile', $script:HeartbeatFile, '-ResultFile', $script:ResultFile) `
            -ResultFile $script:ResultFile -HeartbeatFile $script:HeartbeatFile -StallSeconds 60

        $row.Hung | Should -BeFalse
        $row.Crashed | Should -BeFalse
        $row.Name | Should -Be 'completes'
    }
}
