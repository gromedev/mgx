# Benchmark: Microsoft.Graph SDK vs Mgx resilience vs Invoke-MgxRequest.
#
# Compares wall-clock time and memory for the same operation across three
# approaches. Run this on your own tenant to see real numbers.
#
# Requirements: Connect-MgGraph -Scopes "User.Read.All"

Import-Module Microsoft.Graph.Users
Import-Module Mgx

$property = "id,displayName,mail,department,jobTitle,accountEnabled"
$selectArr = @("id","displayName","mail","department","jobTitle","accountEnabled")

Write-Host "Benchmarking against your tenant..."
Write-Host ""

# --- 1. Bare SDK: Get-MgUser -All ---
Write-Host "1) Get-MgUser -All (bare SDK, no resilience)"
[GC]::Collect()
$memBefore = [GC]::GetTotalMemory($true)
$sw = [System.Diagnostics.Stopwatch]::StartNew()

$sdkUsers = Get-MgUser -All -Property $selectArr
$sw.Stop()

$memAfter = [GC]::GetTotalMemory($false)
$sdkCount = $sdkUsers.Count
$sdkMs = $sw.ElapsedMilliseconds
$sdkMem = [math]::Round(($memAfter - $memBefore) / 1MB, 1)
Write-Host "   $sdkCount users in ${sdkMs}ms (~${sdkMem} MB)"
$sdkUsers = $null

# --- 2. SDK + Enable-MgxResilience ---
Write-Host ""
Write-Host "2) Get-MgUser -All (with Enable-MgxResilience)"
Enable-MgxResilience
[GC]::Collect()
$memBefore = [GC]::GetTotalMemory($true)
$sw = [System.Diagnostics.Stopwatch]::StartNew()

$resilientUsers = Get-MgUser -All -Property $selectArr
$sw.Stop()

$memAfter = [GC]::GetTotalMemory($false)
$resilientCount = $resilientUsers.Count
$resilientMs = $sw.ElapsedMilliseconds
$resilientMem = [math]::Round(($memAfter - $memBefore) / 1MB, 1)
Write-Host "   $resilientCount users in ${resilientMs}ms (~${resilientMem} MB)"
Write-Host "   (retry + circuit breaker + rate limiting active)"
Disable-MgxResilience
$resilientUsers = $null

# --- 3. Invoke-MgxRequest -All (streaming pagination) ---
Write-Host ""
Write-Host "3) Invoke-MgxRequest -All (streaming pagination)"
[GC]::Collect()
$memBefore = [GC]::GetTotalMemory($true)
$sw = [System.Diagnostics.Stopwatch]::StartNew()

$mgxUsers = Invoke-MgxRequest /users -All -Property $selectArr
$sw.Stop()

$memAfter = [GC]::GetTotalMemory($false)
$mgxCount = $mgxUsers.Count
$mgxMs = $sw.ElapsedMilliseconds
$mgxMem = [math]::Round(($memAfter - $memBefore) / 1MB, 1)
Write-Host "   $mgxCount users in ${mgxMs}ms (~${mgxMem} MB)"
$mgxUsers = $null

# --- 4. Export-MgxCollection (constant memory, disk streaming) ---
Write-Host ""
Write-Host "4) Export-MgxCollection (JSONL to disk, constant memory)"
$outFile = Join-Path ([System.IO.Path]::GetTempPath()) "mgx-bench.jsonl"
[GC]::Collect()
$memBefore = [GC]::GetTotalMemory($true)
$sw = [System.Diagnostics.Stopwatch]::StartNew()

$exportResult = Export-MgxCollection /users -OutputFile $outFile -All -Property $selectArr
$sw.Stop()

$memAfter = [GC]::GetTotalMemory($false)
$exportMs = $sw.ElapsedMilliseconds
$exportMem = [math]::Round(($memAfter - $memBefore) / 1MB, 1)
Write-Host "   $($exportResult.ItemCount) users in ${exportMs}ms (~${exportMem} MB)"
Remove-Item $outFile -ErrorAction SilentlyContinue

# --- Summary ---
Write-Host ""
Write-Host "=== Summary ==="
Write-Host ""

$results = @(
    [PSCustomObject]@{ Approach = "Get-MgUser (bare SDK)";       Users = $sdkCount;       TimeMs = $sdkMs;       MemMB = $sdkMem;       Resilience = "None" }
    [PSCustomObject]@{ Approach = "Get-MgUser + MgxResilience";  Users = $resilientCount;  TimeMs = $resilientMs; MemMB = $resilientMem; Resilience = "Retry + CB + RL" }
    [PSCustomObject]@{ Approach = "Invoke-MgxRequest -All";      Users = $mgxCount;        TimeMs = $mgxMs;       MemMB = $mgxMem;       Resilience = "Full stack" }
    [PSCustomObject]@{ Approach = "Export-MgxCollection (JSONL)"; Users = $exportResult.ItemCount; TimeMs = $exportMs; MemMB = $exportMem; Resilience = "Full stack" }
)
$results | Format-Table -AutoSize

# Show telemetry for the Mgx operations
Write-Host "Telemetry (Mgx operations only):"
Get-MgxTelemetry
