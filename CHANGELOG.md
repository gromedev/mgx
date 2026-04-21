# Changelog

## 1.0.1

- Added tab completion for the Uri parameter on all cmdlets that accept Graph API paths (Invoke-MgxRequest, Invoke-MgxBatchRequest, Export-MgxCollection, Expand-MgxRelation, Sync-MgxDelta)
- Extracted `CircuitBreakerMessage` protected property on `MgxCmdletBase` to eliminate repeated inline circuit breaker message strings across six cmdlet files
- Removed redundant XML doc comments on self-documenting members in `MgxCmdletBase` and `ResilientGraphClient`