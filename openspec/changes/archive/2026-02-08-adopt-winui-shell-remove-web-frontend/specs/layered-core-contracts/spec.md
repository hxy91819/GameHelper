## ADDED Requirements

### Requirement: Strict dependency direction across layers
The system architecture SHALL enforce dependency direction `UI/CLI -> Core -> Infrastructure`, and Core MUST NOT reference UI framework assemblies.

#### Scenario: Validate project references
- **WHEN** project references are reviewed for a change
- **THEN** no reference path from Core to UI frameworks exists

### Requirement: Windows integration behind abstractions
All Windows-specific integrations (WMI/ETW/Win32) SHALL be accessed through Core abstractions and implemented in Infrastructure adapters.

#### Scenario: Use adapter for system event source
- **WHEN** a feature requires process/system events
- **THEN** the feature consumes an abstraction contract and the concrete Windows adapter is resolved by dependency injection

### Requirement: Shared logic for UI and CLI shells
User-facing shells SHALL share the same Core use-cases for business behavior to avoid logic divergence.

#### Scenario: Compare behavior between shell entrypoints
- **WHEN** equivalent operation is executed from WinUI and CLI shells
- **THEN** both paths produce equivalent domain outcomes for the same input and environment
