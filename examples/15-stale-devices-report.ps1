# Report devices that haven't checked in for 90+ days.
#
# Stale devices are candidates for cleanup. This filters server-side
# by approximateLastSignInDateTime to avoid pulling every device.
#
# Requirements: Connect-MgGraph -Scopes "Device.Read.All"

Import-Module Mgx

$cutoff = (Get-Date).AddDays(-90).ToString("yyyy-MM-ddTHH:mm:ssZ")

$stale = Invoke-MgxRequest /devices `
    -All `
    -Filter "approximateLastSignInDateTime le $cutoff" `
    -Property displayName,operatingSystem,operatingSystemVersion,approximateLastSignInDateTime,accountEnabled `
    -ConsistencyLevel eventual

Write-Host "Stale devices (90+ days): $($stale.Count)"

$stale |
    Sort-Object approximateLastSignInDateTime |
    Select-Object displayName, operatingSystem, approximateLastSignInDateTime, accountEnabled |
    Format-Table -AutoSize
