# Chain multiple Expand-MgxRelation stages to build rich objects in one pass.
#
# Each stage concurrently fetches a different relation for every piped object.
# The result has the original properties plus Manager and Licenses
# attached as nested objects, all fetched in parallel across users.
#
# Requirements: Connect-MgGraph -Scopes "User.Read.All"

Import-Module Mgx

$enriched = Invoke-MgxRequest /users -Top 20 -Property id,displayName,mail |
    Expand-MgxRelation '/users/{id}/manager'      -As Manager  -Flatten -SkipNotFound |
    Expand-MgxRelation '/users/{id}/licenseDetails' -As Licenses -SkipNotFound

foreach ($user in $enriched) {
    $mgr      = $user.Manager.displayName ?? "(none)"
    $licenses = ($user.Licenses | Select-Object -ExpandProperty skuPartNumber) -join ", "
    if (-not $licenses) { $licenses = "(none)" }
    Write-Host "$($user.displayName)  |  Manager: $mgr  |  Licenses: $licenses"
}
