## REMOVED Requirements

### Requirement: Embedded runtime web API for end-user product entry
**Reason**: Product entry is being consolidated to WinUI native shell and shared Core contracts to reduce dual-runtime complexity.
**Migration**: Route end-user configuration and statistics operations through WinUI shell and optional CLI entrypoints; remove `--web/--port` product runtime path after migration completion.

#### Scenario: Web runtime path deprecation
- **WHEN** the desktop migration reaches cutover completion criteria
- **THEN** embedded web API is no longer required as a product runtime entry surface
