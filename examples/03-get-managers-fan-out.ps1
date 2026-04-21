# Get the manager for every user in the tenant using concurrent fan-out.
#
# The naive approach is a foreach loop: one HTTP call per user.
# With {id} template substitution, Invoke-MgxRequest dispatches all
# requests concurrently (default concurrency: 10) and streams results
# back as they complete.
#
# Requirements: Connect-MgGraph -Scopes "User.Read.All"

Import-Module Mgx

# Get all users first, then fan-out to fetch each manager concurrently.
$users = Invoke-MgxRequest /users -All -Property id,displayName

$users |
    Invoke-MgxRequest '/users/{id}/manager' -SkipNotFound |
    Select-Object id, displayName |
    Format-Table -AutoSize
