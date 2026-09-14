<#
    Module load and assembly-load-context resolution are irreversible within a process, so
    every permutation here runs in its own child pwsh. What a permutation can assert with no
    tenant: the import works, the cmdlets resolve, the engine types JIT against whatever
    assemblies the neighboring modules already loaded, and teardown leaves nothing armed.
    A real Graph call under load order stays with tests/Live and the benchmarks.

    Provenance: GraphSDK-2148 (module isolation) - see tmp/CORPUS.md (private). Az.Accounts ships its
    own System.Text.Json; the interesting failure is a TypeLoadException the first time an
    engine type JITs, which is why the option round-trip and telemetry calls are the probe.

    A missing module is a failure, not a skip: a suite that skips itself when unconfigured
    silently stops running. CI installs all three in the ecosystem job.
#>

BeforeDiscovery {
    $script:RequiredModules = 'Az.Accounts', 'PnP.PowerShell', 'ExchangeOnlineManagement'
    foreach ($m in $script:RequiredModules) {
        if (-not (Get-Module -ListAvailable $m)) {
            throw "Ecosystem tests need the $m module installed. Install-PSResource $m, or run the ecosystem CI job."
        }
    }
}

BeforeAll {
    $repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $script:Manifest = Join-Path $repo 'module/mgx.psd1'
    if (-not (Test-Path $script:Manifest)) { throw "Built module not found at $script:Manifest - run ./build.ps1 first." }

    # The battery runs after the permutation's imports. Order inside matters: the side-load
    # check must precede the Get-MgContext stub, and resilience and fingerprint run after the
    # remove/re-import so they exercise the re-imported module, not the first import.
    $script:Battery = @'
$results = [ordered]@{}
function Check([string]$name, [scriptblock]$body) {
    try { $results[$name] = @{ ok = [bool](& $body); detail = '' } }
    catch { $results[$name] = @{ ok = $false; detail = $_.Exception.Message } }
}
Check 'cmdletsResolve'   { (Get-Command -Module mgx -CommandType Cmdlet).Count -eq 12 }
Check 'noGraphSideLoad'  { @(Get-Module 'Microsoft.Graph*').Count -eq 0 }
Check 'optionRoundTrip'  { Set-MgxOption -MaxRetryAttempts 4; (Get-MgxOption).MaxRetryAttempts -eq 4 }
Check 'telemetry'        { $null -ne (Get-MgxTelemetry) }
Check 'depsResolve'      {
    # Polly and the rate limiter load lazily; Assembly.Load drives the module's own
    # Resolving hook, which is the mechanism whose neighbors could break it.
    $polly = [Reflection.Assembly]::Load('Polly.Core')
    $rate  = [Reflection.Assembly]::Load('System.Threading.RateLimiting')
    $stj   = [Reflection.Assembly]::Load('System.Text.Json')
    ($null -ne $polly) -and ($null -ne $rate) -and ($null -ne $stj)
}
Check 'removeReimport'   {
    Remove-Module mgx -Force
    Import-Module '__MANIFEST__' -ErrorAction Stop
    (Get-Command -Module mgx -CommandType Cmdlet).Count -eq 12 -and $null -ne (Get-MgxOption)
}
Check 'resilienceNamesTheSession' {
    # ThrowTerminatingError is statement-terminating; no -ErrorAction downgrades it,
    # so the record arrives as a caught exception carrying the id.
    try { Enable-MgxResilience; $false }
    catch { $_.FullyQualifiedErrorId -like 'GraphSessionNotFound*' }
}
Check 'fingerprintFromStub' {
    function global:Get-MgContext { [PSCustomObject]@{ TenantId = 'eco-tenant'; ClientId = 'eco-client'; AuthType = 'AppOnly'; Environment = 'Global' } }
    $base = (Get-Command Invoke-MgxRequest).ImplementingType.BaseType
    $m = $base.GetMethod('BuildAuthFingerprint', [Reflection.BindingFlags]'Static,NonPublic,Public')
    $fp = $m.Invoke($null, @((Get-MgContext), $null))
    -not [string]::IsNullOrEmpty($fp)
}
[pscustomobject]@{ battery = $results } | ConvertTo-Json -Depth 4 -Compress
'@ -replace '__MANIFEST__', $script:Manifest

    $script:Permutations = [ordered]@{
        'az-before-mgx'    = @('Import-Module Az.Accounts', 'Import-Module __MANIFEST__')
        'az-after-mgx'     = @('Import-Module __MANIFEST__', 'Import-Module Az.Accounts')
        'pnp-before-mgx'   = @('Import-Module PnP.PowerShell', 'Import-Module __MANIFEST__')
        'pnp-after-mgx'    = @('Import-Module __MANIFEST__', 'Import-Module PnP.PowerShell')
        'exo-before-mgx'   = @('Import-Module ExchangeOnlineManagement', 'Import-Module __MANIFEST__')
        'exo-after-mgx'    = @('Import-Module __MANIFEST__', 'Import-Module ExchangeOnlineManagement')
        'stack-mgx-last'   = @('Import-Module Az.Accounts', 'Import-Module PnP.PowerShell', 'Import-Module ExchangeOnlineManagement', 'Import-Module __MANIFEST__')
        'stack-mgx-first'  = @('Import-Module __MANIFEST__', 'Import-Module Az.Accounts', 'Import-Module PnP.PowerShell', 'Import-Module ExchangeOnlineManagement')
        'interleaved'      = @('Import-Module Az.Accounts', 'Import-Module __MANIFEST__', 'Set-MgxOption -MaxRetryAttempts 3', 'Import-Module PnP.PowerShell', 'Import-Module ExchangeOnlineManagement')
    }

    function Invoke-Permutation([string[]]$imports) {
        $lines = ($imports -replace '__MANIFEST__', $script:Manifest) + $script:Battery
        $out = & pwsh -NoProfile -NonInteractive -Command ($lines -join "`n") 2>&1
        $json = @($out) | Where-Object { "$_" -match '^\{' } | Select-Object -Last 1
        if (-not $json) { throw "Permutation produced no result. Output: $(($out | Select-Object -First 5) -join ' | ')" }
        ($json | ConvertFrom-Json).battery
    }
}

Describe 'Ecosystem isolation' {
    It 'holds under <_>' -ForEach @('az-before-mgx', 'az-after-mgx', 'pnp-before-mgx', 'pnp-after-mgx', 'exo-before-mgx', 'exo-after-mgx', 'stack-mgx-last', 'stack-mgx-first', 'interleaved') {
        $battery = Invoke-Permutation $script:Permutations[$_]
        $failed = $battery.PSObject.Properties | Where-Object { -not $_.Value.ok } |
            ForEach-Object { "$($_.Name): $($_.Value.detail)" }
        $failed -join '; ' | Should -BeNullOrEmpty
    }
}
