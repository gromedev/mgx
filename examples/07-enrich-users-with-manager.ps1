# Enrich a list of users with their manager's display name.
#
# Expand-MgxRelation concurrently fetches the related data for each
# piped object and attaches it as a new property. This replaces a
# manual foreach loop with N serial HTTP calls.
#
# Requirements: Connect-MgGraph -Scopes "User.Read.All"

Import-Module Mgx

Invoke-MgxRequest /users -Top 50 -Property id,displayName,mail |
    Expand-MgxRelation '/users/{id}/manager' -As Manager -Flatten -SkipNotFound |
    Select-Object displayName, mail, @{ n='Manager'; e={ $_.Manager.displayName } } |
    Format-Table -AutoSize
