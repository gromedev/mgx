#Requires -Modules Pester

<#
    Set-Plan's window validation, served counting under a plan fault on the entity route,
    and slowBody's chunks: 1 timing, all in tests/benchmarks/mock-graph-server.ps1.

    A real child process on a real socket, the same way 06 and the pathological gauntlets use
    it: the plan screen, the disposition a fault hands back, and a trickled body are exactly
    the things no in-process fake stands in for.
#>

BeforeAll {
    $script:BenchRoot = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'benchmarks'

    # common.ps1 pins the thread to invariant culture; not something to leave behind in a
    # test run.
    $script:CultureBefore = [System.Threading.Thread]::CurrentThread.CurrentCulture
    . (Join-Path $script:BenchRoot 'common.ps1')

    $script:Server = Start-BenchMockServer -Port 9970
    $script:Base = $script:Server.BaseUrl

    # Posts a plan without Invoke-RestMethod's default throw-on-4xx, so a refusal is a value
    # to assert on rather than an exception to catch. Read inside this function's own scope
    # rather than relied on to cross into the caller's, so the answer does not depend on
    # exactly how -StatusCodeVariable propagates. Defined here, in BeforeAll, rather than at
    # the script's top level: Pester's discovery pass does not carry a plain top-level
    # function into the It blocks that run afterward.
    function Set-MockPlan([hashtable[]] $Rules) {
        $body = @{ rules = $Rules } | ConvertTo-Json -Depth 6 -Compress
        $response = Invoke-RestMethod "$script:Base/plan" -Method Post -ContentType 'application/json' `
            -Body $body -SkipHttpErrorCheck -StatusCodeVariable 'statusCode'
        [pscustomobject]@{ StatusCode = $statusCode; Body = $response }
    }
}

AfterAll {
    Stop-BenchMockServer $script:Server
    [System.Threading.Thread]::CurrentThread.CurrentCulture = $script:CultureBefore
}

Describe 'Set-Plan refuses a zero window or count' {
    BeforeEach {
        $null = Invoke-RestMethod "$script:Base/plan" -Method Post -ContentType 'application/json' -Body '{"rules":[]}'
    }

    It 'refuses an explicit from: 0' {
        $result = Set-MockPlan @(@{ route = '^/v1\.0/users/1$'; kind = 'outage'; from = 0 })
        $result.StatusCode | Should -Be 400
        $result.Body.error.code | Should -Be 'MockPlanRejected'
        $result.Body.error.message | Should -Be "rule 1: 'from' must be at least 1, got 0"
    }

    It 'refuses an explicit count: 0' {
        $result = Set-MockPlan @(@{ route = '^/v1\.0/users/1$'; kind = 'outage'; count = 0 })
        $result.StatusCode | Should -Be 400
        $result.Body.error.message | Should -Be "rule 1: 'count' must be at least 1, got 0"
    }

    It 'refuses an explicit to: 0' {
        $result = Set-MockPlan @(@{ route = '^/v1\.0/users/1$'; kind = 'outage'; to = 0 })
        $result.StatusCode | Should -Be 400
        $result.Body.error.message | Should -Be "rule 1: 'to' must be at least 1, got 0"
    }

    It 'refuses an explicit seconds: 0' {
        $result = Set-MockPlan @(@{ route = '^/v1\.0/users/1$'; kind = 'delayedVisibility'; seconds = 0 })
        $result.StatusCode | Should -Be 400
        $result.Body.error.message | Should -Be "rule 1: 'seconds' must be at least 1, got 0"
    }

    It 'names the failing rule by its 1-based position' {
        $result = Set-MockPlan @(
            @{ route = '^/v1\.0/users/2$'; kind = 'outage' }
            @{ route = '^/v1\.0/users/3$'; kind = 'outage'; count = 0 }
        )
        $result.StatusCode | Should -Be 400
        $result.Body.error.message | Should -Be "rule 2: 'count' must be at least 1, got 0"
    }

    It 'still accepts a rule with no window fields at all, meaning no end' {
        $result = Set-MockPlan @(@{ route = '^/v1\.0/users/9$'; kind = 'outage' })
        $result.StatusCode | Should -Be 200
        $result.Body.ok | Should -BeTrue
    }

    It 'still accepts from and count set at their floor of 1' {
        $result = Set-MockPlan @(@{ route = '^/v1\.0/users/9$'; kind = 'outage'; from = 1; count = 1 })
        $result.StatusCode | Should -Be 200
        $result.Body.ok | Should -BeTrue
    }
}

Describe 'served counts a plan-faulted entity read the way pagesServed counts a cut page' {
    BeforeEach {
        $null = Invoke-RestMethod "$script:Base/plan" -Method Post -ContentType 'application/json' -Body '{"rules":[]}'
        $null = Invoke-RestMethod "$script:Base/reset"
    }

    It 'leaves served unchanged while a throttleStorm rule is firing' {
        $null = Set-MockPlan @(@{ route = '^/v1\.0/users/42$'; kind = 'throttleStorm'; count = 3 })
        $before = Invoke-RestMethod "$script:Base/stats"
        1..3 | ForEach-Object { Invoke-RestMethod "$script:Base/v1.0/users/42" -SkipHttpErrorCheck | Out-Null }
        $after = Invoke-RestMethod "$script:Base/stats"
        ($after.served - $before.served) | Should -Be 0
        ($after.throttleStorm - $before.throttleStorm) | Should -Be 3
    }

    It 'raises served by one once the window closes and the entity is answered whole' {
        $null = Set-MockPlan @(@{ route = '^/v1\.0/users/43$'; kind = 'throttleStorm'; count = 3 })
        $before = Invoke-RestMethod "$script:Base/stats"
        1..4 | ForEach-Object { Invoke-RestMethod "$script:Base/v1.0/users/43" -SkipHttpErrorCheck | Out-Null }
        $after = Invoke-RestMethod "$script:Base/stats"
        ($after.served - $before.served) | Should -Be 1
    }
}

Describe 'Send-Slow honors bodyMs with a single chunk' {
    BeforeEach {
        $null = Invoke-RestMethod "$script:Base/plan" -Method Post -ContentType 'application/json' -Body '{"rules":[]}'
    }

    It 'takes about bodyMs, not an instant write, when chunks is 1' {
        $null = Set-MockPlan @(@{ route = '^/v1\.0/users/777$'; kind = 'slowBody'; chunks = 1; bodyMs = 1500 })
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        Invoke-RestMethod "$script:Base/v1.0/users/777" | Out-Null
        $sw.Stop()
        $sw.ElapsedMilliseconds | Should -BeGreaterThan 1300
        $sw.ElapsedMilliseconds | Should -BeLessThan 5000
    }
}
