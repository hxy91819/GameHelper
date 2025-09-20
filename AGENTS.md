# Agent Instructions for GameHelper

**Scope:** Applies to the entire repository.

## Repository Overview
- GameHelper is a .NET 8.0 solution that provides a console-first assistant for Windows gamers.
- The tool monitors running processes (via WMI or ETW), tracks per-game playtime, and exposes configuration and statistics through a CLI hosted in `GameHelper.ConsoleHost`.
- Supporting libraries live in `GameHelper.Core` (domain models and services) and `GameHelper.Infrastructure` (process monitoring, storage, and platform integrations).

## Project Layout
- `GameHelper.ConsoleHost/`: Entry-point console application exposing CLI commands.
- `GameHelper.Core/`: Core abstractions, domain logic, and shared utilities.
- `GameHelper.Infrastructure/`: Concrete infrastructure (process monitors, configuration, HDR control stubs, persistence helpers).
- `GameHelper.Tests/`: xUnit-based unit and integration tests covering the solution.
- `docs/`: User and contributor documentation.
- Root files such as `Directory.Build.props`, `debug.config.yml`, and `test-etw.config.yml` configure solution-wide settings and sample configs.

## Tooling & Environment
- Requires the .NET 8 SDK for development, testing, and publishing.
- Local testing assumes Windows for ETW coverage, but most unit tests should run cross-platform.
- Publishing instructions use `dotnet publish` targeting `win-x64`.

## Coding Conventions
- Follow the existing C# style in the repo: file-scoped namespaces, explicit access modifiers, and expression-bodied members when they improve clarity.
- Keep types small and purposeful—prefer extracting helpers over large monolithic classes or methods.
- Embrace async/await patterns where IO is involved and avoid blocking calls on async APIs.
- Validate inputs aggressively; throw meaningful exceptions or return early when encountering invalid state.
- Keep dependencies injectable to support testing (use the patterns already established in Core/Infrastructure).

## Testing Expectations
- Run `dotnet test GameHelper.sln` from the repository root after making code changes.
- If `dotnet test` cannot run in the execution environment (e.g., SDK not installed), report the failure reason in your final message.
- When adding functionality that depends on ETW/WMI, include unit tests that can run without elevated Windows permissions when possible.

## Documentation Updates
- Update `README.md` or relevant files under `docs/` whenever user-facing behavior, CLI arguments, or configuration formats change.
- Provide inline XML comments or markdown docs for newly introduced public APIs when it aids discoverability.

## PR Message Requirements
- Summaries should clearly state the functional changes and impacted components.
- List every command run in testing along with its outcome; note skipped or failing checks with rationale.
- Mention documentation updates (or the lack thereof) when applicable.

## Additional Notes
- Prefer extending this file with more specific directory-level instructions if a sub-area gains unique requirements.
- Keep configuration samples (`debug.config.yml`, `test-etw.config.yml`) in sync with any schema or behavioral changes made in code.
