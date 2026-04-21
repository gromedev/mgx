# Add retry, circuit breaker, and rate limiting to existing Microsoft.Graph
# scripts without changing a single line of their code.
#
# Enable-MgxResilience injects a Polly pipeline into the SDK's HTTP transport.
# All SDK cmdlets (Get-MgUser, Get-MgGroup, etc.) then automatically retry
# on 429/5xx, honor Retry-After headers, and back off under load.
#
# Requirements: Connect-MgGraph -Scopes "User.Read.All"

Import-Module Microsoft.Graph.Users
Import-Module Mgx

Enable-MgxResilience

# These are unmodified Microsoft.Graph SDK calls - they now have full
# retry and circuit breaker protection.
$users = Get-MgUser -All -Property displayName, mail
Write-Host "Got $($users.Count) users via SDK (with Mgx resilience)"
