#Requires -Modules Pester

<#
    Baseline promotion and comparison in tests/benchmarks/common.ps1.

    These cover the benchmark harness rather than the built module: no tenant, no Graph
    session, and nothing read out of the real results/ directory - every fixture is written
    under $TestDrive and every call is pointed at it.
#>

BeforeAll {
    $script:BenchRoot = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'benchmarks'

    # common.ps1 pins the thread to invariant culture so benchmark output never prints 39,2s.
    # Right for a benchmark process, and not something to leave behind in a test run.
    $script:CultureBefore = [System.Threading.Thread]::CurrentThread.CurrentCulture
    . (Join-Path $script:BenchRoot 'common.ps1')

    function script:New-BenchEntry
    {
        param
        (
            [Parameter(Mandatory)] [hashtable] $Result,
            [datetime] $RecordedAt = (Get-Date)
        )

        return [pscustomobject]@{
            Result     = [pscustomobject]$Result
            Telemetry  = $null
            MgxVersion = '2.1.5'
            SdkVersion = '2.34.0'
            PSVersion  = '7.6.4'
            RecordedAt = $RecordedAt.ToString('o')
        }
    }

    function script:Write-BenchFixture
    {
        param
        (
            [Parameter(Mandatory)] [string] $Directory,
            [Parameter(Mandatory)] [string] $FileName,
            [Parameter(Mandatory)] [object[]] $Entries
        )

        if (-not (Test-Path -Path $Directory))
        {
            New-Item -ItemType Directory -Path $Directory -Force | Out-Null
        }
        ConvertTo-Json -InputObject @($Entries) -Depth 10 |
            Set-Content -Path (Join-Path $Directory $FileName)
    }

    # A 06 gauntlet result in the shape 06-fault-gauntlet.ps1 records.
    function script:New-GauntletResult
    {
        param ([Parameter(Mandatory)] [int] $MgxMs)

        return @{
            mgx    = [pscustomobject]@{ ElapsedMs = $MgxMs; Output = [pscustomobject]@{ ok = 1000; failed = 0 } }
            sdkMgx = [pscustomobject]@{ ElapsedMs = 246715 }
        }
    }
}

AfterAll {
    [System.Threading.Thread]::CurrentThread.CurrentCulture = $script:CultureBefore
}

Describe 'benchmark scenario table' {
    It 'defines metrics for every benchmark run.ps1 executes' {
        # Everything that writes or reads a baseline keys off this table. A scenario added to
        # the suite without an entry here is pinned as nothing and read as unmeasured.
        $runText = Get-Content -Path (Join-Path $script:BenchRoot 'run.ps1') -Raw
        $suiteIds = [regex]::Matches($runText, "id\s*=\s*'(\d+)'") | ForEach-Object { $_.Groups[1].Value }
        $suiteIds.Count | Should -BeGreaterThan 0

        $known = (Get-BenchScenario).Id
        foreach ($id in $suiteIds)
        {
            $known | Should -Contain $id -Because "run.ps1 runs benchmark $id"
        }
    }

    It 'names each scenario a headline metric first' {
        foreach ($scenario in (Get-BenchScenario))
        {
            $scenario.Metrics.Count | Should -BeGreaterThan 0
            $scenario.Metrics[0].Name | Should -Not -BeNullOrEmpty
        }
    }

    It 'skips an id it has no metrics for rather than throwing' {
        Get-BenchScenario -Id '99' -WarningAction SilentlyContinue | Should -BeNullOrEmpty
    }
}

Describe 'Export-BenchBaseline' {
    BeforeEach {
        $script:Results = Join-Path $TestDrive 'results'
        $script:Baseline = Join-Path $TestDrive 'baseline.json'
        Remove-Item -Path $script:Results, $script:Baseline -Recurse -Force -ErrorAction SilentlyContinue
    }

    It 'promotes the newest entry, its headline metric and its version block' {
        Write-BenchFixture -Directory $script:Results -FileName '06-fault-gauntlet.json' -Entries @(
            (New-BenchEntry -Result (New-GauntletResult -MgxMs 111111) -RecordedAt (Get-Date).AddDays(-2)),
            (New-BenchEntry -Result (New-GauntletResult -MgxMs 134654))
        )

        Export-BenchBaseline -Benchmark '06' -ResultsDirectory $script:Results -Path $script:Baseline

        $pinned = (Get-Content -Path $script:Baseline -Raw | ConvertFrom-Json).Scenarios.'06'
        $pinned.Script | Should -Be '06-fault-gauntlet.ps1'
        $pinned.Headline | Should -Be 'MgxFanoutMs'
        $pinned.Metrics.MgxFanoutMs.Value | Should -Be 134654
        $pinned.Metrics.MgxFanoutMs.Unit | Should -Be 'ms'
        $pinned.Metrics.MgxCompleted.Value | Should -Be 1000
        $pinned.MgxVersion | Should -Be '2.1.5'
        $pinned.SdkVersion | Should -Be '2.34.0'
        $pinned.PSVersion | Should -Be '7.6.4'
        $pinned.RecordedAt | Should -Not -BeNullOrEmpty
    }

    It 'leaves scenarios it was not asked about where they were' {
        Write-BenchFixture -Directory $script:Results -FileName '06-fault-gauntlet.json' -Entries @(
            (New-BenchEntry -Result (New-GauntletResult -MgxMs 134654))
        )
        Write-BenchFixture -Directory $script:Results -FileName '08-delta-sync.json' -Entries @(
            (New-BenchEntry -Result @{ initial = [pscustomobject]@{ ElapsedMs = 145621; Output = [pscustomobject]@{ items = 130233 } } })
        )

        Export-BenchBaseline -Benchmark '08' -ResultsDirectory $script:Results -Path $script:Baseline
        Export-BenchBaseline -Benchmark '06' -ResultsDirectory $script:Results -Path $script:Baseline

        $scenarios = (Get-Content -Path $script:Baseline -Raw | ConvertFrom-Json).Scenarios
        $scenarios.'08'.Metrics.InitialSyncMs.Value | Should -Be 145621
        $scenarios.'06'.Metrics.MgxFanoutMs.Value | Should -Be 134654
    }

    It 'pins no headline when the first metric of the scenario row is absent, rather than the next one' {
        # 09's row names CheckpointedExportMs first. An entry with no 'checkpointed' node still
        # carries four of its five metrics, and the next one that resolves is not a stand-in for
        # the one it is read on - CheckpointOverheadPct is a percentage that crosses zero.
        Write-BenchFixture -Directory $script:Results -FileName '09-kill-resume.json' -Entries @(
            (New-BenchEntry -Result @{
                    baseline     = [pscustomobject]@{ ElapsedMs = 140322 }
                    resume       = [pscustomobject]@{ ElapsedMs = 38104 }
                    verification = [pscustomobject]@{ overheadPct = -3.2; duplicateIds = 0 }
                })
        )

        Export-BenchBaseline -Benchmark '09' -ResultsDirectory $script:Results -Path $script:Baseline `
            -WarningAction SilentlyContinue

        $pinned = (Get-Content -Path $script:Baseline -Raw | ConvertFrom-Json).Scenarios.'09'
        $pinned.Headline | Should -BeNullOrEmpty
        $pinned.Metrics.BaselineExportMs.Value | Should -Be 140322
        $pinned.Metrics.CheckpointOverheadPct.Value | Should -Be -3.2

        # And the comparison says so rather than reading whatever is first in the file.
        $row = Compare-BenchBaseline -Benchmark '09' -ResultsDirectory $script:Results `
            -BaselinePath $script:Baseline
        $row.Status | Should -Be 'not comparable'
        $row.Note | Should -BeLike '*no headline*'
        $row.Metric | Should -BeNullOrEmpty
        $row.DeltaPct | Should -BeNullOrEmpty
    }

    It 'tries a metric''s next source when the first one is not numeric' {
        # 10's WallSeconds tries Arms.paced.Seconds before WallMs. An entry that recorded the
        # scenario as inconclusive on its first source still carries a number on its second,
        # and losing that to the first source's non-numeric value would cost WallSeconds its
        # headline along with it.
        Write-BenchFixture -Directory $script:Results -FileName '10-pacing-under-real-throttling.json' -Entries @(
            (New-BenchEntry -Result @{
                    Mode = 'paced'
                    Arms = [pscustomobject]@{ paced = [pscustomobject]@{ Seconds = 'INCONCLUSIVE'; ThrottleRetries = 12 } }
                    WallMs = 39400
                })
        )

        Export-BenchBaseline -Benchmark '10' -ResultsDirectory $script:Results -Path $script:Baseline `
            -WarningAction SilentlyContinue

        $pinned = (Get-Content -Path $script:Baseline -Raw | ConvertFrom-Json).Scenarios.'10'
        $pinned.Headline | Should -Be 'WallSeconds'
        $pinned.Metrics.WallSeconds.Value | Should -Be 39.4
        $pinned.Metrics.WallSeconds.Path | Should -Be 'WallMs'
    }

    It 'warns which metric was not recorded when none of its sources resolves' {
        # WallSeconds tries Arms.paced.Seconds, then WallMs. An entry that leaves the first
        # inconclusive and never writes the second must not be dropped in silence, and the
        # warning it gets must name the value that was not a number rather than just listing
        # every path tried - WallMs was never written, and a path with nothing at it is not
        # part of the story.
        Write-BenchFixture -Directory $script:Results -FileName '10-pacing-under-real-throttling.json' -Entries @(
            (New-BenchEntry -Result @{
                    Mode = 'paced'
                    Arms = [pscustomobject]@{
                        paced   = [pscustomobject]@{ Seconds = 'INCONCLUSIVE'; ThrottleRetries = 12 }
                        unpaced = [pscustomobject]@{ Seconds = 40.3 }
                    }
                })
        )

        $warnings = $null
        Export-BenchBaseline -Benchmark '10' -ResultsDirectory $script:Results -Path $script:Baseline `
            -WarningVariable warnings 3>$null

        "$warnings" | Should -BeLike "*10: 'WallSeconds' not recorded - 'Arms.paced.Seconds' holds 'INCONCLUSIVE', which is not a number*"
        "$warnings" | Should -Not -BeLike '*WallMs*'

        $pinned = (Get-Content -Path $script:Baseline -Raw | ConvertFrom-Json).Scenarios.'10'
        $pinned.Headline | Should -BeNullOrEmpty
        $pinned.Metrics.WallSeconds | Should -BeNullOrEmpty
        $pinned.Metrics.ThrottleRetries.Value | Should -Be 12
    }

    It 'does not pin an empty string or a boolean as a measurement' {
        # [double] coerces both without throwing - '' to 0, $true to 1 - so a source that
        # never recorded a number could be pinned as though it had. Neither belongs in the
        # baseline, and the warning must name what each source actually held.
        Write-BenchFixture -Directory $script:Results -FileName '10-pacing-under-real-throttling.json' -Entries @(
            (New-BenchEntry -Result @{
                    Mode = 'both'
                    Arms = [pscustomobject]@{
                        paced   = [pscustomobject]@{ Seconds = ''; ThrottleRetries = 3 }
                        unpaced = [pscustomobject]@{ Seconds = 40.3 }
                    }
                    WallMs = $true
                })
        )

        $warnings = $null
        Export-BenchBaseline -Benchmark '10' -ResultsDirectory $script:Results -Path $script:Baseline `
            -WarningVariable warnings 3>$null

        "$warnings" | Should -BeLike "*'Arms.paced.Seconds' holds '', which is not a number*"
        "$warnings" | Should -BeLike "*'WallMs' holds 'True', which is not a number*"

        $pinned = (Get-Content -Path $script:Baseline -Raw | ConvertFrom-Json).Scenarios.'10'
        $pinned.Headline | Should -BeNullOrEmpty
        $pinned.Metrics.WallSeconds | Should -BeNullOrEmpty
        $pinned.Metrics.UnpacedWallSeconds.Value | Should -Be 40.3
        $pinned.Metrics.ThrottleRetries.Value | Should -Be 3
    }

    It 'refuses "NaN", "Infinity" and "-Infinity" as measurements, named in the not-recorded warning' {
        # NumberStyles.Float - the style [double]::TryParse is called with - parses these three
        # strings as valid doubles: NaN, PositiveInfinity, NegativeInfinity. None is a
        # measurement a baseline can hold a percentage delta against, so each must be refused
        # and named rather than pinned.
        Write-BenchFixture -Directory $script:Results -FileName '10-pacing-under-real-throttling.json' -Entries @(
            (New-BenchEntry -Result @{
                    Mode = 'both'
                    Arms = [pscustomobject]@{
                        paced   = [pscustomobject]@{ Seconds = 'NaN'; ThrottleRetries = 3 }
                        unpaced = [pscustomobject]@{ Seconds = '-Infinity' }
                    }
                    WallMs = 'Infinity'
                })
        )

        $warnings = $null
        Export-BenchBaseline -Benchmark '10' -ResultsDirectory $script:Results -Path $script:Baseline `
            -WarningVariable warnings 3>$null

        "$warnings" | Should -BeLike "*10: 'WallSeconds' not recorded - 'Arms.paced.Seconds' holds 'NaN', which is not a number; 'WallMs' holds 'Infinity', which is not a number.*"
        "$warnings" | Should -BeLike "*10: 'UnpacedWallSeconds' not recorded - 'Arms.unpaced.Seconds' holds '-Infinity', which is not a number.*"

        $pinned = (Get-Content -Path $script:Baseline -Raw | ConvertFrom-Json).Scenarios.'10'
        $pinned.Headline | Should -BeNullOrEmpty
        $pinned.Metrics.WallSeconds | Should -BeNullOrEmpty
        $pinned.Metrics.UnpacedWallSeconds | Should -BeNullOrEmpty
        $pinned.Metrics.ThrottleRetries.Value | Should -Be 3
    }

    It 'refuses a typed double NaN, not just the string it prints as' {
        # ConvertTo-Json always re-quotes NaN and the infinities as strings, so the fixture
        # helper above can only exercise the string arm of Test-BenchNumericValue. A result
        # file written with JSON's non-standard bare NaN token round-trips through
        # ConvertFrom-Json as a genuine [double] - the other arm - and that value must be
        # refused too, not coerced into a measurement by the typed-value check alone.
        New-Item -ItemType Directory -Path $script:Results -Force | Out-Null
        $raw = @"
[
  {
    "Result": { "Mode": "paced", "Arms": { "paced": { "Seconds": NaN, "ThrottleRetries": 12 } } },
    "Telemetry": null,
    "MgxVersion": "2.1.5",
    "SdkVersion": "2.34.0",
    "PSVersion": "7.6.4",
    "RecordedAt": "$((Get-Date).ToString('o'))"
  }
]
"@
        Set-Content -Path (Join-Path $script:Results '10-pacing-under-real-throttling.json') -Value $raw

        $warnings = $null
        Export-BenchBaseline -Benchmark '10' -ResultsDirectory $script:Results -Path $script:Baseline `
            -WarningVariable warnings 3>$null

        "$warnings" | Should -BeLike "*10: 'WallSeconds' not recorded - 'Arms.paced.Seconds' holds 'NaN', which is not a number*"

        $pinned = (Get-Content -Path $script:Baseline -Raw | ConvertFrom-Json).Scenarios.'10'
        $pinned.Headline | Should -BeNullOrEmpty
        $pinned.Metrics.WallSeconds | Should -BeNullOrEmpty
        $pinned.Metrics.ThrottleRetries.Value | Should -Be 12
    }

    It 'warns per rescued non-number and pins the metric from the source that resolved it' {
        # A first source that recorded 'INCONCLUSIVE' before a second recorded the real figure
        # used to fall back in silence. The rescue is now visible - one warning per source that
        # was tried and skipped - and the metric still pins from the source that resolved it.
        Write-BenchFixture -Directory $script:Results -FileName '10-pacing-under-real-throttling.json' -Entries @(
            (New-BenchEntry -Result @{
                    Mode = 'paced'
                    Arms = [pscustomobject]@{ paced = [pscustomobject]@{ Seconds = 'INCONCLUSIVE'; ThrottleRetries = 12 } }
                    WallMs = 41200
                })
        )

        $warnings = $null
        Export-BenchBaseline -Benchmark '10' -ResultsDirectory $script:Results -Path $script:Baseline `
            -WarningVariable warnings 3>$null

        "$warnings" | Should -BeLike "*10: 'WallSeconds' - 'Arms.paced.Seconds' holds 'INCONCLUSIVE', which is not a number - trying the next source.*"
        "$warnings" | Should -Not -BeLike '*not recorded*'

        $pinned = (Get-Content -Path $script:Baseline -Raw | ConvertFrom-Json).Scenarios.'10'
        $pinned.Headline | Should -Be 'WallSeconds'
        $pinned.Metrics.WallSeconds.Value | Should -Be 41.2
        $pinned.Metrics.WallSeconds.Path | Should -Be 'WallMs'
    }

    It 'does not warn about an optional source a single-arm entry never writes' {
        # UnpacedWallSeconds' only source is Arms.unpaced.Seconds, written only when run.ps1
        # drives both arms. A standalone -Mode paced run never writes it - that is not a
        # metric gone missing, it is one this entry was never going to carry, and every
        # healthy single-arm run must promote in silence.
        Write-BenchFixture -Directory $script:Results -FileName '10-pacing-under-real-throttling.json' -Entries @(
            (New-BenchEntry -Result @{
                    Mode = 'paced'
                    Arms = [pscustomobject]@{ paced = [pscustomobject]@{ Seconds = 41.2; ThrottleRetries = 3 } }
                })
        )

        $warnings = $null
        Export-BenchBaseline -Benchmark '10' -ResultsDirectory $script:Results -Path $script:Baseline `
            -WarningVariable warnings 3>$null

        $warnings | Should -BeNullOrEmpty

        $pinned = (Get-Content -Path $script:Baseline -Raw | ConvertFrom-Json).Scenarios.'10'
        $pinned.Headline | Should -Be 'WallSeconds'
        $pinned.Metrics.UnpacedWallSeconds | Should -BeNullOrEmpty
        $pinned.Metrics.WallSeconds.Value | Should -Be 41.2
    }

    It 'does not promote an entry older than the run that asked for it' {
        # A benchmark that died before recording leaves the previous run's entry newest.
        Write-BenchFixture -Directory $script:Results -FileName '06-fault-gauntlet.json' -Entries @(
            (New-BenchEntry -Result (New-GauntletResult -MgxMs 134654) -RecordedAt (Get-Date).AddDays(-2))
        )

        Export-BenchBaseline -Benchmark '06' -ResultsDirectory $script:Results -Path $script:Baseline `
            -Since (Get-Date) -WarningAction SilentlyContinue

        Test-Path -Path $script:Baseline | Should -BeFalse
    }

    It 'does not promote a run that recorded itself inconclusive, and names the scenario' {
        # 10 records Conclusive false when its unpaced arm was never refused: the throttled
        # regime it exists to measure was never reached, so the wall times beside the verdict
        # are two unthrottled runs. They are numbers, and every later run would be read
        # against them.
        Write-BenchFixture -Directory $script:Results -FileName '10-pacing-under-real-throttling.json' -Entries @(
            (New-BenchEntry -Result @{
                    Conclusive = $false
                    Arms       = [pscustomobject]@{
                        paced   = [pscustomobject]@{ Seconds = 399.7; ThrottleRetries = 0 }
                        unpaced = [pscustomobject]@{ Seconds = 79.8 }
                    }
                })
        )

        $warnings = $null
        Export-BenchBaseline -Benchmark '10' -ResultsDirectory $script:Results -Path $script:Baseline `
            -WarningVariable warnings 3>$null

        "$warnings" | Should -BeLike '*benchmark 10 recorded an inconclusive run (Conclusive is false) - not promoted*'
        Test-Path -Path $script:Baseline | Should -BeFalse
    }

    It 'quotes the reason an inconclusive entry carries, where it carries one' {
        Write-BenchFixture -Directory $script:Results -FileName '10-pacing-under-real-throttling.json' -Entries @(
            (New-BenchEntry -Result @{
                    Conclusive         = $false
                    InconclusiveReason = 'the unpaced arm was never throttled'
                    Arms               = [pscustomobject]@{
                        paced   = [pscustomobject]@{ Seconds = 399.7; ThrottleRetries = 0 }
                        unpaced = [pscustomobject]@{ Seconds = 79.8 }
                    }
                })
        )

        $warnings = $null
        Export-BenchBaseline -Benchmark '10' -ResultsDirectory $script:Results -Path $script:Baseline `
            -WarningVariable warnings 3>$null

        "$warnings" | Should -BeLike '*(the unpaced arm was never throttled)*'
        "$warnings" | Should -Not -BeLike '*Conclusive is false*'
    }

    It 'promotes a run that recorded itself conclusive' {
        Write-BenchFixture -Directory $script:Results -FileName '10-pacing-under-real-throttling.json' -Entries @(
            (New-BenchEntry -Result @{
                    Conclusive = $true
                    Arms       = [pscustomobject]@{
                        paced   = [pscustomobject]@{ Seconds = 61.4; ThrottleRetries = 3 }
                        unpaced = [pscustomobject]@{ Seconds = 88.2 }
                    }
                })
        )

        Export-BenchBaseline -Benchmark '10' -ResultsDirectory $script:Results -Path $script:Baseline

        $pinned = (Get-Content -Path $script:Baseline -Raw | ConvertFrom-Json).Scenarios.'10'
        $pinned.Headline | Should -Be 'WallSeconds'
        $pinned.Metrics.WallSeconds.Value | Should -Be 61.4
        $pinned.Metrics.UnpacedWallSeconds.Value | Should -Be 88.2
    }

    It 'promotes a scenario that records no verdict at all' {
        # The field is a claim a scenario may make, not one the promotion requires. Seven of
        # the eight scenarios in the file never write it.
        Write-BenchFixture -Directory $script:Results -FileName '06-fault-gauntlet.json' -Entries @(
            (New-BenchEntry -Result (New-GauntletResult -MgxMs 134654))
        )

        Export-BenchBaseline -Benchmark '06' -ResultsDirectory $script:Results -Path $script:Baseline

        $pinned = (Get-Content -Path $script:Baseline -Raw | ConvertFrom-Json).Scenarios.'06'
        $pinned.Metrics.MgxFanoutMs.Value | Should -Be 134654
    }

    It 'leaves the row already pinned where it was when the next run is inconclusive' {
        # A stale conclusive number is still a measurement, and the comparison reports its age.
        # Dropping the row instead would report the scenario as unmeasured, which is the one
        # thing the file exists to prevent.
        Write-BenchFixture -Directory $script:Results -FileName '10-pacing-under-real-throttling.json' -Entries @(
            (New-BenchEntry -Result @{
                    Conclusive = $true
                    Arms       = [pscustomobject]@{
                        paced   = [pscustomobject]@{ Seconds = 61.4; ThrottleRetries = 3 }
                        unpaced = [pscustomobject]@{ Seconds = 88.2 }
                    }
                })
        )
        Export-BenchBaseline -Benchmark '10' -ResultsDirectory $script:Results -Path $script:Baseline

        # A later suite: 10 comes back inconclusive and 06 records normally, so the file is
        # rewritten around the row that must not move.
        Write-BenchFixture -Directory $script:Results -FileName '10-pacing-under-real-throttling.json' -Entries @(
            (New-BenchEntry -Result @{
                    Conclusive = $false
                    Arms       = [pscustomobject]@{
                        paced   = [pscustomobject]@{ Seconds = 399.7; ThrottleRetries = 0 }
                        unpaced = [pscustomobject]@{ Seconds = 79.8 }
                    }
                })
        )
        Write-BenchFixture -Directory $script:Results -FileName '06-fault-gauntlet.json' -Entries @(
            (New-BenchEntry -Result (New-GauntletResult -MgxMs 134654))
        )

        $warnings = $null
        Export-BenchBaseline -Benchmark '06', '10' -ResultsDirectory $script:Results -Path $script:Baseline `
            -WarningVariable warnings 3>$null

        $scenarios = (Get-Content -Path $script:Baseline -Raw | ConvertFrom-Json).Scenarios
        $scenarios.'10'.Metrics.WallSeconds.Value | Should -Be 61.4
        $scenarios.'10'.Metrics.UnpacedWallSeconds.Value | Should -Be 88.2
        $scenarios.'06'.Metrics.MgxFanoutMs.Value | Should -Be 134654
        "$warnings" | Should -BeLike '*the row already in the file is left as it was*'
    }
}

Describe 'Get-BenchScenarioMetric' {
    It 'refuses a source whose scaled value overflows, and tries the next source' {
        # None of the table's own Scale values can overflow a source that was itself finite -
        # 10's only one, WallMs at 0.001, shrinks - so this is exercised directly against a
        # scenario shaped like the real ones rather than through Export-BenchBaseline. A
        # source read within double's range is not the same thing as a source whose scaled
        # value is: '1e308' is a legitimate double, and Scale 1000 pushes it past
        # [double]::MaxValue.
        $scenario = [pscustomobject]@{
            Id            = 'S'
            Discriminator = $null
            Metrics       = @(
                @{ Name = 'ScaledSeconds'; Unit = 's'; Sources = @(@{ Path = 'Raw'; Scale = 1000.0 }, @{ Path = 'Fallback' }) }
            )
        }
        $entry = [pscustomobject]@{ Result = [pscustomobject]@{ Raw = '1e308'; Fallback = 41.5 } }

        $warnings = $null
        $read = Get-BenchScenarioMetric -Scenario $scenario -Entry $entry -WarningVariable warnings 3>$null

        "$warnings" | Should -BeLike "*S: 'ScaledSeconds' - 'Raw' holds '1e308', which scaled by 1000 is not finite - trying the next source.*"
        $read.Metrics.ScaledSeconds.Value | Should -Be 41.5
        $read.Metrics.ScaledSeconds.Path | Should -Be 'Fallback'
    }

    It 'warns and pins nothing when the only source overflows once scaled' {
        $scenario = [pscustomobject]@{
            Id            = 'S'
            Discriminator = $null
            Metrics       = @(
                @{ Name = 'ScaledSeconds'; Unit = 's'; Sources = @(@{ Path = 'Raw'; Scale = 1000.0 }) }
            )
        }
        $entry = [pscustomobject]@{ Result = [pscustomobject]@{ Raw = '1e308' } }

        $warnings = $null
        $read = Get-BenchScenarioMetric -Scenario $scenario -Entry $entry -WarningVariable warnings 3>$null

        "$warnings" | Should -BeLike "*S: 'ScaledSeconds' not recorded - 'Raw' holds '1e308', which scaled by 1000 is not finite.*"
        $read.Metrics.ScaledSeconds | Should -BeNullOrEmpty
    }

    It 'names each source by path when two of them overflow the same metric' {
        # Before the path was named here, two overflowing sources on the same metric printed
        # the identical "'1e308' scaled by <n> is not finite" for both, and the record could
        # not say which one was which. Different scales on the same raw value pin that down.
        $scenario = [pscustomobject]@{
            Id            = 'S'
            Discriminator = $null
            Metrics       = @(
                @{ Name = 'ScaledSeconds'; Unit = 's'; Sources = @(@{ Path = 'RawA'; Scale = 1000.0 }, @{ Path = 'RawB'; Scale = 1e10 }) }
            )
        }
        $entry = [pscustomobject]@{ Result = [pscustomobject]@{ RawA = '1e308'; RawB = '1e308' } }

        $warnings = $null
        $read = Get-BenchScenarioMetric -Scenario $scenario -Entry $entry -WarningVariable warnings 3>$null

        "$warnings" | Should -BeLike "*'RawA' holds '1e308', which scaled by 1000 is not finite*"
        "$warnings" | Should -BeLike "*'RawB' holds '1e308', which scaled by 10000000000 is not finite*"
        $read.Metrics.ScaledSeconds | Should -BeNullOrEmpty
    }

    It 'shows a non-number raw value the way the comparison does, not PowerShell''s default array join' {
        # Interpolating an array or object directly prints PowerShell's own join - '1 2 3' for
        # @(1,2,3), space-separated and unrecognizable as the source. ConvertTo-BenchDisplayValue
        # is what Compare-BenchBaseline already reads a non-number baseline value through; the
        # record path's warning is read the same way rather than through raw interpolation.
        $scenario = [pscustomobject]@{
            Id            = 'S'
            Discriminator = $null
            Metrics       = @(
                @{ Name = 'Metric'; Unit = 'ms'; Sources = @(@{ Path = 'Raw' }) }
            )
        }
        $entry = [pscustomobject]@{ Result = [pscustomobject]@{ Raw = @(1, 2, 3) } }

        $warnings = $null
        $read = Get-BenchScenarioMetric -Scenario $scenario -Entry $entry -WarningVariable warnings 3>$null

        # -BeLike reads a bracketed substring as a wildcard character class, not literal text,
        # so the exact message is compared instead - it also pins the whole sentence rather
        # than just the fragment that changed.
        "$warnings" | Should -Be "S: 'Metric' not recorded - 'Raw' holds '[1,2,3]', which is not a number."
        "$warnings" | Should -Not -BeLike '*1 2 3*'
        $read.Metrics.Metric | Should -BeNullOrEmpty
    }
}

Describe 'Compare-BenchBaseline' {
    BeforeEach {
        $script:Results = Join-Path $TestDrive 'results'
        $script:Baseline = Join-Path $TestDrive 'baseline.json'
        Remove-Item -Path $script:Results, $script:Baseline -Recurse -Force -ErrorAction SilentlyContinue

        Write-BenchFixture -Directory $script:Results -FileName '06-fault-gauntlet.json' -Entries @(
            (New-BenchEntry -Result (New-GauntletResult -MgxMs 134654))
        )
        Export-BenchBaseline -Benchmark '06' -ResultsDirectory $script:Results -Path $script:Baseline
    }

    It 'reports a delta on the headline metric' {
        $row = Compare-BenchBaseline -Benchmark '06' -ResultsDirectory $script:Results -BaselinePath $script:Baseline
        $row.Scenario | Should -Be '06'
        $row.Metric | Should -Be 'MgxFanoutMs'
        $row.Status | Should -Be 'compared'
        $row.DeltaPct | Should -Be 0
        $row.Flagged | Should -BeFalse
    }

    It 'flags a headline metric past the threshold without failing' {
        Write-BenchFixture -Directory $script:Results -FileName '06-fault-gauntlet.json' -Entries @(
            (New-BenchEntry -Result (New-GauntletResult -MgxMs 269308))
        )

        $row = Compare-BenchBaseline -Benchmark '06' -ResultsDirectory $script:Results -BaselinePath $script:Baseline
        $row.DeltaPct | Should -Be 100
        $row.Flagged | Should -BeTrue
        $row.Note | Should -Be 'REGRESSED'
    }

    It 'reports a scenario with no baseline entry as unmeasured rather than skipping it' {
        Write-BenchFixture -Directory $script:Results -FileName '08-delta-sync.json' -Entries @(
            (New-BenchEntry -Result @{ initial = [pscustomobject]@{ ElapsedMs = 145621 } })
        )

        $rows = Compare-BenchBaseline -Benchmark '06', '08' -ResultsDirectory $script:Results -BaselinePath $script:Baseline
        $rows.Count | Should -Be 2
        ($rows | Where-Object { $_.Scenario -eq '08' }).Status | Should -Be 'unmeasured'
    }

    It 'reports a scenario that recorded nothing this run' {
        $rows = Compare-BenchBaseline -Benchmark '06' -ResultsDirectory $script:Results `
            -BaselinePath $script:Baseline -Since (Get-Date).AddDays(1)
        $rows.Status | Should -Be 'no result'
    }

    It 'says so and returns nothing when there is no baseline' {
        $rows = Compare-BenchBaseline -Benchmark '06' -ResultsDirectory $script:Results `
            -BaselinePath (Join-Path $TestDrive 'absent.json')
        $rows | Should -BeNullOrEmpty
    }

    It 'refuses to compare two entries read from different paths' {
        # 10 records a two-arm summary through run.ps1 and a single arm when run standalone.
        # Seconds against milliseconds would read as a 1000x regression.
        Write-BenchFixture -Directory $script:Results -FileName '10-pacing-under-real-throttling.json' -Entries @(
            (New-BenchEntry -Result @{ Arms = [pscustomobject]@{
                        paced   = [pscustomobject]@{ Seconds = 39.4; ThrottleRetries = 12 }
                        unpaced = [pscustomobject]@{ Seconds = 40.3 } } })
        )
        Export-BenchBaseline -Benchmark '10' -ResultsDirectory $script:Results -Path $script:Baseline

        Write-BenchFixture -Directory $script:Results -FileName '10-pacing-under-real-throttling.json' -Entries @(
            (New-BenchEntry -Result @{ Mode = 'paced'; WallMs = 39400; ThrottleRetries = 12 })
        )

        $row = Compare-BenchBaseline -Benchmark '10' -ResultsDirectory $script:Results -BaselinePath $script:Baseline
        $row.Status | Should -Be 'not comparable'
        $row.DeltaPct | Should -BeNullOrEmpty
    }

    It 'refuses a negative baseline, naming the sign' {
        # No headline in the table can go negative today, which is what the guard is for: it
        # keeps that true of the next one added. Over a negative baseline the percentage delta
        # inverts, so -9ms against -4.5ms would be reported as a 100% regression by a figure
        # that in fact doubled in the same direction.
        $negative = [pscustomobject]@{
            SchemaVersion = 1
            GeneratedAt   = (Get-Date).ToString('o')
            Scenarios     = [pscustomobject]@{
                '06' = [pscustomobject]@{
                    Scenario = '06'
                    Script   = '06-fault-gauntlet.ps1'
                    Headline = 'MgxFanoutMs'
                    Metrics  = [pscustomobject]@{
                        MgxFanoutMs = [pscustomobject]@{ Value = -4.5; Unit = 'ms'; Path = 'mgx.ElapsedMs' }
                    }
                }
            }
        }
        ConvertTo-Json -InputObject $negative -Depth 10 | Set-Content -Path $script:Baseline

        Write-BenchFixture -Directory $script:Results -FileName '06-fault-gauntlet.json' -Entries @(
            (New-BenchEntry -Result (New-GauntletResult -MgxMs (-9)))
        )

        $row = Compare-BenchBaseline -Benchmark '06' -ResultsDirectory $script:Results `
            -BaselinePath $script:Baseline
        $row.Status | Should -Be 'not comparable'
        $row.Note | Should -BeLike '*negative*'
        $row.DeltaPct | Should -BeNullOrEmpty
    }

    It 'refuses to compare a paced arm against an unpaced baseline' {
        Write-BenchFixture -Directory $script:Results -FileName '07-adaptive-pacing.json' -Entries @(
            (New-BenchEntry -Result @{ Mode = 'unpaced'; ElapsedMs = 388108; Output = [pscustomobject]@{ failed = 0 } })
        )
        Export-BenchBaseline -Benchmark '07' -ResultsDirectory $script:Results -Path $script:Baseline

        Write-BenchFixture -Directory $script:Results -FileName '07-adaptive-pacing.json' -Entries @(
            (New-BenchEntry -Result @{ Mode = 'paced'; ElapsedMs = 388108; Output = [pscustomobject]@{ failed = 0 } })
        )

        $row = Compare-BenchBaseline -Benchmark '07' -ResultsDirectory $script:Results -BaselinePath $script:Baseline
        $row.Status | Should -Be 'not comparable'
    }

    It 'refuses a baseline holding the string "NaN" or "Infinity", naming it as a non-number' {
        # ConvertTo-Json quotes a non-finite double as a string - "NaN" and "Infinity" among
        # them - so a baseline.json promoted before Export-BenchBaseline refused a non-finite
        # measurement still holds one of those tokens today, as a string rather than a typed
        # double. Test-BenchNumericValue refuses a "NaN" string the same as it refuses any
        # other non-numeric one, so this is caught by the new gate ahead of the cast, not by
        # the finiteness check after it - a stale row like this must still be caught somewhere
        # or the comparison reports it "compared" on a NaN.
        $stale = [pscustomobject]@{
            SchemaVersion = 1
            GeneratedAt   = (Get-Date).ToString('o')
            Scenarios     = [pscustomobject]@{
                '06' = [pscustomobject]@{
                    Scenario = '06'
                    Script   = '06-fault-gauntlet.ps1'
                    Headline = 'MgxFanoutMs'
                    Metrics  = [pscustomobject]@{
                        MgxFanoutMs = [pscustomobject]@{ Value = 'NaN'; Unit = 'ms'; Path = 'mgx.ElapsedMs' }
                    }
                }
            }
        }
        ConvertTo-Json -InputObject $stale -Depth 10 | Set-Content -Path $script:Baseline

        $row = Compare-BenchBaseline -Benchmark '06' -ResultsDirectory $script:Results -BaselinePath $script:Baseline
        $row.Status | Should -Be 'not comparable'
        $row.Note | Should -Be "not comparable - the baseline value is 'NaN', which is not a number"
        $row.DeltaPct | Should -BeNullOrEmpty

        (Get-Content -Path $script:Baseline -Raw).Replace('"NaN"', '"Infinity"') | Set-Content -Path $script:Baseline

        $row2 = Compare-BenchBaseline -Benchmark '06' -ResultsDirectory $script:Results -BaselinePath $script:Baseline
        $row2.Status | Should -Be 'not comparable'
        $row2.Note | Should -Be "not comparable - the baseline value is 'Infinity', which is not a number"
        $row2.DeltaPct | Should -BeNullOrEmpty
    }

    It 'still reports a bare JSON NaN or Infinity baseline token as non-finite, not as a non-number' {
        # A typed double is exempted from the not-a-number gate above even when it is NaN or an
        # infinity, because the gate itself would otherwise refuse it as a non-number - the same
        # refusal a "NaN" string earns - and this arm exists specifically to say something more
        # precise than that about a value that did arrive as a genuine double. The only way a
        # baseline.json value round-trips through ConvertFrom-Json as a typed double rather than
        # a string is a bare, unquoted token, which is not standard JSON but is what a pre-guard
        # Export-BenchBaseline could still have written raw.
        New-Item -ItemType Directory -Path (Split-Path -Parent $script:Baseline) -Force | Out-Null
        $raw = @"
{
  "SchemaVersion": 1,
  "GeneratedAt": "$((Get-Date).ToString('o'))",
  "Scenarios": {
    "06": {
      "Scenario": "06",
      "Script": "06-fault-gauntlet.ps1",
      "Headline": "MgxFanoutMs",
      "Metrics": { "MgxFanoutMs": { "Value": NaN, "Unit": "ms", "Path": "mgx.ElapsedMs" } }
    }
  }
}
"@
        Set-Content -Path $script:Baseline -Value $raw

        $row = Compare-BenchBaseline -Benchmark '06' -ResultsDirectory $script:Results -BaselinePath $script:Baseline
        $row.Status | Should -Be 'not comparable'
        $row.Note | Should -Be 'not comparable - the baseline value is NaN'
        $row.DeltaPct | Should -BeNullOrEmpty
    }

    It 'reports "INCONCLUSIVE", a boolean and an array as not comparable, naming each, with a healthy row still compared' {
        # A stale baseline.json can hold anything a prior run's Value field once did - an
        # inconclusive string, a boolean the old [double] cast would have silently read as 1,
        # or an array or object nothing in the table ever wrote but nothing stopped from being
        # written by hand. Three different shapes on three different scenarios, and a fourth,
        # healthy scenario in the same call must still compare rather than the whole request
        # failing on the first broken row.
        Write-BenchFixture -Directory $script:Results -FileName '01-list-users.json' -Entries @(
            (New-BenchEntry -Result @{ mgx = [pscustomobject]@{ Median = [pscustomobject]@{ ElapsedMs = 4200; PeakWorkingSetMB = 128 } } })
        )
        Write-BenchFixture -Directory $script:Results -FileName '02-fanout-lookup.json' -Entries @(
            (New-BenchEntry -Result @{ mgx = [pscustomobject]@{ Median = [pscustomobject]@{ ElapsedMs = 6100; PeakWorkingSetMB = 96 } } })
        )
        Write-BenchFixture -Directory $script:Results -FileName '03-user-report.json' -Entries @(
            (New-BenchEntry -Result @{ mgx = [pscustomobject]@{ Median = [pscustomobject]@{ ElapsedMs = 8300; PeakWorkingSetMB = 64 } } })
        )

        # '06' rides along from the Describe's own BeforeEach, which already promoted a healthy
        # baseline and result for it - carried forward here as the fourth, unbroken row rather
        # than lost when the other three are written into the same file.
        $healthy06 = (Get-Content -Path $script:Baseline -Raw | ConvertFrom-Json).Scenarios.'06'

        $stale = [pscustomobject]@{
            SchemaVersion = 1
            GeneratedAt   = (Get-Date).ToString('o')
            Scenarios     = [pscustomobject]@{
                '01' = [pscustomobject]@{
                    Scenario = '01'; Script = '01-list-users.ps1'; Headline = 'MgxListMs'
                    Metrics  = [pscustomobject]@{ MgxListMs = [pscustomobject]@{ Value = 'INCONCLUSIVE'; Unit = 'ms'; Path = 'mgx.Median.ElapsedMs' } }
                }
                '02' = [pscustomobject]@{
                    Scenario = '02'; Script = '02-fanout-lookup.ps1'; Headline = 'MgxFanoutMs'
                    Metrics  = [pscustomobject]@{ MgxFanoutMs = [pscustomobject]@{ Value = $true; Unit = 'ms'; Path = 'mgx.Median.ElapsedMs' } }
                }
                '03' = [pscustomobject]@{
                    Scenario = '03'; Script = '03-user-report.ps1'; Headline = 'MgxReportMs'
                    Metrics  = [pscustomobject]@{ MgxReportMs = [pscustomobject]@{ Value = @(1, 2); Unit = 'ms'; Path = 'mgx.Median.ElapsedMs' } }
                }
                '06' = $healthy06
            }
        }
        ConvertTo-Json -InputObject $stale -Depth 10 | Set-Content -Path $script:Baseline

        $rows = Compare-BenchBaseline -Benchmark '01', '02', '03', '06' -ResultsDirectory $script:Results -BaselinePath $script:Baseline

        $byId = @{}
        foreach ($row in $rows) { $byId[$row.Scenario] = $row }

        $byId['01'].Status | Should -Be 'not comparable'
        $byId['01'].Note | Should -Be "not comparable - the baseline value is 'INCONCLUSIVE', which is not a number"
        $byId['02'].Status | Should -Be 'not comparable'
        $byId['02'].Note | Should -Be "not comparable - the baseline value is 'True', which is not a number"
        $byId['03'].Status | Should -Be 'not comparable'
        $byId['03'].Note | Should -Be "not comparable - the baseline value is '[1,2]', which is not a number"

        $byId['06'].Status | Should -Be 'compared'
        $byId['06'].DeltaPct | Should -Be 0
    }

    It 'refuses a current value that is NaN, naming it' {
        # Get-BenchScenarioMetric now refuses every path that could hand back a non-finite
        # pinned value - a raw non-number before scaling, an overflow after it - so nothing
        # this release ships can make a live run's Current arrive here non-finite. The guard
        # below exists for the same reason the baseline one does: a value that arrives
        # non-finite by some path not yet closed must be refused rather than compared, not
        # silently trusted because today's callers happen not to produce one. Standing in for
        # Get-BenchScenarioMetric is the only way to reach that guard, since nothing else can.
        Mock -CommandName Get-BenchScenarioMetric -MockWith {
            [pscustomobject]@{
                Metrics       = [ordered]@{
                    MgxFanoutMs = [pscustomobject]@{ Value = [double]::NaN; Unit = 'ms'; Path = 'mgx.ElapsedMs' }
                }
                Discriminator = $null
            }
        }

        $row = Compare-BenchBaseline -Benchmark '06' -ResultsDirectory $script:Results -BaselinePath $script:Baseline
        $row.Status | Should -Be 'not comparable'
        $row.Note | Should -Be "not comparable - this run's value is NaN"
        $row.DeltaPct | Should -BeNullOrEmpty
    }

    It 'refuses a baseline with no Scenarios, naming the file, instead of indexing into null' {
        # $baseline.Scenarios.PSObject.Properties[...] used to be reached unguarded: a missing
        # key and an explicit null both read back as $null, and indexing into that threw
        # "Cannot index into a null array" without naming the file or which field was absent.
        foreach ($text in @(
                '{"SchemaVersion":1,"GeneratedAt":"2026-01-01T00:00:00Z"}',
                '{"SchemaVersion":1,"GeneratedAt":"2026-01-01T00:00:00Z","Scenarios":null}'
            )) {
            Set-Content -Path $script:Baseline -Value $text

            $thrown = $null
            try { Compare-BenchBaseline -Benchmark '06' -ResultsDirectory $script:Results -BaselinePath $script:Baseline }
            catch { $thrown = $_ }

            $thrown.Exception.Message | Should -Be "Baseline '$script:Baseline' carries no Scenarios; record one with -RecordBaseline before comparing."
        }
    }

    It 'refuses a baseline whose SchemaVersion is not the one Export-BenchBaseline writes' {
        $scenarios = (Get-Content -Path $script:Baseline -Raw | ConvertFrom-Json).Scenarios
        $wrongSchema = [pscustomobject]@{
            SchemaVersion = 2
            GeneratedAt   = (Get-Date).ToString('o')
            Scenarios     = $scenarios
        }
        ConvertTo-Json -InputObject $wrongSchema -Depth 10 | Set-Content -Path $script:Baseline

        $thrown = $null
        try { Compare-BenchBaseline -Benchmark '06' -ResultsDirectory $script:Results -BaselinePath $script:Baseline }
        catch { $thrown = $_ }

        $thrown.Exception.Message | Should -Be "Baseline '$script:Baseline' is schema 2; this suite reads schema 1."
    }
}
