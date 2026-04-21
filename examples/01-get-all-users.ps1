# Get all users from your tenant and display them in a table.
#
# The Microsoft.Graph SDK equivalent (Get-MgUser -All) buffers every user
# in memory before returning. This streams results to the pipeline as each
# page arrives, keeping memory constant regardless of tenant size.
#
# Requirements: Connect-MgGraph -Scopes "User.Read.All"

Import-Module Mgx

Invoke-MgxRequest /users -All -Property displayName,mail,department,jobTitle |
    Format-Table displayName, mail, department, jobTitle -AutoSize
