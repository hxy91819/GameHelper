# Desktop UI Automation

Purpose: Define the requirements and scenarios for automating testing of the Windows desktop UI using FlaUI.

## Requirements

### Requirement: FlaUI-first desktop automation baseline
The test suite SHALL use FlaUI as the default desktop UI automation tool for Windows shell smoke scenarios.

#### Scenario: Execute smoke flow with stable locators
- **WHEN** desktop UI smoke tests are executed
- **THEN** element lookup uses stable AutomationId-first strategy and avoids fragile text-only selectors

### Requirement: Hosted runner scope limitation
GitHub-hosted Windows runners SHALL execute only deterministic and lightweight desktop UI smoke scenarios.

#### Scenario: PR pipeline execution
- **WHEN** desktop automation runs in GitHub-hosted Windows CI
- **THEN** only smoke-tagged deterministic scenarios are selected

### Requirement: Full UI suite on interactive self-hosted runner
The full desktop UI automation suite SHALL run on a self-hosted Windows runner with interactive desktop session.

#### Scenario: Nightly or pre-release full run
- **WHEN** the full desktop UI suite is triggered
- **THEN** execution occurs on self-hosted interactive Windows infrastructure and publishes actionable artifacts for failures
