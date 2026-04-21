@{
    RootModule        = 'mgx.psm1'
    ModuleVersion     = '1.0.2'
    GUID              = 'a3f7e8d2-5b4c-4a1e-9f6d-2c8b0e3a7d5f'
    Author            = 'Thomas Maillo Grome'
    CompanyName       = 'Mgx'
    Copyright         = '(c) 2026 Thomas Maillo Grome. All rights reserved.'
    Description       = 'Resilient companion for Microsoft.Graph PowerShell. Adds retry, circuit breaker, rate limiting, streaming pagination, batching, and fan-out to any Graph API endpoint.'

    PowerShellVersion = '7.5'
    CompatiblePSEditions = @('Core')

    FormatsToProcess  = @('mgx.Format.ps1xml')

    # Pre-load Mgx.Engine.dll so it resolves into the same load context
    # as Mgx.Cmdlets.dll. Without this, MgxTelemetrySummary (a record type
    # returned by MgxTelemetryCollector.GetSummary()) fails to load at JIT
    # time with TypeLoadException when Get-MgxTelemetry is called.
    RequiredAssemblies = @('Mgx.Engine.dll')

    RequiredModules   = @(
        @{ ModuleName = 'Microsoft.Graph.Authentication'; ModuleVersion = '2.10.0' }
    )

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
v1.0.2
- Fixed Linux install: renamed module files to lowercase so Install-Module works on case-sensitive filesystems
- Updated about_Mgx_Tuning version reference

v1.0.1
- Added tab completion for Uri parameter on all cmdlets
- Extracted CircuitBreakerMessage protected property on MgxCmdletBase
- Removed redundant XML doc comments on self-documenting members

v1.0.0 - Initial public release

v0.3.0 - Plug-and-play resilience
- NEW: Expand-MgxRelation: pipeline-composable relation enrichment
  - Enrich Graph objects with related data via concurrent fan-out
  - Auto-detects collection vs singleton endpoints (no silent data loss)
  - -Top caps per-relation items (server-side $top + client-side truncation)
  - -Flatten unwraps single-value relations, warns on multi-item
  - -SkipNotFound/-SkipForbidden for partial failure tolerance
  - Chaining: pipe through multiple Expand-MgxRelation stages
- NEW: Enable-MgxResilience / Disable-MgxResilience / Get-MgxResilience: zero-change resilience injection
  - Wraps SDK HttpClient with Polly retry, circuit breaker, and rate limiting
  - All Microsoft.Graph SDK cmdlets (Get-MgUser, etc.) gain resilience automatically
  - Preserves full SDK handler chain (OData, NationalCloud, Redirect, Auth)
  - Re-call after Connect-MgGraph to re-inject
  - Thread-safe for concurrent runspaces
  - Sovereign cloud support (auto-detects Graph endpoint)
- NEW: Get-MgxOption / Set-MgxOption: runtime resilience pipeline tuning
  - 11 configurable parameters: rate limiting, retry, circuit breaker, timeouts
  - CircuitBreakerSamplingDurationSeconds: control the failure measurement window (5-300s)
  - Partial updates: only explicitly passed values are changed
  - Set-MgxOption -Reset restores all defaults
- FIXED: Circuit breaker and rate limiter now shared across cmdlet invocations
  - Previously created fresh per invocation, making both non-functional
  - New ResiliencePipelineFactory manages shared static Polly pipeline
- FIXED: Rate limiter GC root leak on options change
  - TokenBucketRateLimiter holds an internal Timer (GC root via AutoReplenishment)
  - Old limiters now disposed after TotalTimeoutSeconds delay to avoid ObjectDisposedException
- FIXED: nextLink SSRF validation strengthened
  - Rejects non-HTTPS scheme (prevents token leak over plaintext)
  - Compares full authority (host + port), not just host
  - ConcurrentFanOut rejects all nextLinks if initial URL parse fails
- FIXED: FindType() cache no longer caches null results
  - Previously, importing Mgx before Microsoft.Graph permanently broke lookups
- IMPROVED: Request cloning copies Options, Version, and all content headers on retry

v0.2.0 - Restructure as companion module
- NEW: Invoke-MgxRequest: general-purpose resilient client for any Graph endpoint
  - Streaming pagination with -All, -Top, -PageSize
  - Fan-out concurrency with {id} template substitution
  - Write operations (POST, PATCH, PUT, DELETE)
  - -ApiVersion (v1.0/beta), -ConsistencyLevel, -NoPageSize
  - -SkipNotFound/-SkipForbidden for fan-out error handling
  - -Raw for JSON string output, -CheckpointPath for resume
  - Progress reporting, pipeline stop support
  - ArgumentCompleters for tab completion
- IMPROVED: @odata.type preserved as ODataType property (polymorphic queries)
- IMPROVED: DateTime strings parsed to DateTimeOffset
- NEW: Export-MgxCollection: JSONL streaming export with checkpoint/resume
  - Raw JSON to disk, no PSObject overhead, constant memory
  - -All, -Top, -CheckpointPath for resumable exports
  - SupportsShouldProcess for -WhatIf/-Confirm
- IMPROVED: Set-MgxOption only overrides explicitly passed values
- FIXED: Set-MgxOption referenced deleted base class
- REMOVED: Get-MgxUser, Get-MgxGroup, Get-MgxGroupMember, Get-MgxApplication, Get-MgxServicePrincipal, Get-MgxDirectoryRole
  - Use Invoke-MgxRequest '/users', '/groups', etc. instead
'@
        }
    }
}
