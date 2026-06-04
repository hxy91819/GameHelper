# Agent Instructions for GameHelper

**Scope:** Applies to the entire repository.

## Role Of This File
- `AGENTS.md` only contains rules that must be followed in every development task.
- Long-lived engineering conventions should live under `docs/`, especially `docs/architecture/`.
- User-facing usage belongs in `README.md`, not here.

## Repository Overview
- GameHelper is a .NET 8.0 solution for Windows game monitoring and playtime management.
- Main shells are `GameHelper.ConsoleHost` and `GameHelper.WinUI`.
- Shared logic lives in `GameHelper.Core` and `GameHelper.Infrastructure`.

## Project Layout
- `GameHelper.ConsoleHost/`: Entry-point console application exposing CLI commands.
- `GameHelper.WinUI/`: WinUI shell that reuses the same core services.
- `GameHelper.Core/`: Core abstractions, domain logic, and shared utilities.
- `GameHelper.Infrastructure/`: Concrete infrastructure (process monitors, configuration, HDR control stubs, persistence helpers).
- `GameHelper.Tests/`: xUnit-based unit and integration tests covering the solution.
- `docs/`: Project behavior, design, and development standards.
- Root files such as `Directory.Build.props`, `debug.config.yml`, and `test-etw.config.yml` configure solution-wide settings and sample configs.

## Tooling & Environment
- Requires the .NET 8 SDK for development, testing, and publishing. Install the SDK in the container when it is missing before running solution commands.
- Local testing assumes Windows for ETW coverage, but most unit tests should run cross-platform.
- Publishing instructions use `dotnet publish` targeting `win-x64`.

## Communication
- 默认使用中文回复用户的请求，除非用户特别指定其它语言。

## Always-Follow Rules
- Treat `docs/` as the source of truth for project behavior, design, and long-lived engineering standards.
- If behavior or configuration changes, update the relevant docs under `docs/` and the user-facing summary in `README.md`.
- Prefer adding detailed conventions to directory docs instead of expanding this file, unless the rule must be obeyed in every task.

## Engineering Conventions

### Service Lifecycle & State Consistency
- All services implementing `Start()` / `Stop()` must maintain **state symmetry**:
  - `Start()` succeeds → `IsRunning = true`.
  - `Stop()` succeeds or best-effort cleanup completes → `IsRunning = false`.
  - **Do not return from an exception path without updating the state flag**; after catch-block cleanup, synchronize the flag to the actual service state.
- Start and Stop must be designed as pairs: if `Start()` contains rollback logic (stopping already-started sub-services on failure), `Stop()` must contain symmetric best-effort cleanup logic.

### Resource Management
- Any type holding an `IDisposable` field must **dispose the old instance before reassigning the field** (or ensure it was already deterministically released).
- Retry paths must not skip cleanup of the old resource; always call `SafeCleanup()` or equivalent **before** creating the new resource.
- Use the `SafeCleanup()` pattern: wrap every individual cleanup step in its own `try/catch` so that a single-point failure does not block subsequent cleanup steps.

### Concurrency Safety
- Any mutable state accessed by **background threads** (ETW callback threads, timer threads, event handlers) must use thread-safe collections (`ConcurrentDictionary`, `ConcurrentQueue`, etc.) or explicit synchronization primitives.
- **Do not use `ContainsKey` + `Add` combinations in concurrent write paths**; prefer atomic operations such as `TryAdd`, `TryRemove`, or `TryUpdate`.
- Caches keyed by OS-recycled identifiers (PID, handle) must assume **key reuse**; stale entries must be removed promptly.

### Caching & Feature Toggles
- When a feature toggle affects a data **write path**, simultaneously review the impact on **read paths and eviction paths**.
- Cache invalidation must be bound to feature lifecycle: evaluate whether the cache needs to be actively cleared or write-restricted when the feature is disabled.
- Cache keys derived from recycled OS resources require timely eviction; do not assume the key space is unique for the lifetime of the cache.

## Testing Expectations
- Run `dotnet build GameHelper.sln` from the repository root and ensure it completes without compilation errors before submitting any changes.
- Run `dotnet test GameHelper.sln` from the repository root after making code changes.
- If `dotnet test` cannot run in the execution environment (e.g., SDK not installed), report the failure reason in your final message.
- When adding functionality that depends on ETW/WMI, include unit tests that can run without elevated Windows permissions when possible.

## Documentation Updates
- Update `README.md` or relevant files under `docs/` whenever user-facing behavior, CLI arguments, or configuration formats change.
- Provide inline XML comments or markdown docs for newly introduced public APIs when it aids discoverability.

## Reference Docs
- Architecture and standards: `docs/architecture/index.md`
- Product behavior and scope: `docs/prd/index.md`
- CLI usage: `docs/guides/cli.md`

## PR Message Requirements
- Summaries should clearly state the functional changes and impacted components.
- List every command run in testing along with its outcome; note skipped or failing checks with rationale.
- Mention documentation updates (or the lack thereof) when applicable.

## Additional Notes
- Prefer extending this file with more specific directory-level instructions if a sub-area gains unique requirements.
- Keep configuration samples (`debug.config.yml`, `test-etw.config.yml`) in sync with any schema or behavioral changes made in code.
