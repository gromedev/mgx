#Requires -Modules Pester

<#
    Invoke-TestHarness's own result contract, from tests/TestHarness.psm1: a container that
    cannot run at all - a parse error, a top-level throw - must not read as a clean pass.
    Pester already reports this on the object it hands back (Result, FailedContainersCount);
    these pin that it keeps doing so, and that the harness's own warning names the file.

    Each case drives Invoke-TestHarness in a child pwsh process rather than in-process:
    Invoke-TestHarness re-imports Pester with -Force on every call, and Pester does not
    support that while a run it started is still on the call stack - a second such call inside
    this container's own run hangs rather than fails, so the nested run is given a process of
    its own.
#>

BeforeAll {
    $script:HarnessModule = Join-Path (Split-Path -Parent $PSScriptRoot) 'TestHarness.psm1'

    # Written once for the container and driven with the arguments below: a scenario is just
    # the fixture files plus the path this runs Invoke-TestHarness against.
    $script:ChildRunner = Join-Path $TestDrive 'Invoke-HarnessChild.ps1'
    Set-Content -Path $script:ChildRunner -Value @'
param(
    [Parameter(Mandatory)] [string] $HarnessModule,
    [Parameter(Mandatory)] [string] $TestPath,
    [Parameter(Mandatory)] [string] $TestResultsFile,
    [Parameter(Mandatory)] [string] $ResultFile
)
Import-Module -Name $HarnessModule -Force
$warnings = $null
$result = Invoke-TestHarness -TestPath $TestPath -TestResultsFile $TestResultsFile `
    -IgnoreCodeCoverage -WarningVariable warnings -WarningAction SilentlyContinue
[pscustomobject]@{
    Result                = $result.Result
    FailedCount           = $result.FailedCount
    FailedContainersCount = $result.FailedContainersCount
    PassedCount           = $result.PassedCount
    Warnings              = @($warnings | ForEach-Object { $_.ToString() })
} | ConvertTo-Json -Depth 5 | Set-Content -Path $ResultFile -Encoding utf8
'@

    function Invoke-HarnessChild
    {
        <#
            .SYNOPSIS
                Runs Invoke-TestHarness -TestPath $TestPath in its own pwsh process and
                returns the JSON result it wrote, so the harness's re-import of Pester never
                runs nested inside this run.
        #>
        [CmdletBinding()]
        param ([Parameter(Mandatory)] [string] $TestPath)

        $stamp = [Guid]::NewGuid().ToString('N')
        $resultFile = Join-Path $TestDrive "result-$stamp.json"
        $testResultsFile = Join-Path $TestDrive "nunit-$stamp.xml"
        $stdout = Join-Path $TestDrive "stdout-$stamp.log"
        $stderr = Join-Path $TestDrive "stderr-$stamp.log"

        $arguments = @(
            '-NoProfile', '-NonInteractive', '-File', $script:ChildRunner,
            '-HarnessModule', $script:HarnessModule,
            '-TestPath', $TestPath,
            '-TestResultsFile', $testResultsFile,
            '-ResultFile', $resultFile
        )
        $process = Start-Process -FilePath 'pwsh' -ArgumentList $arguments -PassThru -NoNewWindow `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        if (-not $process.WaitForExit(60000))
        {
            try { $process.Kill() } catch { }
            throw "Invoke-TestHarness child process did not exit within 60s for '$TestPath'."
        }
        if (-not (Test-Path -Path $resultFile))
        {
            $errorText = Get-Content -Path $stderr -Raw -ErrorAction SilentlyContinue
            throw "Invoke-TestHarness child process (exit $($process.ExitCode)) wrote no result for '$TestPath'. Stderr: $errorText"
        }
        return Get-Content -Path $resultFile -Raw | ConvertFrom-Json
    }
}

Describe 'Invoke-TestHarness result contract' {
    Context 'a test path with a parse error' {
        BeforeAll {
            $script:Dir = Join-Path $TestDrive 'parse-error'
            New-Item -ItemType Directory -Path $script:Dir -Force | Out-Null
            Set-Content -Path (Join-Path $script:Dir 'Ok.Tests.ps1') -Value 'Describe "ok" { It "passes" { $true | Should -BeTrue } }'
            # Unclosed block: Pester's own discovery fails to parse the file, the same way a
            # dropped brace or a moved helper does.
            Set-Content -Path (Join-Path $script:Dir 'Broken.Tests.ps1') -Value 'Describe "broken" {'
            $script:Result = Invoke-HarnessChild -TestPath $script:Dir
        }

        It 'reports Result Failed' {
            $script:Result.Result | Should -Be 'Failed'
        }

        It 'counts the container as failed, not a test' {
            $script:Result.FailedContainersCount | Should -Be 1
            $script:Result.FailedCount | Should -Be 0
        }

        It 'warns naming the broken file' {
            ($script:Result.Warnings -join "`n") | Should -Match ([Regex]::Escape('Broken.Tests.ps1'))
        }
    }

    Context 'a test path with a top-level throw' {
        BeforeAll {
            $script:Dir = Join-Path $TestDrive 'top-level-throw'
            New-Item -ItemType Directory -Path $script:Dir -Force | Out-Null
            Set-Content -Path (Join-Path $script:Dir 'Ok.Tests.ps1') -Value 'Describe "ok" { It "passes" { $true | Should -BeTrue } }'
            Set-Content -Path (Join-Path $script:Dir 'Broken.Tests.ps1') -Value 'throw "dot-sourced helper is gone"'
            $script:Result = Invoke-HarnessChild -TestPath $script:Dir
        }

        It 'reports Result Failed' {
            $script:Result.Result | Should -Be 'Failed'
        }

        It 'counts the container as failed, not a test' {
            $script:Result.FailedContainersCount | Should -Be 1
            $script:Result.FailedCount | Should -Be 0
        }

        It 'warns naming the broken file' {
            ($script:Result.Warnings -join "`n") | Should -Match ([Regex]::Escape('Broken.Tests.ps1'))
        }
    }

    Context 'a clean test path' {
        BeforeAll {
            $script:Dir = Join-Path $TestDrive 'clean'
            New-Item -ItemType Directory -Path $script:Dir -Force | Out-Null
            Set-Content -Path (Join-Path $script:Dir 'Ok.Tests.ps1') -Value 'Describe "ok" { It "passes" { $true | Should -BeTrue } }'
            $script:Result = Invoke-HarnessChild -TestPath $script:Dir
        }

        It 'reports Result Passed' {
            $script:Result.Result | Should -Be 'Passed'
        }

        It 'writes no warning' {
            $script:Result.Warnings.Count | Should -Be 0
        }
    }
}
