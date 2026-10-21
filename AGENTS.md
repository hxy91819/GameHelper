# Agent Instructions for GameHelper

**Scope:** Applies to the entire repository.

## Role Of This File
- `AGENTS.md` only contains rules that must be followed in every development task.
- Long-lived engineering conventions should live under `docs/`, especially `docs/architecture/`.
- User-facing usage belongs in `README.md`, not here.

## Repository Overview
- GameHelper is a .NET 8.0 solution for Windows game monitoring and playtime management.
- Main shell is `GameHelper.ConsoleHost`; the former `GameHelper.WinUI` shell was removed and archived at tag `archive/winui`.
- Shared logic lives in `GameHelper.Core` and `GameHelper.Infrastructure`.

## Project Layout
- `GameHelper.ConsoleHost/`: Entry-point console application exposing CLI commands.
- `GameHelper.Core/`: Core abstractions, domain logic, and shared utilities.
- `GameHelper.Infrastructure/`: Concrete infrastructure (process monitors, configuration, HDR control stubs, persistence helpers).
- `GameHelper.Tests/`: xUnit-based unit and integration tests covering the solution.
- `docs/`: Project behavior, design, and development standards.
- Root files such as `Directory.Build.props`, `debug.config.yml`, and `test-etw.config.yml` configure solution-wide settings and sample configs.

## Tooling & Environment
- Requires the .NET 8 SDK for development, testing, and publishing. Install the SDK in the container when it is missing before running solution commands.
- Windows is the only supported platform. Develop, build, test, and run CI on Windows; Linux/macOS compatibility is outside the project scope.
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
- Tests must not depend on the wall clock or fixed calendar dates (a green run can fail after midnight). Inject `TimeProvider` or use relative times; before closing a task, scan tests for `DateTime.Now`/`Today` used in assertions.

## Verification & Deployment Workflow (mandatory after every development task)
- After development is complete, follow `docs/guides/verify-and-deploy-workflow.md` **in full**: build + test, publish, on-machine verification with an isolated test instance, then deploy to the local publish directory.
- **Direct update is a standing, pre-authorized policy.** The user has granted blanket authorization for "update directly after finishing changes": end every development task with `powershell -File scripts\verify-on-machine.ps1 -SkipVerify -Restart`, which stops the running GameHelper instance, cleans up orphaned ETW sessions, atomically swaps the publish directory, relaunches, and confirms the new PID stays alive — without asking per task. In-flight game sessions survive the restart via the in-flight snapshot recovery mechanism (see the workflow doc's "跨重启会话续算" section).
- **The continuity mechanism is the safety net for direct update.** Only fall back to the conservative path (keep the retained build, let the user decide when to restart) when continuity cannot work — e.g., the running instance predates the mechanism (before 2026-10-17), or the recovery snapshot is unusable while a monitored game is active.
- Test instances must isolate data via `GAMEHELPER_DATA_DIR` (sandboxed data dir) and skip the single-instance mutex via `GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE=1` so the user's instance is never affected.
- Deploy targets the shortcut path `GameHelper.ConsoleHost/bin/Release/net8.0-windows/win-x64/publish/`. Deployment itself is an all-or-nothing directory swap with automatic rollback — never copy files individually into a publish directory (mixing framework-dependent and self-contained layouts breaks the next launch).
- One-shot command: `powershell -File scripts\verify-on-machine.ps1` (append `-SkipVerify` to deploy only, append `-SkipVerify -Restart` for the standard direct-update closeout).
- Every completed feature or fix must update the local publish directory and restart the runtime without a console through the WPF background entry. Verify monitoring readiness and library IPC; see the workflow for acceptance checks. Visible legacy Console UI tests are opt-in.
- The deploy writes `deploy-manifest.json` (HEAD SHA, branch, dirty files) into the publish directory and warns when the working tree is dirty. Use `-RequireClean` when the deployed binary must equal a commit; never let unrelated work-in-progress ride along silently.
- Post-launch health probes (no console, process integrity level, data-directory write, ETW session when `monitor: ETW`, and library IPC) run inside the deploy and roll back on failure. A surviving PID alone is not a completion signal.
- Environment-level failures need a baseline, not a guess: reproduce against `git archive HEAD` in a temporary directory (never stash — the workspace is shared) and cite concrete evidence such as the process integrity level, ACLs or file labels. Do not close a task with "possibly antivirus".

## Git: Commit & Push After Every Fully Completed Feature
- "Fully completed" means the task closed the full loop: build + test green, docs updated, and the direct-update deploy succeeded. When a feature (or a coherent batch of completed features) reaches that state, **commit and push immediately** — do not leave completed work uncommitted waiting for further instruction.
- Stage only the files belonging to the delivered work (check `git status`; do not sweep unrelated WIP into the commit). Commit messages are concise Chinese subject lines summarizing function + impacted components, consistent with existing history, optionally with a short body listing verification commands and outcomes (mirroring the PR message requirements above).
- Push directly to the current branch (main). If the push is rejected (remote ahead), pull/rebase and retry; only skip committing when the deployment itself failed — an undeployed feature follows the normal workflow instead.
- Prefer several small, individually verifiable commits over one large feature commit; a change touching dozens of files should be split by capability so it can be reviewed and rolled back.

## Documentation Updates
- Update `README.md` or relevant files under `docs/` whenever user-facing behavior, CLI arguments, or configuration formats change.
- Provide inline XML comments or markdown docs for newly introduced public APIs when it aids discoverability.

## Reference Docs
- Architecture and standards: `docs/architecture/index.md`
- Product behavior and scope: `docs/prd/index.md`
- CLI usage: `docs/guides/cli.md`
- Verification & deployment workflow: `docs/guides/verify-and-deploy-workflow.md`

## PR Message Requirements
- Summaries should clearly state the functional changes and impacted components.
- List every command run in testing along with its outcome; note skipped or failing checks with rationale.
- Mention documentation updates (or the lack thereof) when applicable.

## Additional Notes
- Prefer extending this file with more specific directory-level instructions if a sub-area gains unique requirements.
- Keep configuration samples (`debug.config.yml`, `test-etw.config.yml`) in sync with any schema or behavioral changes made in code.

## Agent skills

### Issue tracker

Issues are tracked as GitHub issues on `hxy91819/GameHelper` via the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

Uses the five canonical triage labels (`needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`). See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: `CONTEXT.md` at the repo root plus `docs/adr/`. See `docs/agents/domain.md`.
