#Requires -Modules Pester

<#
    The load shape benchmark 10 issues, and whether it can reach the throttled regime, in
    tests/benchmarks/common.ps1.

    The thresholds are passed in rather than read from results/, so these run the same
    arithmetic on every machine whatever any local calibration says. The numbers used below
    are the ones benchmark 13 measured against the seeded tenant: 22,954 RU of burst allowance
    and 731 RU/s served while refusing.
#>

BeforeAll {
    $script:BenchRoot = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'benchmarks'

    $script:CultureBefore = [System.Threading.Thread]::CurrentThread.CurrentCulture
    . (Join-Path $script:BenchRoot 'common.ps1')

    $script:Seeded = @{ BurstRu = 22954; SustainedRuPerSecond = 731 }

    function Get-Shape
    {
        param
        (
            [int] $Count = 60000,
            [int] $Pipelines = 12,
            [int] $Concurrency = 128,
            [int] $IdPool = 5000
        )

        return Get-BenchPacingLoadShape -Count $Count -Pipelines $Pipelines `
            -Concurrency $Concurrency -IdPool $IdPool @script:Seeded
    }

    # The defaults in the script's own param block. A default that stops being admissible is the
    # failure this benchmark shipped once already, so the test reads them out of the file.
    $script:ScriptDefaults = @{}
    $text = Get-Content -Path (Join-Path $script:BenchRoot '10-pacing-under-real-throttling.ps1') -Raw
    foreach ($name in 'Count', 'Pipelines', 'Concurrency', 'IdPool')
    {
        $match = [regex]::Match($text, "\[int\]\s*\`$$name\s*=\s*(\d+)")
        if ($match.Success) { $script:ScriptDefaults[$name] = [int]$match.Groups[1].Value }
    }
}

AfterAll {
    [System.Threading.Thread]::CurrentThread.CurrentCulture = $script:CultureBefore
}

Describe 'Get-BenchPacingLoadShape' {
    It 'issues distinct ids only, so the request count is Pipelines x IdsPerPipeline' {
        # Invoke-MgxRequest deduplicates the ids it is piped: a slice that repeats one issues a
        # single request for it, and the arm reports a volume it never sent.
        $shape = Get-Shape -Count 60000 -Pipelines 12 -IdPool 5000
        $shape.IdsPerPipeline | Should -Be 5000
        $shape.TotalRequests | Should -Be 60000
        $shape.TotalRequests | Should -Be ($shape.IdsPerPipeline * $shape.Pipelines)
        $shape.Clamped | Should -BeFalse
    }

    It 'clamps a -Count the pool cannot cover and says it clamped' {
        # 120,000 over 8 pipelines of a 5,000 id pool is 40,000 requests, whatever was asked for.
        $shape = Get-Shape -Count 120000 -Pipelines 8 -IdPool 5000
        $shape.Requested | Should -Be 120000
        $shape.IdsPerPipeline | Should -Be 5000
        $shape.TotalRequests | Should -Be 40000
        $shape.Clamped | Should -BeTrue
    }

    It 'divides a -Count under the ceiling evenly and does not clamp' {
        $shape = Get-Shape -Count 24000 -Pipelines 12 -IdPool 5000
        $shape.IdsPerPipeline | Should -Be 2000
        $shape.TotalRequests | Should -Be 24000
        $shape.Clamped | Should -BeFalse
    }

    It 'scales the projected draw with the pipelines and the concurrency' {
        (Get-Shape -Pipelines 12 -Concurrency 128).ExpectedRuPerSecond | Should -Be 2160
        (Get-Shape -Pipelines 8 -Concurrency 128).ExpectedRuPerSecond | Should -Be 1440
        (Get-Shape -Pipelines 12 -Concurrency 64).ExpectedRuPerSecond | Should -Be 1080
    }

    It 'refuses a set whose rate cannot drain the allowance, whatever its volume' {
        # The point the first version of the benchmark missed: no -Count exhausts a burst
        # allowance that refills faster than the load draws it down.
        $shape = Get-Shape -Count 500000 -Pipelines 2 -IdPool 250000
        $shape.TotalRequests | Should -Be 500000
        $shape.ExpectedRuPerSecond | Should -Be 360
        $shape.RateClears | Should -BeFalse
        $shape.Admissible | Should -BeFalse
        $shape.Reasons -join ' ' | Should -Match 'volume cannot buy a rate'
    }

    It 'refuses a set that never spends past the allowance' {
        $shape = Get-Shape -Count 12000 -Pipelines 12 -IdPool 5000
        $shape.RateClears | Should -BeTrue
        $shape.VolumeClears | Should -BeFalse
        $shape.Admissible | Should -BeFalse
        $shape.ThrottledSeconds | Should -Be 0
    }

    It 'admits a set that clears both, and times the two phases' {
        $shape = Get-Shape -Count 60000 -Pipelines 12 -IdPool 5000
        $shape.RateClears | Should -BeTrue
        $shape.VolumeClears | Should -BeTrue
        $shape.Admissible | Should -BeTrue
        $shape.Reasons | Should -BeNullOrEmpty

        # 22,954 RU at 2,160 RU/s, then the remaining 37,046 at the 731 RU/s served while refusing.
        $shape.BurstSeconds | Should -Be 10.6
        $shape.ThrottledSeconds | Should -Be 50.7
        $shape.EstimatedSeconds | Should -Be 61.3
    }
}

Describe 'Get-BenchPipelineSlice' {
    BeforeAll {
        $script:Pool = 1..5000 | ForEach-Object { "id-$_" }
    }

    It 'gives every pipeline its own count of ids, none of them repeated' {
        # The invariant the arm's request count rests on. A repeated id inside one pipeline is
        # deduplicated away by Invoke-MgxRequest and never sent.
        $slices = Get-BenchPipelineSlice -Pool $script:Pool -Pipelines 12 -IdsPerPipeline 5000

        $slices.Count | Should -Be 12
        foreach ($slice in $slices)
        {
            $slice.Count | Should -Be 5000
            @($slice | Sort-Object -Unique).Count | Should -Be 5000
        }
    }

    It 'staggers where the pipelines start so they are not all on one object' {
        $slices = Get-BenchPipelineSlice -Pool $script:Pool -Pipelines 12 -IdsPerPipeline 100

        @($slices | ForEach-Object { $_[0] } | Sort-Object -Unique).Count | Should -Be 12
    }

    It 'holds the invariant for a short slice and for a pool smaller than the pipeline count' {
        foreach ($case in @(
                @{ Pipelines = 12; Ids = 2000 },
                @{ Pipelines = 3;  Ids = 5000 },
                @{ Pipelines = 1;  Ids = 1 }))
        {
            $slices = Get-BenchPipelineSlice -Pool $script:Pool -Pipelines $case.Pipelines -IdsPerPipeline $case.Ids
            foreach ($slice in $slices) { @($slice | Sort-Object -Unique).Count | Should -Be $case.Ids }
        }

        $tiny = Get-BenchPipelineSlice -Pool @('a', 'b', 'c') -Pipelines 8 -IdsPerPipeline 3
        $tiny.Count | Should -Be 8
        foreach ($slice in $tiny) { @($slice | Sort-Object -Unique).Count | Should -Be 3 }
    }

    It 'refuses to hand out more distinct ids than the pool holds' {
        { Get-BenchPipelineSlice -Pool @('a', 'b') -Pipelines 2 -IdsPerPipeline 3 } |
            Should -Throw -ExpectedMessage '*deduplicates*'
    }
}

Describe 'benchmark 10 defaults' {
    It 'reads four defaults out of the script' {
        $script:ScriptDefaults.Keys.Count | Should -Be 4
    }

    It 'ships a default run that is admissible on the calibrated tenant' {
        $shape = Get-BenchPacingLoadShape -Count $script:ScriptDefaults.Count `
            -Pipelines $script:ScriptDefaults.Pipelines `
            -Concurrency $script:ScriptDefaults.Concurrency `
            -IdPool $script:ScriptDefaults.IdPool @script:Seeded

        $shape.Clamped | Should -BeFalse -Because 'a default that asks for more than it can issue reports a volume it never sent'
        $shape.Admissible | Should -BeTrue -Because 'run.ps1 passes no arguments, so the defaults are what gets measured'
    }
}
