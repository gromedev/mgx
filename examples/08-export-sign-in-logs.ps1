# Export sign-in audit logs to JSONL with checkpoint/resume.
#
# Sign-in logs can contain millions of records. This streams directly
# to disk - no memory accumulation. If the script is interrupted
# (Ctrl+C, network drop, etc.), re-run with the same paths and it
# resumes from the last completed page.
#
# Requirements: Connect-MgGraph -Scopes "AuditLog.Read.All"

Import-Module Mgx

$outputFile     = "./signins.jsonl"
$checkpointFile = "./signins-checkpoint.json"

# Filter to last 7 days
$since = (Get-Date).AddDays(-7).ToString("yyyy-MM-ddTHH:mm:ssZ")

$result = Export-MgxCollection /auditLogs/signIns `
    -OutputFile $outputFile `
    -CheckpointPath $checkpointFile `
    -Filter "createdDateTime ge $since" `
    -All `
    -ApiVersion beta

Write-Host "Exported $($result.ItemCount) sign-in records to $outputFile"
