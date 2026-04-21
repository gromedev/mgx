---
external help file: Mgx.Cmdlets.dll-Help.xml
Module Name: Mgx
online version: https://github.com/gromedev/mgx/blob/main/module/help/Get-MgxResilience.md
schema: 2.0.0
---

# Get-MgxResilience

## SYNOPSIS
Check the current state of MgxResilience injection.

## SYNTAX

```
Get-MgxResilience [<CommonParameters>]
```

## DESCRIPTION
Get-MgxResilience returns a PSObject indicating whether resilience injection is enabled and whether the injected handler is still active on the SDK's HttpClient.

## EXAMPLES

### Example 1: Check resilience state
```powershell
Get-MgxResilience
```

Returns an object with IsEnabled and IsActive properties.

### Example 2: Conditional re-injection
```powershell
if (-not (Get-MgxResilience).IsActive) {
    Enable-MgxResilience
}
```

Re-enables resilience only if it's not currently active.

## PARAMETERS

### CommonParameters
This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable, -InformationAction, -InformationVariable, -OutVariable, -OutBuffer, -PipelineVariable, -Verbose, -WarningAction, and -WarningVariable. For more information, see [about_CommonParameters](http://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

### None

## OUTPUTS

### System.Management.Automation.PSObject
Object with IsEnabled (bool) and IsActive (bool) properties.

## NOTES

## RELATED LINKS
[Enable-MgxResilience](Enable-MgxResilience.md)
[Disable-MgxResilience](Disable-MgxResilience.md)
