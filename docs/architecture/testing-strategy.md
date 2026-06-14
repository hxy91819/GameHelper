# Testing Strategy

The test suite should protect observable behaviour before large refactors. Tests should target stable interfaces and seams rather than private implementation details.

## Required Gates

Run these before committing code changes:

```powershell
dotnet build GameHelper.sln
dotnet test GameHelper.sln
```

For narrow iterations, run the smallest relevant `dotnet test --filter ...` first, then finish with the full solution test command before committing.

## Coverage Priorities

- **Lifecycle symmetry**: services that start and stop external resources must leave state flags consistent after success, failure, and cleanup paths.
- **Process monitoring**: ETW/WMI/no-op adapters must preserve event semantics expected by core automation.
- **Automation behaviour**: matching, active session reference counting, HDR scheduling, and stop-event toggling must remain stable.
- **Persistence compatibility**: YAML config, `DataKey`, `entryId`, and CSV playtime history must survive roundtrips and migrations.
- **Shell workflows**: CLI commands, interactive shell flows, and file-drop forwarding should be covered through service-facing tests or smoke tests.
- **Documentation navigation**: local Markdown links must stay valid while docs are archived or rewritten.

## Windows-Specific Tests

- WMI and ETW integration tests are Windows-only.
- ETW tests that require administrator privileges should skip or self-report when not elevated.
- WinUI desktop smoke tests are opt-in through the existing test project settings.

## Known Gaps

- The top-level console host composition is still hard to test directly because `Program.cs` mixes host creation, command dispatch, single-instance handling, and Windows auto-start side effects.
- A future refactor should extract host construction and command dispatch into testable modules before adding broad console-host process smoke tests.
