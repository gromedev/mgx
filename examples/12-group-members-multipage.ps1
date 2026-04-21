# Get members of multiple groups concurrently, handling pagination per group.
#
# Each group can have hundreds of pages of members. Fan-out with -All
# handles pagination independently per group - the pipeline receives
# members from all groups interleaved as pages complete.
#
# Requirements: Connect-MgGraph -Scopes "Group.Read.All", "User.Read.All"

Import-Module Mgx

# Get all groups, then stream all members from all groups concurrently
$allMembers = Invoke-MgxRequest /groups -All -Property id,displayName |
    Invoke-MgxRequest '/groups/{id}/members' -All -SkipNotFound -SkipForbidden

Write-Host "Total members across all groups: $($allMembers.Count)"
$allMembers | Group-Object '@odata.type' | Format-Table Name, Count -AutoSize
