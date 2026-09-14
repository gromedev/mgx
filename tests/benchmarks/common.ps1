# Shared plumbing for the Mgx benchmark suite. Dot-source from each benchmark script:
#   . "$PSScriptRoot/common.ps1"
# Auth resolution order: existing Graph session, then AZURE_* certificate variables, then
# AZURE_* secret variables, then $env:MGX_BENCH_APP / ~/.mgx-bench/app.json. With both a
# certificate path and a secret set, the certificate wins - and a certificate path that
# does not exist throws rather than falling through, so a stale path masks the secret.

$ErrorActionPreference = 'Stop'

# Benchmark output must be locale-stable (39.2s, never 39,2s) - results land in the README.
[System.Threading.Thread]::CurrentThread.CurrentCulture = [System.Globalization.CultureInfo]::InvariantCulture

function Connect-MgxBenchmark {
    if (-not (Get-Module Microsoft.Graph.Authentication)) {
        Import-Module Microsoft.Graph.Authentication -ErrorAction Stop
    }

    $ctx = Get-MgContext
    if ($ctx) {
        Write-Host "Using existing Graph session ($($ctx.AuthType): $($ctx.Account ?? $ctx.ClientId))"
        return
    }

    # Certificate first. Each benchmark runs in a fresh pwsh process, so there is never a
    # session to inherit, and the client-secret file below is the only other path - which is
    # why this suite could not run at all once that file went away. The same three AZURE_*
    # variables everything else in this repo uses, and no secret at rest.
    if ($env:AZURE_TENANT_ID -and $env:AZURE_CLIENT_ID -and $env:AZURE_CLIENT_CERTIFICATE_PATH) {
        $pfx = $env:AZURE_CLIENT_CERTIFICATE_PATH
        if (-not (Test-Path $pfx)) { throw "AZURE_CLIENT_CERTIFICATE_PATH points at '$pfx', which does not exist." }
        $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $pfx, $env:AZURE_CLIENT_CERTIFICATE_PASSWORD)
        Connect-MgGraph -TenantId $env:AZURE_TENANT_ID -ClientId $env:AZURE_CLIENT_ID `
            -Certificate $cert -NoWelcome
        Write-Host "Connected app-only by certificate ($($env:AZURE_CLIENT_ID))"
        return
    }

    # A secret in the environment, for an app that has no certificate uploaded. Same three
    # variables minus the certificate, and like the certificate path it leaves nothing at rest.
    if ($env:AZURE_TENANT_ID -and $env:AZURE_CLIENT_ID -and $env:AZURE_CLIENT_SECRET) {
        $cred = [pscredential]::new($env:AZURE_CLIENT_ID,
            (ConvertTo-SecureString $env:AZURE_CLIENT_SECRET -AsPlainText -Force))
        Connect-MgGraph -TenantId $env:AZURE_TENANT_ID -ClientSecretCredential $cred -NoWelcome
        Write-Host "Connected app-only by secret ($($env:AZURE_CLIENT_ID))"
        return
    }

    $credPath = if ($env:MGX_BENCH_APP) { $env:MGX_BENCH_APP }
                else { Join-Path $HOME '.mgx-bench/app.json' }
    if (-not (Test-Path $credPath)) {
        throw "No Graph session, no AZURE_* certificate or secret variables, and no app credentials at '$credPath'."
    }
    $cfg  = Get-Content $credPath -Raw | ConvertFrom-Json
    $cred = [pscredential]::new($cfg.appId, (ConvertTo-SecureString $cfg.clientSecret -AsPlainText -Force))
    Connect-MgGraph -TenantId $cfg.tenantId -ClientSecretCredential $cred -NoWelcome
    Write-Host "Connected app-only ($($cfg.appId))"
}

# Mints a raw app-only bearer token for the Invoke-RestMethod baselines,
# which deliberately bypass every SDK/Mgx layer.
function Get-BenchAppToken {
    # The raw-REST contender needs a bearer token of its own. Under certificate auth there is
    # no secret to POST, so mint one with a signed client assertion (private_key_jwt) using the
    # same certificate the SDK session uses.
    if ($env:AZURE_TENANT_ID -and $env:AZURE_CLIENT_ID -and $env:AZURE_CLIENT_CERTIFICATE_PATH) {
        $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $env:AZURE_CLIENT_CERTIFICATE_PATH, $env:AZURE_CLIENT_CERTIFICATE_PASSWORD)
        $aud  = "https://login.microsoftonline.com/$($env:AZURE_TENANT_ID)/oauth2/v2.0/token"
        $now  = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()

        # x5t is the SHA-1 thumbprint, base64url - Entra rejects the assertion without it.
        $b64u = { param($bytes) [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_') }
        $hdr  = @{ alg = 'RS256'; typ = 'JWT'; x5t = (& $b64u $cert.GetCertHash()) } | ConvertTo-Json -Compress
        $pay  = @{ aud = $aud; iss = $env:AZURE_CLIENT_ID; sub = $env:AZURE_CLIENT_ID
                   jti = [guid]::NewGuid().ToString(); nbf = $now; exp = $now + 600 } | ConvertTo-Json -Compress

        $unsigned = "$(& $b64u ([Text.Encoding]::UTF8.GetBytes($hdr))).$(& $b64u ([Text.Encoding]::UTF8.GetBytes($pay)))"
        # RSACertificateExtensions.GetRSAPrivateKey is an EXTENSION method; PowerShell cannot
        # invoke it as $cert.GetRSAPrivateKey(), so call the static form explicitly.
        $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
        if (-not $rsa) { throw "Certificate '$($env:AZURE_CLIENT_CERTIFICATE_PATH)' has no usable RSA private key." }
        $sig = $rsa.SignData(
            [Text.Encoding]::UTF8.GetBytes($unsigned),
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $assertion = "$unsigned.$(& $b64u $sig)"

        foreach ($attempt in 1..3) {
            try {
                $resp = Invoke-RestMethod -Method POST -Uri $aud -TimeoutSec 30 -Body @{
                    grant_type            = 'client_credentials'
                    client_id             = $env:AZURE_CLIENT_ID
                    client_assertion_type = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer'
                    client_assertion      = $assertion
                    scope                 = 'https://graph.microsoft.com/.default'
                }
                return $resp.access_token
            }
            catch { if ($attempt -eq 3) { throw }; Start-Sleep -Seconds 5 }
        }
    }

    if ($env:AZURE_TENANT_ID -and $env:AZURE_CLIENT_ID -and $env:AZURE_CLIENT_SECRET) {
        foreach ($attempt in 1..3) {
            try {
                $resp = Invoke-RestMethod -Method POST -TimeoutSec 30 `
                    -Uri "https://login.microsoftonline.com/$($env:AZURE_TENANT_ID)/oauth2/v2.0/token" `
                    -Body @{ grant_type = 'client_credentials'; client_id = $env:AZURE_CLIENT_ID
                             client_secret = $env:AZURE_CLIENT_SECRET
                             scope = 'https://graph.microsoft.com/.default' }
                return $resp.access_token
            }
            catch { if ($attempt -eq 3) { throw }; Start-Sleep -Seconds 5 }
        }
    }

    $credPath = if ($env:MGX_BENCH_APP) { $env:MGX_BENCH_APP }
                else { Join-Path $HOME '.mgx-bench/app.json' }
    if (-not (Test-Path $credPath)) { throw "No app credentials at '$credPath' (set MGX_BENCH_APP)." }
    $cfg = Get-Content $credPath -Raw | ConvertFrom-Json
    # Bounded + retried: an unbounded token mint hung a 5,000-call benchmark at item 500
    foreach ($attempt in 1..3) {
        try {
            $resp = Invoke-RestMethod -Method POST -Uri "https://login.microsoftonline.com/$($cfg.tenantId)/oauth2/v2.0/token" `
                -Body @{ grant_type = 'client_credentials'; client_id = $cfg.appId; client_secret = $cfg.clientSecret; scope = 'https://graph.microsoft.com/.default' } `
                -TimeoutSec 30
            return $resp.access_token
        }
        catch { if ($attempt -eq 3) { throw }; Start-Sleep -Seconds 5 }
    }
}

function Get-BenchUserIds {
    <#
    .SYNOPSIS
        Ids to run a benchmark against, preferring the seeded 'bench.u' cohort.

    .DESCRIPTION
        Five benchmarks (02, 03, 04, 07, 08) filter on startsWith(userPrincipalName,'bench.u'),
        and nothing in this repo creates those users - so on any tenant that was not hand-seeded
        the suite died with "seed the tenant first" and no instructions. That is most of the
        reason the benchmark results in results/ went stale: the suite simply would not run.

        Seeded users are still preferred, because they are disposable and a write benchmark can
        safely PATCH them. When there are not enough, fall back to ordinary users and say so
        loudly - a number measured against a different cohort is still a number, it just must not
        be compared against a seeded run. Callers that WRITE must pass -RequireSeeded.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [int] $Count,
        [string] $Prefix = 'bench.u',
        [switch] $RequireSeeded
    )

    $ids = [System.Collections.Generic.List[string]]::new()
    Invoke-MgxRequest /users -All -Filter "startsWith(userPrincipalName,'$Prefix')" -Property id -WarningAction SilentlyContinue |
        Select-Object -First $Count | ForEach-Object { $ids.Add($_.id) }

    if ($ids.Count -ge $Count) {
        Write-Host "  using $($ids.Count) seeded '$Prefix' users"
        return , $ids
    }

    if ($RequireSeeded) {
        throw ("This benchmark WRITES to the users it touches, so it will only run against the " +
               "disposable '$Prefix' cohort. Found $($ids.Count) of $Count. Seed the tenant, or " +
               "run a read-only benchmark instead.")
    }

    Write-Warning ("Only $($ids.Count) '$Prefix' users; falling back to ordinary tenant users. " +
                   "Read-only, but do NOT compare this run against a seeded one.")
    $ids.Clear()
    Invoke-MgxRequest /users -All -Property id -WarningAction SilentlyContinue |
        Select-Object -First $Count | ForEach-Object { $ids.Add($_.id) }

    if ($ids.Count -eq 0) { throw "No users at all in this tenant - nothing to benchmark." }
    if ($ids.Count -lt $Count) {
        Write-Warning "Tenant has only $($ids.Count) users; running at that size instead of $Count."
    }
    return , $ids
}

function Import-MgxLocal {
    # Users FIRST: it auto-loads its exactly-matching Microsoft.Graph.Authentication.
    # Importing Mgx (or Authentication) first pulls the newest Auth assembly, and a
    # different-versioned Users then fails with "assembly with same name already loaded".
    if (-not (Get-Module Microsoft.Graph.Users)) { Import-Module Microsoft.Graph.Users }
    # Local build first (repo checkout), gallery module as fallback.
    $local = Join-Path $PSScriptRoot '../../module/mgx.psd1'
    if (Test-Path $local) { Import-Module $local -Force }
    else { Import-Module Mgx -Force }
}

# The gauntlets point the whole Graph stack at a local mock through a named Graph
# environment. Add-MgEnvironment writes that name into the user profile, so an environment
# one run adds outlives it: the next run finds MgxBench already there, keeps whatever
# GraphEndpoint the first run wrote, and aims its SDK contenders at a port this run's mock
# does not hold - which reports no completions, or, when an orphaned listener still holds
# that port, counts answered by a server this run never started. Point it at this run's
# endpoint every time, and take it out again when the run ends.
function Set-BenchGraphEnvironment {
    param(
        [Parameter(Mandatory)] [string] $GraphEndpoint,
        [string] $Name = 'MgxBench'
    )
    Remove-BenchGraphEnvironment -Name $Name
    Add-MgEnvironment -Name $Name -GraphEndpoint $GraphEndpoint `
        -AzureADEndpoint 'https://login.microsoftonline.com' | Out-Null
    $Name
}

function Remove-BenchGraphEnvironment {
    param([string] $Name = 'MgxBench')
    # Remove-MgEnvironment raises "Environment <name> not found" as a terminating error that
    # -ErrorAction does not downgrade, so the name is looked up first: a finally block has to
    # run on a machine where the environment was never added, and on the second call of a run
    # that already removed it.
    if (Get-MgEnvironment -Name $Name -ErrorAction SilentlyContinue) {
        Remove-MgEnvironment -Name $Name | Out-Null
    }
}

# Starts mock-graph-server.ps1 as a child process on the first free port at or above $Port,
# waits for it to answer /ping, and returns the process, the port and the base URL. The port
# is searched rather than assumed: a crashed run can leave a listener behind, and a second
# gauntlet may be running on the same machine. The caller stops it in its finally.
function Start-BenchMockServer {
    param(
        [int] $Port = 8787,
        [int] $Range = 20,
        [int] $ReadySeconds = 10
    )
    $free = ($Port..($Port + $Range)) | Where-Object {
        -not (Test-Connection -TargetName localhost -TcpPort $_ -TimeoutSeconds 1 -Quiet)
    } | Select-Object -First 1
    if (-not $free) { throw 'no free port found in range' }
    $base = "http://localhost:$free"
    $process = Start-Process pwsh -PassThru `
        -ArgumentList '-NoProfile', '-File', (Join-Path $PSScriptRoot 'mock-graph-server.ps1'), '-Port', $free
    $up = $false
    foreach ($i in 1..($ReadySeconds * 5)) {
        try { $null = Invoke-RestMethod "$base/ping" -TimeoutSec 1; $up = $true; break }
        catch { Start-Sleep -Milliseconds 200 }
    }
    if (-not $up) {
        if (-not $process.HasExited) { $process.Kill() }
        throw "mock server did not come up on port $free"
    }
    Write-Host "mock server up (pid $($process.Id))"
    [pscustomobject]@{ Process = $process; Port = $free; BaseUrl = $base }
}

# By pid, and only ever by pid: another gauntlet's server is the same executable under the
# same name, and a pattern kill takes it with this one.
function Stop-BenchMockServer {
    param([object] $Server)
    if ($Server -and $Server.Process -and -not $Server.Process.HasExited) { $Server.Process.Kill() }
}

# Points the whole Graph stack at a mock endpoint: the SDK's own cmdlets, and through the
# session the clients Mgx builds. The token is a string the mock does not read. The tenant id
# it is given is read - by Mgx: -AccessToken auth leaves AuthContext.TenantId empty, and the
# auth fingerprint treats an empty TenantId as "not connected" (1.0.4 identity fix).
function Connect-BenchMockGraph {
    param(
        [Parameter(Mandatory)] [string] $GraphEndpoint,
        [string] $Token = 'mock-token-not-validated',
        [string] $TenantId = 'gauntlet-mock-tenant'
    )
    $null = Set-BenchGraphEnvironment -GraphEndpoint $GraphEndpoint
    Disconnect-MgGraph -ErrorAction SilentlyContinue | Out-Null
    Connect-MgGraph -Environment MgxBench `
        -AccessToken (ConvertTo-SecureString $Token -AsPlainText -Force) -NoWelcome
    [Microsoft.Graph.PowerShell.Authentication.GraphSession]::Instance.AuthContext.TenantId = $TenantId
}

# Runs one measured pass: telemetry snapshot around $Script, wall time, peak working set
# sampled from a background thread. Returns a result object; does not write anywhere.
function Measure-BenchPass {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [scriptblock] $Script
    )

    [GC]::Collect(); [GC]::WaitForPendingFinalizers(); [GC]::Collect()
    $proc = [System.Diagnostics.Process]::GetCurrentProcess()
    $proc.Refresh()
    $wsBefore = $proc.WorkingSet64
    $heapBefore = [GC]::GetTotalMemory($true)

    # Peak-RSS sampler in compiled C#: a PowerShell scriptblock cannot run on a bare
    # .NET thread (no runspace), so the sampling loop must not be PowerShell at all.
    if (-not ('MgxBench.RssSampler' -as [type])) {
        Add-Type -TypeDefinition @'
namespace MgxBench {
    public class RssSampler {
        private System.Threading.Thread _t;
        private volatile bool _stop;
        public long Peak;
        public void Start() {
            var p = System.Diagnostics.Process.GetCurrentProcess();
            Peak = p.WorkingSet64;
            _t = new System.Threading.Thread(() => {
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                while (!_stop) {
                    proc.Refresh();
                    if (proc.WorkingSet64 > Peak) Peak = proc.WorkingSet64;
                    System.Threading.Thread.Sleep(200);
                }
            });
            _t.IsBackground = true;
            _t.Start();
        }
        public void Stop() { _stop = true; if (_t != null) _t.Join(2000); }
    }
}
'@
    }
    $sampler = [MgxBench.RssSampler]::new()
    $sampler.Start()

    $telemetryBefore = $null
    try { $telemetryBefore = Get-MgxTelemetry } catch { }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $scriptOutput = & $Script
    $sw.Stop()

    $sampler.Stop()

    $telemetryAfter = $null
    try { $telemetryAfter = Get-MgxTelemetry } catch { }

    $proc.Refresh()
    $heapAfter = [GC]::GetTotalMemory($false)

    $tel = $null
    if ($telemetryBefore -and $telemetryAfter) {
        $tel = [pscustomobject]@{
            Requests          = $telemetryAfter.Requests         - $telemetryBefore.Requests
            ThrottleRetries   = $telemetryAfter.ThrottleRetries  - $telemetryBefore.ThrottleRetries
            OtherRetries      = $telemetryAfter.OtherRetries     - $telemetryBefore.OtherRetries
            HttpMs            = $telemetryAfter.HttpMs           - $telemetryBefore.HttpMs
            RateLimiterWaitMs = $telemetryAfter.RateLimiterWaitMs - $telemetryBefore.RateLimiterWaitMs
            RetryDelayMs      = $telemetryAfter.RetryDelayMs     - $telemetryBefore.RetryDelayMs
            BatchItemThrottles = $telemetryAfter.BatchItemThrottles - $telemetryBefore.BatchItemThrottles
        }
    }

    [pscustomobject]@{
        Name          = $Name
        ElapsedMs     = $sw.ElapsedMilliseconds
        PeakWorkingSetMB   = [math]::Round($sampler.Peak / 1MB, 1)
        WorkingSetDeltaMB  = [math]::Round(($sampler.Peak - $wsBefore) / 1MB, 1)
        ManagedHeapDeltaMB = [math]::Round(($heapAfter - $heapBefore) / 1MB, 1)
        MgxTelemetry  = $tel
        Output        = $scriptOutput
        Timestamp     = (Get-Date).ToString('o')
    }
}

# Runs Measure-BenchPass $Runs times and returns per-run results plus the median-by-time run.
function Measure-BenchMedian {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [scriptblock] $Script,
        [int] $Runs = 3
    )
    $passes = for ($i = 1; $i -le $Runs; $i++) {
        Write-Host ("  {0}: run {1}/{2}..." -f $Name, $i, $Runs)
        Measure-BenchPass -Name ("{0} (run {1})" -f $Name, $i) -Script $Script
    }
    $sorted = $passes | Sort-Object ElapsedMs
    [pscustomobject]@{
        Name   = $Name
        Median = $sorted[[math]::Floor(($sorted.Count - 1) / 2)]
        Runs   = $passes
    }
}

# Runs a benchmark contender in a child pwsh under a stall watchdog. The child writes
# its Measure-BenchPass result as JSON to $ResultFile and touches $HeartbeatFile as it
# progresses. If the heartbeat goes stale (dead-socket hang: observed twice with bare
# SDK cmdlets - no default timeout), the child is killed and the hang itself becomes
# the recorded outcome.
function Invoke-WatchdoggedContender {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $ScriptPath,
        [string[]] $ArgumentList = @(),
        [Parameter(Mandatory)] [string] $ResultFile,
        [Parameter(Mandatory)] [string] $HeartbeatFile,
        [int] $StallSeconds = 300
    )
    Remove-Item $ResultFile, $HeartbeatFile -ErrorAction SilentlyContinue
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process pwsh -PassThru -NoNewWindow `
        -ArgumentList (@('-NoProfile', '-File', $ScriptPath) + $ArgumentList)
    # Captured now, not read off $p later: Process.StartTime reads back null on Unix once the
    # child has exited, and a child that dies before its first Beat (an import or connect
    # failure inside the first poll) has usually done exactly that by the time the fallback
    # below is needed.
    $launchedAt = Get-Date
    while (-not $p.HasExited) {
        Start-Sleep -Seconds 10
        $lastBeat = if (Test-Path $HeartbeatFile) { (Get-Item $HeartbeatFile).LastWriteTime } else { $launchedAt }
        if (((Get-Date) - $lastBeat).TotalSeconds -gt $StallSeconds) {
            $p.Kill()
            Write-Host ("  WATCHDOG: '{0}' no progress for {1}s - killed after {2:F0}s total" -f $Name, $StallSeconds, $sw.Elapsed.TotalSeconds)
            return [pscustomobject]@{
                Name = $Name; Hung = $true; Crashed = $false; ElapsedMs = [long]$sw.ElapsedMilliseconds
                Output = [pscustomobject]@{ note = "HUNG - no progress for ${StallSeconds}s, killed by watchdog" }
            }
        }
    }
    if (Test-Path $ResultFile) {
        $r = Get-Content $ResultFile -Raw | ConvertFrom-Json
        $r | Add-Member -NotePropertyName Hung -NotePropertyValue $false -Force
        $r | Add-Member -NotePropertyName Crashed -NotePropertyValue $false -Force
        return $r
    }
    # Exited on its own, within the poll granularity above, before ever touching
    # $HeartbeatFile or $ResultFile - an import or connect failure, not a stall. Hung stays
    # false: nothing here was killed for want of progress, so the elapsed time is watchdog
    # poll noise (up to 10s) rather than a measurement, and Crashed is what callers should
    # branch on instead of reading Hung as a catch-all for "no result".
    [pscustomobject]@{
        Name = $Name; Hung = $false; Crashed = $true; ExitCode = $p.ExitCode
        ElapsedMs = [long]$sw.ElapsedMilliseconds
        Output = [pscustomobject]@{ note = "exited $($p.ExitCode) without a result" }
    }
}

function Get-BenchPacingLoadShape {
    <#
    .SYNOPSIS
        The load a pacing arm will issue, and whether it can reach the throttled regime.

    .DESCRIPTION
        Invoke-MgxRequest deduplicates the ids it is piped, by design, so a slice that repeats
        an id issues one request for it: the request count of an arm is the count of DISTINCT
        ids handed to each pipeline, never the volume asked for. A pipeline can be handed the
        shared pool at most once, so an arm can issue at most Pipelines x IdPool requests and
        anything above that is clamped - reported, because an arm that believes it issued more
        than it did describes a regime it never entered.

        Admissibility has two halves, and a run needs both:

          rate    the offered draw must exceed the rate the tenant keeps serving at while it
                  refuses, or the burst allowance never drains and nothing is ever refused.
                  Volume cannot buy this: it is set by how much is in flight.
          volume  the run must then spend long enough past the allowance for the throttled
                  phase to be at least as long as the unthrottled one, or the comparison is
                  mostly a comparison of two unthrottled runs.

        The rate here is a projection, from the 180 RU/s that 128 requests in flight measured
        against the seeded tenant; the arms report what they actually drew, and the verdict is
        taken from that. One process can hold roughly 2,000 requests in flight - the transport
        pools 20 connections and multiplexes HTTP/2 streams over them - so a pipeline count
        whose product with the concurrency passes that will not offer the rate projected here.

    .PARAMETER BurstRu
        Resource units the tenant serves before its first refusal.

    .PARAMETER SustainedRuPerSecond
        The rate it keeps serving at once it is refusing.

    .PARAMETER RuPerSecondPerPipeline
        Measured draw of one pipeline at concurrency 128. Scaled linearly with -Concurrency.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [ValidateRange(1, [int]::MaxValue)] [int] $Count,
        [Parameter(Mandatory)] [ValidateRange(1, 512)] [int] $Pipelines,
        [Parameter(Mandatory)] [ValidateRange(1, 128)] [int] $Concurrency,
        [Parameter(Mandatory)] [ValidateRange(1, [int]::MaxValue)] [int] $IdPool,
        [Parameter(Mandatory)] [ValidateRange(1, [int]::MaxValue)] [int] $BurstRu,
        [Parameter(Mandatory)] [ValidateRange(1, [int]::MaxValue)] [int] $SustainedRuPerSecond,
        [double] $RuPerSecondPerPipeline = 180
    )

    $idsPerPipeline = [math]::Min([int][math]::Ceiling($Count / $Pipelines), $IdPool)
    $total          = $idsPerPipeline * $Pipelines
    $inFlight       = $Pipelines * $Concurrency
    $expectedRate   = [math]::Round($Pipelines * $RuPerSecondPerPipeline * ($Concurrency / 128), 0)

    $burstSeconds     = [math]::Round($BurstRu / $expectedRate, 1)
    $throttledSeconds = [math]::Round([math]::Max(0, $total - $BurstRu) / $SustainedRuPerSecond, 1)

    $rateClears   = $expectedRate -gt $SustainedRuPerSecond
    $volumeClears = $total -gt $BurstRu -and $throttledSeconds -ge $burstSeconds

    $reasons = @()
    if (-not $rateClears) {
        $reasons += ("offered draw {0} RU/s does not exceed the {1} RU/s the tenant serves while refusing - raise -Pipelines (volume cannot buy a rate)" -f `
            $expectedRate, $SustainedRuPerSecond)
    }
    if (-not $volumeClears) {
        $reasons += ("{0} requests leaves {1}s in the throttled regime against {2}s inside the {3} RU burst allowance - raise -Count, and -IdPool or -Pipelines with it" -f `
            $total, $throttledSeconds, $burstSeconds, $BurstRu)
    }

    [pscustomobject]@{
        Requested            = $Count
        Pipelines            = $Pipelines
        Concurrency          = $Concurrency
        IdPool               = $IdPool
        IdsPerPipeline       = $idsPerPipeline
        TotalRequests        = $total
        Clamped              = $total -lt $Count
        InFlight             = $inFlight
        ExpectedRuPerSecond  = $expectedRate
        BurstRu              = $BurstRu
        SustainedRuPerSecond = $SustainedRuPerSecond
        BurstSeconds         = $burstSeconds
        ThrottledSeconds     = $throttledSeconds
        EstimatedSeconds     = [math]::Round($burstSeconds + $throttledSeconds, 1)
        RateClears           = $rateClears
        VolumeClears         = $volumeClears
        Admissible           = $rateClears -and $volumeClears
        Reasons              = $reasons
    }
}

function Get-BenchPipelineSlice {
    <#
    .SYNOPSIS
        The ids each pipeline of a fan-out arm issues: distinct within a pipeline, staggered
        across them.

    .DESCRIPTION
        Every pipeline may issue the whole shared pool once, and each starts at its own rotated
        offset so the pipelines are not all pulling the same object at the same moment. The walk
        takes fewer steps than the pool is long, so it never wraps onto its own start and no
        slice can hold a repeat - which is the invariant the arm's request count rests on, and
        which this checks before handing the slices back rather than trusting the arithmetic.

    .OUTPUTS
        One list of ids per pipeline, in pipeline order.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string[]] $Pool,
        [Parameter(Mandatory)] [ValidateRange(1, 512)] [int] $Pipelines,
        [Parameter(Mandatory)] [ValidateRange(1, [int]::MaxValue)] [int] $IdsPerPipeline
    )

    if ($Pool.Count -eq 0) { throw 'no ids to slice across the pipelines.' }
    if ($IdsPerPipeline -gt $Pool.Count) {
        throw ("a pipeline cannot issue $IdsPerPipeline distinct ids from a pool of $($Pool.Count); " +
               'Invoke-MgxRequest deduplicates its pipeline ids, so the repeats would never be sent.')
    }

    $slices = [System.Collections.Generic.List[object]]::new()
    $stride = [math]::Max(1, [int][math]::Floor($Pool.Count / $Pipelines))
    for ($i = 0; $i -lt $Pipelines; $i++) {
        $slice = [System.Collections.Generic.List[string]]::new()
        $start = ($i * $stride) % $Pool.Count
        for ($j = 0; $j -lt $IdsPerPipeline; $j++) { $slice.Add($Pool[($start + $j) % $Pool.Count]) }

        $distinct = @($slice | Sort-Object -Unique).Count
        if ($distinct -ne $slice.Count) {
            throw ("pipeline $i was given $($slice.Count - $distinct) duplicate id(s) of $($slice.Count); " +
                   'the arm would issue fewer requests than it reports.')
        }
        $slices.Add($slice)
    }
    , $slices
}

# --- Which tenant, and which tree, produced a number -----------------------------------------
#
# Mgx, SDK and PowerShell versions plus a timestamp do not identify a measurement. Two runs
# against two tenants are the same entry with different numbers in it, so a cross-tenant claim
# cannot be audited from results/; and every working tree that has not bumped the version
# carries the same version string, so a slow number cannot be attributed to the code or to the
# tenant after the fact. Everything below answers null rather than throwing when its source is
# absent: the no-tenant benchmarks must still record.

function Get-BenchDocumentedBudget {
    <#
    .SYNOPSIS
        Graph's documented resource-unit budget for an application+tenant pair, by tenant size.

    .DESCRIPTION
        The budget is banded on the tenant's user count rather than fixed:

            S   under 50 users      3,500 ResourceUnits / 10s     350 RU/s
            M   50 to 500 users     5,000 ResourceUnits / 10s     500 RU/s
            L   above 500 users     8,000 ResourceUnits / 10s     800 RU/s

        Source: https://learn.microsoft.com/en-us/graph/throttling-limits - "Identity and access
        service limits", whose application+tenant pair row carries those three quotas and whose
        note reads "The tenant sizes are defined as follows: S - under 50 users, M - between 50
        and 500 users, and L - above 500 users."

        One function, because the denominator a result records its draw against and the tier a
        calibration run reports have to be the same number or the two artifacts disagree about
        what a tenant is allowed.

    .PARAMETER UserCount
        The tenant's user count. Null (unknown) yields null: an assumed tier is worse than none.
    #>
    [CmdletBinding()]
    param([object] $UserCount)

    if ($null -eq $UserCount) { return $null }
    $users = [long]$UserCount
    if ($users -lt 0) { return $null }

    $tier = if ($users -lt 50)   { @{ Tier = 'S'; Band = 'under 50 users';   RuPer10Seconds = 3500 } }
            elseif ($users -le 500) { @{ Tier = 'M'; Band = '50 to 500 users';  RuPer10Seconds = 5000 } }
            else                 { @{ Tier = 'L'; Band = 'above 500 users'; RuPer10Seconds = 8000 } }

    [pscustomobject]@{
        Tier           = $tier.Tier
        Band           = $tier.Band
        UserCount      = $users
        RuPer10Seconds = $tier.RuPer10Seconds
        RuPerSecond    = $tier.RuPer10Seconds / 10
    }
}

function Get-BenchDirectoryObjectCount {
    <#
    .SYNOPSIS
        The tenant's user count, in one request.

    .DESCRIPTION
        $count=true on a $top=1 page carries @odata.count for the whole collection, which is the
        cheapest exact count Graph offers - the /users/$count segment needs the same
        ConsistencyLevel header and answers in text/plain, which the SDK does not surface.

        Through the SDK's own cmdlet rather than Invoke-MgxRequest: this runs as a result is
        being written, sometimes moments after a benchmark that deliberately drove the tenant
        into refusing, and it must not sit in mgx's retry pipeline waiting out a Retry-After
        that belongs to no measurement. The uri is relative, so it resolves against whatever
        endpoint the session was connected to rather than assuming the worldwide cloud.
    #>
    [CmdletBinding()]
    param()

    $response = Invoke-MgGraphRequest -Method GET -OutputType HashTable -ErrorAction Stop `
        -Uri 'v1.0/users?$top=1&$count=true' `
        -Headers @{ ConsistencyLevel = 'eventual' }
    $count = $response['@odata.count']
    if ($null -eq $count) { return $null }
    [long]$count
}

function Get-BenchTenantFact {
    <#
    .SYNOPSIS
        Which tenant a benchmark ran against, how big it is, and what it is budgeted at.

    .DESCRIPTION
        Computed once per process and cached: every benchmark records at least one result and
        several record many, and none of them should pay for the same count twice.

        All three are null when there is no Graph session - the ordinary state of the no-tenant
        benchmarks, and of the parent process of a benchmark whose arms connect in children.
        A tenant that answers the context but not the count records the tenant and null for the
        rest, because a partial fact is still an auditable one.

        An Entra tenant id is a GUID, and a session whose id is not one is not a directory any
        of this describes: 06 connects a mock Graph under a made-up tenant name and must stay
        off the network, so a non-GUID id records as no tenant rather than as a count request
        nobody asked for.

    .PARAMETER Refresh
        Recompute rather than answer from the cache.
    #>
    [CmdletBinding()]
    param([switch] $Refresh)

    if ($script:BenchTenantFact -and -not $Refresh) { return $script:BenchTenantFact }

    $fact = [pscustomobject]@{
        TenantId             = $null
        DirectoryObjectCount = $null
        Budget               = $null
    }
    try {
        $ctx = Get-MgContext -ErrorAction Stop
        $tenantGuid = [guid]::Empty
        if ($ctx -and $ctx.TenantId -and [guid]::TryParse($ctx.TenantId, [ref] $tenantGuid)) {
            $fact.TenantId = $ctx.TenantId
            $fact.DirectoryObjectCount = Get-BenchDirectoryObjectCount
            $fact.Budget = Get-BenchDocumentedBudget -UserCount $fact.DirectoryObjectCount
        }
    }
    catch {
        # No Microsoft.Graph.Authentication, no session, or a tenant that would not answer the
        # count. A recorded null says "not known"; a thrown error would lose the whole result.
        Write-Verbose "tenant facts unavailable: $($_.Exception.Message)"
    }
    $script:BenchTenantFact = $fact
    $fact
}

function Get-BenchCommit {
    <#
    .SYNOPSIS
        The short commit of the tree the suite ran from, with a dirty marker.

    .DESCRIPTION
        The marker carries more than the hash does: a number measured over uncommitted edits is
        not reproducible from the hash alone, and saying so is the difference between an entry
        that can be re-run and one that only looks like it can. Null outside a git checkout.
    #>
    [CmdletBinding()]
    param()

    if ($script:BenchCommitRead) { return $script:BenchCommit }
    $script:BenchCommitRead = $true
    $script:BenchCommit = $null
    try {
        $hash = & git -C $PSScriptRoot rev-parse --short HEAD 2>$null
        if ($hash) {
            $commit = "$hash".Trim()
            if (@(& git -C $PSScriptRoot status --porcelain 2>$null).Count -gt 0) {
                $commit = "$commit-dirty"
            }
            $script:BenchCommit = $commit
        }
    }
    catch {
        # No git, or not a checkout. Recording nothing is right; guessing is not.
        $script:BenchCommit = $null
    }
    $script:BenchCommit
}

# The five fields of a recorded entry that say WHERE its numbers came from: which module and
# which SDK ran, against which tenant, how big that tenant is, and what the documentation
# budgets a tenant that size at. Everything else in an entry describes the run itself.
$script:BenchIdentityField = @(
    'MgxVersion'
    'SdkVersion'
    'TenantId'
    'DirectoryObjectCount'
    'DocumentedBudgetRuPerSecond'
)

# One identity field off one entry. Get-BenchResultValue resolves a path through PSObject
# properties, which is every entry read back out of a results file but not a hashtable a caller
# assembled by hand, so a dictionary is read as one. A blank string is read as no value: an arm
# that recorded an empty tenant did not record a tenant, and pairing that against an arm that
# did is a disagreement, not a name to print with nothing after it.
function Get-BenchIdentityField {
    param(
        [object] $Node,
        [Parameter(Mandatory)] [string] $Name
    )
    $value = if ($Node -is [System.Collections.IDictionary]) { $Node[$Name] }
             else { Get-BenchResultValue -Node $Node -Path $Name }
    if ($value -is [string] -and [string]::IsNullOrWhiteSpace($value)) { return $null }
    $value
}

# Reads that identity off the entries the arms of a comparison recorded, for a caller writing a
# combined entry from a process that did none of the measuring itself.
#
# Arms that disagree on a field have no shared answer to give, so the field is recorded as null
# and the disagreement is named: two arms measured against two tenants, or under two module
# versions, are a comparison of two different things, and the entry has to say so rather than
# pick whichever arm was read first. Values are compared as they print, so a count recorded as
# a long by one arm and an int by the other still agrees.
function Get-BenchEntryIdentity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [object[]] $Entries
    )
    $identity = [ordered]@{}
    foreach ($name in $script:BenchIdentityField) {
        $seen = [ordered]@{}
        foreach ($entry in $Entries) {
            $value = Get-BenchIdentityField -Node $entry -Name $name
            $key = if ($null -eq $value) { '(none)' } else { [string]$value }
            if (-not $seen.Contains($key)) { $seen[$key] = $value }
        }
        if ($seen.Count -le 1) {
            $identity[$name] = @($seen.Values)[0]
        }
        else {
            $identity[$name] = $null
            Write-Warning "the runs disagree on ${name} ($((@($seen.Keys)) -join ', ')) - recorded as null."
        }
    }
    [pscustomobject]$identity
}

# Appends a result object to results/<benchmark>.json (one JSON doc per file, array of entries).
function Write-BenchResult {
    param(
        [Parameter(Mandatory)] [string] $Benchmark,
        [Parameter(Mandatory)] [object] $Result,
        # The run's wall clock. Only needed for the RU rate below; picked up from $Result.WallMs
        # when the caller already records one.
        [long] $WallMs = 0,
        # Where the per-run log lives. Named for the same reason Export-BenchBaseline and
        # Compare-BenchBaseline name theirs: so a test can be pointed somewhere that is not the
        # suite's own measurements.
        [string] $ResultsDirectory = (Join-Path $PSScriptRoot 'results'),
        # Who measured, for a benchmark whose arms ran somewhere other than this process. The
        # five fields below are read from this process by default, which is right wherever this
        # process did the work; a comparison that starts its arms as child runs records from a
        # parent that imported no local module and connected to no tenant, and the default then
        # describes the parent - whichever copy of Mgx happened to autoload there, and no tenant
        # at all. Get-BenchEntryIdentity reads this off the arm entries. Supplied, it is taken
        # whole and a field it does not carry is recorded as null, because falling back to this
        # process for that one field is exactly the substitution the parameter exists to stop.
        # Everything else in the entry still describes the process that writes it.
        [object] $Identity
    )
    $dir = $ResultsDirectory
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $file = Join-Path $dir "$Benchmark.json"
    $entries = @()
    if (Test-Path $file) { $entries = @(Get-Content $file -Raw | ConvertFrom-Json) }
    # Resource units are the currency Graph actually throttles directory workloads in, so a
    # benchmark that records only wall time describes half the cost. Captured from session
    # telemetry, which accumulates x-ms-resource-unit across every response.
    $telemetry = $null
    $wall = if ($WallMs -gt 0) { $WallMs }
            elseif ($Result.WallMs -and [long]$Result.WallMs -gt 0) { [long]$Result.WallMs }
            else { 0 }
    # The counters are snapshotted before the tenant facts below, which cost one request the
    # first time a process records: what gets recorded has to describe the benchmark and
    # nothing else.
    $t = $null
    try { $t = Get-MgxTelemetry -ErrorAction Stop }
    catch {
        # A benchmark that does not load Mgx (the bare-SDK comparison arms) has no telemetry.
        # That is expected; record its absence rather than failing the run.
        $t = $null
    }

    $fact = Get-BenchTenantFact
    $documentedBudget = $fact.Budget?.RuPerSecond

    if ($t) {
        $telemetry = [pscustomobject]@{
            ResourceUnits    = $t.ResourceUnitsConsumed
            TotalRequests    = $t.Requests
            Succeeded        = $t.Succeeded
            Failed           = $t.Failed
            ThrottleRetries  = $t.ThrottleRetries
            OtherRetries     = $t.OtherRetries
            PacingWaitMs     = $t.AdaptivePacingWaitMs
            PacingActivations= $t.AdaptivePacingActivations
            RateLimiterWaitMs= $t.RateLimiterWaitMs
            RuPerRequest     = $(if ($t.Requests -gt 0) {
                                    [math]::Round($t.ResourceUnitsConsumed / $t.Requests, 2)
                                } else { 0 })
            # Per-workload state, parsed from the pacer's own description. RU itself is a single
            # tenant-wide counter, but the buckets tell you WHICH workload was being paced when
            # the units were spent - a directory fan-out and a drive pull draw on limits that are
            # documented and measured as independent, so a total alone hides which one is near
            # its ceiling.
            PacingState      = $t.PacingState
            PacingBuckets    = $(
                                    if ($t.PacingState) {
                                        @($t.PacingState -split ';' | Where-Object { $_ } |
                                          ForEach-Object {
                                              $name, $rest = $_ -split ':', 2
                                              [pscustomobject]@{
                                                  Workload = $name.Trim()
                                                  State    = if ($rest) { $rest.Trim() } else { '' }
                                              }
                                          })
                                    } else { @() }
                                )
            # -1 means Graph never sent x-ms-throttle-limit-percentage. Measured live it never
            # arrives, even during active 429s, so recording it per run is how we would notice
            # if that ever changed.
            LastThrottlePct  = $t.LastThrottlePercentage
            # A rate is only meaningful against the run's WALL clock. Telemetry's TotalElapsedMs
            # is the SUM of per-request durations, so a concurrent run exceeds its own wall time
            # by roughly the concurrency factor - dividing by it yields RU per request-second and
            # understates the budget draw by that factor (a concurrency-128 run measured at 182
            # RU/s reported 1.6). Callers that know their wall clock pass it; the rest record no
            # rate at all, because a missing number is auditable and a wrong one is not.
            WallMs           = $(if ($wall -gt 0) { $wall } else { $null })
            RuPerSecond      = $(if ($wall -gt 0) {
                                    [math]::Round($t.ResourceUnitsConsumed / ($wall / 1000), 1)
                                } else { $null })
            # Recorded as a ratio so a run that approaches the ceiling is obvious without
            # re-deriving the arithmetic each time. Against this tenant's documented budget
            # where the tier is known, and against the 800 RU/s of a large tenant where it is
            # not - BudgetBasis says which, because the same ratio over two denominators is two
            # different claims.
            BudgetFraction   = $(if ($wall -gt 0) {
                                    $denominator = if ($null -ne $documentedBudget) { $documentedBudget } else { 800 }
                                    [math]::Round(($t.ResourceUnitsConsumed / ($wall / 1000)) / $denominator, 3)
                                } else { $null })
            BudgetBasis      = $(if ($wall -le 0) { $null }
                                 elseif ($null -ne $documentedBudget) { 'documented' }
                                 else { 'assumed-800' })
        }
    }

    # The telemetry block above keeps this process's own denominator whatever -Identity says:
    # its numerator is this process's counters, and a draw measured here against a budget
    # documented elsewhere is neither run's number.
    $stamp = $Identity
    if (-not $PSBoundParameters.ContainsKey('Identity')) {
        $stamp = [pscustomobject]@{
            MgxVersion                  = (Get-Module Mgx -ErrorAction SilentlyContinue)?.Version?.ToString()
            SdkVersion                  = (Get-Module Microsoft.Graph.Authentication -ErrorAction SilentlyContinue)?.Version?.ToString()
            TenantId                    = $fact.TenantId
            DirectoryObjectCount        = $fact.DirectoryObjectCount
            DocumentedBudgetRuPerSecond = $documentedBudget
        }
    }

    $meta = [pscustomobject]@{
        Result     = $Result
        Telemetry  = $telemetry
        MgxVersion = Get-BenchIdentityField -Node $stamp -Name 'MgxVersion'
        SdkVersion = Get-BenchIdentityField -Node $stamp -Name 'SdkVersion'
        PSVersion  = $PSVersionTable.PSVersion.ToString()
        # Which tenant produced the number, how big it is, and what the documentation budgets a
        # tenant that size at. Null together when there was no session behind the numbers.
        TenantId                    = Get-BenchIdentityField -Node $stamp -Name 'TenantId'
        DirectoryObjectCount        = Get-BenchIdentityField -Node $stamp -Name 'DirectoryObjectCount'
        DocumentedBudgetRuPerSecond = Get-BenchIdentityField -Node $stamp -Name 'DocumentedBudgetRuPerSecond'
        # Which tree produced it. Versions are identical across working trees.
        Commit                      = Get-BenchCommit
        # First-class, not only inside the telemetry block: a scenario that loads no Mgx records
        # no telemetry at all, and its wall clock is still the figure it is read on.
        WallMs                      = $(if ($wall -gt 0) { $wall } else { $null })
        RecordedAt = (Get-Date).ToString('o')
    }
    # -InputObject (not pipeline) so a single-element array still serializes as a JSON array
    $all = @($entries) + @($meta)
    ConvertTo-Json -InputObject $all -Depth 12 | Set-Content -Path $file
    Write-Host "  result appended to $file"
}

# --- The baseline snapshot ------------------------------------------------------------------
#
# results/<benchmark>.json is the append-only per-run log: every run adds an entry and nothing
# is ever promoted out of it, so a number measured on a laptop in August is indistinguishable
# from the one a release was signed off against - and .gitignore keeps that whole directory
# local, so nobody else sees either. baseline.json beside it is the pinned snapshot: the newest
# entry of each named scenario, reduced to the figures that scenario exists to produce, and
# committed, so a later run has something to be read against.
#
# One table names those figures. The FIRST metric of a scenario is its headline - the figure
# that scenario is read on; the rest ride along so the committed file says what the run looked
# like without anyone re-deriving it from a log. Everything that writes or reads a baseline goes
# through this table, so a scenario cannot be pinned on one figure and read on another.
#
# Every headline is lower-is-better and strictly positive. That is deliberate: a percentage
# delta on a figure that can cross zero flips its own sign - 09's checkpoint overhead comes out
# slightly negative on a quiet tenant - so such a figure is recorded and never made a headline.
function Get-BenchScenario {
    <#
    .SYNOPSIS
        The benchmark scenarios that can be promoted into baseline.json, and the metrics of each.

    .DESCRIPTION
        Each scenario carries the script that produces it and an ordered list of metrics. A
        metric names its unit and one or more Sources: dotted paths into the recorded Result
        object, tried in order, the first that resolves winning. Scenario 10 needs the list -
        run through run.ps1 it records a two-arm summary, run standalone with -Mode it records
        one arm - and a Scale brings a path recorded in another unit onto the metric's own.

        Discriminator, where a scenario has one, is a path whose value is recorded beside the
        metrics. 07 and 10 each record a Mode, and a number off the paced arm is not the same
        number as one off the unpaced arm: reading one against the other is a wrong answer
        rather than a missing one.

    .PARAMETER Id
        Scenario ids to return, in the order given. Omit for all of them. An id with no
        definition is skipped with a warning rather than throwing: a promotion at the end of a
        two-hour suite must not lose the scenarios it does know because of one it does not.
    #>
    [CmdletBinding()]
    param([string[]] $Id)

    $table = [ordered]@{
        '01' = @{
            Script  = '01-list-users.ps1'
            Metrics = @(
                @{ Name = 'MgxListMs';           Unit = 'ms'; Sources = @(@{ Path = 'mgx.Median.ElapsedMs' }) }
                @{ Name = 'MgxTimeToFirstMs';    Unit = 'ms'; Sources = @(@{ Path = 'ttfrMgx.Median.ElapsedMs' }) }
                @{ Name = 'MgxPeakWorkingSetMB'; Unit = 'MB'; Sources = @(@{ Path = 'mgx.Median.PeakWorkingSetMB' }) }
            )
        }
        '02' = @{
            Script  = '02-fanout-lookup.ps1'
            Metrics = @(
                @{ Name = 'MgxFanoutMs';         Unit = 'ms'; Sources = @(@{ Path = 'mgx.Median.ElapsedMs' }) }
                @{ Name = 'MgxPeakWorkingSetMB'; Unit = 'MB'; Sources = @(@{ Path = 'mgx.Median.PeakWorkingSetMB' }) }
            )
        }
        '03' = @{
            Script  = '03-user-report.ps1'
            Metrics = @(
                @{ Name = 'MgxReportMs';         Unit = 'ms'; Sources = @(@{ Path = 'mgx.Median.ElapsedMs' }) }
                @{ Name = 'MgxPeakWorkingSetMB'; Unit = 'MB'; Sources = @(@{ Path = 'mgx.Median.PeakWorkingSetMB' }) }
            )
        }
        '04' = @{
            Script  = '04-batch-create.ps1'
            Metrics = @(
                @{ Name = 'MgxBatchCreateMs'; Unit = 'ms'; Sources = @(@{ Path = 'mgxCreate.ElapsedMs' }) }
                @{ Name = 'MgxBatchPatchMs';  Unit = 'ms'; Sources = @(@{ Path = 'batchPatch.ElapsedMs' }) }
                @{ Name = 'MgxFanoutPatchMs'; Unit = 'ms'; Sources = @(@{ Path = 'fanoutPatch.ElapsedMs' }) }
            )
        }
        '05' = @{
            # Memory is the claim here, not speed, so peak working set is the headline and the
            # wall time rides along. The managed-heap delta is the figure the README quotes for
            # the streaming arms, and it sits near zero by design - too near to carry a
            # percentage, which is why it is recorded rather than diffed.
            Script  = '05-memory-export.ps1'
            Metrics = @(
                @{ Name = 'MgxExportPeakWorkingSetMB';   Unit = 'MB'; Sources = @(@{ Path = 'mgxExport.PeakWorkingSetMB' }) }
                @{ Name = 'MgxExportMs';                 Unit = 'ms'; Sources = @(@{ Path = 'mgxExport.ElapsedMs' }) }
                @{ Name = 'MgxExportManagedHeapDeltaMB'; Unit = 'MB'; Sources = @(@{ Path = 'mgxExport.ManagedHeapDeltaMB' }) }
            )
        }
        '06' = @{
            Script  = '06-fault-gauntlet.ps1'
            Metrics = @(
                @{ Name = 'MgxFanoutMs';  Unit = 'ms'; Sources = @(@{ Path = 'mgx.ElapsedMs' }) }
                @{ Name = 'SdkMgxMs';     Unit = 'ms'; Sources = @(@{ Path = 'sdkMgx.ElapsedMs' }) }
                @{ Name = 'MgxCompleted'; Unit = '';   Sources = @(@{ Path = 'mgx.Output.ok' }) }
                @{ Name = 'MgxFailed';    Unit = '';   Sources = @(@{ Path = 'mgx.Output.failed' }) }
            )
        }
        '07' = @{
            Script        = '07-adaptive-pacing.ps1'
            Discriminator = 'Mode'
            Metrics       = @(
                @{ Name = 'ArmMs';     Unit = 'ms'; Sources = @(@{ Path = 'ElapsedMs' }) }
                @{ Name = 'ArmFailed'; Unit = '';   Sources = @(@{ Path = 'Output.failed' }) }
            )
        }
        '08' = @{
            Script  = '08-delta-sync.ps1'
            Metrics = @(
                @{ Name = 'InitialSyncMs';     Unit = 'ms'; Sources = @(@{ Path = 'initial.ElapsedMs' }) }
                @{ Name = 'IncrementalSyncMs'; Unit = 'ms'; Sources = @(@{ Path = 'incremental.ElapsedMs' }) }
                @{ Name = 'SteadySyncMs';      Unit = 'ms'; Sources = @(@{ Path = 'steady.ElapsedMs' }) }
                @{ Name = 'InitialItems';      Unit = '';   Sources = @(@{ Path = 'initial.Output.items' }) }
            )
        }
        '09' = @{
            # The checkpointed export is the headline: it is what checkpointing costs, in the
            # same units as everything else. The overhead percentage the script derives from it
            # reads better and is recorded, but it can land either side of zero on a quiet
            # tenant, so it is not what gets diffed.
            Script  = '09-kill-resume.ps1'
            Metrics = @(
                @{ Name = 'CheckpointedExportMs';  Unit = 'ms'; Sources = @(@{ Path = 'checkpointed.ElapsedMs' }) }
                @{ Name = 'BaselineExportMs';      Unit = 'ms'; Sources = @(@{ Path = 'baseline.ElapsedMs' }) }
                @{ Name = 'ResumeMs';              Unit = 'ms'; Sources = @(@{ Path = 'resume.ElapsedMs' }) }
                @{ Name = 'CheckpointOverheadPct'; Unit = '%';  Sources = @(@{ Path = 'verification.overheadPct' }) }
                @{ Name = 'DuplicateIds';          Unit = '';   Sources = @(@{ Path = 'verification.duplicateIds' }) }
            )
        }
        '10' = @{
            Script        = '10-pacing-under-real-throttling.ps1'
            Discriminator = 'Mode'
            Metrics       = @(
                @{ Name = 'WallSeconds';        Unit = 's'; Sources = @(@{ Path = 'Arms.paced.Seconds' }, @{ Path = 'WallMs'; Scale = 0.001 }) }
                @{ Name = 'UnpacedWallSeconds'; Unit = 's'; Sources = @(@{ Path = 'Arms.unpaced.Seconds' }) }
                @{ Name = 'ThrottleRetries';    Unit = '';  Sources = @(@{ Path = 'Arms.paced.ThrottleRetries' }, @{ Path = 'ThrottleRetries' }) }
            )
        }
        '18' = @{
            # A latency distribution, so the headline is the whole window's p90 of the HTTP
            # round trip - lower is better and it cannot reach zero. The clamp ratio the script
            # derives from the five-minute buckets is the figure the claim is stated in, and it
            # rides along rather than leading: a window that never stretched reports 1.0, which
            # is the good outcome and would read as an improvement against anything pinned.
            # P90CallElapsedMs is the same window measured at the caller, pacer waits included;
            # it is here so the two are never mistaken for one another.
            #
            # Discriminated on the preceding activity rather than on the drive: the whole point
            # of the measurement is that the same drive answers differently depending on what it
            # has just been through, so two runs are comparable only where the operator states
            # the same history for both. The drive itself would be the other candidate and is
            # deliberately not used - baseline.json is committed, and a drive id in it would put
            # a tenant's identifiers in the repository, which is why the live suite passes one
            # through MGX_LIVE_CONTENT_URI instead. The entry's own Result records the drive.
            Script        = '18-spo-latency-clamp.ps1'
            Discriminator = 'PrecedingActivity'
            Metrics       = @(
                @{ Name = 'P90ElapsedMs';     Unit = 'ms'; Sources = @(@{ Path = 'Overall.P90ElapsedMs' }) }
                @{ Name = 'P50ElapsedMs';     Unit = 'ms'; Sources = @(@{ Path = 'Overall.P50ElapsedMs' }) }
                @{ Name = 'P99ElapsedMs';     Unit = 'ms'; Sources = @(@{ Path = 'Overall.P99ElapsedMs' }) }
                @{ Name = 'P90CallElapsedMs'; Unit = 'ms'; Sources = @(@{ Path = 'Overall.P90CallElapsedMs' }) }
                @{ Name = 'ClampRatio';       Unit = 'x';  Sources = @(@{ Path = 'Overall.ClampRatio' }) }
                @{ Name = 'Throttled429';     Unit = '';   Sources = @(@{ Path = 'Overall.Throttled429' }) }
            )
        }
    }

    $wanted = if ($Id) { $Id } else { @($table.Keys) }
    foreach ($key in $wanted) {
        if (-not $table.Contains($key)) {
            Write-Warning "benchmark '$key' has no baseline metrics in Get-BenchScenario - skipped."
            continue
        }
        $s = $table[$key]
        [pscustomobject]@{
            Id            = $key
            Script        = $s.Script
            # Write-BenchResult names the file after the benchmark, and every benchmark passes
            # its own script name without the extension.
            ResultFile    = [System.IO.Path]::GetFileNameWithoutExtension($s.Script) + '.json'
            Discriminator = $s.Discriminator
            Metrics       = $s.Metrics
        }
    }
}

# Walks a dotted path into a recorded Result object. Returns $null for any segment that is not
# there, which is how a metric reports itself absent rather than throwing halfway through a
# promotion.
function Get-BenchResultValue {
    param(
        [object] $Node,
        [Parameter(Mandatory)] [string] $Path
    )
    foreach ($segment in ($Path -split '\.')) {
        if ($null -eq $Node) { return $null }
        $property = $Node.PSObject.Properties[$segment]
        if (-not $property) { return $null }
        $Node = $property.Value
    }
    $Node
}

# The newest entry of one benchmark's results file, or $null. -Since rejects an entry older than
# the run that asked for it: a benchmark that died before recording leaves the previous run's
# entry newest, and promoting or comparing that reports a stale number as a fresh one.
function Get-BenchLatestResult {
    param(
        [Parameter(Mandatory)] [string] $ResultFile,
        [datetime] $Since = [datetime]::MinValue
    )
    if (-not (Test-Path $ResultFile)) { return $null }
    $entries = @(Get-Content $ResultFile -Raw | ConvertFrom-Json)
    if ($entries.Count -eq 0) { return $null }
    $entry = $entries[-1]

    # ConvertFrom-Json turns a round-trip timestamp into a DateTime on its own; a file written
    # by another host may still hand back the string it was written as.
    $recorded = $entry.RecordedAt
    if ($recorded -isnot [datetime]) {
        try {
            $recorded = [datetime]::Parse([string]$recorded, [cultureinfo]::InvariantCulture,
                                          [System.Globalization.DateTimeStyles]::RoundtripKind)
        }
        catch { $recorded = [datetime]::MinValue }
    }
    if ($recorded -lt $Since) { return $null }
    $entry
}

# A value is a measurement only when it is a finite number: one of PowerShell's numeric types,
# or a string that parses as one under the invariant culture the benchmark process is pinned
# to. [double] coerces almost anything without throwing - '' becomes 0, $true becomes 1 - so a
# source that never recorded a number would otherwise be pinned as though it had. NaN and the
# infinities pass both tests unless they are refused explicitly: a typed double or single can
# already hold one mid-computation, and NumberStyles.Float parses the strings "NaN",
# "Infinity" and "-Infinity" as valid doubles, so each arm is checked for
# [double]::IsFinite before the value is accepted.
function Test-BenchNumericValue([object] $Value) {
    if ($Value -is [int] -or $Value -is [long] -or $Value -is [decimal]) {
        return $true
    }
    if ($Value -is [double] -or $Value -is [single]) {
        return [double]::IsFinite([double]$Value)
    }
    $parsed = 0.0
    $Value -is [string] -and [double]::TryParse($Value, [System.Globalization.NumberStyles]::Float,
        [System.Globalization.CultureInfo]::InvariantCulture, [ref] $parsed) -and [double]::IsFinite($parsed)
}

# Names what a value not accepted by Test-BenchNumericValue actually held, for a warning or a
# comparison note to quote. A string or a boolean prints the way PowerShell already prints it
# elsewhere in this file ('INCONCLUSIVE', 'True'). An array, an object, or JSON null read back
# off a stale baseline.json has no such print of its own worth trusting - $null and '' would
# both print as nothing, and an array joins its elements with spaces - so each renders the way
# ConvertTo-Json -Compress would, the same shape the file was written in.
function ConvertTo-BenchDisplayValue([object] $Value) {
    if ($null -eq $Value -or $Value -is [array] -or $Value -is [System.Collections.IDictionary] -or $Value -is [pscustomobject]) {
        return ConvertTo-Json -InputObject $Value -Compress -Depth 5
    }
    "$Value"
}

# Reads one scenario's metrics out of a recorded entry: an ordered map of
# name -> @{ Value; Unit; Path }, plus the discriminator value where the scenario has one.
function Get-BenchScenarioMetric {
    param(
        [Parameter(Mandatory)] [object] $Scenario,
        [Parameter(Mandatory)] [object] $Entry
    )
    $metrics = [ordered]@{}
    foreach ($m in $Scenario.Metrics) {
        $sources = @($m.Sources)
        $resolved = $false
        $notNumbers = [System.Collections.Generic.List[object]]::new()
        for ($i = 0; $i -lt $sources.Count; $i++) {
            $source = $sources[$i]
            $value = Get-BenchResultValue -Node $Entry.Result -Path $source.Path
            if ($null -eq $value) { continue }
            if (-not (Test-BenchNumericValue $value)) {
                $notNumbers.Add([pscustomobject]@{
                        Description = "'$($source.Path)' holds '$(ConvertTo-BenchDisplayValue $value)', which is not a number"
                    })
                continue
            }
            $scale  = if ($source.ContainsKey('Scale')) { [double]$source.Scale } else { 1.0 }
            $number = if ($value -is [string]) { [double]::Parse($value, [System.Globalization.CultureInfo]::InvariantCulture) } else { [double]$value }
            $scaled = $number * $scale
            # A source that read within double's range can still overflow once Scale is
            # applied - the table's own Scale (10's WallMs, 0.001) shrinks, so this cannot
            # happen through it today, but a larger Scale would push a source that was itself
            # finite past it. What must be finite is the multiply's result, not the raw
            # value, so it is checked after scaling rather than before - and a source that
            # fails here is a source that held a non-number just as surely as 'INCONCLUSIVE'
            # is: the walk tries the next one and says so. Named the same way its sibling
            # above names a raw non-number, so two overflowing sources on the same metric
            # can still be told apart by path.
            if (-not [double]::IsFinite($scaled)) {
                $notNumbers.Add([pscustomobject]@{
                        Description = "'$($source.Path)' holds '$(ConvertTo-BenchDisplayValue $value)', which scaled by $scale is not finite"
                    })
                continue
            }
            $metrics[$m.Name] = [pscustomobject]@{
                Value = [math]::Round($scaled, 3)
                Unit  = $m.Unit
                Path  = $source.Path
            }
            $resolved = $true
            break
        }
        if ($resolved) {
            # Every source ahead of the one that resolved held something that was not a
            # number - a first source that recorded 'INCONCLUSIVE' before a second recorded
            # the real figure, say. The next source covering for it should not read as
            # silence, so each earns its own warning naming what it held.
            foreach ($n in $notNumbers) {
                Write-Warning "$($Scenario.Id): '$($m.Name)' - $($n.Description) - trying the next source."
            }
        }
        # A source with nothing at its path was never written - 10's single-arm entries never
        # write the other arm's, and that is not a metric gone missing, it is one this entry
        # was never going to carry. Only a source that held something, and held a value that
        # is not a number, earns a warning - naming what it held.
        elseif ($notNumbers.Count -gt 0) {
            $held = ($notNumbers | ForEach-Object { $_.Description }) -join '; '
            Write-Warning "$($Scenario.Id): '$($m.Name)' not recorded - $held."
        }
    }
    $discriminator = $null
    if ($Scenario.Discriminator) {
        $discriminator = Get-BenchResultValue -Node $Entry.Result -Path $Scenario.Discriminator
    }
    [pscustomobject]@{ Metrics = $metrics; Discriminator = $discriminator }
}

function Export-BenchBaseline {
    <#
    .SYNOPSIS
        Promotes the newest results entry of each named benchmark into baseline.json.

    .DESCRIPTION
        Writes one record per scenario: its id, the script that produced it, the metrics
        Get-BenchScenario names for it, and the Mgx/SDK/PowerShell versions and timestamp
        Write-BenchResult already recorded alongside the numbers. Scenarios already in the file
        and not named here are left as they are, so promoting one scenario does not discard the
        six the release baseline is otherwise made of.

        A scenario whose entry records Result.Conclusive false is reported and left out: the run
        itself says it did not measure what the scenario exists to measure, and whatever row was
        already pinned for it stays. Where such an entry also carries Result.InconclusiveReason,
        the warning quotes it.

    .PARAMETER Benchmark
        Scenario ids to promote.

    .PARAMETER Since
        Ignore entries recorded before this. run.ps1 passes the moment the suite started, so a
        benchmark that recorded nothing is reported rather than promoted from a previous run.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string[]] $Benchmark,
        [string] $ResultsDirectory = (Join-Path $PSScriptRoot 'results'),
        [string] $Path = (Join-Path $PSScriptRoot 'baseline.json'),
        [datetime] $Since = [datetime]::MinValue
    )

    $scenarios = [ordered]@{}
    if (Test-Path $Path) {
        $existing = Get-Content $Path -Raw | ConvertFrom-Json
        if ($existing.Scenarios) {
            foreach ($p in $existing.Scenarios.PSObject.Properties) { $scenarios[$p.Name] = $p.Value }
        }
    }

    $promoted = 0
    foreach ($scenario in (Get-BenchScenario -Id $Benchmark)) {
        $file  = Join-Path $ResultsDirectory $scenario.ResultFile
        $entry = Get-BenchLatestResult -ResultFile $file -Since $Since
        if (-not $entry) {
            Write-Warning "benchmark $($scenario.Id) recorded no result in $file - not promoted."
            continue
        }
        # A run that says it measured nothing is not a baseline. 10 is the shape of it: it
        # records Result.Conclusive false when its unpaced arm was never refused, which means
        # the throttled regime it exists to compare against was never reached - and the wall
        # times it did record are then two unthrottled runs wearing a throttling benchmark's
        # name. Promoting that pins a number every later run is read against, so the row it
        # would overwrite is kept instead: a stale conclusive figure is still a measurement,
        # and none at all is what the comparison already reports as unmeasured. A scenario that
        # records no verdict at all promotes as before - the field is a claim, not a
        # requirement.
        $conclusive = Get-BenchResultValue -Node $entry.Result -Path 'Conclusive'
        if ($null -ne $conclusive -and -not $conclusive) {
            $why = Get-BenchResultValue -Node $entry.Result -Path 'InconclusiveReason'
            if ([string]::IsNullOrWhiteSpace([string]$why)) { $why = 'Conclusive is false' }
            $kept = if ($scenarios.Contains($scenario.Id)) {
                        '; the row already in the file is left as it was'
                    } else { '' }
            Write-Warning "benchmark $($scenario.Id) recorded an inconclusive run ($why) - not promoted$kept."
            continue
        }
        $read = Get-BenchScenarioMetric -Scenario $scenario -Entry $entry
        if ($read.Metrics.Count -eq 0) {
            Write-Warning "benchmark $($scenario.Id) recorded an entry carrying none of its metrics - not promoted."
            continue
        }
        # The headline is the first metric of the scenario's own row, and only where that
        # metric resolved. Taking the first metric that HAPPENED to resolve promotes whichever
        # one came next instead - a different figure, in a different unit, read as though it
        # were the one the scenario is pinned on. 09 is the shape of it: with its leading
        # sources absent from an entry, the next resolvable metric is CheckpointOverheadPct, a
        # percentage that lands either side of zero on a quiet tenant. An entry that did not
        # record its own headline is pinned with the metrics it did record and no headline, so
        # the comparison reports it rather than reading something else in its place.
        $tableHeadline = @($scenario.Metrics)[0].Name
        $headline = if ($read.Metrics.Contains($tableHeadline)) { $tableHeadline } else { $null }
        $scenarios[$scenario.Id] = [pscustomobject]@{
            Scenario      = $scenario.Id
            Script        = $scenario.Script
            Headline      = $headline
            Discriminator = $read.Discriminator
            Metrics       = $read.Metrics
            MgxVersion    = $entry.MgxVersion
            SdkVersion    = $entry.SdkVersion
            PSVersion     = $entry.PSVersion
            RecordedAt    = $entry.RecordedAt
            PromotedAt    = (Get-Date).ToString('o')
        }
        $promoted++
        if ($headline) {
            Write-Host ("  {0}: {1} = {2}{3}" -f $scenario.Id, $headline,
                $read.Metrics[$headline].Value, $read.Metrics[$headline].Unit)
        }
        else {
            Write-Warning "benchmark $($scenario.Id) recorded no '$tableHeadline' - pinned with its other metrics and no headline; the comparison will report it as not comparable."
        }
    }

    if ($promoted -eq 0) {
        Write-Warning "nothing promoted; $Path left as it was."
        return
    }

    $sorted = [ordered]@{}
    foreach ($key in (@($scenarios.Keys) | Sort-Object)) { $sorted[$key] = $scenarios[$key] }
    $document = [pscustomobject]@{
        SchemaVersion = 1
        GeneratedAt   = (Get-Date).ToString('o')
        Scenarios     = $sorted
    }
    ConvertTo-Json -InputObject $document -Depth 10 | Set-Content -Path $Path
    Write-Host "  baseline written to $Path ($promoted promoted, $($sorted.Count) scenario(s) in the file)"
}

function Compare-BenchBaseline {
    <#
    .SYNOPSIS
        Reads a finished run against baseline.json and prints a delta per scenario.

    .DESCRIPTION
        Reports; never fails the run. The tenant's own throttling ceiling has moved 40% between
        consecutive days, so a hard gate on these numbers is noise wearing a verdict's clothes.
        A scenario with no baseline entry prints as unmeasured, and one whose fresh entry is
        missing or not comparable prints as that - a silent skip is how a regression hides.

        Emits one row object per scenario as well as printing them, so a caller can assert.

    .PARAMETER ThresholdPercent
        How far the headline metric may move before the row is flagged.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string[]] $Benchmark,
        [string] $ResultsDirectory = (Join-Path $PSScriptRoot 'results'),
        [string] $BaselinePath = (Join-Path $PSScriptRoot 'baseline.json'),
        [datetime] $Since = [datetime]::MinValue,
        # Proposed, not derived: 50%. The tenant's throttling ceiling has been seen to move 40%
        # between consecutive days, so anything tighter flags the weather. 50% sits just above
        # that band, which makes a flag mean "further than the worst environmental swing anyone
        # has recorded here" - a weak claim, but a true one. Once baseline.json has been
        # re-recorded a few times, a per-scenario band derived from those runs replaces it.
        [double] $ThresholdPercent = 50
    )

    if (-not (Test-Path $BaselinePath)) {
        Write-Host ''
        Write-Host '=== BASELINE COMPARISON ==='
        Write-Host "no baseline at $BaselinePath - nothing to read this run against."
        Write-Host 'record one with ./run.ps1 -RecordBaseline when a run is worth pinning.'
        return
    }

    $baseline = Get-Content $BaselinePath -Raw | ConvertFrom-Json
    if (-not $baseline.Scenarios) {
        throw "Baseline '$BaselinePath' carries no Scenarios; record one with -RecordBaseline before comparing."
    }
    if ($baseline.SchemaVersion -ne 1) {
        throw "Baseline '$BaselinePath' is schema $($baseline.SchemaVersion); this suite reads schema 1."
    }
    Write-Host ''
    Write-Host ('=== BASELINE COMPARISON (flagged past {0:F0}%) ===' -f $ThresholdPercent)
    Write-Host ('{0,-4} {1,-28} {2,14} {3,14} {4,9}  {5}' -f 'id', 'metric', 'baseline', 'this run', 'delta', 'note')

    $rows = @()
    foreach ($scenario in (Get-BenchScenario -Id $Benchmark)) {
        $pinnedProperty = $baseline.Scenarios.PSObject.Properties[$scenario.Id]
        $pinned = if ($pinnedProperty) { $pinnedProperty.Value } else { $null }
        $entry  = Get-BenchLatestResult -ResultFile (Join-Path $ResultsDirectory $scenario.ResultFile) -Since $Since
        $fresh  = if ($entry) { Get-BenchScenarioMetric -Scenario $scenario -Entry $entry } else { $null }

        $row = [pscustomobject]@{
            Scenario = $scenario.Id
            Metric   = $null
            Unit     = $null
            Baseline = $null
            Current  = $null
            DeltaPct = $null
            Status   = $null
            Flagged  = $false
            Note     = $null
        }

        if (-not $pinned) {
            $row.Status = 'unmeasured'
            $row.Note   = 'unmeasured - no baseline entry'
        }
        elseif (-not $fresh) {
            $row.Status = 'no result'
            $row.Note   = 'no result recorded by this run'
        }
        elseif (-not $pinned.Headline) {
            # Pinned without one: the promoted run did not record the metric its scenario is
            # read on. The metrics beside it are not substitutes - each is a different figure -
            # so the row says there is nothing to compare instead of comparing something else.
            $row.Status = 'not comparable'
            $row.Note   = 'not comparable - the baseline entry has no headline, so its run never recorded the metric this scenario is read on'
        }
        else {
            $row.Metric = $pinned.Headline
            $baselineProperty = $pinned.Metrics.PSObject.Properties[$pinned.Headline]
            $b = if ($baselineProperty) { $baselineProperty.Value } else { $null }
            $f = $fresh.Metrics[$pinned.Headline]

            if (-not $b -or -not $f) {
                $row.Status = 'unmeasured'
                $row.Note   = "unmeasured - '$($pinned.Headline)' is not in both"
            }
            elseif ($b.Path -ne $f.Path -or $b.Unit -ne $f.Unit) {
                $row.Status = 'not comparable'
                $row.Note   = "not comparable - baseline read $($b.Path), this run $($f.Path)"
            }
            elseif ($scenario.Discriminator -and
                    [string]$pinned.Discriminator -ne [string]$fresh.Discriminator) {
                $row.Status = 'not comparable'
                $row.Note   = "not comparable - $($scenario.Discriminator) was '$($pinned.Discriminator)', this run '$($fresh.Discriminator)'"
            }
            else {
                $row.Unit = $b.Unit
                # [double] coerces almost anything without throwing - '' becomes 0, $true
                # becomes 1 - so a stale baseline.json holding something that was never a
                # number would cast into a plausible-looking figure instead of being refused.
                # Test-BenchNumericValue is asked first, on the raw value, before either side
                # is cast - the same gate a raw source reading already goes through in
                # Get-BenchScenarioMetric. It also refuses NaN and the infinities outright, so
                # a value already typed [double] or [single] is exempted from it here: a bare
                # JSON NaN or Infinity token round-trips through ConvertFrom-Json as a genuine
                # typed double, and that is a number for the cast and the finiteness check
                # below to refuse, not one for this gate to call not a number at all.
                $bIsTyped = $b.Value -is [double] -or $b.Value -is [single]
                $fIsTyped = $f.Value -is [double] -or $f.Value -is [single]
                if (-not $bIsTyped -and -not (Test-BenchNumericValue $b.Value)) {
                    $row.Status = 'not comparable'
                    $row.Note   = "not comparable - the baseline value is '$(ConvertTo-BenchDisplayValue $b.Value)', which is not a number"
                }
                elseif (-not $fIsTyped -and -not (Test-BenchNumericValue $f.Value)) {
                    $row.Status = 'not comparable'
                    $row.Note   = "not comparable - this run's value is '$(ConvertTo-BenchDisplayValue $f.Value)', which is not a number"
                }
                else {
                    # [double]$b.Value already reads "NaN", "Infinity" and "-Infinity" as the
                    # doubles they name - PowerShell's own numeric conversion, not
                    # [double]::Parse, accepts those three tokens under any culture - so a
                    # stale baseline.json recorded before Export-BenchBaseline refused a
                    # non-finite measurement, or a bare JSON NaN or Infinity token the gate
                    # above let through untested, casts here to a genuine non-finite double
                    # rather than throwing. Nothing pins a non-finite Current today, but the
                    # same cast is used for it, so both are checked before either reaches the
                    # arithmetic below.
                    $row.Baseline = [double]$b.Value
                    $row.Current  = [double]$f.Value
                    if (-not [double]::IsFinite($row.Baseline)) {
                        $row.Status = 'not comparable'
                        $row.Note   = "not comparable - the baseline value is $($row.Baseline)"
                    }
                    elseif (-not [double]::IsFinite($row.Current)) {
                        $row.Status = 'not comparable'
                        $row.Note   = "not comparable - this run's value is $($row.Current)"
                    }
                    elseif ([math]::Abs($row.Baseline) -lt 1e-9) {
                        $row.Status = 'not comparable'
                        $row.Note   = 'not comparable - the baseline value is zero'
                    }
                    elseif ($row.Baseline -lt 0) {
                        # A percentage delta over a negative denominator inverts its own sign: a
                        # figure that moved further below the baseline comes out positive and reads
                        # as the bad direction, and one that improved reads as a gain. Every
                        # headline in the table is meant to be strictly positive, so a negative one
                        # is a figure that was never fit to be a headline - which is a thing to say,
                        # not a delta to compute.
                        $row.Status = 'not comparable'
                        $row.Note   = "not comparable - the baseline value is negative ($($row.Baseline)$($row.Unit)); a percentage delta over it would invert its own sign"
                    }
                    else {
                        $row.DeltaPct = [math]::Round((($row.Current - $row.Baseline) / $row.Baseline) * 100, 1)
                        $row.Status   = 'compared'
                        # Lower is better for every headline in the table, so a positive delta is the
                        # bad direction. A move the other way is flagged too: a scenario that halves
                        # has usually measured less work rather than done the same work faster.
                        if ($row.DeltaPct -gt $ThresholdPercent) {
                            $row.Flagged = $true
                            $row.Note    = 'REGRESSED'
                        }
                        elseif ($row.DeltaPct -lt -$ThresholdPercent) {
                            $row.Flagged = $true
                            $row.Note    = 'IMPROVED - check it measured the same work'
                        }
                    }
                }
            }
        }

        $show = {
            param($value, $unit)
            if ($null -eq $value) { '-' } else { '{0:F1}{1}' -f $value, $unit }
        }
        $color = if ($row.Flagged) { 'Yellow' } elseif ($row.Status -ne 'compared') { 'DarkGray' } else { 'Gray' }
        Write-Host ('{0,-4} {1,-28} {2,14} {3,14} {4,9}  {5}' -f `
            $row.Scenario,
            $(if ($row.Metric) { $row.Metric } else { '-' }),
            (& $show $row.Baseline $row.Unit),
            (& $show $row.Current $row.Unit),
            $(if ($null -ne $row.DeltaPct) { '{0:+0.0;-0.0;0.0}%' -f $row.DeltaPct } else { '-' }),
            $(if ($row.Note) { $row.Note } else { $row.Status })) -ForegroundColor $color

        $rows += $row
    }

    Write-Host 'reported, not gated: these numbers move with the tenant, so nothing here fails the run.'
    $rows
}

# --- Benchmark 18: the SPO latency-clamp summary, read from its checkpointed rows -------------
#
# 18-spo-latency-clamp.ps1 used to build its buckets and totals while the load ran, from
# whatever rows happened to be in memory at each step. Measured against the run recorded
# 2026-09-10 (StartedAt 13:16:34, EndedAt 14:31:36, a 75-minute window with a 15-minute machine
# sleep in it): the Stopwatch the header timed itself with does not advance while suspended, so
# it printed "59.6 minute(s)" for a window that ran 75; the two buckets the sleep spanned were
# never created at all, because a live accumulator only ever holds a key for a minute a row
# actually arrived in; the printed table had no Errors column, so the 1,005 rows the sleep's
# breaker trip wrote were invisible next to the 429 count everyone was already looking at; and
# the result's PacingState is the pacer's one most-recent latency sample - that run's own last
# row happened to be a slow one, so the figure sitting beside Overall.P90ElapsedMs and
# ClampRatio read as the verdict when it was one call out of 5,919 in its bucket.
#
# The functions below read the rows file back rather than trust an accumulator that was live
# through all of that: every row the run checkpointed is on disk before the summary is built,
# so a bucket, a count or a headline computed from it is the same whether it runs while the load
# is finishing or an hour later against a copy. Get-ClampSummary is what the script calls once
# its load completes, and what a Pester test calls against a synthetic rows.jsonl it wrote
# itself - the same computation either way, rather than a live one and a tested one that can
# drift apart.

function Get-ClampPercentile {
    <#
    .SYNOPSIS
        Nearest-rank percentile: the value the tenant actually served, never an interpolation.
    #>
    [CmdletBinding()]
    param([object[]] $Values, [double] $Percentile)
    if (-not $Values -or @($Values).Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    $rank = [int][math]::Ceiling(($Percentile / 100.0) * $sorted.Count)
    if ($rank -lt 1) { $rank = 1 }
    if ($rank -gt $sorted.Count) { $rank = $sorted.Count }
    [math]::Round([double]$sorted[$rank - 1], 1)
}

function Get-ClampClockGap {
    <#
    .SYNOPSIS
        Whether the wall clock outran the run's Stopwatch by more than a bucket - a suspended
        machine, not a pacer wait or a breaker hold, because a Stopwatch can only fail to
        advance by not running.

    .PARAMETER StartedAt
    .PARAMETER EndedAt
        Get-Date at the start and end of the load, wall time.

    .PARAMETER WallMs
        The load's own Stopwatch, in milliseconds - ElapsedMilliseconds, read after Stop().

    .PARAMETER BucketMinutes
        The run's bucket width, and the gap's own tolerance: a wall/Stopwatch drift inside one
        bucket is ordinary scheduling noise, not a sleep.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [datetime] $StartedAt,
        [Parameter(Mandatory)] [datetime] $EndedAt,
        [Parameter(Mandatory)] [long] $WallMs,
        [Parameter(Mandatory)] [ValidateRange(1, [int]::MaxValue)] [int] $BucketMinutes
    )

    $wallSpanMs = ($EndedAt - $StartedAt).TotalMilliseconds
    $gapMs = $wallSpanMs - $WallMs
    $gapMinutes = [math]::Round($gapMs / 60000.0, 1)
    $refused = $gapMs -gt ($BucketMinutes * 60000.0)

    [pscustomobject]@{
        Refused    = $refused
        GapMinutes = $gapMinutes
        Reason     = $(if ($refused) {
            "the machine was not awake for $gapMinutes minute(s) of the window; rows kept, summary withheld"
        } else { $null })
    }
}

function Get-ClampRows {
    <#
    .SYNOPSIS
        Parses 18's checkpointed rows.jsonl - one call attempt per line - back into objects.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Path)

    if (-not (Test-Path $Path)) { throw "Get-ClampRows: no rows file at '$Path'." }
    @(Get-Content -Path $Path | Where-Object { $_ } | ForEach-Object { ConvertFrom-Json -InputObject $_ })
}

function Get-ClampRefusalKind {
    <#
    .SYNOPSIS
        Groups a refusal message to what comes before its first colon - two "Circuit breaker
        tripped: too many failures..." trips with different wait times group under the same
        "Circuit breaker tripped" - or the whole message when it carries no colon at all, such
        as "no response traced".
    #>
    [CmdletBinding()]
    param([AllowNull()] [AllowEmptyString()] [string] $Message)

    if (-not $Message) { return $null }
    (($Message -split ':', 2)[0]).Trim()
}

function Get-ClampBuckets {
    <#
    .SYNOPSIS
        18's per-bucket table: every index from 0 through Ceiling(Minutes/BucketMinutes)-1, so
        a bucket the run never wrote a row to is reported rather than silently missing.

    .DESCRIPTION
        A row's own minute-offset from StartedAt decides its bucket, exactly as the live run
        computes it - so a bucket built here from the checkpoint agrees with the one the run
        would have shown if its accumulator had covered the whole window. The index range is
        computed from Minutes and BucketMinutes rather than from the rows themselves: the rows
        say which buckets got traffic, not how many the run was asked to cover, and the gap
        between the two is exactly what a marked "no rows" bucket exists to say.
    #>
    [CmdletBinding()]
    param(
        [AllowNull()] [object[]] $Rows,
        [Parameter(Mandatory)] [datetime] $StartedAt,
        [Parameter(Mandatory)] [ValidateRange(1, [int]::MaxValue)] [int] $Minutes,
        [Parameter(Mandatory)] [ValidateRange(1, [int]::MaxValue)] [int] $BucketMinutes
    )

    $buckets = [ordered]@{}
    foreach ($row in @($Rows)) {
        $minutesIn = ((Get-Date $row.Timestamp) - $StartedAt).TotalMinutes
        if ($minutesIn -lt 0) { $minutesIn = 0 }
        $index = [int][math]::Floor($minutesIn / $BucketMinutes)
        $key = "b$index"
        if (-not $buckets.Contains($key)) {
            $buckets[$key] = [ordered]@{
                Bucket = $index; FromMinute = $index * $BucketMinutes; Calls = 0; Attempts = 0
                Ok = 0; Throttled429 = 0; Errors = 0
                ThrottlePercentageSeen = 0; ThrottleScopeSeen = 0; RetryAfterSeen = 0; RateLimitSeen = 0
                Latencies = [System.Collections.Generic.List[double]]::new()
                Note = $null
            }
        }
        $b = $buckets[$key]
        $b.Attempts++
        if ($row.Attempt -eq 1) { $b.Calls++ }
        if ($row.Status -ge 200 -and $row.Status -lt 300) { $b.Ok++ }
        elseif ($row.Status -eq 429) { $b.Throttled429++ }
        if ($row.Status -eq 0 -or $row.Error) { $b.Errors++ }
        if ($row.ThrottleLimitPercentage) { $b.ThrottlePercentageSeen++ }
        if ($row.ThrottleScope) { $b.ThrottleScopeSeen++ }
        if ($row.RetryAfter) { $b.RetryAfterSeen++ }
        if ($row.RateLimitHeader) { $b.RateLimitSeen++ }
        if ($null -ne $row.ElapsedMs -and $row.Status -ge 200 -and $row.Status -lt 300) {
            $b.Latencies.Add([double]$row.ElapsedMs)
        }
    }

    $bucketCount = [int][math]::Ceiling($Minutes / [double]$BucketMinutes)
    for ($index = 0; $index -lt $bucketCount; $index++) {
        $key = "b$index"
        if (-not $buckets.Contains($key)) {
            $buckets[$key] = [ordered]@{
                Bucket = $index; FromMinute = $index * $BucketMinutes; Calls = 0; Attempts = 0
                Ok = 0; Throttled429 = 0; Errors = 0
                ThrottlePercentageSeen = 0; ThrottleScopeSeen = 0; RetryAfterSeen = 0; RateLimitSeen = 0
                Latencies = [System.Collections.Generic.List[double]]::new()
                Note = 'no rows'
            }
        }
    }

    @(
        foreach ($key in $buckets.Keys) {
            $b = $buckets[$key]
            [pscustomobject]@{
                Bucket                 = $b.Bucket
                FromMinute             = $b.FromMinute
                Calls                  = $b.Calls
                Attempts               = $b.Attempts
                Ok                     = $b.Ok
                Throttled429           = $b.Throttled429
                Errors                 = $b.Errors
                P50ElapsedMs           = Get-ClampPercentile -Values $b.Latencies -Percentile 50
                P90ElapsedMs           = Get-ClampPercentile -Values $b.Latencies -Percentile 90
                P99ElapsedMs           = Get-ClampPercentile -Values $b.Latencies -Percentile 99
                ThrottlePercentageSeen = $b.ThrottlePercentageSeen
                ThrottleScopeSeen      = $b.ThrottleScopeSeen
                RetryAfterSeen         = $b.RetryAfterSeen
                RateLimitSeen          = $b.RateLimitSeen
                Note                   = $b.Note
            }
        }
    ) | Sort-Object Bucket
}

function Get-ClampOverall {
    <#
    .SYNOPSIS
        18's whole-window totals: outcome counts, percentiles, throttle-signal counts, the
        clamp ratio, and refusals grouped by kind.

    .PARAMETER Buckets
        Get-ClampBuckets' own output, for the clamp ratio - worst bucket P90 over the first
        bucket's - which has to read the same buckets the table beside it prints.
    #>
    [CmdletBinding()]
    param(
        [AllowNull()] [object[]] $Rows,
        [Parameter(Mandatory)] [AllowNull()] [object[]] $Buckets
    )

    $rowList = @($Rows)
    $latencies = [System.Collections.Generic.List[double]]::new()
    $callLatencies = [System.Collections.Generic.List[double]]::new()
    $perWorker = @{}
    $callCount = 0; $okCount = 0; $throttled = 0; $errorCount = 0
    $sawPercentage = 0; $sawScope = 0; $sawRetryAfter = 0; $sawRateLimit = 0
    $refusals = [ordered]@{}

    foreach ($row in $rowList) {
        if ($row.Attempt -eq 1) { $callCount++ }
        if ($row.Status -ge 200 -and $row.Status -lt 300) { $okCount++ }
        elseif ($row.Status -eq 429) { $throttled++ }
        if ($row.Status -eq 0 -or $row.Error) {
            $errorCount++
            $kind = Get-ClampRefusalKind -Message $row.Error
            if ($kind) {
                if (-not $refusals.Contains($kind)) { $refusals[$kind] = 0 }
                $refusals[$kind]++
            }
        }
        if ($row.ThrottleLimitPercentage) { $sawPercentage++ }
        if ($row.ThrottleScope) { $sawScope++ }
        if ($row.RetryAfter) { $sawRetryAfter++ }
        if ($row.RateLimitHeader) { $sawRateLimit++ }
        if (-not $perWorker.ContainsKey($row.Worker)) { $perWorker[$row.Worker] = 0 }
        $perWorker[$row.Worker]++
        if ($null -ne $row.ElapsedMs -and $row.Status -ge 200 -and $row.Status -lt 300) {
            $latencies.Add([double]$row.ElapsedMs)
        }
        if ($row.Attempt -eq 1 -and $null -ne $row.CallElapsedMs) { $callLatencies.Add([double]$row.CallElapsedMs) }
    }

    $bucketList = @($Buckets)
    $firstP90 = @($bucketList | Where-Object { $null -ne $_.P90ElapsedMs })[0]?.P90ElapsedMs
    $worstP90 = ($bucketList | Where-Object { $null -ne $_.P90ElapsedMs } |
                 Measure-Object -Property P90ElapsedMs -Maximum).Maximum
    $clampRatio = if ($firstP90 -and $firstP90 -gt 0 -and $null -ne $worstP90) {
        [math]::Round($worstP90 / $firstP90, 2)
    } else { $null }

    [ordered]@{
        Calls                  = $callCount
        Attempts               = $rowList.Count
        Ok                     = $okCount
        Throttled429           = $throttled
        Errors                 = $errorCount
        Workers                = $perWorker.Count
        P50ElapsedMs           = Get-ClampPercentile -Values $latencies -Percentile 50
        P90ElapsedMs           = Get-ClampPercentile -Values $latencies -Percentile 90
        P99ElapsedMs           = Get-ClampPercentile -Values $latencies -Percentile 99
        P90CallElapsedMs       = Get-ClampPercentile -Values $callLatencies -Percentile 90
        ThrottlePercentageSeen = $sawPercentage
        ThrottleScopeSeen      = $sawScope
        RetryAfterSeen         = $sawRetryAfter
        RateLimitSeen          = $sawRateLimit
        ClampRatio             = $clampRatio
        Refusals               = $refusals
    }
}

function Get-ClampHeadline {
    <#
    .SYNOPSIS
        The one-line verdict, read from the buckets: whole-window p50/p90 and the worst
        bucket's own p90, never a single live sample.

    .DESCRIPTION
        Get-MgxTelemetry's PacingState carries the pacer's most recent latency sample - useful
        for watching the pacer live, and exactly one call wide. Printed beside a headline that
        is actually labeled as one, it stops reading as the run's verdict by default; this
        function is that headline, and the caller prints PacingState only as "last pacer
        sample", never in its place.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [object] $Overall,
        [AllowNull()] [object[]] $Buckets
    )

    $worstBucket = @($Buckets) | Where-Object { $null -ne $_.P90ElapsedMs } |
        Sort-Object P90ElapsedMs -Descending | Select-Object -First 1

    $sentence = "whole window: p50 {0}ms, p90 {1}ms over {2} answered attempt(s); worst bucket (minute {3}) p90 {4}ms" -f `
        ($Overall.P50ElapsedMs ?? '-'), ($Overall.P90ElapsedMs ?? '-'), $Overall.Ok, `
        $(if ($worstBucket) { $worstBucket.FromMinute } else { '-' }), `
        $(if ($worstBucket) { $worstBucket.P90ElapsedMs } else { '-' })
    if ($null -ne $Overall.ClampRatio) {
        $sentence += (" ({0:F2}x the first bucket's)" -f $Overall.ClampRatio)
    }

    [pscustomobject]@{
        P50ElapsedMs            = $Overall.P50ElapsedMs
        P90ElapsedMs            = $Overall.P90ElapsedMs
        WorstBucketFromMinute   = $(if ($worstBucket) { $worstBucket.FromMinute } else { $null })
        WorstBucketP90ElapsedMs = $(if ($worstBucket) { $worstBucket.P90ElapsedMs } else { $null })
        ClampRatio              = $Overall.ClampRatio
        Sentence                = $sentence
    }
}

function Get-ClampSummary {
    <#
    .SYNOPSIS
        18's whole summary, built from its checkpointed rows file: the clock-gap refusal, every
        bucket, the whole-window totals and refusals-by-kind, and the headline.

    .DESCRIPTION
        Callable on a rows file path alone, with the run's own clocks and window - the same call
        the script makes once its load finishes, and the one a test makes against a synthetic
        rows.jsonl it wrote itself. When StartedAt/EndedAt and the Stopwatch disagree by more
        than a bucket (Get-ClampClockGap), the rows stay checkpointed on disk, but Buckets,
        Overall and Headline all come back $null: a summary built across a gap the machine was
        not awake for is not a distribution, and the caller is expected to print
        Refused/RefusalReason and stop rather than read the rest of this object.

    .PARAMETER RowsPath
        18's checkpointed rows.jsonl.

    .PARAMETER StartedAt
    .PARAMETER EndedAt
        Wall time (Get-Date) at the start and end of the load.

    .PARAMETER WallMs
        The load's own Stopwatch, in milliseconds.

    .PARAMETER Minutes
    .PARAMETER BucketMinutes
        The window and bucket width the run was asked for - fixes the bucket count independent
        of which buckets the rows happen to touch.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $RowsPath,
        [Parameter(Mandatory)] [datetime] $StartedAt,
        [Parameter(Mandatory)] [datetime] $EndedAt,
        [Parameter(Mandatory)] [long] $WallMs,
        [Parameter(Mandatory)] [ValidateRange(1, [int]::MaxValue)] [int] $Minutes,
        [Parameter(Mandatory)] [ValidateRange(1, [int]::MaxValue)] [int] $BucketMinutes
    )

    $gap = Get-ClampClockGap -StartedAt $StartedAt -EndedAt $EndedAt -WallMs $WallMs -BucketMinutes $BucketMinutes
    if ($gap.Refused) {
        return [pscustomobject]@{
            Refused       = $true
            RefusalReason = $gap.Reason
            GapMinutes    = $gap.GapMinutes
            Buckets       = $null
            Overall       = $null
            Headline      = $null
        }
    }

    $rows = Get-ClampRows -Path $RowsPath
    $buckets = Get-ClampBuckets -Rows $rows -StartedAt $StartedAt -Minutes $Minutes -BucketMinutes $BucketMinutes
    $overall = Get-ClampOverall -Rows $rows -Buckets $buckets
    $headline = Get-ClampHeadline -Overall $overall -Buckets $buckets

    [pscustomobject]@{
        Refused       = $false
        RefusalReason = $null
        GapMinutes    = $gap.GapMinutes
        Buckets       = $buckets
        Overall       = $overall
        Headline      = $headline
    }
}
