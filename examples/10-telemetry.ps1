# View session telemetry after running Graph operations.
#
# Get-MgxTelemetry shows accumulated request counts, retry events,
# throttle waits, and timing across all Mgx cmdlet calls in the session.
# Useful for understanding what your script actually did.
#
# Requirements: Connect-MgGraph -Scopes "User.Read.All"

Import-Module Mgx

# Run some operations
$users = Invoke-MgxRequest /users -All -Property id,displayName
Write-Host "Users: $($users.Count)"

$groups = Invoke-MgxRequest /groups -All -Property id,displayName
Write-Host "Groups: $($groups.Count)"

# See what happened
Get-MgxTelemetry
