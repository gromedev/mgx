# Bulk-update multiple users via $batch (20 requests per HTTP call).
#
# Instead of one PATCH per user (N HTTP calls), Invoke-MgxBatchRequest
# bundles up to 20 requests per call automatically. Throttle-aware pacing
# prevents 429s under load.
#
# This example sets the department field on a list of users.
#
# Requirements: Connect-MgGraph -Scopes "User.ReadWrite.All"

Import-Module Mgx

# Replace these with real user IDs from your tenant.
$userIds = @(
    "00000000-0000-0000-0000-000000000001"
    "00000000-0000-0000-0000-000000000002"
    "00000000-0000-0000-0000-000000000003"
)

$results = $userIds `
    | ForEach-Object { "/users/$_" } `
    | Invoke-MgxBatchRequest -Method PATCH -Body @{ department = "Engineering" }

$ok   = ($results | Where-Object { $_.Status -lt 400 }).Count
$fail = ($results | Where-Object { $_.Status -ge 400 }).Count
Write-Host "Updated: $ok  Failed: $fail"
