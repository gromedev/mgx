<#
    These exist because the rest of the suite mocks HTTP. A mocked test proves the code does
    what it was written to do; it cannot prove the request that leaves the process is one Graph
    accepts. Enable-MgxResilience shipped a wrapper client with no BaseAddress and a green
    suite: every test passed an absolute URI, and every relative-URI call would have thrown.

    So: relative URIs wherever a caller would use one, real responses, no mocks in this file.

    Read-only. Nothing here writes to a tenant.

    Run:  Invoke-Pester -Path ./tests/Live
    Needs AZURE_TENANT_ID / AZURE_CLIENT_ID / AZURE_CLIENT_CERTIFICATE_PATH for a directory
    tenant, and optionally MGX_LIVE_CONTENT_* for the content tests.
#>

BeforeAll {
    $repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    if (-not $env:AZURE_TENANT_ID -or -not $env:AZURE_CLIENT_ID -or
        (-not $env:AZURE_CLIENT_CERTIFICATE_PATH -and -not $env:AZURE_CLIENT_SECRET)) {
        throw "Live tests need AZURE_TENANT_ID, AZURE_CLIENT_ID, and either " +
              "AZURE_CLIENT_CERTIFICATE_PATH or AZURE_CLIENT_SECRET. " +
              "They are not skipped on purpose: a live suite that skips itself is a live suite nobody runs."
    }
    . "$repo/tests/benchmarks/common.ps1"
    Import-MgxLocal
    Connect-MgxBenchmark | Out-Null

    # Sized from the tenant rather than assumed. These tests have to hold on a 19-user tenant
    # and a 100,000-user one: a live suite that only passes against one directory is one that
    # gets switched off the first time it meets another.
    $tok = Get-BenchAppToken
    $script:UserCount = [int](Invoke-RestMethod -Uri 'https://graph.microsoft.com/v1.0/users/$count' `
        -Headers @{ Authorization = "Bearer $tok"; ConsistencyLevel = 'eventual' })
    # Enough to need more than one page where the tenant allows it, never more than it holds.
    $script:Slice = [Math]::Min(150, $script:UserCount)
    $script:SmallSlice = [Math]::Min(5, $script:UserCount)
}

Describe 'Invoke-MgxRequest' {
    It 'returns objects for a relative URI' {
        (Invoke-MgxRequest '/users?$top=2' -WarningAction SilentlyContinue).Count | Should -Be 2
    }
    It 'honors -Property' {
        (Invoke-MgxRequest '/users?$top=1' -Property id -WarningAction SilentlyContinue).id | Should -Not -BeNullOrEmpty
    }
    It 'honors -Filter' {
        Invoke-MgxRequest /users -Filter "startsWith(displayName,'b')" -Top 1 | Should -Not -BeNullOrEmpty
    }
    It 'caps the total at -Top' {
        (Invoke-MgxRequest /users -Top $script:Slice -Property id | Measure-Object).Count |
            Should -Be $script:Slice
    }
    It 'caps the total at -Top even with -All' {
        (Invoke-MgxRequest /users -All -Top $script:Slice -Property id | Measure-Object).Count |
            Should -Be $script:Slice
    }
    It 'reaches the beta endpoint under -ApiVersion beta' {
        (Invoke-MgxRequest /users -ApiVersion beta -Top $script:SmallSlice -Property id | Measure-Object).Count |
            Should -Be $script:SmallSlice
    }
    It 'stamps each entity of an {id} fan-out with the id it came from' {
        # The only thing that says which piped id produced which object once the fan-out has
        # interleaved them. Nothing else in the suite asserts the property exists.
        $ids = @(Invoke-MgxRequest /users -Top $script:SmallSlice -Property id | ForEach-Object { $_.id })
        $seen = @($ids | Invoke-MgxRequest '/users/{id}' -Property id | ForEach-Object { $_['_MgxSourceId'] })
        Compare-Object ($ids | Sort-Object) ($seen | Sort-Object) | Should -BeNullOrEmpty
    }
    It 'refuses an {id} Uri with nothing piped to it' {
        { Invoke-MgxRequest '/users/{id}' } | Should -Throw '*pipeline*'
    }
    It 'returns the user the SDK returns for the same id' {
        # Read against the SDK rather than against itself: same tenant, same id, same three
        # fields. A mock can agree with whatever the test author wrote; the directory cannot.
        $id  = (Invoke-MgxRequest '/users?$top=1' -Property id -WarningAction SilentlyContinue).id
        $mgx = Invoke-MgxRequest "/users/$id" -Property id,displayName,userPrincipalName
        $mg  = Get-MgUser -UserId $id -Property Id,DisplayName,UserPrincipalName
        $mgx.id                | Should -Be $mg.Id
        $mgx.displayName       | Should -Be $mg.DisplayName
        $mgx.userPrincipalName | Should -Be $mg.UserPrincipalName
    }
}

Describe 'Invoke-MgxBatchRequest' {
    It 'executes a batch of relative URIs' {
        $ids = Invoke-MgxRequest /users -Top $script:SmallSlice -Property id | ForEach-Object { $_.id }
        $res = $ids | ForEach-Object { "/users/$_" } | Invoke-MgxBatchRequest -Method GET
        ($res | Measure-Object).Count | Should -Be $script:SmallSlice
    }
}

Describe 'Expand-MgxRelation' {
    It 'fans out over a relation' {
        $users = Invoke-MgxRequest /users -Top $script:SmallSlice -Property id,displayName
        $r = $users | Expand-MgxRelation -Uri '/users/{id}/manager' -As manager -SkipNotFound -SkipForbidden
        $r | Should -Not -BeNull
    }
}

Describe 'Export-MgxCollection' {
    It 'writes one JSONL line per object' {
        $f = Join-Path ([IO.Path]::GetTempPath()) "mgxlive-$([Guid]::NewGuid().ToString('N')).jsonl"
        try {
            Export-MgxCollection /users -OutputFile $f -Top $script:Slice -Property id | Out-Null
            (Get-Content $f).Count | Should -Be $script:Slice
        } finally { Remove-Item $f -ErrorAction SilentlyContinue }
    }
}

Describe 'Sync-MgxDelta' {
    It 'baselines with -Latest and writes state' {
        $d = Join-Path ([IO.Path]::GetTempPath()) "mgxlive-$([Guid]::NewGuid().ToString('N')).json"
        try {
            Sync-MgxDelta /users/delta -DeltaPath $d -Latest | Out-Null
            Test-Path $d | Should -BeTrue
        } finally { Remove-Item $d -ErrorAction SilentlyContinue }
    }
}

Describe 'Get-MgxOption' {
    It 'returns the current settings' { Get-MgxOption | Should -Not -BeNull }
}

Describe 'Set-MgxOption' {
    It 'changes a setting and reports it back' {
        $before = (Get-MgxOption).RateLimitPerSecond
        try {
            Set-MgxOption -RateLimitPerSecond 42
            (Get-MgxOption).RateLimitPerSecond | Should -Be 42
        } finally { if ($before) { Set-MgxOption -RateLimitPerSecond $before } }
    }
}

Describe 'Get-MgxTelemetry' {
    It 'counts requests the session made' {
        Invoke-MgxRequest '/users?$top=1' -WarningAction SilentlyContinue | Out-Null
        (Get-MgxTelemetry).Requests | Should -BeGreaterThan 0
    }
    It 'zeroes on -Reset' {
        Get-MgxTelemetry -Reset | Out-Null
        (Get-MgxTelemetry).Requests | Should -Be 0
    }
}

Describe 'Enable-MgxResilience' {
    # The regression this file was written for: the wrapper dropped BaseAddress, so a RELATIVE
    # -Uri threw from PrepareUri before any handler ran. Absolute URIs were unaffected, which is
    # why a full green suite said nothing.
    AfterEach { Disable-MgxResilience -ErrorAction SilentlyContinue | Out-Null }

    It 'leaves a relative-URI SDK call working' {
        Enable-MgxResilience | Out-Null
        Invoke-MgGraphRequest -Method GET -Uri '/v1.0/users?$top=1' | Should -Not -BeNull
    }
    It 'leaves SDK cmdlets working' {
        Enable-MgxResilience | Out-Null
        (Get-MgUser -Top ([Math]::Min(2, $script:UserCount)) -Property id | Measure-Object).Count |
            Should -Be ([Math]::Min(2, $script:UserCount))
    }
}

Describe 'Disable-MgxResilience' {
    It 'restores plain SDK calls' {
        Enable-MgxResilience | Out-Null
        Disable-MgxResilience | Out-Null
        Invoke-MgGraphRequest -Method GET -Uri '/v1.0/users?$top=1' | Should -Not -BeNull
    }
}

Describe 'Get-MgxResilience' {
    It 'reports state either side of a wrap' {
        Get-MgxResilience | Should -Not -BeNull
        Enable-MgxResilience | Out-Null
        try { Get-MgxResilience | Should -Not -BeNull } finally { Disable-MgxResilience | Out-Null }
    }
}

Describe 'Get-MgxContent' {
    # Content needs Files.Read.All / Sites.Read.All, which the directory tenant does not carry.
    # Point MGX_LIVE_CONTENT_URI at a drive item on a tenant that does.
    BeforeAll { $script:contentUri = $env:MGX_LIVE_CONTENT_URI }

    It 'downloads a byte range' -Skip:(-not $env:MGX_LIVE_CONTENT_URI) {
        $bytes = Get-MgxContent $script:contentUri -First 256
        $bytes.Length | Should -Be 256
    }
    It 'downloads a whole file to -OutFile' -Skip:(-not $env:MGX_LIVE_CONTENT_URI) {
        $f = Join-Path ([IO.Path]::GetTempPath()) "mgxlive-$([Guid]::NewGuid().ToString('N')).bin"
        try {
            Get-MgxContent $script:contentUri -OutFile $f | Out-Null
            (Get-Item $f).Length | Should -BeGreaterThan 0
        } finally { Remove-Item $f -ErrorAction SilentlyContinue }
    }
}
