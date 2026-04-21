---
external help file: Mgx.Cmdlets.dll-Help.xml
Module Name: Mgx
online version: https://github.com/gromedev/mgx/blob/main/module/help/Enable-MgxResilience.md
schema: 2.0.0
---

# Enable-MgxResilience

## SYNOPSIS
Inject Polly resilience into the Microsoft.Graph SDK's HTTP transport.

## SYNTAX

```
Enable-MgxResilience [-ProgressAction <ActionPreference>] [-WhatIf] [-Confirm] [<CommonParameters>]
```

## DESCRIPTION
Enable-MgxResilience wraps the Microsoft.Graph SDK's existing HttpClient with a ResilientDelegatingHandler, adding Polly-based retry, circuit breaker, rate limiting, and timeout policies. After calling this cmdlet, all SDK cmdlets (Get-MgUser, Get-MgGroup, etc.) automatically gain resilience with zero script changes.

The SDK's full handler chain (ODataQueryOptionsHandler, NationalCloudHandler, RedirectHandler, AuthenticationHandler, etc.) is preserved. The resilience handler is added on top.

Calling Enable-MgxResilience when already enabled re-injects if the SDK reset its client (e.g., after Connect-MgGraph or Set-MgRequestContext).

## EXAMPLES

### Example 1: Enable resilience for SDK cmdlets
```powershell
Connect-MgGraph -Scopes "User.Read.All"
Enable-MgxResilience
Get-MgUser -Top 100   # Now has retry, circuit breaker, and rate limiting
```

All subsequent Microsoft.Graph SDK cmdlets gain resilience automatically.

### Example 2: Re-inject after reconnecting
```powershell
Connect-MgGraph -Scopes "User.Read.All"
Enable-MgxResilience
# ... later ...
Connect-MgGraph -TenantId "other-tenant"
Enable-MgxResilience   # Re-inject for the new connection
```

## PARAMETERS

### -Confirm
Prompts you for confirmation before running the cmdlet.

```yaml
Type: SwitchParameter
Parameter Sets: (All)
Aliases: cf

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -WhatIf
Shows what would happen if the cmdlet runs. The cmdlet is not run.

```yaml
Type: SwitchParameter
Parameter Sets: (All)
Aliases: wi

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -ProgressAction
Determines how the cmdlet responds to progress updates.

```yaml
Type: ActionPreference
Parameter Sets: (All)
Aliases: proga

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### CommonParameters
This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable, -InformationAction, -InformationVariable, -OutVariable, -OutBuffer, -PipelineVariable, -Verbose, -WarningAction, and -WarningVariable. For more information, see [about_CommonParameters](http://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

### None

## OUTPUTS

### None

## NOTES
The SDK's built-in RetryHandler still runs inside the wrapped chain. Retries can compound, bounded by the total timeout (default 300s) and circuit breaker.

## RELATED LINKS
[Disable-MgxResilience](Disable-MgxResilience.md)
[Get-MgxResilience](Get-MgxResilience.md)
[Set-MgxOption](Set-MgxOption.md)
