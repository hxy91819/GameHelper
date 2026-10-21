# Dependency Direction Rules

GameHelper enforces the following project dependency direction:

- `GameHelper.ConsoleHost` is the shell entrypoint. It may depend on `GameHelper.Core` and `GameHelper.Infrastructure`.
- `GameHelper.Core` contains contracts and orchestration logic. It must not reference shell projects or UI frameworks.
- `GameHelper.Infrastructure` contains concrete adapters and may reference `GameHelper.Core` only.

## Enforcement

- The automated test `LayerDependencyRulesTests` validates that:
  - `GameHelper.Core` has no project reference to shell projects such as `GameHelper.ConsoleHost`.
  - `GameHelper.Infrastructure` references only `GameHelper.Core`.
- Any change to project references must keep this direction:
  - `ConsoleHost -> Core` and `ConsoleHost -> Infrastructure -> Core`.
