# Mgx Module Loader
# Loads Mgx.Cmdlets.dll into the Default ALC.
# ALC dependency isolation is handled by AlcInitializer (IModuleAssemblyInitializer in Mgx.Cmdlets.dll).

$ModuleRoot = $PSScriptRoot

# Load the main cmdlet assembly
$CmdletsDll = Join-Path $ModuleRoot 'Mgx.Cmdlets.dll'
if (Test-Path $CmdletsDll) {
    Import-Module $CmdletsDll
} else {
    Write-Error "Mgx.Cmdlets.dll not found at $CmdletsDll. Did you run the build script?"
}

# Clean up static state on module removal to prevent resource leaks
$MyInvocation.MyCommand.ScriptBlock.Module.OnRemove = {
    [Mgx.Cmdlets.Base.MgxCmdletBase]::ResetHttpClient()
    [Mgx.Engine.Http.ResiliencePipelineFactory]::Reset()
}

# Tab completion for Graph API resource paths
$script:UriCompletions = @(
    @{ Text = 'users';                       Tip = 'All users in the tenant' }
    @{ Text = 'groups';                      Tip = 'All groups' }
    @{ Text = 'applications';                Tip = 'App registrations' }
    @{ Text = 'servicePrincipals';           Tip = 'Enterprise apps / service principals' }
    @{ Text = 'devices';                     Tip = 'Registered devices' }
    @{ Text = 'directoryRoles';              Tip = 'Directory roles' }
    @{ Text = 'domains';                     Tip = 'Verified domains' }
    @{ Text = 'organization';                Tip = 'Tenant info' }
    @{ Text = 'subscribedSkus';              Tip = 'License SKUs' }
    @{ Text = 'teams';                       Tip = 'Teams' }
    @{ Text = 'sites';                       Tip = 'SharePoint sites' }
    @{ Text = 'drives';                      Tip = 'OneDrive drives' }
    @{ Text = 'auditLogs/signIns';           Tip = 'Sign-in logs' }
    @{ Text = 'auditLogs/directoryAudits';   Tip = 'Directory audit logs' }
)

$script:UriCompleter = {
    param($commandName, $parameterName, $wordToComplete, $commandAst, $fakeBoundParameters)
    $script:UriCompletions | Where-Object { $_.Text -like "$wordToComplete*" } | ForEach-Object {
        [System.Management.Automation.CompletionResult]::new($_.Text, $_.Text, 'ParameterValue', $_.Tip)
    }
}

foreach ($cmd in 'Invoke-MgxRequest', 'Invoke-MgxBatchRequest', 'Export-MgxCollection', 'Expand-MgxRelation', 'Sync-MgxDelta') {
    Register-ArgumentCompleter -CommandName $cmd -ParameterName Uri -ScriptBlock $script:UriCompleter
}
