@{
    RootModule        = 'mgx.psm1'
    ModuleVersion     = '2.1.5'
    GUID              = 'a3f7e8d2-5b4c-4a1e-9f6d-2c8b0e3a7d5f'
    Author            = 'Thomas Maillo Grome'
    CompanyName       = 'Mgx'
    Copyright         = '(c) 2026 Thomas Maillo Grome. All rights reserved.'
    Description       = 'Resilient companion for Microsoft.Graph PowerShell. Adds retry, circuit breaker, rate limiting, streaming pagination, batching, and fan-out to any Graph API endpoint.'

    PowerShellVersion = '7.4'
    CompatiblePSEditions = @('Core')

    FormatsToProcess  = @('mgx.Format.ps1xml')

    # Pre-load Mgx.Engine.dll so it resolves into the same load context
    # as Mgx.Cmdlets.dll. Without this, MgxTelemetrySummary (a record type
    # returned by MgxTelemetryCollector.GetSummary()) fails to load at JIT
    # time with TypeLoadException when Get-MgxTelemetry is called.
    RequiredAssemblies = @('Mgx.Engine.dll')

    # Microsoft.Graph.Authentication is deliberately NOT in RequiredModules.
    #
    # Auth is discovered reflectively at call time (GraphSession.Instance, falling back to
    # Get-MgContext), so nothing here links against the SDK and the module imports without it.
    # Declaring it would force the dependency on every consumer - including hosts that supply
    # their own Graph auth and only want the resilience layer - and would install a second copy
    # alongside whatever they already load. Cmdlets that need a token raise
    # GraphAuthModuleNotLoaded with install instructions when it is genuinely absent.

    CmdletsToExport   = @(
        'Invoke-MgxRequest'
        'Invoke-MgxBatchRequest'
        'Export-MgxCollection'
        'Expand-MgxRelation'
        'Set-MgxOption'
        'Get-MgxOption'
        'Enable-MgxResilience'
        'Disable-MgxResilience'
        'Get-MgxResilience'
        'Get-MgxTelemetry'
        'Sync-MgxDelta'
        'Get-MgxContent'
    )

    FunctionsToExport = @()
    VariablesToExport = @()
    AliasesToExport   = @()

    PrivateData = @{
        PSData = @{
            Tags         = @('Microsoft', 'Graph', 'MicrosoftGraph', 'API', 'Azure', 'EntraID', 'Resilience', 'PowerShell', 'Polly', 'Retry', 'RateLimit', 'Batch', 'Delta', 'Throttling', 'Pagination')
            LicenseUri   = 'https://github.com/gromedev/mgx/blob/main/LICENSE'
            ProjectUri   = 'https://github.com/gromedev/mgx'
            ReleaseNotes = @'
v2.1.5
Fixed
- Two runs resuming or recovering one interrupted export or sync no longer write into the same file: the second stops and names the file the first holds.
- Recovering an interrupted run never follows a link, waits on a pipe, or starts over on a copy that failed; every stop says what going on would have done and leaves both files as found.
- A checkpoint from before 2.1 keeps the temp file it stands for when another run refuses it, and Sync-MgxDelta recovers that shape as Export-MgxCollection does.
- Invoke-MgxBatchRequest refuses an item it cannot read instead of ending the run, cuts every pre-authenticated URL from the dead-letter file, checks the file before sending, and keeps item errors and telemetry under a Stop preference.
- Set-MgxOption previews and prompts only for a change it makes; Invoke-MgxRequest asks its checkpoint question once per run; -WhatIf previews on every cmdlet report what the run would do.
- Removing the module returns every option, the endpoint and the telemetry counters to their defaults.
Added
- A committed benchmark baseline with a comparison run, a programmable fault plan for the mock Graph server, and pathological and interference gauntlets.
- Fault injection at every point of an operation, pagination checks at page-size boundaries, and a help-freshness test over the compiled help.
See CHANGELOG.md for the full list.
'@
        }
    }
}
