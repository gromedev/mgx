# Report all disabled user accounts in the tenant.
#
# Uses server-side filtering so only matching records are transferred.
# -ConsistencyLevel eventual enables advanced query support required
# for certain filter expressions on large tenants.
#
# Requirements: Connect-MgGraph -Scopes "User.Read.All"

Import-Module Mgx

$disabled = Invoke-MgxRequest /users `
    -All `
    -Filter "accountEnabled eq false" `
    -Property displayName,mail,userPrincipalName,createdDateTime `
    -ConsistencyLevel eventual

Write-Host "Disabled accounts: $($disabled.Count)"
$disabled | Sort-Object createdDateTime | Format-Table displayName, mail, createdDateTime -AutoSize
