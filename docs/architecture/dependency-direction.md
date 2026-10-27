# Dependency Direction Rules

GameHelper enforces the following project dependency direction:

- `GameHelper.ConsoleHost` and `GameHelper.Desktop` may depend on `GameHelper.Core` and `GameHelper.Infrastructure`. Desktop communicates with the running ConsoleHost through user/config-scoped named pipes; it does not reference the ConsoleHost project or duplicate automation.
- `GameHelper.Core` contains contracts and orchestration logic. It must not reference shell projects or UI frameworks. Configuration value validation (`AppConfigValidator`) and settings orchestration (`SettingsService`) live here and must not reference Infrastructure or YAML types.
- `GameHelper.Infrastructure` contains concrete adapters and may reference `GameHelper.Core` only. YAML persistence owns wire names, structure/type diagnostics and legacy normalization, and calls Core for value validation.

## Enforcement

- The automated test `LayerDependencyRulesTests` validates that:
  - `GameHelper.Core` has no project reference to shell projects such as `GameHelper.ConsoleHost`.
  - `GameHelper.Infrastructure` references only `GameHelper.Core`.
- Any change to project references must keep this direction:
  - `ConsoleHost -> Core` and `ConsoleHost -> Infrastructure -> Core`.
