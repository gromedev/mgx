# Changelog

## 1.0.2

- Fixed Linux install: renamed `Mgx.psd1`, `Mgx.psm1`, and `Mgx.Format.ps1xml` to lowercase so `Install-Module Mgx` works on case-sensitive filesystems (PSGallery lowercases the module folder name)
- Updated `about_Mgx_Tuning` version reference to v1.0.1

## 1.0.1

- Added tab completion for the Uri parameter on all cmdlets that accept Graph API paths (Invoke-MgxRequest, Invoke-MgxBatchRequest, Export-MgxCollection, Expand-MgxRelation, Sync-MgxDelta)
- Extracted `CircuitBreakerMessage` protected property on `MgxCmdletBase` to eliminate repeated inline circuit breaker message strings across six cmdlet files
- Removed redundant XML doc comments on self-documenting members in `MgxCmdletBase` and `ResilientGraphClient`