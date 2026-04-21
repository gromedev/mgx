# Export all users to a JSONL file (one JSON object per line).
#
# Unlike collecting results in memory and piping to ConvertTo-Json,
# Export-MgxCollection writes directly to disk as pages arrive.
# Memory usage stays flat even on a 200k-user tenant.
#
# If interrupted, re-run with the same -CheckpointPath to resume
# from where it left off.
#
# Requirements: Connect-MgGraph -Scopes "User.Read.All"

Import-Module Mgx

$outputFile    = "./users.jsonl"
$checkpointFile = "./users-checkpoint.json"

$result = Export-MgxCollection /users `
    -OutputFile $outputFile `
    -CheckpointPath $checkpointFile `
    -All `
    -Property id,displayName,mail,department,jobTitle,accountEnabled

Write-Host "Exported $($result.ItemCount) users to $outputFile"
