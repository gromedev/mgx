# Query multiple Graph endpoints in a single HTTP round-trip.
#
# Invoke-MgxBatchRequest accepts mixed URLs, methods, and bodies
# in one batch. This avoids serial requests when you need data
# from unrelated endpoints (users, groups, apps, skus, etc.).
#
# Requirements: Connect-MgGraph -Scopes "User.Read.All", "Group.Read.All", "Application.Read.All"

Import-Module Mgx

# Mixed GET: pull a snapshot from four different endpoints at once
$results = @(
    "/users?`$top=5&`$select=displayName,userPrincipalName"
    "/groups?`$top=5&`$select=displayName,groupTypes"
    "/applications?`$top=5&`$select=displayName,appId"
    "/subscribedSkus"
) | Invoke-MgxBatchRequest

Write-Host "Batch returned $($results.Count) responses"
$results | ForEach-Object {
    $name = $_.displayName ?? $_.skuPartNumber ?? '(collection)'
    Write-Host "  $name"
}

# Mixed methods: read some things from different endpoints, all in one call
$requests = @(
    [PSCustomObject]@{ Url = "/me";              Method = "GET" }
    [PSCustomObject]@{ Url = "/organization";    Method = "GET" }
    [PSCustomObject]@{ Url = "/users?`$top=1";   Method = "GET" }
)
$mixed = $requests | Invoke-MgxBatchRequest

Write-Host "`nMixed-method batch:"
$mixed | ForEach-Object {
    $name = $_.displayName ?? '(response)'
    Write-Host "  $name"
}
