## ADDED Requirements

### Requirement: WinUI shell as primary runtime UI
The system SHALL provide a WinUI 3 desktop shell as the primary user interface on Windows, replacing the Web dashboard as the default runtime entry point.

#### Scenario: Launch native shell
- **WHEN** the user starts the desktop application on Windows
- **THEN** the WinUI shell is shown as the main interactive interface

### Requirement: Core-service based UI interactions
The WinUI shell SHALL invoke application use-cases through Core service contracts and MUST NOT embed duplicated domain logic in page code-behind.

#### Scenario: Execute settings update through Core contract
- **WHEN** the user updates a setting in the WinUI shell
- **THEN** the shell calls the Core contract and the resulting state is persisted via configured infrastructure providers

### Requirement: Windows-only runtime scope clarity
The desktop shell SHALL declare Windows-only runtime scope while preserving developer clarity for non-Windows contributors.

#### Scenario: Non-Windows runtime invocation
- **WHEN** the desktop shell is invoked on a non-Windows environment
- **THEN** the runtime reports unsupported platform status without ambiguous failure behavior
