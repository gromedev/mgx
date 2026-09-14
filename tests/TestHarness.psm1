#Requires -Version 7.0

<#
    .SYNOPSIS
        Pester harness for the mgx module.

    .DESCRIPTION
        Wraps Invoke-Pester so CI and local runs share one entry point.
        These tests cover the PowerShell-facing surface only: the module manifest,
        the format file, and the cmdlet/parameter contract of the built module.

        Engine and cmdlet internals (HTTP retry, pagination, JSON conversion) are
        covered by the xUnit suite in tests/Mgx.IntegrationTests, run via `dotnet test`.
#>

$script:RepoRoot = Split-Path -Parent $PSScriptRoot
$script:ModuleName = 'mgx'
$script:ModuleRoot = Join-Path $script:RepoRoot 'module'
$script:ManifestPath = Join-Path $script:ModuleRoot "$script:ModuleName.psd1"

function Get-MgxTestPath
{
    <#
        .SYNOPSIS
            Paths the tests need, resolved from the repository layout.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param ()

    return @{
        RepoRoot     = $script:RepoRoot
        ModuleName   = $script:ModuleName
        ModuleRoot   = $script:ModuleRoot
        ManifestPath = $script:ManifestPath
        FormatPath   = Join-Path $script:ModuleRoot "$script:ModuleName.Format.ps1xml"
        HelpRoot     = Join-Path $script:ModuleRoot 'help'
    }
}

function Invoke-TestHarness
{
    <#
        .SYNOPSIS
            Run the Pester test suite for the mgx module.

        .PARAMETER TestResultsFile
            NUnit XML results path. Defaults to tests/TestResults.xml.

        .PARAMETER IgnoreCodeCoverage
            Skip code coverage. Coverage of a binary module from Pester is not
            meaningful (there is no PowerShell source to instrument), so coverage
            is never collected; the switch exists for CI call compatibility.

        .PARAMETER TestPath
            Directory to search for *.Tests.ps1. Defaults to tests/Unit.

        .OUTPUTS
            The Pester run object. Callers check $result.Result (not 'Passed' when a test
            failed OR a container could not run) and $result.FailedContainersCount.
    #>
    [CmdletBinding()]
    param
    (
        [Parameter()]
        [System.String]
        $TestResultsFile = (Join-Path $PSScriptRoot 'TestResults.xml'),

        [Parameter()]
        [Switch]
        $IgnoreCodeCoverage,

        [Parameter()]
        [System.String]
        $TestPath = (Join-Path $PSScriptRoot 'Unit'),

        # Live-tagged blocks need a real Graph session. They are excluded by default so CI
        # and a cold clone both pass; pass this to run them against a connected tenant.
        [Parameter()]
        [Switch]
        $IncludeLive
    )

    $pesterModule = Get-Module -Name Pester -ListAvailable |
        Sort-Object -Property Version -Descending |
        Select-Object -First 1

    if ($null -eq $pesterModule)
    {
        throw 'Pester is not installed. Run: Install-PSResource -Name Pester -TrustRepository'
    }

    if ($pesterModule.Version.Major -lt 5)
    {
        throw "Pester 5.0 or later is required; found $($pesterModule.Version)."
    }

    Import-Module -Name $pesterModule.Path -Force

    # The module must be built before the surface tests can inspect it
    if (-not (Test-Path -Path $script:ManifestPath))
    {
        throw "Module manifest not found at '$script:ManifestPath'. Run ./build.ps1 first."
    }

    $configuration = New-PesterConfiguration
    $configuration.Run.Path = $TestPath
    if (-not $IncludeLive.IsPresent)
    {
        $configuration.Filter.ExcludeTag = 'Live'
    }
    $configuration.Run.PassThru = $true
    $configuration.Output.Verbosity = 'Detailed'
    $configuration.TestResult.Enabled = $true
    $configuration.TestResult.OutputFormat = 'NUnitXml'
    $configuration.TestResult.OutputPath = $TestResultsFile

    # Binary module: there is no PowerShell code to instrument, so coverage is
    # never enabled. -IgnoreCodeCoverage is accepted for CI call compatibility.
    $configuration.CodeCoverage.Enabled = $false

    if (-not $IgnoreCodeCoverage.IsPresent)
    {
        Write-Verbose -Message 'Code coverage is not collected for a binary module; continuing without it.'
    }

    $result = Invoke-Pester -Configuration $configuration

    # FailedCount counts tests only. A container that never ran at all - a parse error, a
    # top-level throw, a dot-sourced helper that is gone - fails the container and leaves
    # FailedCount at 0, so a caller checking FailedCount alone sees a clean pass. Name it here
    # so a local run sees it even when the caller only logs the summary line.
    foreach ($container in $result.FailedContainers)
    {
        $firstError = $container.ErrorRecord[0].Exception.Message
        Write-Warning -Message "Container failed: $($container.Name) - $firstError"
    }

    return $result
}

Export-ModuleMember -Function Invoke-TestHarness, Get-MgxTestPath
