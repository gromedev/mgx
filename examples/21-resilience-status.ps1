# Check and toggle SDK resilience injection.
#
# Get-MgxResilience reports whether the Polly pipeline is currently
# injected into the Microsoft.Graph SDK HTTP transport.
# Disable-MgxResilience removes it and restores the original SDK behavior.
#
# Requirements: Connect-MgGraph -Scopes "User.Read.All"

Import-Module Microsoft.Graph.Users
Import-Module Mgx

# Check current state - not injected yet
Get-MgxResilience

# Inject
Enable-MgxResilience
Get-MgxResilience   # IsInjected: True

# SDK cmdlets now have retry + circuit breaker
$users = Get-MgUser -Top 5 -Property displayName
Write-Host "Got $($users.Count) users via SDK (with resilience)"

# Remove injection - restore original SDK behavior
Disable-MgxResilience
Get-MgxResilience   # IsInjected: False
