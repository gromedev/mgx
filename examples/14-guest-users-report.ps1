# Report all guest (external) users in the tenant.
#
# Guest accounts have userType eq 'Guest'. This exports them to JSONL
# for audit purposes and also prints a summary to the console.
#
# Requirements: Connect-MgGraph -Scopes "User.Read.All"

Import-Module Mgx

$guests = Invoke-MgxRequest /users `
    -All `
    -Filter "userType eq 'Guest'" `
    -Property displayName,mail,userPrincipalName,createdDateTime,externalUserState `
    -ConsistencyLevel eventual

Write-Host "Guest accounts: $($guests.Count)"

$guests |
    Sort-Object createdDateTime -Descending |
    Select-Object displayName, mail, externalUserState, createdDateTime |
    Format-Table -AutoSize
