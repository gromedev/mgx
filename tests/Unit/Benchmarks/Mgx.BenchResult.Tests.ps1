#Requires -Modules Pester

<#
    What a recorded benchmark result says about the run that produced it, in
    tests/benchmarks/common.ps1: the documented budget tiers, and the tenant and tree identity
    Write-BenchResult stamps on every entry.

    No tenant and no Graph session: Get-MgContext, the count call and the telemetry snapshot
    are redefined in the scope common.ps1 is dot-sourced into, which is where Write-BenchResult
    resolves them from, and every entry is written under $TestDrive.
#>

BeforeAll {
    $script:BenchRoot = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'benchmarks'

    # common.ps1 pins the thread to invariant culture so benchmark output never prints 39,2s.
    # Right for a benchmark process, and not something to leave behind in a test run.
    $script:CultureBefore = [System.Threading.Thread]::CurrentThread.CurrentCulture
    . (Join-Path $script:BenchRoot 'common.ps1')

    # The faked tenant and the faked counters, each with CmdletBinding because the callers
    # below pass -ErrorAction. Defined after the dot-source and in the same scope, so these
    # are the definitions the real code finds.
    $script:FakeTenantId = '00000000-1111-2222-3333-444444444444'
    $script:FakeUserCount = 19

    function Get-MgContext
    {
        [CmdletBinding()]
        param ()

        if (-not $script:FakeSession) { return $null }
        return [pscustomobject]@{ TenantId = $script:FakeTenantId; ClientId = 'probe'; AuthType = 'AppOnly' }
    }

    function Get-BenchDirectoryObjectCount
    {
        [CmdletBinding()]
        param ()

        $script:CountCalls++
        return [long]$script:FakeUserCount
    }

    # 1,750 units over a 5,000 ms wall clock is 350 RU/s exactly, so the recorded fraction
    # names its own denominator: 1.0 against the S tier's 350, 0.438 against the 800 of a
    # tenant whose tier is not known.
    function Get-MgxTelemetry
    {
        [CmdletBinding()]
        param ()

        return [pscustomobject]@{
            ResourceUnitsConsumed     = 1750
            Requests                  = 1750
            Succeeded                 = 1750
            Failed                    = 0
            ThrottleRetries           = 0
            OtherRetries              = 0
            AdaptivePacingWaitMs      = 0
            AdaptivePacingActivations = 0
            RateLimiterWaitMs         = 0
            LastThrottlePercentage    = -1.0
            PacingState               = 'directory: latency 100ms (1.0x of 100ms baseline)'
        }
    }

    function Write-BenchProbe
    {
        param
        (
            [Parameter(Mandatory)] [string] $Name,
            [switch] $Session,
            [long] $UserCount = 19,
            [long] $WallMs = 5000,
            [string] $TenantId = '00000000-1111-2222-3333-444444444444',
            [object] $Identity
        )

        $script:FakeSession = [bool]$Session
        $script:FakeUserCount = $UserCount
        $script:FakeTenantId = $TenantId
        $script:CountCalls = 0
        Get-BenchTenantFact -Refresh | Out-Null

        # Passed on only when the caller passed one, so the probe covers both the supplied and
        # the unsupplied path rather than turning $null into an identity of five nulls.
        $supplied = @{}
        if ($PSBoundParameters.ContainsKey('Identity')) { $supplied['Identity'] = $Identity }

        Write-BenchResult -Benchmark $Name -ResultsDirectory $TestDrive -WallMs $WallMs `
            -Result ([pscustomobject]@{ Probe = $Name }) @supplied | Out-Null

        return @(Get-Content -Path (Join-Path $TestDrive "$Name.json") -Raw | ConvertFrom-Json)[-1]
    }
}

AfterAll {
    [System.Threading.Thread]::CurrentThread.CurrentCulture = $script:CultureBefore
}

Describe 'Get-BenchDocumentedBudget' {
    It 'puts <UserCount> users in tier <Tier> at <RuPerSecond> RU/s' -ForEach @(
        @{ UserCount = 0;      Tier = 'S'; RuPerSecond = 350 }
        @{ UserCount = 19;     Tier = 'S'; RuPerSecond = 350 }
        @{ UserCount = 49;     Tier = 'S'; RuPerSecond = 350 }
        @{ UserCount = 50;     Tier = 'M'; RuPerSecond = 500 }
        @{ UserCount = 500;    Tier = 'M'; RuPerSecond = 500 }
        @{ UserCount = 501;    Tier = 'L'; RuPerSecond = 800 }
        @{ UserCount = 130233; Tier = 'L'; RuPerSecond = 800 }
    ) {
        $budget = Get-BenchDocumentedBudget -UserCount $UserCount
        $budget.Tier | Should -Be $Tier
        $budget.RuPerSecond | Should -Be $RuPerSecond
        $budget.RuPer10Seconds | Should -Be ($RuPerSecond * 10)
        $budget.UserCount | Should -Be $UserCount
        $budget.Band | Should -Not -BeNullOrEmpty
    }

    It 'answers null for a count it does not have' {
        # An assumed tier is worse than none: it would put a denominator nobody measured into
        # the ratio and label it documented.
        Get-BenchDocumentedBudget -UserCount $null | Should -BeNullOrEmpty
        Get-BenchDocumentedBudget -UserCount -1 | Should -BeNullOrEmpty
    }
}

Describe 'Write-BenchResult metadata' {
    It 'records the tenant, its size and its documented budget' {
        $entry = Write-BenchProbe -Name 'tenant' -Session -UserCount 19

        $entry.TenantId | Should -Be $script:FakeTenantId
        $entry.DirectoryObjectCount | Should -Be 19
        $entry.DocumentedBudgetRuPerSecond | Should -Be 350
    }

    It 'treats a session whose tenant id is not a GUID as no tenant' {
        # 06 connects a mock Graph and names the tenant 'gauntlet-mock-tenant'. There is no
        # directory behind that to count, and the benchmark has to stay off the network.
        $entry = Write-BenchProbe -Name 'mocktenant' -Session -TenantId 'gauntlet-mock-tenant'

        $entry.TenantId | Should -BeNullOrEmpty
        $entry.DirectoryObjectCount | Should -BeNullOrEmpty
        $script:CountCalls | Should -Be 0 -Because 'a mock session must not be counted against a real tenant'
    }

    It 'records those three as null rather than failing with no session' {
        # 06 and the mock-server benchmarks never connect. They still have to record.
        $entry = Write-BenchProbe -Name 'notenant'

        $entry.PSObject.Properties.Name | Should -Contain 'TenantId'
        $entry.TenantId | Should -BeNullOrEmpty
        $entry.DirectoryObjectCount | Should -BeNullOrEmpty
        $entry.DocumentedBudgetRuPerSecond | Should -BeNullOrEmpty
        $entry.Result.Probe | Should -Be 'notenant'
    }

    It 'records the commit of the tree that ran, with a dirty marker' {
        $entry = Write-BenchProbe -Name 'commit'

        # Null outside a checkout, which CI is not - so assert the shape wherever there is one.
        if ($entry.Commit)
        {
            $entry.Commit | Should -Match '^[0-9a-f]{7,40}(-dirty)?$'
        }
    }

    It 'records the wall clock as metadata, not only inside the telemetry block' {
        $entry = Write-BenchProbe -Name 'wall' -WallMs 5000
        $entry.WallMs | Should -Be 5000

        $noWall = Write-BenchProbe -Name 'nowall' -WallMs 0
        $noWall.WallMs | Should -BeNullOrEmpty
    }

    It 'measures the draw against the documented budget and says so' {
        # 350 RU/s against the S tier's 350 RU/s is the whole budget.
        $entry = Write-BenchProbe -Name 'documented' -Session -UserCount 19

        $entry.Telemetry.RuPerSecond | Should -Be 350
        $entry.Telemetry.BudgetFraction | Should -Be 1
        $entry.Telemetry.BudgetBasis | Should -Be 'documented'
    }

    It 'measures the same draw against a larger tenant as a smaller fraction' {
        $entry = Write-BenchProbe -Name 'documented-large' -Session -UserCount 130233

        $entry.Telemetry.BudgetFraction | Should -Be 0.438
        $entry.Telemetry.BudgetBasis | Should -Be 'documented'
    }

    It 'falls back to 800 RU/s with no documented tier and labels the fallback' {
        $entry = Write-BenchProbe -Name 'assumed'

        $entry.Telemetry.BudgetFraction | Should -Be 0.438
        $entry.Telemetry.BudgetBasis | Should -Be 'assumed-800'
    }
}

Describe 'Write-BenchResult -Identity' {
    It 'stamps the entry with the identity it was handed, not this process''s' {
        # The case this exists for: a comparison whose arms ran as child processes writes its
        # combined entry from a parent that imported no local module and connected to nothing.
        # A session is faked here so the process HAS a tenant of its own to leak - the entry
        # must carry the arms' tenant, not that one.
        $entry = Write-BenchProbe -Name 'identity' -Session -UserCount 19 -Identity ([pscustomobject]@{
                MgxVersion                  = '2.1.4'
                SdkVersion                  = '2.34.0'
                TenantId                    = 'e5011376-aaaa-bbbb-cccc-dddddddddddd'
                DirectoryObjectCount        = 100000
                DocumentedBudgetRuPerSecond = 800
            })

        $entry.MgxVersion | Should -Be '2.1.4'
        $entry.SdkVersion | Should -Be '2.34.0'
        $entry.TenantId | Should -Be 'e5011376-aaaa-bbbb-cccc-dddddddddddd'
        $entry.TenantId | Should -Not -Be $script:FakeTenantId
        $entry.DirectoryObjectCount | Should -Be 100000
        $entry.DocumentedBudgetRuPerSecond | Should -Be 800
    }

    It 'takes a hashtable as readily as an object' {
        $entry = Write-BenchProbe -Name 'identity-hash' -Identity @{
            MgxVersion = '2.1.4'; SdkVersion = '2.34.0'; TenantId = 'e5011376'
            DirectoryObjectCount = 100000; DocumentedBudgetRuPerSecond = 800
        }

        $entry.MgxVersion | Should -Be '2.1.4'
        $entry.DirectoryObjectCount | Should -Be 100000
    }

    It 'records a field the identity does not carry as null rather than falling back' {
        # Get-BenchEntryIdentity answers null for a field the arms disagreed on. Substituting
        # this process's value there would put back exactly what the parameter exists to keep
        # out, and label a disagreement as a measurement.
        $entry = Write-BenchProbe -Name 'identity-partial' -Session -UserCount 19 `
            -Identity ([pscustomobject]@{ MgxVersion = '2.1.4'; TenantId = $null })

        $entry.MgxVersion | Should -Be '2.1.4'
        $entry.TenantId | Should -BeNullOrEmpty
        $entry.SdkVersion | Should -BeNullOrEmpty
        $entry.DirectoryObjectCount | Should -BeNullOrEmpty
        $entry.DocumentedBudgetRuPerSecond | Should -BeNullOrEmpty
    }

    It 'leaves the run''s own fields to the process that wrote them' {
        $entry = Write-BenchProbe -Name 'identity-run' -WallMs 5000 -Identity ([pscustomobject]@{
                MgxVersion = '2.1.4'; PSVersion = '1.0'; WallMs = 42
            })

        $entry.PSVersion | Should -Be $PSVersionTable.PSVersion.ToString()
        $entry.WallMs | Should -Be 5000
        $entry.RecordedAt | Should -Not -BeNullOrEmpty
    }

    It 'reads this process with no identity supplied' {
        $entry = Write-BenchProbe -Name 'identity-absent' -Session -UserCount 19

        $entry.MgxVersion | Should -Be (Get-Module Mgx -ErrorAction SilentlyContinue)?.Version?.ToString()
        $entry.TenantId | Should -Be $script:FakeTenantId
        $entry.DirectoryObjectCount | Should -Be 19
        $entry.DocumentedBudgetRuPerSecond | Should -Be 350
    }
}

Describe 'Get-BenchEntryIdentity' {
    BeforeAll {
        function script:New-ArmEntry
        {
            param
            (
                [string] $MgxVersion = '2.1.4',
                [string] $SdkVersion = '2.34.0',
                [object] $TenantId = 'e5011376-aaaa-bbbb-cccc-dddddddddddd',
                [object] $DirectoryObjectCount = 100000,
                [object] $DocumentedBudgetRuPerSecond = 800
            )

            return [pscustomobject]@{
                MgxVersion                  = $MgxVersion
                SdkVersion                  = $SdkVersion
                TenantId                    = $TenantId
                DirectoryObjectCount        = $DirectoryObjectCount
                DocumentedBudgetRuPerSecond = $DocumentedBudgetRuPerSecond
            }
        }
    }

    It 'answers the five fields the arms agree on' {
        $identity = Get-BenchEntryIdentity -Entries @((New-ArmEntry), (New-ArmEntry))

        $identity.MgxVersion | Should -Be '2.1.4'
        $identity.SdkVersion | Should -Be '2.34.0'
        $identity.TenantId | Should -Be 'e5011376-aaaa-bbbb-cccc-dddddddddddd'
        $identity.DirectoryObjectCount | Should -Be 100000
        $identity.DocumentedBudgetRuPerSecond | Should -Be 800
    }

    It 'counts a long and an int holding the same number as agreement' {
        # The arms record through the same writer, but one of them reaching the count as a long
        # and the other as an int is not a disagreement about which tenant ran.
        $identity = Get-BenchEntryIdentity -Entries @(
            (New-ArmEntry -DirectoryObjectCount ([long]100000)),
            (New-ArmEntry -DirectoryObjectCount ([int]100000))
        )

        $identity.DirectoryObjectCount | Should -Be 100000
    }

    It 'answers null for a field the arms disagree on, naming it and both values' {
        $warnings = $null
        $identity = Get-BenchEntryIdentity -WarningVariable warnings 3>$null -Entries @(
            (New-ArmEntry -MgxVersion '2.1.4'),
            (New-ArmEntry -MgxVersion '2.1.1')
        )

        $identity.MgxVersion | Should -BeNullOrEmpty
        "$warnings" | Should -BeLike '*disagree on MgxVersion (2.1.4, 2.1.1)*'
        $identity.TenantId | Should -Be 'e5011376-aaaa-bbbb-cccc-dddddddddddd' -Because 'a field they agree on is still answered'
    }

    It 'names a field one arm recorded and the other did not as a disagreement' {
        # An arm with no tenant and an arm with one did not run against the same thing, and
        # taking the one that has a value would say they did.
        $warnings = $null
        $identity = Get-BenchEntryIdentity -WarningVariable warnings 3>$null -Entries @(
            (New-ArmEntry),
            (New-ArmEntry -TenantId $null)
        )

        $identity.TenantId | Should -BeNullOrEmpty
        "$warnings" | Should -BeLike '*disagree on TenantId (e5011376-aaaa-bbbb-cccc-dddddddddddd, (none))*'
    }

    It 'answers null without warning when neither arm recorded a field' {
        $warnings = $null
        $identity = Get-BenchEntryIdentity -WarningVariable warnings 3>$null -Entries @(
            (New-ArmEntry -TenantId $null -DocumentedBudgetRuPerSecond $null),
            (New-ArmEntry -TenantId $null -DocumentedBudgetRuPerSecond $null)
        )

        $identity.TenantId | Should -BeNullOrEmpty
        $identity.DocumentedBudgetRuPerSecond | Should -BeNullOrEmpty
        "$warnings" | Should -BeNullOrEmpty
    }

    It 'reads a single arm''s entry as that arm''s identity' {
        $identity = Get-BenchEntryIdentity -Entries @((New-ArmEntry -MgxVersion '2.1.5'))

        $identity.MgxVersion | Should -Be '2.1.5'
    }
}

Describe 'Export-BenchBaseline over a recorded entry' {
    It 'still reads the version block off an entry carrying the new fields' {
        # The promotion reads MgxVersion/SdkVersion/PSVersion/RecordedAt by name. The tenant
        # fields extend that block; they must not have moved what was already in it.
        $results = Join-Path $TestDrive 'promote'
        Write-BenchResult -Benchmark '06-fault-gauntlet' -ResultsDirectory $results -Result ([pscustomobject]@{
                mgx    = [pscustomobject]@{ ElapsedMs = 134654; Output = [pscustomobject]@{ ok = 1000; failed = 0 } }
                sdkMgx = [pscustomobject]@{ ElapsedMs = 246715 }
            }) | Out-Null

        $baseline = Join-Path $TestDrive 'promoted-baseline.json'
        Export-BenchBaseline -Benchmark '06' -ResultsDirectory $results -Path $baseline

        $pinned = (Get-Content -Path $baseline -Raw | ConvertFrom-Json).Scenarios.'06'
        $pinned.Headline | Should -Be 'MgxFanoutMs'
        $pinned.Metrics.MgxFanoutMs.Value | Should -Be 134654
        $pinned.PSVersion | Should -Be $PSVersionTable.PSVersion.ToString()
        $pinned.RecordedAt | Should -Not -BeNullOrEmpty
    }
}
