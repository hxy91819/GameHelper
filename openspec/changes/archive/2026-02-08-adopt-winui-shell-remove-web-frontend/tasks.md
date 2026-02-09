## 1. Foundation and Architecture Guardrails

- [x] 1.1 Create WinUI shell project and add it to `GameHelper.sln` with .NET 8 baseline configuration.
- [x] 1.2 Define and document dependency direction rules (`UI/CLI -> Core -> Infrastructure`) and enforce project reference boundaries.
- [x] 1.3 Introduce/normalize Core application service contracts for shell-facing use-cases (settings, games, stats, monitor controls).
- [x] 1.4 Ensure all Windows-specific integrations are consumed through Core abstractions and injected implementations.

## 2. WinUI Shell Delivery (Parity-first)

- [x] 2.1 Implement shell scaffolding (app startup, navigation, lifecycle hooks) using MVVM + DI.
- [x] 2.2 Implement settings workflow in WinUI shell via Core contracts with persistence verification.
- [x] 2.3 Implement games management workflow in WinUI shell via Core contracts (list/add/edit/delete/toggle).
- [x] 2.4 Implement statistics workflow in WinUI shell via Core contracts (overview + detail journeys).
- [x] 2.5 Add stable AutomationId identifiers for key interactive controls in all smoke-covered views.

## 3. CLI and Transitional Compatibility

- [x] 3.1 Keep CLI shell functional as optional fallback entrypoint and verify shared Core behavior parity.
- [x] 3.2 Mark `--web/--port` path as deprecated in transition phase with clear user-facing guidance.
- [x] 3.3 Define and implement cutover criteria for removing product runtime web entry path.

## 4. Remove Web/Next.js Runtime Path

- [x] 4.1 Remove `GameHelper.Web` runtime dependency and related build/publish wiring from product pipeline.
- [x] 4.2 Remove `GameHelper.ConsoleHost/Api/*` product runtime responsibilities once WinUI parity criteria are met.
- [x] 4.3 Remove obsolete web-specific CLI arguments and related code paths after cutover validation.
- [x] 4.4 Clean up obsolete frontend docs/config references and ensure no stale runtime links remain.

## 5. Testing by Layer

- [x] 5.1 Add/adjust Core unit tests for updated contracts and domain orchestration paths (cross-platform runnable).
- [x] 5.2 Add integration tests for Infrastructure adapters via contract-focused scenarios and deterministic fixtures.
- [x] 5.3 Add WinUI shell viewmodel-level tests for state transitions without UI automation dependency.
- [x] 5.4 Add FlaUI smoke tests for critical desktop paths using AutomationId-first locators.
- [x] 5.5 Add flakiness controls for desktop smoke tests (timeouts, retries policy, test isolation, artifact capture).

## 6. CI and Release Pipeline

- [x] 6.1 Update CI to keep cross-platform logic tests on hosted runners while adding Windows desktop smoke lane.
- [x] 6.2 Configure desktop smoke execution scope on GitHub-hosted Windows to deterministic lightweight set only.
- [x] 6.3 Prepare/enable self-hosted interactive Windows runner workflow for full desktop UI suite.
- [x] 6.4 Update release pipeline to produce WinUI-centric Windows artifacts and validate rollback channel availability.

## 7. Documentation and Operational Readiness

- [x] 7.1 Update README architecture/runtime sections to reflect WinUI-first model and Web path retirement.
- [x] 7.2 Document developer workflow split: non-Windows core testing vs Windows shell/system integration testing.
- [x] 7.3 Document UI automation strategy (FlaUI-first, locator conventions, runner strategy, troubleshooting).
- [x] 7.4 Add migration notes for users moving from web runtime path to native shell workflows.
