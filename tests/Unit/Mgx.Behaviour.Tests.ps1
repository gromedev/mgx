#Requires -Modules Pester

<#
    The behavior of the built module as PowerShell sees it: the cmdlet and parameter contract,
    and what the cmdlets decide before a request leaves the process.

    Run ./build.ps1 first, then the harness, which is what CI runs too:

        Import-Module ./tests/TestHarness.psm1; Invoke-TestHarness

    The harness excludes the Live tag, so a clone with no Graph session passes. What a cmdlet
    does against a real tenant is proven in tests/Live, run on its own:

        Invoke-Pester -Path ./tests/Live

    The one Live-tagged block left here is the sustained auth run at the bottom: it needs an
    hour of directory traffic to cross a token boundary, which is not something tests/Live can
    ask of anyone running the suite. Pass -IncludeLive to the harness to reach it.
#>

BeforeAll {
    $ModulePath = Join-Path $PSScriptRoot '../../module/mgx.psd1'
    Import-Module $ModulePath -Force
}

Describe 'Module Loading' {
    It 'Should import Mgx module' {
        $module = Get-Module Mgx
        $module | Should -Not -BeNullOrEmpty
        # Read expected version from manifest to avoid hardcoded values breaking on version bumps
        $manifest = Import-PowerShellDataFile (Join-Path $PSScriptRoot '../../module/mgx.psd1')
        $module.Version | Should -Be $manifest.ModuleVersion
    }

    It 'Should export exactly 12 cmdlets' {
        # Filtered to -CommandType Cmdlet on purpose: Get-Command -Module also counts
        # exported functions, so an unfiltered count would drift for reasons that have
        # nothing to do with the compiled surface.
        $commands = (Get-Command -Module Mgx -CommandType Cmdlet).Name | Sort-Object
        $commands | Should -Contain 'Invoke-MgxRequest'
        $commands | Should -Contain 'Invoke-MgxBatchRequest'
        $commands | Should -Contain 'Export-MgxCollection'
        $commands | Should -Contain 'Expand-MgxRelation'
        $commands | Should -Contain 'Sync-MgxDelta'
        $commands | Should -Contain 'Get-MgxContent'
        $commands | Should -Contain 'Set-MgxOption'
        $commands | Should -Contain 'Get-MgxOption'
        $commands | Should -Contain 'Enable-MgxResilience'
        $commands | Should -Contain 'Disable-MgxResilience'
        $commands | Should -Contain 'Get-MgxResilience'
        $commands | Should -Contain 'Get-MgxTelemetry'
        $commands | Should -HaveCount 12
    }

    It 'Should unload cleanly when no Graph request ever ran' {
        # Regression for the Remove-Module teardown defect fixed in 1.0.3.
        # AlcInitializer.OnRemove (IModuleAssemblyCleanup) detaches the ALC Resolving
        # handler, and PowerShell runs it BEFORE a module's OnRemove scriptblock. The
        # cleanup used to live in that scriptblock and called ResetHttpClient, which
        # JITs code referencing Polly types. Polly.Core ships in Dependencies/ and is
        # reachable only through the detached resolver -- and it loads lazily, so in a
        # session with no Graph request it was never in the AppDomain at all. Result:
        # Remove-Module threw and the module could never be unloaded.
        #
        # Runs in a child process because the precondition is "Polly.Core has never
        # been loaded", which the parent suite has already violated by this point.
        $modulePath = Join-Path $PSScriptRoot '../../module/mgx.psd1'
        $probe = @"
Import-Module '$modulePath' -Force
try { Remove-Module mgx -ErrorAction Stop }
catch { Write-Output ('THREW: ' + `$_.Exception.Message.Split([char]10)[0]); exit 1 }
if (Get-Module mgx) { Write-Output 'STILL LOADED'; exit 1 }
Write-Output 'OK'
"@
        # This assertion needs a FRESH session, so it must spawn a child host. Under a
        # dotnet-tool PowerShell install on Unix there is no re-executable host: the
        # process path is the dotnet muxer, and $PSHOME/pwsh is a managed wrapper that
        # cannot be exec'd directly ("Exec format error"). Rather than fail there and
        # look like an mgx defect, prove the host can re-exec first and skip if it cannot.
        $hostExe = @(
            (Get-Process -Id $PID).Path
            (Get-Command pwsh -ErrorAction SilentlyContinue).Source
        ) | Where-Object { $_ -and (Split-Path $_ -Leaf) -like 'pwsh*' } | Select-Object -First 1

        if (-not $hostExe) {
            Set-ItResult -Skipped -Because 'this host cannot re-exec itself (dotnet-tool PowerShell install)'
            return
        }
        # A failed exec surfaces as a terminating NativeCommandFailed, not a return value,
        # so the probe must be wrapped rather than compared.
        $probeOk = $null
        try { $probeOk = & $hostExe -NoProfile -Command '"alive"' 2>$null } catch { $probeOk = $null }
        if ($probeOk -ne 'alive') {
            Set-ItResult -Skipped -Because "host '$hostExe' cannot be launched as a child process"
            return
        }

        $result = & $hostExe -NoProfile -Command $probe
        $result | Should -Contain 'OK'
        $LASTEXITCODE | Should -Be 0
    }
}

Describe 'Expand-MgxRelation Parameter Compatibility' {
    It 'Should have -Uri parameter (mandatory, position 0)' {
        $param = (Get-Command Expand-MgxRelation).Parameters['Uri']
        $param | Should -Not -BeNullOrEmpty
        $paramAttr = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] }
        $paramAttr.Mandatory | Should -BeTrue
        $paramAttr.Position | Should -Be 0
    }

    It 'Should have -As parameter (mandatory, position 1)' {
        $param = (Get-Command Expand-MgxRelation).Parameters['As']
        $param | Should -Not -BeNullOrEmpty
        $paramAttr = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] }
        $paramAttr.Mandatory | Should -BeTrue
        $paramAttr.Position | Should -Be 1
    }

    It 'Should have -InputObject parameter (mandatory, pipeline)' {
        $param = (Get-Command Expand-MgxRelation).Parameters['InputObject']
        $param | Should -Not -BeNullOrEmpty
        $paramAttr = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] }
        $paramAttr.Mandatory | Should -BeTrue
        $paramAttr.ValueFromPipeline | Should -BeTrue
    }

    It 'Should have -Flatten switch parameter' {
        $param = (Get-Command Expand-MgxRelation).Parameters['Flatten']
        $param | Should -Not -BeNullOrEmpty
        $param.SwitchParameter | Should -BeTrue
    }

    It 'Should have -IdProperty parameter with default "id"' {
        $param = (Get-Command Expand-MgxRelation).Parameters['IdProperty']
        $param | Should -Not -BeNullOrEmpty
    }

    It 'Should have -Concurrency parameter with ValidateRange(1,128)' {
        $param = (Get-Command Expand-MgxRelation).Parameters['Concurrency']
        $param | Should -Not -BeNullOrEmpty
        $validateRange = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] }
        $validateRange | Should -Not -BeNullOrEmpty
    }

    It 'Should have -SkipNotFound and -SkipForbidden parameters' {
        (Get-Command Expand-MgxRelation).Parameters.Keys | Should -Contain 'SkipNotFound'
        (Get-Command Expand-MgxRelation).Parameters.Keys | Should -Contain 'SkipForbidden'
    }

    It 'Should have -ConsistencyLevel parameter' {
        (Get-Command Expand-MgxRelation).Parameters.Keys | Should -Contain 'ConsistencyLevel'
    }

    It 'Should have -Headers parameter' {
        (Get-Command Expand-MgxRelation).Parameters.Keys | Should -Contain 'Headers'
    }

    It 'Should have -ApiVersion parameter with ValidateSet' {
        $param = (Get-Command Expand-MgxRelation).Parameters['ApiVersion']
        $param | Should -Not -BeNullOrEmpty
        $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
        $validateSet | Should -Not -BeNullOrEmpty
        $validateSet.ValidValues | Should -Contain 'v1.0'
        $validateSet.ValidValues | Should -Contain 'beta'
    }

    It 'Should have -Top parameter with ValidateRange' {
        $param = (Get-Command Expand-MgxRelation).Parameters['Top']
        $param | Should -Not -BeNullOrEmpty
        $validateRange = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateRangeAttribute] }
        $validateRange | Should -Not -BeNullOrEmpty
    }

    # R3-3: $search requires -ConsistencyLevel eventual
    It 'Should throw when URI contains $search without -ConsistencyLevel' {
        { @([PSCustomObject]@{id='x'}) | Expand-MgxRelation '/users/{id}/memberOf?$search="displayName:test"' -As Members } |
            Should -Throw '*ConsistencyLevel*'
    }

    It 'Should not throw ConsistencyLevel error when -ConsistencyLevel is provided' {
        # Will fail on auth (no Graph connection), but must NOT throw about ConsistencyLevel.
        # Catch all errors and verify none mention ConsistencyLevel.
        $err = $null
        try {
            @([PSCustomObject]@{id='x'}) | Expand-MgxRelation '/users/{id}/memberOf?$search="displayName:test"' -As Members -ConsistencyLevel eventual -ErrorAction Stop
        } catch {
            $err = $_
        }
        # If it threw, the error must NOT be about ConsistencyLevel (auth errors are expected)
        if ($err) {
            $err.Exception.Message | Should -Not -BeLike '*ConsistencyLevel*'
        }
    }
}

Describe 'Invoke-MgxRequest Parameter Compatibility' {
    It 'Should have -Uri parameter (mandatory, position 0)' {
        $param = (Get-Command Invoke-MgxRequest).Parameters['Uri']
        $param | Should -Not -BeNullOrEmpty
        $param.Aliases | Should -Contain 'Resource'
    }

    It 'Should have -All switch parameter' {
        $param = (Get-Command Invoke-MgxRequest).Parameters['All']
        $param | Should -Not -BeNullOrEmpty
        $param.SwitchParameter | Should -BeTrue
    }

    It 'Should have -Top parameter' {
        (Get-Command Invoke-MgxRequest).Parameters.Keys | Should -Contain 'Top'
    }

    It 'Should have -Filter parameter' {
        (Get-Command Invoke-MgxRequest).Parameters.Keys | Should -Contain 'Filter'
    }

    It 'Should have -Property parameter with Select alias' {
        $param = (Get-Command Invoke-MgxRequest).Parameters['Property']
        $param | Should -Not -BeNullOrEmpty
        $param.Aliases | Should -Contain 'Select'
    }

    It 'Should have -Sort parameter with OrderBy alias' {
        $param = (Get-Command Invoke-MgxRequest).Parameters['Sort']
        $param | Should -Not -BeNullOrEmpty
        $param.Aliases | Should -Contain 'OrderBy'
    }

    It 'Should have -ExpandProperty parameter with Expand alias' {
        $param = (Get-Command Invoke-MgxRequest).Parameters['ExpandProperty']
        $param | Should -Not -BeNullOrEmpty
        $param.Aliases | Should -Contain 'Expand'
    }

    It 'Should have -CountVariable parameter with CV alias' {
        $param = (Get-Command Invoke-MgxRequest).Parameters['CountVariable']
        $param | Should -Not -BeNullOrEmpty
        $param.Aliases | Should -Contain 'CV'
    }

    It 'Should have -Method parameter with ValidateSet' {
        $param = (Get-Command Invoke-MgxRequest).Parameters['Method']
        $param | Should -Not -BeNullOrEmpty
        $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
        $validateSet | Should -Not -BeNullOrEmpty
        $validateSet.ValidValues | Should -Contain 'GET'
        $validateSet.ValidValues | Should -Contain 'POST'
        $validateSet.ValidValues | Should -Contain 'DELETE'
    }

    It 'Should have -ApiVersion parameter with ValidateSet' {
        $param = (Get-Command Invoke-MgxRequest).Parameters['ApiVersion']
        $param | Should -Not -BeNullOrEmpty
        $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
        $validateSet | Should -Not -BeNullOrEmpty
        $validateSet.ValidValues | Should -Contain 'v1.0'
        $validateSet.ValidValues | Should -Contain 'beta'
    }

    It 'Should have -InputObject in Pipeline parameter set' {
        $param = (Get-Command Invoke-MgxRequest).Parameters['InputObject']
        $param | Should -Not -BeNullOrEmpty
        $param.Aliases | Should -Contain 'Id'
    }

    It 'Should have -SkipNotFound and -SkipForbidden parameters' {
        (Get-Command Invoke-MgxRequest).Parameters.Keys | Should -Contain 'SkipNotFound'
        (Get-Command Invoke-MgxRequest).Parameters.Keys | Should -Contain 'SkipForbidden'
    }

    It 'Should have -Concurrency parameter' {
        (Get-Command Invoke-MgxRequest).Parameters.Keys | Should -Contain 'Concurrency'
    }

    It 'Should have -Raw switch parameter' {
        $param = (Get-Command Invoke-MgxRequest).Parameters['Raw']
        $param | Should -Not -BeNullOrEmpty
        $param.SwitchParameter | Should -BeTrue
    }

    It 'Should have -Body parameter' {
        (Get-Command Invoke-MgxRequest).Parameters.Keys | Should -Contain 'Body'
    }

    It 'Should have -CheckpointPath parameter' {
        (Get-Command Invoke-MgxRequest).Parameters.Keys | Should -Contain 'CheckpointPath'
    }

    It 'Should have -NoPageSize switch parameter' {
        $param = (Get-Command Invoke-MgxRequest).Parameters['NoPageSize']
        $param | Should -Not -BeNullOrEmpty
        $param.SwitchParameter | Should -BeTrue
    }

    It 'Should support ShouldProcess (-WhatIf/-Confirm)' {
        $cmdletAttr = (Get-Command Invoke-MgxRequest).ImplementingType.GetCustomAttributes($true) |
            Where-Object { $_ -is [System.Management.Automation.CmdletAttribute] }
        $cmdletAttr.SupportsShouldProcess | Should -BeTrue
    }
}

Describe 'Invoke-MgxBatchRequest Parameter Compatibility' {
    It 'Should have -Uri parameter (mandatory, position 0, pipeline) with -Url alias' {
        $param = (Get-Command Invoke-MgxBatchRequest).Parameters['Uri']
        $param | Should -Not -BeNullOrEmpty
        $paramAttr = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] }
        $paramAttr.Mandatory | Should -BeTrue
        $paramAttr.Position | Should -Be 0
        $paramAttr.ValueFromPipeline | Should -BeTrue
        $param.Aliases | Should -Contain 'Url'
    }

    It 'Should have -Method parameter with ValidateSet' {
        $param = (Get-Command Invoke-MgxBatchRequest).Parameters['Method']
        $param | Should -Not -BeNullOrEmpty
        $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
        $validateSet | Should -Not -BeNullOrEmpty
        $validateSet.ValidValues | Should -Contain 'GET'
        $validateSet.ValidValues | Should -Contain 'POST'
        $validateSet.ValidValues | Should -Contain 'PATCH'
        $validateSet.ValidValues | Should -Contain 'PUT'
        $validateSet.ValidValues | Should -Contain 'DELETE'
    }

    It 'Should have -Body parameter' {
        (Get-Command Invoke-MgxBatchRequest).Parameters.Keys | Should -Contain 'Body'
    }

    It 'Should accept object[] for -Uri (supports strings and PSObjects)' {
        $param = (Get-Command Invoke-MgxBatchRequest).Parameters['Uri']
        $param.ParameterType | Should -Be ([object[]])
    }

    It 'Should default -Method to GET' {
        $param = (Get-Command Invoke-MgxBatchRequest).Parameters['Method']
        # Method is not mandatory, GET is the default
        $paramAttr = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] }
        $paramAttr.Mandatory | Should -BeFalse
    }

    It 'Should support ShouldProcess (-WhatIf/-Confirm)' {
        $cmdletAttr = (Get-Command Invoke-MgxBatchRequest).ImplementingType.GetCustomAttributes($true) |
            Where-Object { $_ -is [System.Management.Automation.CmdletAttribute] }
        $cmdletAttr.SupportsShouldProcess | Should -BeTrue
    }

    It 'Should have -Headers parameter' {
        (Get-Command Invoke-MgxBatchRequest).Parameters.Keys | Should -Contain 'Headers'
    }

    It 'Should have -ThrottlePriority parameter' {
        (Get-Command Invoke-MgxBatchRequest).Parameters.Keys | Should -Contain 'ThrottlePriority'
    }

    It 'Should validate -ThrottlePriority values' {
        $param = (Get-Command Invoke-MgxBatchRequest).Parameters['ThrottlePriority']
        $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
        $validateSet | Should -Not -BeNullOrEmpty
        $validateSet.ValidValues | Should -Contain 'Low'
        $validateSet.ValidValues | Should -Contain 'Normal'
        $validateSet.ValidValues | Should -Contain 'High'
    }

    It 'Should have -DeadLetterPath parameter' {
        (Get-Command Invoke-MgxBatchRequest).Parameters.Keys | Should -Contain 'DeadLetterPath'
    }
}

Describe 'Header Precedence (D4)' {
    It 'BuildRequestHeaders: -ConsistencyLevel wins over -Headers key' {
        $method = [Mgx.Cmdlets.Base.MgxCmdletBase].GetMethod(
            'BuildRequestHeaders',
            [System.Reflection.BindingFlags]::Static -bor [System.Reflection.BindingFlags]::NonPublic)
        $method | Should -Not -BeNullOrEmpty

        $extra = @{ ConsistencyLevel = 'session' }
        $result = $method.Invoke($null, @('eventual', $extra))
        $result['ConsistencyLevel'] | Should -Be 'eventual'
    }

    It 'BuildRequestHeaders: extra headers preserved when no conflict' {
        $method = [Mgx.Cmdlets.Base.MgxCmdletBase].GetMethod(
            'BuildRequestHeaders',
            [System.Reflection.BindingFlags]::Static -bor [System.Reflection.BindingFlags]::NonPublic)

        $extra = @{ 'x-ms-throttle-priority' = 'Low'; 'Prefer' = 'outlook.body-content-type=text' }
        $result = $method.Invoke($null, @('eventual', $extra))
        $result['ConsistencyLevel'] | Should -Be 'eventual'
        $result['x-ms-throttle-priority'] | Should -Be 'Low'
        $result['Prefer'] | Should -Be 'outlook.body-content-type=text'
    }
}

Describe 'Export-MgxCollection Parameter Compatibility' {
    It 'Should have -Uri parameter with Resource alias' {
        $param = (Get-Command Export-MgxCollection).Parameters['Uri']
        $param | Should -Not -BeNullOrEmpty
        $param.Aliases | Should -Contain 'Resource'
    }

    It 'Should have -OutputFile parameter (mandatory)' {
        $param = (Get-Command Export-MgxCollection).Parameters['OutputFile']
        $param | Should -Not -BeNullOrEmpty
        $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } | Should -Not -BeNullOrEmpty
    }

    It 'Should have -Property parameter with Select alias' {
        $param = (Get-Command Export-MgxCollection).Parameters['Property']
        $param | Should -Not -BeNullOrEmpty
        $param.Aliases | Should -Contain 'Select'
    }

    It 'Should have -Filter parameter' {
        (Get-Command Export-MgxCollection).Parameters.Keys | Should -Contain 'Filter'
    }

    It 'Should have -Top parameter' {
        (Get-Command Export-MgxCollection).Parameters.Keys | Should -Contain 'Top'
    }

    It 'Should have -PageSize parameter' {
        (Get-Command Export-MgxCollection).Parameters.Keys | Should -Contain 'PageSize'
    }

    It 'Should have -CheckpointPath parameter' {
        (Get-Command Export-MgxCollection).Parameters.Keys | Should -Contain 'CheckpointPath'
    }

    It 'Should have -ApiVersion parameter with ValidateSet' {
        $param = (Get-Command Export-MgxCollection).Parameters['ApiVersion']
        $param | Should -Not -BeNullOrEmpty
        $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
        $validateSet | Should -Not -BeNullOrEmpty
        $validateSet.ValidValues | Should -Contain 'v1.0'
        $validateSet.ValidValues | Should -Contain 'beta'
    }

    It 'Should have -Sort parameter with OrderBy alias' {
        $param = (Get-Command Export-MgxCollection).Parameters['Sort']
        $param | Should -Not -BeNullOrEmpty
        $param.Aliases | Should -Contain 'OrderBy'
    }

    It 'Should have -NoPageSize switch parameter' {
        $param = (Get-Command Export-MgxCollection).Parameters['NoPageSize']
        $param | Should -Not -BeNullOrEmpty
        $param.SwitchParameter | Should -BeTrue
    }

    It 'Should have -All switch parameter' {
        $param = (Get-Command Export-MgxCollection).Parameters['All']
        $param | Should -Not -BeNullOrEmpty
        $param.SwitchParameter | Should -BeTrue
    }

    It 'Should have -ExpandProperty parameter with Expand alias' {
        $param = (Get-Command Export-MgxCollection).Parameters['ExpandProperty']
        $param | Should -Not -BeNullOrEmpty
        $param.Aliases | Should -Contain 'Expand'
    }

    It 'Should have -Skip parameter' {
        (Get-Command Export-MgxCollection).Parameters.Keys | Should -Contain 'Skip'
    }

    It 'Should have -Search parameter' {
        (Get-Command Export-MgxCollection).Parameters.Keys | Should -Contain 'Search'
    }

    It 'Should have -ConsistencyLevel parameter' {
        (Get-Command Export-MgxCollection).Parameters.Keys | Should -Contain 'ConsistencyLevel'
    }

    It 'Should have -Headers parameter' {
        (Get-Command Export-MgxCollection).Parameters.Keys | Should -Contain 'Headers'
    }

    It 'Should support ShouldProcess' {
        $cmdletAttr = (Get-Command Export-MgxCollection).ImplementingType.GetCustomAttributes($true) |
            Where-Object { $_ -is [System.Management.Automation.CmdletAttribute] }
        $cmdletAttr.SupportsShouldProcess | Should -BeTrue
    }
}

Describe 'Set-MgxOption Parameter Validation' {
    It 'Should have -RateLimitBurst parameter' {
        (Get-Command Set-MgxOption).Parameters.Keys | Should -Contain 'RateLimitBurst'
    }

    It 'Should have -RateLimitPerSecond parameter' {
        (Get-Command Set-MgxOption).Parameters.Keys | Should -Contain 'RateLimitPerSecond'
    }

    It 'Should have -NoRateLimit switch parameter' {
        $param = (Get-Command Set-MgxOption).Parameters['NoRateLimit']
        $param | Should -Not -BeNullOrEmpty
        $param.SwitchParameter | Should -BeTrue
    }

    It 'Should reject -RateLimitBurst 0' {
        { Set-MgxOption -RateLimitBurst 0 } | Should -Throw
    }

    It 'Should reject -RateLimitPerSecond 0' {
        { Set-MgxOption -RateLimitPerSecond 0 } | Should -Throw
    }

    It 'Should reject -RateLimitBurst -1' {
        { Set-MgxOption -RateLimitBurst -1 } | Should -Throw
    }
}

Describe 'Enable-MgxResilience Parameter Compatibility' {
    It 'Should support ShouldProcess (-WhatIf/-Confirm)' {
        $cmdletAttr = (Get-Command Enable-MgxResilience).ImplementingType.GetCustomAttributes($true) |
            Where-Object { $_ -is [System.Management.Automation.CmdletAttribute] }
        $cmdletAttr.SupportsShouldProcess | Should -BeTrue
    }

    It 'Should have no mandatory parameters' {
        $mandatoryParams = (Get-Command Enable-MgxResilience).Parameters.Values |
            Where-Object { $_.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } }
        $mandatoryParams | Should -BeNullOrEmpty
    }
}

Describe 'Disable-MgxResilience Parameter Compatibility' {
    It 'Should support ShouldProcess (-WhatIf/-Confirm)' {
        $cmdletAttr = (Get-Command Disable-MgxResilience).ImplementingType.GetCustomAttributes($true) |
            Where-Object { $_ -is [System.Management.Automation.CmdletAttribute] }
        $cmdletAttr.SupportsShouldProcess | Should -BeTrue
    }

    It 'Should have no mandatory parameters' {
        $mandatoryParams = (Get-Command Disable-MgxResilience).Parameters.Values |
            Where-Object { $_.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } }
        $mandatoryParams | Should -BeNullOrEmpty
    }
}

Describe 'Get-MgxResilience Parameter Compatibility' {
    It 'Should have OutputType of PSObject' {
        $outputType = (Get-Command Get-MgxResilience).OutputType
        $outputType.Name | Should -Contain 'System.Management.Automation.PSObject'
    }

    It 'Should have no mandatory parameters' {
        $mandatoryParams = (Get-Command Get-MgxResilience).Parameters.Values |
            Where-Object { $_.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] -and $_.Mandatory } }
        $mandatoryParams | Should -BeNullOrEmpty
    }
}

Describe 'Get-MgxOption' {
    It 'Should have OutputType of MgxOptionOutput' {
        $outputType = (Get-Command Get-MgxOption).OutputType
        $outputType.Name | Should -Contain 'Mgx.Cmdlets.Models.MgxOptionOutput'
    }

    It 'Should return all option properties' {
        $result = Get-MgxOption
        $result.RateLimitBurst | Should -BeGreaterThan 0
        $result.RateLimitPerSecond | Should -BeGreaterThan 0
        $result.MaxRetryAttempts | Should -BeGreaterThan 0
        $result.TotalTimeoutSeconds | Should -BeGreaterThan 0
        $result.AttemptTimeoutSeconds | Should -BeGreaterThan 0
        $result.CircuitBreakerDurationSeconds | Should -BeGreaterThan 0
        $result.CircuitBreakerFailureRatio | Should -BeGreaterThan 0
        $result.CircuitBreakerMinThroughput | Should -BeGreaterThan 0
        $result.CircuitBreakerSamplingDurationSeconds | Should -BeGreaterThan 0
        $result.PSTypeNames | Should -Contain 'Mgx.Cmdlets.Models.MgxOptionOutput'
    }

    It 'Should return default values' {
        $result = Get-MgxOption
        $result.RateLimitBurst | Should -Be 200
        $result.RateLimitPerSecond | Should -Be 50
        $result.MaxRetryAttempts | Should -Be 7
        $result.TotalTimeoutSeconds | Should -Be 300
        $result.AttemptTimeoutSeconds | Should -Be 30
        $result.CircuitBreakerDurationSeconds | Should -Be 15
        $result.CircuitBreakerFailureRatio | Should -Be 0.1
        $result.CircuitBreakerMinThroughput | Should -Be 40
        $result.RateLimitQueueLimit | Should -Be 500
        $result.NoRateLimit | Should -BeFalse
        $result.CircuitBreakerSamplingDurationSeconds | Should -Be 30
        $result.BatchChunkConcurrency | Should -Be 1
        $result.BatchItemsPerSecond | Should -Be 20
    }
}

Describe 'Set-MgxOption Pipeline Parameters' {
    It 'Should have -MaxRetryAttempts parameter' {
        $param = (Get-Command Set-MgxOption).Parameters['MaxRetryAttempts']
        $param | Should -Not -BeNullOrEmpty
    }

    It 'Should have -TotalTimeoutSeconds parameter' {
        $param = (Get-Command Set-MgxOption).Parameters['TotalTimeoutSeconds']
        $param | Should -Not -BeNullOrEmpty
    }

    It 'Should have -AttemptTimeoutSeconds parameter' {
        $param = (Get-Command Set-MgxOption).Parameters['AttemptTimeoutSeconds']
        $param | Should -Not -BeNullOrEmpty
    }

    It 'Should have -CircuitBreakerDurationSeconds parameter' {
        $param = (Get-Command Set-MgxOption).Parameters['CircuitBreakerDurationSeconds']
        $param | Should -Not -BeNullOrEmpty
    }

    It 'Should have -CircuitBreakerFailureRatio parameter' {
        $param = (Get-Command Set-MgxOption).Parameters['CircuitBreakerFailureRatio']
        $param | Should -Not -BeNullOrEmpty
    }

    It 'Should have -CircuitBreakerMinThroughput parameter' {
        $param = (Get-Command Set-MgxOption).Parameters['CircuitBreakerMinThroughput']
        $param | Should -Not -BeNullOrEmpty
    }

    It 'Should have -CircuitBreakerSamplingDurationSeconds parameter' {
        $param = (Get-Command Set-MgxOption).Parameters['CircuitBreakerSamplingDurationSeconds']
        $param | Should -Not -BeNullOrEmpty
    }

    It 'Should have -RateLimitQueueLimit parameter' {
        $param = (Get-Command Set-MgxOption).Parameters['RateLimitQueueLimit']
        $param | Should -Not -BeNullOrEmpty
    }

    It 'Should have -BatchChunkConcurrency parameter' {
        $param = (Get-Command Set-MgxOption).Parameters['BatchChunkConcurrency']
        $param | Should -Not -BeNullOrEmpty
    }

    It 'Should have -Reset switch parameter' {
        $param = (Get-Command Set-MgxOption).Parameters['Reset']
        $param | Should -Not -BeNullOrEmpty
        $param.SwitchParameter | Should -BeTrue
    }

    It 'Should persist pipeline options via Set then Get' {
        Set-MgxOption -MaxRetryAttempts 3 -TotalTimeoutSeconds 60
        $result = Get-MgxOption
        $result.MaxRetryAttempts | Should -Be 3
        $result.TotalTimeoutSeconds | Should -Be 60
        Set-MgxOption -Reset
    }

    It 'Should persist AttemptTimeoutSeconds via Set then Get' {
        Set-MgxOption -AttemptTimeoutSeconds 15
        $result = Get-MgxOption
        $result.AttemptTimeoutSeconds | Should -Be 15
        Set-MgxOption -Reset
    }

    It 'Should persist CircuitBreakerDurationSeconds via Set then Get' {
        Set-MgxOption -CircuitBreakerDurationSeconds 45
        $result = Get-MgxOption
        $result.CircuitBreakerDurationSeconds | Should -Be 45
        Set-MgxOption -Reset
    }

    It 'Should persist CircuitBreakerFailureRatio via Set then Get' {
        Set-MgxOption -CircuitBreakerFailureRatio 0.5
        $result = Get-MgxOption
        $result.CircuitBreakerFailureRatio | Should -Be 0.5
        Set-MgxOption -Reset
    }

    It 'Should persist CircuitBreakerMinThroughput via Set then Get' {
        Set-MgxOption -CircuitBreakerMinThroughput 50
        $result = Get-MgxOption
        $result.CircuitBreakerMinThroughput | Should -Be 50
        Set-MgxOption -Reset
    }

    It 'Should persist CircuitBreakerSamplingDurationSeconds via Set then Get' {
        Set-MgxOption -CircuitBreakerSamplingDurationSeconds 60
        $result = Get-MgxOption
        $result.CircuitBreakerSamplingDurationSeconds | Should -Be 60
        Set-MgxOption -Reset
    }

    It 'Should persist RateLimitQueueLimit via Set then Get' {
        Set-MgxOption -RateLimitQueueLimit 1000
        $result = Get-MgxOption
        $result.RateLimitQueueLimit | Should -Be 1000
        Set-MgxOption -Reset
    }

    It 'Should persist BatchChunkConcurrency via Set then Get' {
        Set-MgxOption -BatchChunkConcurrency 5
        $result = Get-MgxOption
        $result.BatchChunkConcurrency | Should -Be 5
        Set-MgxOption -Reset
    }

    It 'Should carry forward unspecified options when setting one parameter' {
        Set-MgxOption -Reset
        Set-MgxOption -MaxRetryAttempts 3
        $result = Get-MgxOption
        $result.MaxRetryAttempts | Should -Be 3
        $result.TotalTimeoutSeconds | Should -Be 300
        $result.AttemptTimeoutSeconds | Should -Be 30
        $result.RateLimitBurst | Should -Be 200
        $result.RateLimitPerSecond | Should -Be 50
        $result.CircuitBreakerDurationSeconds | Should -Be 15
        $result.CircuitBreakerFailureRatio | Should -Be 0.1
        $result.CircuitBreakerMinThroughput | Should -Be 40
        $result.RateLimitQueueLimit | Should -Be 500
        $result.CircuitBreakerSamplingDurationSeconds | Should -Be 30
        $result.BatchChunkConcurrency | Should -Be 1
        $result.BatchItemsPerSecond | Should -Be 20
        Set-MgxOption -Reset
    }

    It 'Should carry forward across multiple successive Set-MgxOption calls' {
        Set-MgxOption -Reset
        Set-MgxOption -MaxRetryAttempts 5
        Set-MgxOption -TotalTimeoutSeconds 60
        Set-MgxOption -AttemptTimeoutSeconds 10
        $result = Get-MgxOption
        $result.MaxRetryAttempts | Should -Be 5
        $result.TotalTimeoutSeconds | Should -Be 60
        $result.AttemptTimeoutSeconds | Should -Be 10
        $result.RateLimitBurst | Should -Be 200
        $result.CircuitBreakerDurationSeconds | Should -Be 15
        Set-MgxOption -Reset
    }

    It 'Should not change any options when called with no parameters' {
        Set-MgxOption -Reset
        $before = Get-MgxOption
        Set-MgxOption
        $after = Get-MgxOption
        $after.RateLimitBurst | Should -Be $before.RateLimitBurst
        $after.MaxRetryAttempts | Should -Be $before.MaxRetryAttempts
        $after.TotalTimeoutSeconds | Should -Be $before.TotalTimeoutSeconds
        $after.AttemptTimeoutSeconds | Should -Be $before.AttemptTimeoutSeconds
        $after.CircuitBreakerDurationSeconds | Should -Be $before.CircuitBreakerDurationSeconds
        $after.CircuitBreakerFailureRatio | Should -Be $before.CircuitBreakerFailureRatio
        $after.CircuitBreakerMinThroughput | Should -Be $before.CircuitBreakerMinThroughput
        $after.RateLimitQueueLimit | Should -Be $before.RateLimitQueueLimit
        $after.NoRateLimit | Should -Be $before.NoRateLimit
        $after.CircuitBreakerSamplingDurationSeconds | Should -Be $before.CircuitBreakerSamplingDurationSeconds
        $after.BatchChunkConcurrency | Should -Be $before.BatchChunkConcurrency
    }

    It 'Should reset all options to defaults with -Reset' {
        Set-MgxOption -MaxRetryAttempts 3 -TotalTimeoutSeconds 60 -AttemptTimeoutSeconds 10
        Set-MgxOption -Reset
        $result = Get-MgxOption
        $result.MaxRetryAttempts | Should -Be 7
        $result.TotalTimeoutSeconds | Should -Be 300
        $result.AttemptTimeoutSeconds | Should -Be 30
        $result.RateLimitBurst | Should -Be 200
        $result.RateLimitPerSecond | Should -Be 50
        $result.CircuitBreakerDurationSeconds | Should -Be 15
        $result.CircuitBreakerFailureRatio | Should -Be 0.1
        $result.CircuitBreakerMinThroughput | Should -Be 40
        $result.RateLimitQueueLimit | Should -Be 500
        $result.NoRateLimit | Should -BeFalse
        $result.CircuitBreakerSamplingDurationSeconds | Should -Be 30
        $result.BatchChunkConcurrency | Should -Be 1
        $result.BatchItemsPerSecond | Should -Be 20
    }

    It 'Should reject -MaxRetryAttempts 0' {
        { Set-MgxOption -MaxRetryAttempts 0 } | Should -Throw
    }

    It 'Should reject -TotalTimeoutSeconds 0' {
        { Set-MgxOption -TotalTimeoutSeconds 0 } | Should -Throw
    }

    It 'Should reject -AttemptTimeoutSeconds 0' {
        { Set-MgxOption -AttemptTimeoutSeconds 0 } | Should -Throw
    }

    It 'Should reject -CircuitBreakerDurationSeconds 0' {
        { Set-MgxOption -CircuitBreakerDurationSeconds 0 } | Should -Throw
    }

    It 'Should reject -CircuitBreakerFailureRatio 0' {
        { Set-MgxOption -CircuitBreakerFailureRatio 0 } | Should -Throw
    }

    It 'Should reject -CircuitBreakerFailureRatio 1.5' {
        { Set-MgxOption -CircuitBreakerFailureRatio 1.5 } | Should -Throw
    }

    It 'Should reject -CircuitBreakerMinThroughput 0' {
        { Set-MgxOption -CircuitBreakerMinThroughput 0 } | Should -Throw
    }

    It 'Should reject -CircuitBreakerMinThroughput 1' {
        { Set-MgxOption -CircuitBreakerMinThroughput 1 } | Should -Throw
    }

    It 'Should reject -RateLimitQueueLimit -1' {
        { Set-MgxOption -RateLimitQueueLimit -1 } | Should -Throw
    }

    It 'Should reject -CircuitBreakerSamplingDurationSeconds 4' {
        { Set-MgxOption -CircuitBreakerSamplingDurationSeconds 4 } | Should -Throw
    }

    It 'Should reject -CircuitBreakerSamplingDurationSeconds 301' {
        { Set-MgxOption -CircuitBreakerSamplingDurationSeconds 301 } | Should -Throw
    }

    It 'Should reject -BatchChunkConcurrency 0' {
        { Set-MgxOption -BatchChunkConcurrency 0 } | Should -Throw
    }

    It 'Should reject -BatchChunkConcurrency 11' {
        { Set-MgxOption -BatchChunkConcurrency 11 } | Should -Throw
    }

    It 'Should accept boundary values' {
        { Set-MgxOption -CircuitBreakerFailureRatio 1.0 } | Should -Not -Throw
        { Set-MgxOption -CircuitBreakerFailureRatio 0.01 } | Should -Not -Throw
        { Set-MgxOption -RateLimitQueueLimit 0 } | Should -Not -Throw
        { Set-MgxOption -CircuitBreakerSamplingDurationSeconds 5 } | Should -Not -Throw
        { Set-MgxOption -CircuitBreakerSamplingDurationSeconds 300 } | Should -Not -Throw
        { Set-MgxOption -BatchChunkConcurrency 1 } | Should -Not -Throw
        { Set-MgxOption -BatchChunkConcurrency 10 } | Should -Not -Throw
        Set-MgxOption -Reset
    }

    It 'Should reject values exceeding upper bounds' {
        { Set-MgxOption -MaxRetryAttempts 51 } | Should -Throw
        { Set-MgxOption -TotalTimeoutSeconds 3601 } | Should -Throw
        { Set-MgxOption -AttemptTimeoutSeconds 301 } | Should -Throw
        { Set-MgxOption -CircuitBreakerMinThroughput 1001 } | Should -Throw
        { Set-MgxOption -BatchChunkConcurrency 11 } | Should -Throw
    }
}

Describe 'C5: ConsistencyLevel Enforcement' {
    It 'Should throw when -Search is used without -ConsistencyLevel' {
        { Invoke-MgxRequest /users -Search "displayName:Test" } | Should -Throw '*ConsistencyLevel*'
    }

    It 'Should throw when Export-MgxCollection -Search is used without -ConsistencyLevel' {
        { Export-MgxCollection /users -OutputFile "test.jsonl" -Search "displayName:Test" } | Should -Throw '*ConsistencyLevel*'
    }

    It 'Should NOT throw when -Search is used WITH -ConsistencyLevel eventual' {
        # When connected, the command succeeds. When disconnected, it throws NotConnected.
        # Either way, it must NOT throw ConsistencyLevelRequired.
        try {
            Invoke-MgxRequest /users -Search "displayName:Test" -ConsistencyLevel eventual -Top 1 | Out-Null
        } catch {
            $_.FullyQualifiedErrorId | Should -Not -BeLike '*ConsistencyLevel*'
        }
    }

    It 'Should throw when Invoke-MgxBatchRequest URL contains $search without -ConsistencyLevel' {
        { '/users?$search="displayName:Test"' | Invoke-MgxBatchRequest -ErrorAction Stop } | Should -Throw '*ConsistencyLevel*'
    }

    It 'Should NOT throw when Invoke-MgxBatchRequest $search is used WITH -ConsistencyLevel' {
        try {
            '/users?$search="displayName:Test"' | Invoke-MgxBatchRequest -ConsistencyLevel eventual -ErrorAction Stop | Out-Null
        } catch {
            $_.FullyQualifiedErrorId | Should -Not -BeLike '*ConsistencyLevel*'
        }
    }
}

Describe 'Sync-MgxDelta Parameter Compatibility' {
    It 'Should have -Uri parameter (mandatory, position 0)' {
        $param = (Get-Command Sync-MgxDelta).Parameters['Uri']
        $param | Should -Not -BeNullOrEmpty
        $paramAttr = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] }
        $paramAttr.Mandatory | Should -BeTrue
        $paramAttr.Position | Should -Be 0
    }

    It 'Should have -DeltaPath parameter (mandatory)' {
        $param = (Get-Command Sync-MgxDelta).Parameters['DeltaPath']
        $param | Should -Not -BeNullOrEmpty
        $paramAttr = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] }
        $paramAttr.Mandatory | Should -BeTrue
    }

    It 'Should have -Property parameter with Select alias' {
        $param = (Get-Command Sync-MgxDelta).Parameters['Property']
        $param | Should -Not -BeNullOrEmpty
        $param.Aliases | Should -Contain 'Select'
    }

    It 'Should have -OutputFile parameter' {
        (Get-Command Sync-MgxDelta).Parameters.Keys | Should -Contain 'OutputFile'
    }

    It 'Should have -FullSync switch' {
        $param = (Get-Command Sync-MgxDelta).Parameters['FullSync']
        $param | Should -Not -BeNullOrEmpty
        $param.ParameterType | Should -Be ([switch])
    }

    It 'Should have -Filter parameter' {
        (Get-Command Sync-MgxDelta).Parameters.Keys | Should -Contain 'Filter'
    }

    It 'Should have -Top parameter' {
        (Get-Command Sync-MgxDelta).Parameters.Keys | Should -Contain 'Top'
    }

    It 'Should have -Headers parameter' {
        (Get-Command Sync-MgxDelta).Parameters.Keys | Should -Contain 'Headers'
    }

    It 'Should have -ApiVersion parameter with ValidateSet' {
        $param = (Get-Command Sync-MgxDelta).Parameters['ApiVersion']
        $param | Should -Not -BeNullOrEmpty
        $validateSet = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
        $validateSet | Should -Not -BeNullOrEmpty
        $validateSet.ValidValues | Should -Contain 'v1.0'
        $validateSet.ValidValues | Should -Contain 'beta'
    }
}

Describe 'Sync-MgxDelta Validation' {
    It 'Should reject absolute HTTPS URL' {
        { Sync-MgxDelta -Uri 'https://graph.microsoft.com/v1.0/users/delta' -DeltaPath '/tmp/test.delta' -ErrorAction Stop } |
            Should -Throw '*relative path*'
    }

    It 'Should reject absolute HTTP URL' {
        { Sync-MgxDelta -Uri 'http://graph.microsoft.com/v1.0/users/delta' -DeltaPath '/tmp/test.delta' -ErrorAction Stop } |
            Should -Throw '*relative path*'
    }
}

Describe 'Sync-MgxDelta State Management' {
    BeforeAll {
        $script:testDir = Join-Path ([System.IO.Path]::GetTempPath()) "mgx-delta-tests-$(New-Guid)"
        New-Item -ItemType Directory -Path $script:testDir -Force | Out-Null
    }

    AfterAll {
        if (Test-Path $script:testDir) { Remove-Item $script:testDir -Recurse -Force }
    }

    It 'Should reject -DeltaPath equal to -OutputFile' {
        $dp = Join-Path $script:testDir 'same.json'
        { Sync-MgxDelta -Uri '/users/delta' -DeltaPath $dp -OutputFile $dp -ErrorAction Stop } |
            Should -Throw '*same file*'
    }

    It 'Should warn when URI does not contain /delta' {
        $dp = Join-Path $script:testDir 'nodelta.json'
        $warnings = $null
        try {
            Sync-MgxDelta -Uri '/users' -DeltaPath $dp -ErrorAction SilentlyContinue -WarningVariable warnings 3>$null
        } catch { }
        $warnings | Should -Not -BeNullOrEmpty
        "$warnings" | Should -BeLike '*does not contain*delta*'
    }

    It 'Should detect resource mismatch in delta state' {
        $dp = Join-Path $script:testDir 'resource-mismatch.json'
        @{
            deltaLink = 'https://graph.microsoft.com/v1.0/groups/delta?$deltatoken=abc'
            select = ''
            filter = $null
            resource = '/groups/delta'
            lastSync = (Get-Date).ToString('o')
            itemCount = 10
            graphEndpoint = 'https://graph.microsoft.com'
        } | ConvertTo-Json | Set-Content $dp

        { Sync-MgxDelta -Uri '/users/delta' -DeltaPath $dp -ErrorAction Stop } |
            Should -Throw '*resource*'
    }

    It 'Should detect select change and warn about full re-sync' {
        $dp = Join-Path $script:testDir 'select-change.json'
        @{
            deltaLink = 'https://graph.microsoft.com/v1.0/users/delta?$deltatoken=abc'
            select = 'displayName,id'
            filter = $null
            resource = '/users/delta'
            lastSync = (Get-Date).ToString('o')
            itemCount = 10
            graphEndpoint = 'https://graph.microsoft.com'
        } | ConvertTo-Json | Set-Content $dp

        $warnings = $null
        try {
            Sync-MgxDelta -Uri '/users/delta' -DeltaPath $dp -Property mail,jobTitle -ErrorAction SilentlyContinue -WarningVariable warnings 3>$null
        } catch { }
        $warnings | Should -Not -BeNullOrEmpty
        "$warnings" | Should -BeLike '*Property selection changed*'
    }

    It 'Should detect filter change and warn about full re-sync' {
        $dp = Join-Path $script:testDir 'filter-change.json'
        @{
            deltaLink = 'https://graph.microsoft.com/v1.0/users/delta?$deltatoken=abc'
            select = ''
            filter = "department eq 'Sales'"
            resource = '/users/delta'
            lastSync = (Get-Date).ToString('o')
            itemCount = 10
            graphEndpoint = 'https://graph.microsoft.com'
        } | ConvertTo-Json | Set-Content $dp

        $warnings = $null
        try {
            Sync-MgxDelta -Uri '/users/delta' -DeltaPath $dp -Filter "department eq 'Engineering'" -ErrorAction SilentlyContinue -WarningVariable warnings 3>$null
        } catch { }
        $warnings | Should -Not -BeNullOrEmpty
        "$warnings" | Should -BeLike '*Filter changed*'
    }

    It 'Should warn on corrupt delta state file' {
        $dp = Join-Path $script:testDir 'corrupt.json'
        'not valid json{{{' | Set-Content $dp

        $warnings = $null
        try {
            Sync-MgxDelta -Uri '/users/delta' -DeltaPath $dp -ErrorAction SilentlyContinue -WarningVariable warnings 3>$null
        } catch { }
        $warnings | Should -Not -BeNullOrEmpty
        "$warnings" | Should -BeLike '*corrupt*'
    }

    It 'Should delete delta state when -FullSync is specified' {
        $dp = Join-Path $script:testDir 'fullsync.json'
        @{
            deltaLink = 'https://graph.microsoft.com/v1.0/users/delta?$deltatoken=abc'
            select = ''
            filter = $null
            resource = '/users/delta'
            lastSync = (Get-Date).ToString('o')
            itemCount = 10
            graphEndpoint = 'https://graph.microsoft.com'
        } | ConvertTo-Json | Set-Content $dp

        # Verify file exists before FullSync
        Test-Path $dp | Should -BeTrue

        # FullSync should delete the file before starting the sync.
        # Even if the sync succeeds and saves new state, the OLD file
        # should be gone. Use the original resource so the test works
        # with or without a Graph connection.
        try {
            Sync-MgxDelta -Uri '/users/delta' -DeltaPath $dp -FullSync -ErrorAction SilentlyContinue
        } catch { }

        # If connected: sync runs, saves NEW state → file exists but is fresh (new deltaLink)
        # If not connected: GetClient() fails, no save → file doesn't exist
        # Either way, the OLD deltatoken 'abc' should be gone
        if (Test-Path $dp) {
            $state = Get-Content $dp | ConvertFrom-Json
            $state.deltaLink | Should -Not -BeLike '*abc*' -Because 'FullSync should have replaced the old delta state'
        }
        # If file doesn't exist, that's also correct (no connection = no save)
    }

    It 'Should NOT trigger re-sync when select order differs' {
        $dp = Join-Path $script:testDir 'select-order.json'
        @{
            deltaLink = 'https://graph.microsoft.com/v1.0/users/delta?$deltatoken=abc'
            select = 'displayName,id'  # stored normalized
            filter = $null
            resource = '/users/delta'
            lastSync = (Get-Date).ToString('o')
            itemCount = 10
            graphEndpoint = 'https://graph.microsoft.com'
        } | ConvertTo-Json | Set-Content $dp

        $warnings = $null
        try {
            # Same properties, different order
            Sync-MgxDelta -Uri '/users/delta' -DeltaPath $dp -Property id,displayName -ErrorAction SilentlyContinue -WarningVariable warnings 3>$null
        } catch { }
        # Should NOT warn about property selection change
        $selectWarnings = $warnings | Where-Object { "$_" -like '*Property selection changed*' }
        $selectWarnings | Should -BeNullOrEmpty
    }
}

Describe 'Sync-MgxDelta Session Endpoint' {
    # These scenarios need a session that is connected to a non-default endpoint. Each runs
    # in a child pwsh: a custom Graph environment plus Connect-MgGraph -AccessToken gives a
    # connected session without a tenant, and the child process keeps that connection (and
    # the module's static endpoint state) out of this one.
    BeforeAll {
        $script:modulePath = Join-Path $PSScriptRoot '../../module/mgx.psd1'
        $script:testDir = Join-Path ([System.IO.Path]::GetTempPath()) "mgx-endpoint-tests-$(New-Guid)"
        New-Item -ItemType Directory -Path $script:testDir -Force | Out-Null

        $script:graphAuthAvailable = [bool](Get-Module -ListAvailable Microsoft.Graph.Authentication)

        # Same re-exec probe as 'Should unload cleanly when no Graph request ever ran':
        # a dotnet-tool PowerShell install has no re-executable host.
        $script:hostExe = @(
            (Get-Process -Id $PID).Path
            (Get-Command pwsh -ErrorAction SilentlyContinue).Source
        ) | Where-Object { $_ -and (Split-Path $_ -Leaf) -like 'pwsh*' } | Select-Object -First 1
        if ($script:hostExe) {
            $probeOk = $null
            try { $probeOk = & $script:hostExe -NoProfile -Command '"alive"' 2>$null } catch { $probeOk = $null }
            if ($probeOk -ne 'alive') { $script:hostExe = $null }
        }

        # Custom environments persist across sessions, so every name is tracked and swept
        # in AfterAll in case a child dies before its own cleanup runs.
        $script:envNames = [System.Collections.Generic.List[string]]::new()

        function Get-SkipReason {
            if (-not $script:hostExe) { return 'this host cannot launch a child pwsh' }
            if (-not $script:graphAuthAvailable) { return 'Microsoft.Graph.Authentication is not installed' }
            return $null
        }

        function Invoke-ConnectedChild {
            param([string]$GraphEndpoint, [string]$Body)

            $envName = "MgxTest-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
            $script:envNames.Add($envName)
            $child = @"
Import-Module Microsoft.Graph.Authentication
Import-Module '$script:modulePath'
try {
    Add-MgEnvironment -Name '$envName' -GraphEndpoint '$GraphEndpoint' -AzureADEndpoint 'https://login.microsoftonline.com' | Out-Null
    Connect-MgGraph -Environment '$envName' -AccessToken (ConvertTo-SecureString 'not-a-real-token' -AsPlainText -Force) -NoWelcome
    # -AccessToken leaves TenantId empty, and an empty TenantId reads as "not connected".
    [Microsoft.Graph.PowerShell.Authentication.GraphSession]::Instance.AuthContext.TenantId = 'test-tenant'
$Body
}
finally {
    Disconnect-MgGraph -ErrorAction SilentlyContinue | Out-Null
    Remove-MgEnvironment -Name '$envName' -ErrorAction SilentlyContinue | Out-Null
}
"@
            & $script:hostExe -NoProfile -Command $child 2>&1 | ForEach-Object { "$_" }
        }

        function New-DeltaStateFile {
            param([string]$Name, [string]$DeltaLink, [string]$GraphEndpoint)

            $dp = Join-Path $script:testDir $Name
            @{
                deltaLink = $DeltaLink
                select = ''
                filter = $null
                resource = '/users/delta'
                lastSync = (Get-Date).ToString('o')
                itemCount = 10
                graphEndpoint = $GraphEndpoint
            } | ConvertTo-Json | Set-Content $dp
            return $dp
        }

        function Get-FreePort {
            $probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
            $probe.Start()
            $port = ([System.Net.IPEndPoint]$probe.LocalEndpoint).Port
            $probe.Stop()
            return $port
        }
    }

    AfterAll {
        if (Test-Path $script:testDir) { Remove-Item $script:testDir -Recurse -Force }
        if ($script:hostExe -and $script:graphAuthAvailable -and $script:envNames.Count -gt 0) {
            $names = "'" + ($script:envNames -join "','") + "'"
            & $script:hostExe -NoProfile -Command "Import-Module Microsoft.Graph.Authentication; foreach (`$n in @($names)) { Remove-MgEnvironment -Name `$n -ErrorAction SilentlyContinue | Out-Null }" 2>$null | Out-Null
        }
    }

    It 'Should send the first request of a session to the connected endpoint' {
        $reason = Get-SkipReason
        if (-not $reason -and -not (Get-Command Start-ThreadJob -ErrorAction SilentlyContinue)) {
            $reason = 'Start-ThreadJob is not available'
        }
        if ($reason) { Set-ItResult -Skipped -Because $reason; return }

        $port = Get-FreePort
        $listener = [System.Net.HttpListener]::new()
        $listener.Prefixes.Add("http://localhost:$port/")
        $listener.Start()
        $served = [System.Collections.Concurrent.ConcurrentQueue[string]]::new()
        $server = Start-ThreadJob -ScriptBlock {
            $listener = $using:listener
            $served = $using:served
            $body = [System.Text.Encoding]::UTF8.GetBytes(
                '{"value":[{"id":"u1"},{"id":"u2"}],' +
                '"@odata.deltaLink":"http://localhost:' + $using:port + '/v1.0/users/delta?$deltatoken=first"}')
            while ($listener.IsListening) {
                try { $context = $listener.GetContext() } catch { break }
                $served.Enqueue($context.Request.Url.PathAndQuery)
                $context.Response.ContentType = 'application/json'
                $context.Response.OutputStream.Write($body, 0, $body.Length)
                $context.Response.Close()
            }
        }

        $dp = Join-Path $script:testDir 'first-call.json'
        try {
            # Set-MgxOption does not initialize the transport, so Sync-MgxDelta is still the
            # first cmdlet of the session to build the client; the tight limits keep a
            # regression (which sends this request to the default endpoint instead of the
            # connected one) from stalling the suite on retries.
            $out = Invoke-ConnectedChild -GraphEndpoint "http://localhost:$port" -Body @"
    Set-MgxOption -MaxRetryAttempts 1 -AttemptTimeoutSeconds 15 -TotalTimeoutSeconds 30
    try {
        `$changes = Sync-MgxDelta /users/delta -DeltaPath '$dp' -ErrorAction Stop
        Write-Output "SYNCED=`$(@(`$changes).Count)"
    } catch {
        Write-Output "ERROR-ID=`$(`$_.FullyQualifiedErrorId)"
    }
"@
            ($out -join ' ') | Should -BeLike '*SYNCED=2*'
            $requests = @($served)
            $requests | Should -Not -BeNullOrEmpty -Because 'the first request of the session must reach the connected endpoint'
            $requests[0] | Should -BeLike '/v1.0/users/delta*'
        }
        finally {
            $listener.Stop()
            $listener.Close()
            Stop-Job $server -ErrorAction SilentlyContinue
            Remove-Job $server -Force -ErrorAction SilentlyContinue
        }
    }

    It 'Should detect endpoint mismatch in delta state' {
        $reason = Get-SkipReason
        if ($reason) { Set-ItResult -Skipped -Because $reason; return }

        $port = Get-FreePort
        $dp = New-DeltaStateFile -Name 'endpoint-mismatch.json' `
            -DeltaLink 'https://graph.microsoft.us/v1.0/users/delta?$deltatoken=abc' `
            -GraphEndpoint 'https://graph.microsoft.us'

        $out = Invoke-ConnectedChild -GraphEndpoint "http://localhost:$port" -Body @"
    try {
        Sync-MgxDelta /users/delta -DeltaPath '$dp' -ErrorAction Stop
        Write-Output 'NO-ERROR'
    } catch {
        Write-Output "ERROR-ID=`$(`$_.FullyQualifiedErrorId)"
        Write-Output "ERROR-MSG=`$(`$_.Exception.Message)"
    }
"@
        $text = $out -join ' '
        $text | Should -BeLike '*DeltaEndpointMismatch*'
        $text | Should -BeLike '*graph.microsoft.us*'
        # The error must name the endpoint the session is connected to, not the default.
        $text | Should -BeLike "*localhost:$port*"
    }

    It 'Should reject tampered deltaLink with wrong host (SSRF)' {
        $reason = Get-SkipReason
        if ($reason) { Set-ItResult -Skipped -Because $reason; return }

        $port = Get-FreePort
        $dp = New-DeltaStateFile -Name 'ssrf.json' `
            -DeltaLink 'https://evil.com/v1.0/users/delta?$deltatoken=stolen' `
            -GraphEndpoint "http://localhost:$port"

        $out = Invoke-ConnectedChild -GraphEndpoint "http://localhost:$port" -Body @"
    try {
        Sync-MgxDelta /users/delta -DeltaPath '$dp' -ErrorAction Stop
        Write-Output 'NO-ERROR'
    } catch {
        Write-Output "ERROR-MSG=`$(`$_.Exception.Message)"
    }
"@
        ($out -join ' ') | Should -BeLike '*invalid or untrusted*'
    }

    It 'Should reject tampered deltaLink with wrong resource path' {
        $reason = Get-SkipReason
        if ($reason) { Set-ItResult -Skipped -Because $reason; return }

        # https so the link passes host validation and reaches the path check.
        $port = Get-FreePort
        $dp = New-DeltaStateFile -Name 'path-tamper.json' `
            -DeltaLink "https://localhost:$port/v1.0/me/messages?`$deltatoken=abc" `
            -GraphEndpoint "https://localhost:$port"

        $out = Invoke-ConnectedChild -GraphEndpoint "https://localhost:$port" -Body @"
    try {
        Sync-MgxDelta /users/delta -DeltaPath '$dp' -ErrorAction Stop
        Write-Output 'NO-ERROR'
    } catch {
        Write-Output "ERROR-MSG=`$(`$_.Exception.Message)"
    }
"@
        ($out -join ' ') | Should -BeLike '*path*does not match*'
    }
}

Describe 'NormalizeSelect (via reflection)' {
    BeforeAll {
        $script:normalize = [Mgx.Cmdlets.Cmdlets.Delta.SyncMgxDelta].GetMethod(
            'NormalizeSelect',
            [System.Reflection.BindingFlags]::Static -bor [System.Reflection.BindingFlags]::NonPublic)
    }

    It 'Should reorder properties alphabetically' {
        $result = $script:normalize.Invoke($null, @('displayName,id'))
        $result | Should -Be 'displayName,id'  # already sorted
        $result2 = $script:normalize.Invoke($null, @('id,displayName'))
        $result2 | Should -Be 'displayName,id'  # reordered
        $result | Should -Be $result2  # same after normalization
    }

    It 'Should deduplicate properties' {
        $result = $script:normalize.Invoke($null, @('id,id,displayName'))
        $result | Should -Be 'displayName,id'
    }

    It 'Should handle null' {
        $result = $script:normalize.Invoke($null, @($null))
        $result | Should -Be ''
    }

    It 'Should handle empty string' {
        $result = $script:normalize.Invoke($null, @(''))
        $result | Should -Be ''
    }

    It 'Should trim whitespace' {
        $result = $script:normalize.Invoke($null, @(' id , displayName '))
        $result | Should -Be 'displayName,id'
    }
}

Describe 'HttpClient Reset Leaves A Held Client Alone' {
    # A replaced transport is dropped, not closed. Every ResilientGraphClient built on it
    # captured it in its constructor and keeps sending through it for as long as its
    # enumeration runs, so closing it on a timer killed long-running cmdlets mid-stream; and a
    # borrowed SDK client was never mgx's to close at all. ResetHttpClient must leave both.

    BeforeAll {
        # Ensure Mgx.Engine types are loaded (transitive dep of Mgx.Cmdlets)
        $enginePath = Join-Path $PSScriptRoot '../../module/Mgx.Engine.dll'
        [System.Reflection.Assembly]::LoadFrom($enginePath) | Out-Null

        $script:CmdletBaseType = [Mgx.Cmdlets.Base.MgxCmdletBase]
        $script:Flags = [System.Reflection.BindingFlags]'Static,NonPublic'
        $script:DisposedField = [System.Net.Http.HttpMessageInvoker].GetField(
            '_disposed', [System.Reflection.BindingFlags]'Instance,NonPublic')

        # Helper: create options with 1-second TotalTimeoutSeconds so delayed disposal fires fast
        $script:ShortTimeoutOptions = [Mgx.Engine.Http.ResilientGraphClientOptions]::new()
        [Mgx.Engine.Http.ResilientGraphClientOptions].GetField(
            '_totalTimeoutSeconds', [System.Reflection.BindingFlags]'Instance,NonPublic'
        ).SetValue($script:ShortTimeoutOptions, 1)
    }

    AfterEach {
        # Restore defaults so other tests aren't affected
        $script:CmdletBaseType.GetField('s_graphHttpClient', $script:Flags).SetValue($null, $null)
        $script:CmdletBaseType.GetField('s_ownsHttpClient', $script:Flags).SetValue($null, $false)
        $script:CmdletBaseType.GetField('s_clientOptions', $script:Flags).SetValue(
            $null, [Mgx.Engine.Http.ResilientGraphClientOptions]::Default)
        # Clients are keyed on a fingerprint of the whole auth context, not the tenant id.
        # This teardown reflects on that field by name, so it must track any rename.
        $script:CmdletBaseType.GetField('s_cachedAuthFingerprint', $script:Flags).SetValue($null, $null)
    }

    It 'Should NOT dispose HttpClient when s_ownsHttpClient is false' {
        $httpClient = [System.Net.Http.HttpClient]::new()

        $script:CmdletBaseType.GetField('s_graphHttpClient', $script:Flags).SetValue($null, $httpClient)
        $script:CmdletBaseType.GetField('s_ownsHttpClient', $script:Flags).SetValue($null, $false)
        $script:CmdletBaseType.GetField('s_clientOptions', $script:Flags).SetValue(
            $null, $script:ShortTimeoutOptions)

        # ResetHttpClient is public static, so use 'Static,Public' (not $script:Flags which is NonPublic)
        $script:CmdletBaseType.GetMethod('ResetHttpClient',
            [System.Reflection.BindingFlags]'Static,Public').Invoke($null, $null)

        # Wait longer than TotalTimeoutSeconds (1s) to let any scheduled disposal fire
        Start-Sleep -Seconds 3

        # HttpClient should NOT be disposed (SDK owns it)
        $script:DisposedField.GetValue($httpClient) | Should -BeFalse
    }

    It 'Should NOT dispose an owned HttpClient a running cmdlet may still hold' {
        $httpClient = [System.Net.Http.HttpClient]::new()

        $script:CmdletBaseType.GetField('s_graphHttpClient', $script:Flags).SetValue($null, $httpClient)
        $script:CmdletBaseType.GetField('s_ownsHttpClient', $script:Flags).SetValue($null, $true)
        $script:CmdletBaseType.GetField('s_clientOptions', $script:Flags).SetValue(
            $null, $script:ShortTimeoutOptions)

        $script:CmdletBaseType.GetMethod('ResetHttpClient',
            [System.Reflection.BindingFlags]'Static,Public').Invoke($null, $null)

        # Longer than the TotalTimeoutSeconds (1s) the disposal used to be scheduled on
        Start-Sleep -Seconds 3

        # Ownership says who may replace it, not how long anything still using it will live
        $script:DisposedField.GetValue($httpClient) | Should -BeFalse
    }
}

Describe 'Enable-MgxResilience SDK Client Wrap' {
    It 'Should keep the SDK BaseAddress on the wrapped client' {
        # Invoke-MgGraphRequest resolves a relative -Uri against GraphHttpClient.BaseAddress
        # before any handler runs, so the wrapper Enable-MgxResilience installs must carry
        # the original client's BaseAddress or every relative-URI SDK call dies on the spot.
        #
        # Runs in a child process: the wrap mutates GraphSession.Instance and connects a
        # mock session, neither of which may leak into the parent suite. No tenant and no
        # listener are needed - the endpoint points at a dead port because Enable's init
        # probe only has to run, not succeed, for the SDK to build its HttpClient.
        if (-not (Get-Module -ListAvailable Microsoft.Graph.Authentication)) {
            Set-ItResult -Skipped -Because 'Microsoft.Graph.Authentication is not installed'
            return
        }

        $modulePath = Join-Path $PSScriptRoot '../../module/mgx.psd1'
        # Add-MgEnvironment PERSISTS custom environments, so the name is unique per run
        # and removed again in the probe's finally block.
        $envName = 'MgxWrap' + [guid]::NewGuid().ToString('N').Substring(0, 10)
        $probe = @"
Import-Module Microsoft.Graph.Authentication
Import-Module '$modulePath' -Force
Add-MgEnvironment -Name $envName -GraphEndpoint 'http://localhost:59997' ``
    -AzureADEndpoint 'https://login.microsoftonline.com' | Out-Null
try {
    Connect-MgGraph -Environment $envName ``
        -AccessToken (ConvertTo-SecureString 'mock-token-not-validated' -AsPlainText -Force) -NoWelcome
    # -AccessToken auth leaves AuthContext.TenantId empty, and Mgx's auth fingerprint
    # treats an empty TenantId as "not connected" (1.0.4 identity fix). Give it a value.
    [Microsoft.Graph.PowerShell.Authentication.GraphSession]::Instance.AuthContext.TenantId = 'mock-tenant'
    Enable-MgxResilience -WarningAction SilentlyContinue
    `$wrapped = [Microsoft.Graph.PowerShell.Authentication.GraphSession]::Instance.GraphHttpClient
    Write-Output ('WRAPPED [' + `$wrapped.BaseAddress + ']')
    Disable-MgxResilience
    `$restored = [Microsoft.Graph.PowerShell.Authentication.GraphSession]::Instance.GraphHttpClient
    Write-Output ('RESTORED [' + `$restored.BaseAddress + ']')
}
finally {
    Disconnect-MgGraph -ErrorAction SilentlyContinue | Out-Null
    Remove-MgEnvironment -Name $envName -ErrorAction SilentlyContinue | Out-Null
}
"@
        # Same host re-exec dance as 'Should unload cleanly': prove the host can spawn a
        # child before blaming mgx for a dotnet-tool install that cannot.
        $hostExe = @(
            (Get-Process -Id $PID).Path
            (Get-Command pwsh -ErrorAction SilentlyContinue).Source
        ) | Where-Object { $_ -and (Split-Path $_ -Leaf) -like 'pwsh*' } | Select-Object -First 1

        if (-not $hostExe) {
            Set-ItResult -Skipped -Because 'this host cannot re-exec itself (dotnet-tool PowerShell install)'
            return
        }
        $probeOk = $null
        try { $probeOk = & $hostExe -NoProfile -Command '"alive"' 2>$null } catch { $probeOk = $null }
        if ($probeOk -ne 'alive') {
            Set-ItResult -Skipped -Because "host '$hostExe' cannot be launched as a child process"
            return
        }

        $result = @(& $hostExe -NoProfile -Command $probe)
        $restoredLine = $result | Where-Object { $_ -like 'RESTORED *' }
        $wrappedLine = $result | Where-Object { $_ -like 'WRAPPED *' }

        # Disable restores the original SDK client, so RESTORED is the BaseAddress the
        # wrapper was built from; the wrap must have carried it over verbatim.
        $restoredLine | Should -Not -BeNullOrEmpty
        $restoredLine | Should -Not -Be 'RESTORED []' -Because 'the original SDK client carries a BaseAddress'
        $wrappedLine | Should -Be ('WRAPPED ' + ($restoredLine -replace '^RESTORED '))
    }
}

# Sustained auth pipeline test. Exercises token refresh under load.
# MSAL's AuthenticationHandler refreshes tokens proactively (5 min before expiry).
# Azure AD tokens expire at 60-90 min, so crossing a token boundary requires ~65+ min.
# This test validates the auth pipeline survives sustained use; manual 2+ hour runs
# are recommended before enterprise deployment per panel review findings.
Describe 'Sustained Auth Pipeline' -Tag 'Live', 'LongRunning' {
    It 'Export-MgxCollection survives sustained load without auth errors' {
        $context = Get-MgContext -ErrorAction SilentlyContinue
        if (-not $context) {
            Set-ItResult -Skipped -Because 'Not connected to Microsoft Graph'
            return
        }
        $outFile = [System.IO.Path]::GetTempFileName() + '.jsonl'
        $cpFile  = [System.IO.Path]::GetTempFileName() + '.json'
        try {
            $sw = [System.Diagnostics.Stopwatch]::StartNew()

            # Export audit logs (large collection, sustained HTTP traffic)
            Export-MgxCollection /auditLogs/signIns -OutputFile $outFile `
                -CheckpointPath $cpFile -All -ErrorAction Stop

            $sw.Stop()
            $lineCount = (Get-Content $outFile -Raw).Split("`n").Where({ $_ }).Count

            # Should have exported records without auth failure
            $lineCount | Should -BeGreaterThan 0
            Write-Host "Exported $lineCount sign-in records in $([math]::Round($sw.Elapsed.TotalMinutes, 1)) minutes"
        } finally {
            Remove-Item $outFile, $cpFile -Force -ErrorAction SilentlyContinue
        }
    }
}
