# Incremental sync: only fetch users that changed since the last run.
#
# The first run downloads all users and saves a delta token to disk.
# Every subsequent run fetches only changes (new, modified, deleted)
# since the previous sync. Token management is automatic.
#
# Requirements: Connect-MgGraph -Scopes "User.Read.All"

Import-Module Mgx

$deltaFile = "./users-delta.json"

$changes = Sync-MgxDelta /users/delta `
    -DeltaPath $deltaFile `
    -Property id,displayName,mail,accountEnabled

Write-Host "Changes since last sync: $($changes.Count)"
$changes | Format-Table id, displayName, mail, accountEnabled -AutoSize
