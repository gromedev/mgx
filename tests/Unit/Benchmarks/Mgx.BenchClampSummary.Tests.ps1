#Requires -Modules Pester

<#
    Get-ClampSummary and the functions it composes (Get-ClampClockGap, Get-ClampBuckets,
    Get-ClampOverall, Get-ClampHeadline) in tests/benchmarks/common.ps1 - the summary
    18-spo-latency-clamp.ps1 builds from its checkpointed rows.jsonl once a load finishes.

    No tenant and no live load: every case writes a synthetic rows.jsonl under $TestDrive and
    calls Get-ClampSummary on its path directly, the same call the script makes.
#>

BeforeAll {
    $script:BenchRoot = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'benchmarks'

    # common.ps1 pins the thread to invariant culture so benchmark output never prints 39,2s.
    # Right for a benchmark process, and not something to leave behind in a test run.
    $script:CultureBefore = [System.Threading.Thread]::CurrentThread.CurrentCulture
    . (Join-Path $script:BenchRoot 'common.ps1')

    # One row, in the shape 18 writes to its rows.jsonl. ElapsedMs and ErrorText are the two
    # fields each case varies; the rest are fixed because nothing here reads them.
    function script:Write-ClampRow {
        param(
            [Parameter(Mandatory)] [string] $Path,
            [Parameter(Mandatory)] [datetime] $Timestamp,
            [Parameter(Mandatory)] [int] $Status,
            [object] $ElapsedMs = $null,
            [string] $ErrorText = $null,
            [int] $Worker = 1
        )
        $row = [pscustomobject]@{
            Timestamp = $Timestamp.ToString('o'); Worker = $Worker; Label = 'item'; Uri = '/drives/x/items/1'
            Attempt = 1; Attempts = 1; Status = $Status; ElapsedMs = $ElapsedMs
            CallElapsedMs = 100; PacingWaitMs = $null
            ResourceUnit = $null; ThrottleLimitPercentage = $null; ThrottleScope = $null
            RetryAfter = $null; RateLimitHeader = $false
            Error = $ErrorText
        }
        Add-Content -Path $Path -Value (ConvertTo-Json -InputObject $row -Compress -Depth 4)
    }
}

AfterAll {
    [System.Threading.Thread]::CurrentThread.CurrentCulture = $script:CultureBefore
}

Describe 'Get-ClampSummary' {
    BeforeEach {
        $script:RowsPath = Join-Path $TestDrive "$([guid]::NewGuid()).jsonl"
        $script:Started = [datetime]'2026-01-01T00:00:00Z'
    }

    Context 'the clock gap' {
        It 'refuses and names the gap when the wall clock outran the stopwatch by more than a bucket' {
            # 20 minutes wall clock, a 5-minute Stopwatch - the 5757093 run's own shape, a
            # machine sleep the Stopwatch does not advance through.
            Write-ClampRow -Path $script:RowsPath -Timestamp $script:Started.AddMinutes(1) -Status 200 -ElapsedMs 100
            $ended = $script:Started.AddMinutes(20)

            $summary = Get-ClampSummary -RowsPath $script:RowsPath -StartedAt $script:Started -EndedAt $ended `
                -WallMs 300000 -Minutes 20 -BucketMinutes 5

            $summary.Refused | Should -BeTrue
            $summary.GapMinutes | Should -Be 15
            $summary.RefusalReason | Should -Be 'the machine was not awake for 15 minute(s) of the window; rows kept, summary withheld'
            $summary.Buckets | Should -BeNullOrEmpty
            $summary.Overall | Should -BeNullOrEmpty
        }

        It 'does not refuse when the wall clock and the stopwatch agree within a bucket' {
            Write-ClampRow -Path $script:RowsPath -Timestamp $script:Started.AddMinutes(1) -Status 200 -ElapsedMs 100
            $ended = $script:Started.AddMinutes(2)

            # A 1-second wall/Stopwatch drift - ordinary scheduling noise, not a sleep.
            $summary = Get-ClampSummary -RowsPath $script:RowsPath -StartedAt $script:Started -EndedAt $ended `
                -WallMs 119000 -Minutes 2 -BucketMinutes 5

            $summary.Refused | Should -BeFalse
            $summary.Overall | Should -Not -BeNullOrEmpty
        }
    }

    Context 'buckets' {
        It 'marks a bucket with no rows rather than omitting it, filling every index in the window' {
            # Rows land in minute 1 (bucket 0) and minute 12 (bucket 2); bucket 1 (minutes
            # 5-10) gets none, the way buckets 2 and 3 got none across the 5757093 run's sleep.
            Write-ClampRow -Path $script:RowsPath -Timestamp $script:Started.AddMinutes(1) -Status 200 -ElapsedMs 100
            Write-ClampRow -Path $script:RowsPath -Timestamp $script:Started.AddMinutes(12) -Status 200 -ElapsedMs 100
            $ended = $script:Started.AddMinutes(15)

            $summary = Get-ClampSummary -RowsPath $script:RowsPath -StartedAt $script:Started -EndedAt $ended `
                -WallMs ([long]($ended - $script:Started).TotalMilliseconds) -Minutes 15 -BucketMinutes 5

            $summary.Refused | Should -BeFalse
            $summary.Buckets.Count | Should -Be 3
            $summary.Buckets[1].Bucket | Should -Be 1
            $summary.Buckets[1].Attempts | Should -Be 0
            $summary.Buckets[1].Note | Should -Be 'no rows'
            $summary.Buckets[0].Note | Should -BeNullOrEmpty
            $summary.Buckets[2].Note | Should -BeNullOrEmpty
        }
    }

    Context 'errors' {
        It 'counts a Status-0 refusal in Errors and groups it by the text before its first colon' {
            Write-ClampRow -Path $script:RowsPath -Timestamp $script:Started.AddMinutes(1) -Status 200 -ElapsedMs 100
            1..3 | ForEach-Object {
                Write-ClampRow -Path $script:RowsPath -Timestamp $script:Started.AddMinutes(1) -Status 0 `
                    -ErrorText 'Circuit breaker tripped: too many failures caused Mgx to temporarily stop requests. Wait 15s.'
            }
            Write-ClampRow -Path $script:RowsPath -Timestamp $script:Started.AddMinutes(1) -Status 0 -ErrorText 'no response traced'
            $ended = $script:Started.AddMinutes(2)

            $summary = Get-ClampSummary -RowsPath $script:RowsPath -StartedAt $script:Started -EndedAt $ended `
                -WallMs ([long]($ended - $script:Started).TotalMilliseconds) -Minutes 2 -BucketMinutes 5

            $summary.Overall.Errors | Should -Be 4
            $summary.Buckets[0].Errors | Should -Be 4
            $summary.Overall.Refusals['Circuit breaker tripped'] | Should -Be 3
            $summary.Overall.Refusals['no response traced'] | Should -Be 1
        }
    }

    Context 'the headline' {
        It 'takes the headline from the bucket distribution, not a single late sample' {
            # Bucket 0: nine calls at 200ms, then one late straggler at 1864ms - the shape of
            # the 5757093 run's own last row, which PacingState quoted as though it were the
            # headline. Nearest-rank p90 of ten values with one high outlier is still 200.
            1..9 | ForEach-Object { Write-ClampRow -Path $script:RowsPath -Timestamp $script:Started.AddMinutes(1) -Status 200 -ElapsedMs 200 }
            Write-ClampRow -Path $script:RowsPath -Timestamp $script:Started.AddMinutes(1) -Status 200 -ElapsedMs 1864
            # Bucket 1: nine calls at 900ms - the worst bucket, and the one the headline names.
            1..9 | ForEach-Object { Write-ClampRow -Path $script:RowsPath -Timestamp $script:Started.AddMinutes(6) -Status 200 -ElapsedMs 900 }
            $ended = $script:Started.AddMinutes(10)

            $summary = Get-ClampSummary -RowsPath $script:RowsPath -StartedAt $script:Started -EndedAt $ended `
                -WallMs ([long]($ended - $script:Started).TotalMilliseconds) -Minutes 10 -BucketMinutes 5

            $summary.Buckets[0].P90ElapsedMs | Should -Be 200
            $summary.Headline.WorstBucketFromMinute | Should -Be 5
            $summary.Headline.WorstBucketP90ElapsedMs | Should -Be 900
            $summary.Headline.Sentence | Should -BeLike '*900*'
            $summary.Headline.Sentence | Should -Not -BeLike '*1864*'
        }
    }
}
