# Testing Strategy

The test suite should protect observable behaviour before large refactors. Tests should target stable interfaces and seams rather than private implementation details.

## Required Gates

Run these before committing code changes:

```powershell
dotnet build GameHelper.sln
dotnet test GameHelper.sln
```

For narrow iterations, run the smallest relevant `dotnet test --filter ...` first, then finish with the full solution test command before committing.

For ConsoleHost publish or startup-path changes, also run:

```powershell
.\scripts\publish-console-smoke.ps1
```

CI runs the same script on Windows to publish ConsoleHost and execute the published `GameHelper.ConsoleHost.exe` against a temporary YAML config.

## Coverage Priorities

- **Lifecycle symmetry**: services that start and stop external resources must leave state flags consistent after success, failure, and cleanup paths.
- **Process monitoring**: ETW/WMI/no-op adapters must preserve event semantics expected by core automation.
- **Automation behaviour**: matching, active session reference counting, HDR scheduling, and stop-event toggling must remain stable.
- **Persistence compatibility**: YAML config, `DataKey`, compact game entries, and CSV playtime history must survive roundtrips and migrations.
- **Configuration atomicity**: failed and concurrent `IGameConfiguration.Change` calls must never partially commit or discard unrelated settings.
- **Executable Identity**: name-only and path identities must expose the same order-independent, read-only projections across catalog flows.
- **Process Observation contracts**: ETW, WMI, and no-op adapters must share policy, filtering, stop-event, lifecycle, and PID eviction semantics.
- **Playtime codec**: writer-to-reader roundtrips must cover commas, quotes, newlines, malformed rows, and legacy JSON import.
- **Shell workflows**: CLI commands, interactive shell flows, and file-drop forwarding should be covered through service-facing tests or smoke tests.
- **File-drop Intake**: a batch commits once, reloads automation once after success, and performs neither action for invalid input.
- **Command dispatch**: non-interactive CLI routing should be covered through `ConsoleCommandDispatcher` tests rather than process-level tests when startup side effects are not under test.
- **Published console artifact**: `scripts/publish-console-smoke.ps1` covers the process-level path for the published executable, embedded YAML validator resources, and config values requiring YAML quoting.
- **Documentation navigation**: local Markdown links must stay valid while docs are archived or rewritten.

## Windows-Specific Tests

- WMI and ETW integration tests are Windows-only.
- ETW tests that require administrator privileges should skip or self-report when not elevated.
- WinUI desktop smoke tests are opt-in through the existing test project settings.

## Known Gaps

- Console host construction is covered through `ConsoleHostBootstrapper`, and non-interactive command routing is covered through `ConsoleCommandDispatcher`.
- Console process-level smoke coverage is intentionally narrow: it exercises published `validate-config` and `config list` only, avoiding long-running monitor and interactive shell flows.
