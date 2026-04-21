# Bulk delete users with -WhatIf to preview before committing.
#
# All Mgx write operations support -WhatIf and -Confirm.
# Run without -WhatIf to execute; add -Confirm for interactive approval
# per item. Useful for destructive operations on production tenants.
#
# This example deletes all disabled guest accounts older than 180 days.
#
# Requirements: Connect-MgGraph -Scopes "User.ReadWrite.All"

Import-Module Mgx

$cutoff = (Get-Date).AddDays(-180).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", [System.Globalization.CultureInfo]::InvariantCulture)

$targets = Invoke-MgxRequest /users `
    -All `
    -Filter "userType eq 'Guest' and accountEnabled eq false and createdDateTime le $cutoff" `
    -Property id,displayName,mail,createdDateTime `
    -ConsistencyLevel eventual

Write-Host "Targets: $($targets.Count)"

if ($targets.Count -eq 0) {
    Write-Host "No matching guests found - nothing to delete."
    return
}

$targets | Format-Table displayName, mail, createdDateTime -AutoSize

# Preview - remove -WhatIf to execute
$targets | Invoke-MgxRequest '/users/{id}' -Method DELETE -WhatIf
