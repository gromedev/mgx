# Build script for Mgx PowerShell module
# Compiles both projects and stages output into module/ directory

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # Where the module is staged. Defaults to this repo's own module/, so a plain
    # ./build.ps1 behaves exactly as before. Pointing it elsewhere - a fork's rebranded
    # build, a scratch install - never touches the repo's module/.
    [string]$ModuleRoot = (Join-Path $PSScriptRoot 'module'),

    # The manifest/psm1/format-file base name. Only matters together with -ModuleRoot:
    # the repository's own module/ is always mgx, so renaming without redirecting throws.
    [string]$ModuleName = 'mgx'
)

$ErrorActionPreference = 'Stop'

# Trim trailing separators before comparing or joining: "-ModuleRoot ./module/" would otherwise
# compare unequal to the repo's own module/ (they differ by a trailing slash), take the copy
# path below, and die copying mgx.psd1 onto itself. Windows and macOS default volumes fold case,
# so two spellings of the same path there name the same directory; Linux volumes are
# case-sensitive, so only there does a differently-cased path mean a different directory.
$ModuleRoot = [System.IO.Path]::GetFullPath($ModuleRoot, $PSScriptRoot).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
$RepoModuleRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'module')).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
$PathComparison = if ($IsLinux) { [StringComparison]::Ordinal } else { [StringComparison]::OrdinalIgnoreCase }

# Spelling isn't identity: on macOS /tmp is a symlink to /private/tmp, so
# "-ModuleRoot /tmp/x/module" and this repo's own "/private/tmp/x/module" normalize to two
# different strings that are nonetheless the same directory - the path compare below then
# misses the collision, takes the "renamed copy" branch, and dies "Cannot overwrite the item
# ... with itself" copying mgx.psd1 onto itself. Device+inode name a directory the way the
# filesystem does, independent of which symlinked spelling reached it, so prefer that when
# both directories already exist and we're not on Windows (whose Get-Item exposes no
# UnixStat). Fall back to the path compare where either side doesn't exist yet (a fresh
# -ModuleRoot has nothing to stat) or UnixStat itself is unavailable (older PowerShell, an
# unusual provider). A -ModuleRoot that is itself a symlink to module/ needs the same care:
# Get-Item -LiteralPath on a link stats the link's own inode, not the directory it points to,
# so each side is resolved to its final link target before its UnixStat is read.
$IsRepoModuleRoot = $null
if (-not $IsWindows -and (Test-Path -LiteralPath $ModuleRoot) -and (Test-Path -LiteralPath $RepoModuleRoot)) {
    $moduleRootItem = Get-Item -LiteralPath $ModuleRoot
    if ($moduleRootItem.LinkType) {
        $moduleRootTarget = $moduleRootItem.ResolveLinkTarget($true)
        $moduleRootItem = if ($moduleRootTarget -and (Test-Path -LiteralPath $moduleRootTarget.FullName)) { Get-Item -LiteralPath $moduleRootTarget.FullName } else { $null }
    }
    $repoModuleRootItem = Get-Item -LiteralPath $RepoModuleRoot
    if ($repoModuleRootItem.LinkType) {
        $repoModuleRootTarget = $repoModuleRootItem.ResolveLinkTarget($true)
        $repoModuleRootItem = if ($repoModuleRootTarget -and (Test-Path -LiteralPath $repoModuleRootTarget.FullName)) { Get-Item -LiteralPath $repoModuleRootTarget.FullName } else { $null }
    }
    $moduleRootStat = if ($moduleRootItem) { $moduleRootItem.UnixStat } else { $null }
    $repoModuleRootStat = if ($repoModuleRootItem) { $repoModuleRootItem.UnixStat } else { $null }
    if ($moduleRootStat -and $repoModuleRootStat) {
        $IsRepoModuleRoot = ($moduleRootStat.DeviceId -eq $repoModuleRootStat.DeviceId) -and
            ($moduleRootStat.Inode -eq $repoModuleRootStat.Inode)
    }
}
if ($null -eq $IsRepoModuleRoot) {
    $IsRepoModuleRoot = [string]::Equals($ModuleRoot, $RepoModuleRoot, $PathComparison)
}

# A -ModuleRoot that is itself a link needs to be refused here, before anything is created
# or cleaned: a dangling link's target doesn't exist, and a link to a plain file isn't a
# directory either way. Left unchecked, the copy branch below dies partway through with
# "Could not find a part of the path" - accurate about the symptom, silent about the cause.
$moduleRootLinkItem = Get-Item -LiteralPath $ModuleRoot -ErrorAction SilentlyContinue
if ($moduleRootLinkItem -and $moduleRootLinkItem.LinkType) {
    $moduleRootLinkTarget = $moduleRootLinkItem.ResolveLinkTarget($true)
    $targetExists = $moduleRootLinkTarget -and (Test-Path -LiteralPath $moduleRootLinkTarget.FullName)
    $targetDisplay = if ($moduleRootLinkTarget) { $moduleRootLinkTarget.FullName } else { $moduleRootLinkItem.Target }
    $reason = if (-not $targetExists) {
        'does not exist'
    } elseif (-not (Test-Path -LiteralPath $moduleRootLinkTarget.FullName -PathType Container)) {
        'is not a directory'
    }
    if ($reason) {
        throw "-ModuleRoot '$ModuleRoot' is a link to '$targetDisplay', which $reason. " +
              "Point -ModuleRoot at a directory, or create the target first."
    }
}

# An existing plain file - not a link, the case just above - reaches the same copy branch
# below and dies with the same raw "Could not find a part of the path" the link check exists
# to replace.
if (Test-Path -LiteralPath $ModuleRoot -PathType Leaf) {
    throw "-ModuleRoot '$ModuleRoot' is a file, not a directory. Point -ModuleRoot at a directory."
}

if ($ModuleName -ne 'mgx' -and $IsRepoModuleRoot) {
    throw "-ModuleName only applies with -ModuleRoot: the repository's own module/ is always mgx. " +
          "Pass -ModuleRoot <directory> to stage a renamed module."
}

# $ModuleName becomes a file name two blocks down (Join-Path $ModuleRoot "$ModuleName.psd1"):
# an empty string stages a hidden .psd1 that "succeeds" and imports nothing, and a separator
# like 'a/b' makes Copy-Item treat it as a subdirectory nothing created, dying "Could not find
# a part of the path" with no mention of -ModuleName anywhere in the message. A name built out
# of the punctuation alone passes the pattern and is no better: '.' stages '..psd1', '..psm1'
# and '..Format.ps1xml' and imports 12 commands under a module whose name is a single dot, and
# '_' and '-' name modules nobody asked for the same way. One letter or digit somewhere in it
# is what makes the staged files something a caller can ask for by name.
if ($ModuleName -notmatch '^[A-Za-z0-9._-]+$' -or $ModuleName -notmatch '[A-Za-z0-9]') {
    throw "-ModuleName '$ModuleName' is not a module base name. Give a plain name such as 'mgx' or 'fork'."
}

$DepsDir = Join-Path $ModuleRoot 'Dependencies'

Write-Host "Building Mgx ($Configuration)..." -ForegroundColor Cyan

# A ModuleRoot outside the repo starts empty. Stage the manifest, psm1, format file and
# about_* help topics from the repo's own module/ under $ModuleName's file names, so the
# version gate below, the integrity check, and Test-ModuleManifest afterward all have
# something to read. Copied verbatim: what a renamed manifest's RootModule or
# FormatsToProcess says is the source's concern, and the default name keeps them
# consistent. New-ExternalHelp regenerates the MAML later; it does not write these.
if (-not $IsRepoModuleRoot) {
    New-Item $ModuleRoot -ItemType Directory -Force | Out-Null
    Copy-Item (Join-Path $RepoModuleRoot 'mgx.psd1') (Join-Path $ModuleRoot "$ModuleName.psd1") -Force
    Copy-Item (Join-Path $RepoModuleRoot 'mgx.psm1') (Join-Path $ModuleRoot "$ModuleName.psm1") -Force
    Copy-Item (Join-Path $RepoModuleRoot 'mgx.Format.ps1xml') (Join-Path $ModuleRoot "$ModuleName.Format.ps1xml") -Force

    if ($ModuleName -ne 'mgx') {
        # The copy above is byte-for-byte, so the staged manifest still declares RootModule =
        # 'mgx.psm1' and FormatsToProcess = @('mgx.Format.ps1xml') while the files sitting beside
        # it are named $ModuleName.*. Import-Module resolves RootModule against a file that was
        # never staged and fails "Cannot find path ... mgx.psm1" - far from build.ps1 and with no
        # mention of -ModuleName. Rewrite just those two values by text substitution on the staged
        # copy only: Update-ModuleManifest reformats the whole file (key order, comments, quoting)
        # for what should be a two-value change.
        $stagedManifestPath = Join-Path $ModuleRoot "$ModuleName.psd1"
        $stagedManifestText = Get-Content -LiteralPath $stagedManifestPath -Raw
        $rootModulePattern = "(?m)^(\s*RootModule\s*=\s*)'mgx\.psm1'"
        $formatsPattern = "(?m)^(\s*FormatsToProcess\s*=\s*)@\('mgx\.Format\.ps1xml'\)"
        if ($stagedManifestText -notmatch $rootModulePattern -or $stagedManifestText -notmatch $formatsPattern) {
            throw "Module integrity check failed: $stagedManifestPath no longer has the RootModule = " +
                  "'mgx.psm1' / FormatsToProcess = @('mgx.Format.ps1xml') lines this rewrite expects, " +
                  "so it would still import as mgx."
        }
        $stagedManifestText = $stagedManifestText -replace $rootModulePattern, "`${1}'$ModuleName.psm1'"
        $stagedManifestText = $stagedManifestText -replace $formatsPattern, "`${1}@('$ModuleName.Format.ps1xml')"
        Set-Content -LiteralPath $stagedManifestPath -Value $stagedManifestText -NoNewline
    }

    $repoHelpEnUs = Join-Path $RepoModuleRoot 'en-US'
    if (Test-Path $repoHelpEnUs) {
        $helpEnUs = Join-Path $ModuleRoot 'en-US'
        New-Item $helpEnUs -ItemType Directory -Force | Out-Null
        Get-ChildItem $repoHelpEnUs -Filter 'about_*.help.txt' | Copy-Item -Destination $helpEnUs -Force
    }
}

# Version gate: MgxSdkVersion derives the SdkVersion header from the assembly version set in
# Directory.Build.props, so the header can no longer drift from the code on its own (it said
# 0.3.0 for three releases while the module shipped 1.0.x). The manifest ModuleVersion is
# still a separate declaration, so the one remaining mismatch - props vs manifest - is
# checked before the compile, failing in a second instead of a minute.
$manifestVersion = (Import-PowerShellDataFile (Join-Path $ModuleRoot "$ModuleName.psd1")).ModuleVersion
$propsFile = Join-Path $PSScriptRoot 'Directory.Build.props'
# Select-Object -First on a filtered list: .Project.PropertyGroup.Version returns an ARRAY the
# moment a second top-level <PropertyGroup> exists, and comparing an array to a string does not
# fail loudly - it just stops gating.
$propsVersion = @(([xml](Get-Content $propsFile -Raw)).Project.PropertyGroup.Version |
    Where-Object { $_ }) | Select-Object -First 1
if (-not $propsVersion) {
    throw "Version gate failed: no <Version> found in $propsFile"
}
if ($propsVersion -ne $manifestVersion) {
    throw "Version gate failed: Directory.Build.props <Version> is '$propsVersion' but $ModuleName.psd1 " +
          "ModuleVersion is '$manifestVersion'. Update one to match the other."
}
# "mgx/" names the wire header (MgxSdkVersion) sent with every request; that is the SDK's own
# name and does not change with -ModuleName - only the manifest file being checked does.
Write-Host "Version gate: mgx/$propsVersion matches $ModuleName.psd1" -ForegroundColor DarkGray

# Clean previous build artifacts
if (Test-Path $DepsDir) { Remove-Item $DepsDir -Recurse -Force }
$binDir = Join-Path $ModuleRoot 'bin'
if (Test-Path $binDir) { Remove-Item $binDir -Recurse -Force }
Get-ChildItem $ModuleRoot -Filter '*.dll' | Remove-Item -Force
Get-ChildItem $ModuleRoot -Filter '*.pdb' | Remove-Item -Force
Get-ChildItem $ModuleRoot -Filter '*.deps.json' | Remove-Item -Force

# Build the solution
dotnet build "$PSScriptRoot/Mgx.slnx" -c $Configuration --nologo "-p:ModuleRoot=$ModuleRoot"
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }

# Copy Cmdlets + Engine DLLs (load in default ALC)
# Detect TFM from csproj instead of hardcoding (survives TFM upgrades)
$csproj = [xml](Get-Content "$PSScriptRoot/src/Mgx.Cmdlets/Mgx.Cmdlets.csproj")
$tfm = @($csproj.Project.PropertyGroup.TargetFramework | Where-Object { $_ }) | Select-Object -First 1
$CmdletsOutput = Join-Path $PSScriptRoot "src/Mgx.Cmdlets/bin/$Configuration/$tfm"
Copy-Item "$CmdletsOutput/Mgx.Cmdlets.dll" $ModuleRoot -Force
Copy-Item "$CmdletsOutput/Mgx.Cmdlets.pdb" $ModuleRoot -Force -ErrorAction SilentlyContinue
Copy-Item "$CmdletsOutput/Mgx.Engine.dll" $ModuleRoot -Force
Copy-Item "$CmdletsOutput/Mgx.Engine.pdb" $ModuleRoot -Force -ErrorAction SilentlyContinue

# Copy deps.json (useful for diagnostic tooling; deleted by clean step above)
$depsJson = Join-Path $CmdletsOutput 'Mgx.Cmdlets.deps.json'
if (Test-Path $depsJson) {
    Copy-Item $depsJson $ModuleRoot -Force
} else {
    Write-Warning "Mgx.Cmdlets.deps.json not found in build output - diagnostics may be limited"
}

# Copy dependencies into Dependencies/ (loaded via ALC Resolving handler on first use)
# Polly.Core and System.Threading.RateLimiting are not in the module root;
# Mgx.Engine is NOT here - it is loaded via RequiredAssemblies in mgx.psd1 (see comment there).
New-Item $DepsDir -ItemType Directory -Force | Out-Null

# Copy dependency DLLs that need ALC isolation (Polly, RateLimiting)
# After D1+D2: replaced Microsoft.Extensions.Http.Resilience with direct Polly.Core 8.6.6
# This eliminated 28 transitive dependencies (51 total down to 23 packages)
#
# System.IO.Pipelines is here for a different reason: nothing in mgx compiles against it. It is
# in the closure of the System.Text.Json a PowerShell 7.5 or newer host supplies, and net8.0 -
# what src/ targets - does not include it in Microsoft.NETCore.App, so a host that does not
# supply it has nowhere to resolve it from unless the module carries it. See Mgx.Cmdlets.csproj.
$DepsToIsolate = @(
    'Polly.Core.dll'
    'System.Threading.RateLimiting.dll'
    'System.IO.Pipelines.dll'
)

foreach ($dep in $DepsToIsolate) {
    $depPath = Join-Path $CmdletsOutput $dep
    if (Test-Path $depPath) {
        Copy-Item $depPath $DepsDir -Force
    } else {
        throw "Required dependency not found: $dep (expected at $depPath)"
    }
}

# Verify module output is in expected state
$RequiredRoot = @('Mgx.Cmdlets.dll', 'Mgx.Engine.dll', "$ModuleName.psd1", "$ModuleName.psm1")
$RequiredDeps = @('Polly.Core.dll', 'System.Threading.RateLimiting.dll', 'System.IO.Pipelines.dll')

foreach ($f in $RequiredRoot) {
    if (-not (Test-Path (Join-Path $ModuleRoot $f))) {
        throw "Module integrity check failed: $f missing from module root"
    }
}
foreach ($f in $RequiredDeps) {
    if (-not (Test-Path (Join-Path $DepsDir $f))) {
        throw "Module integrity check failed: $f missing from Dependencies/"
    }
}
$orphans = Get-ChildItem $DepsDir -Filter 'Mgx.*.dll'
if ($orphans) {
    throw "Module integrity check failed: Mgx assemblies found in Dependencies/ (should only be in root): $($orphans.Name -join ', ')"
}

# The checks above only prove the required files exist - never that Import-Module can actually
# load them. A staged manifest whose RootModule or FormatsToProcess still names the wrong file
# (see the rewrite above) passes every Test-Path check here and only fails once a caller runs
# Import-Module for real, with a path error that says nothing about build.ps1 or -ModuleName.
# Import the staged manifest for real, in a fresh process, so it sees exactly what that caller
# would. The try/catch is load-bearing, not decoration: a bad RootModule or FormatsToProcess
# fails manifest validation through a WriteError that -ErrorAction Stop turns terminating only
# for that statement - pwsh's own exit code stays 0 and execution falls through to
# Get-Command unless the catch below turns it into an explicit exit 1. Emitting $_ as a plain
# expression (not Write-Error) keeps the caught message free of the ANSI codes PowerShell's
# default error formatting would otherwise embed in it.
$stagedPsd1 = Join-Path $ModuleRoot "$ModuleName.psd1"
$importResult = & pwsh -NoProfile -NonInteractive -c "try { Import-Module '$stagedPsd1' -ErrorAction Stop; (Get-Command -Module '$ModuleName').Count } catch { `$_.Exception.Message; exit 1 }" 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "Module integrity check failed: Import-Module '$stagedPsd1' failed in a fresh shell: $importResult"
}
Write-Host "Imported ${ModuleName}: $importResult commands" -ForegroundColor DarkGray

# Compiled help is generated from module/help/*.md, and nothing regenerated it: the shipped
# MAML drifted two releases behind its own source, so Get-Help documented output shapes removed
# in 2.0.0, denied a parameter added in 2.1.0, and gave a range the cmdlet rejects. Regenerating
# on every build is what stops the two diverging again.
$helpSource = Join-Path $PSScriptRoot 'module/help'
$helpOutput = Join-Path $ModuleRoot 'en-US'
if (Test-Path $helpSource) {
    # Already-imported counts: -ListAvailable only searches PSModulePath, so a platyPS loaded
    # from an explicit path (Save-Module into a folder, then Import-Module by path - which is how
    # a CI box without a profile gets one) looked absent and failed the build.
    if ((Get-Module platyPS) -or (Get-Module -ListAvailable platyPS)) {
        if (-not (Get-Module platyPS)) { Import-Module platyPS -ErrorAction Stop }
        New-ExternalHelp -Path $helpSource -OutputPath $helpOutput -Force | Out-Null
        Write-Host "Regenerated compiled help from module/help" -ForegroundColor DarkGray
    }
    else {
        throw ("platyPS is not installed, so module/en-US/Mgx.Cmdlets.dll-Help.xml cannot be " +
            "regenerated and would ship stale - which is how Get-Help came to describe output " +
            "shapes removed two releases earlier. Install-Module platyPS -Scope CurrentUser.")
    }
}

Write-Host "`nBuild complete!" -ForegroundColor Green
Write-Host "Module output: $ModuleRoot" -ForegroundColor Yellow
Write-Host "`nTo use:" -ForegroundColor Cyan
Write-Host "  Import-Module '$ModuleRoot/$ModuleName.psd1'" -ForegroundColor White
Write-Host "  Connect-MgGraph -Scopes 'User.Read.All'" -ForegroundColor White
Write-Host "  Invoke-MgxRequest /users -All" -ForegroundColor White
