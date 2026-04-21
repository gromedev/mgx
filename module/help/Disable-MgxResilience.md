---
external help file: Mgx.Cmdlets.dll-Help.xml
Module Name: Mgx
online version: https://github.com/gromedev/mgx/blob/main/module/help/Disable-MgxResilience.md
schema: 2.0.0
---

# Disable-MgxResilience

## SYNOPSIS
Remove Polly resilience injection from the Microsoft.Graph SDK.

## SYNTAX

```
Disable-MgxResilience [-ProgressAction <ActionPreference>] [-WhatIf] [-Confirm] [<CommonParameters>]
```

## DESCRIPTION
Disable-MgxResilience removes the Polly resilience injection from the Microsoft.Graph SDK's HTTP transport, restoring the original SDK HttpClient that was saved by Enable-MgxResilience.

After calling this cmdlet, SDK cmdlets revert to their default retry behavior.

## EXAMPLES

### Example 1: Disable resilience
```powershell
Disable-MgxResilience
```

Restores the SDK's original HTTP transport.

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

## RELATED LINKS
[Enable-MgxResilience](Enable-MgxResilience.md)
[Get-MgxResilience](Get-MgxResilience.md)
