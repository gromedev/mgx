# Tune resilience options for a specific workload.
#
# Set-MgxOption lets you adjust the rate limiter, retry count, circuit
# breaker, and timeouts at runtime without restarting the session.
# Only the parameters you pass are changed; everything else stays.
#
# Use Get-MgxOption to inspect current settings.
#
# Requirements: Connect-MgGraph -Scopes "User.Read.All"

Import-Module Mgx

# View current defaults
Get-MgxOption

# Slow down for a tenant that throttles aggressively
Set-MgxOption -BatchItemsPerSecond 5 -MaxRetryAttempts 10

# Run your workload
$users = Invoke-MgxRequest /users -All -Property id,displayName
Write-Host "Users: $($users.Count)"

# Restore all defaults when done
Set-MgxOption -Reset

Get-MgxOption
