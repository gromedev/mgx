---
external help file: Mgx.Cmdlets.dll-Help.xml
Module Name: Mgx
online version: https://github.com/gromedev/mgx/blob/main/module/help/Get-MgxOption.md
schema: 2.0.0
---

# Get-MgxOption

## SYNOPSIS
Display current resilience and rate limiting configuration.

## SYNTAX

```
Get-MgxOption [<CommonParameters>]
```

## DESCRIPTION
Get-MgxOption returns the current resilience and rate limiting configuration as a PSObject. This includes retry settings, circuit breaker parameters, rate limiter configuration, and timeout values.

## EXAMPLES

### Example 1: View current options
```powershell
Get-MgxOption
```

Displays all current resilience settings including retry attempts, timeouts, circuit breaker thresholds, and rate limiter configuration.

### Example 2: Check a specific setting
```powershell
(Get-MgxOption).TotalTimeoutSeconds
```

Returns the current total timeout value.

## PARAMETERS

### CommonParameters
This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable, -InformationAction, -InformationVariable, -OutVariable, -OutBuffer, -PipelineVariable, -Verbose, -WarningAction, and -WarningVariable. For more information, see [about_CommonParameters](http://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

### None

## OUTPUTS

### System.Management.Automation.PSObject
A PSObject with properties for each configuration setting.

## NOTES

## RELATED LINKS
[Set-MgxOption](Set-MgxOption.md)
