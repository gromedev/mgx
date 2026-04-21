# Export all Conditional Access policies to JSON.
#
# CA policies are only available on the beta endpoint. -ApiVersion beta
# accesses them without installing any extra modules.
#
# Requirements: Connect-MgGraph -Scopes "Policy.Read.All"

Import-Module Mgx

$policies = Invoke-MgxRequest /identity/conditionalAccess/policies `
    -All `
    -ApiVersion beta `
    -Property id,displayName,state,createdDateTime,modifiedDateTime

Write-Host "Conditional Access policies: $($policies.Count)"
$policies | Sort-Object displayName | Format-Table displayName, state, modifiedDateTime -AutoSize

# Export full policy details to JSON for backup/audit
$policies | ConvertTo-Json -Depth 10 | Out-File "./conditional-access-policies.json"
Write-Host "Full export saved to conditional-access-policies.json"
