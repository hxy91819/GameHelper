# GameHelper Architecture

GameHelper is a Windows-focused .NET 8 application for game process monitoring, playtime tracking, and configuration-driven automation. The repository has one shell over the core behaviour: the console host. A former WinUI desktop shell was removed and archived at tag `archive/winui`.

## System Shape

```text
                  GameHelper.ConsoleHost
                       |              |
                       v              v
              GameHelper.Core <-- GameHelper.Infrastructure
```

- Shells own presentation, command routing, and desktop-specific user experience.
- `prototypes/GameHelper.WpfPrototype` is a separate, disposable read-only WPF layout experiment; it is not a production shell and does not own automation or startup. See [its scope](../guides/wpf-prototype.md).
- `GameHelper.Core` owns models, contracts, orchestration, catalog matching policy, settings, statistics, and monitor lifecycle coordination.
- `GameHelper.Infrastructure` owns concrete adapters for process monitors, YAML/CSV persistence, HDR control, DDC/CI monitor preset control, Steam resolution, and Windows startup integration.
- Dependency direction is enforced by tests and documented in [Dependency Direction Rules](./dependency-direction.md).

## Primary Runtime Flows

- **Configuration**: shells call core catalog/settings services; infrastructure persists the compact `config.yml` shape (`monitor`, `startup`, and `games`).
- **Monitoring**: `MonitorControlService` starts the process monitor and `GameAutomationService` as a lifecycle pair. Runtime process filters are derived from enabled game candidates and refreshed on config reload. ETW and WMI both keep expensive event enrichment behind this candidate-name gate. ETW sessions embed their owner PID and recover after external loss. Recovery retains observed process identities, reconciles missed exits and PID reuse, and emits stop notifications to release playtime references. Cleanup preserves live, legacy, and unknown-owner sessions.
- **Automation**: process events first pass a cheap executable-name candidate gate; full path resolution is lazy and only used for configured candidates that need path matching or disambiguation. WMI process detail lookup follows the same rule and is not performed for non-candidate start events. Metadata/fuzzy matching is limited to that candidate set. Active game sessions drive playtime tracking, HDR scheduling, optional first-process speed application, and monitor preset-mode switching: during the configured daylight window an active game requests the game preset, everything else requests the comfort preset, both applied read-then-write over DDC/CI (stateless assertion with a self-healing tick). HDR scheduling only rolls back HDR changes made by GameHelper itself. Speed control uses the `IGameSpeedController` seam; the Windows adapter generates a PID-specific transient Cheat Engine table and launches the configured user installation.
- **Statistics**: playtime records are read from local files and joined to current config by stable `DataKey`.
- **Stats sync**: `SyncService` aggregates playtime into a report + daily CSV and pushes it through an upload channel (GitHub via local git.exe or REST API). A console background loop checks interval/mtime gates every 15 minutes; session-end paths perform no extra disk writes.
- **File drop**: duplicate app launches forward dropped files to the running console process, which updates config and reloads automation.

## Key Modules And Seams

Foreground break reminders have a separate in-memory clock from persisted process-session playtime. `GameProcessMatcher` provides read-only classification shared with automation. `BreakReminderService` uses `TimeProvider`, a foreground activity provider, and a notification seam; automation start/stop/reload drives its lifecycle. Sampling is serialized, matching caches include process creation identity and expire on configuration reload, and unavailable observations cannot establish a completed break. The Windows adapter checks the input desktop and compares uptime with unbiased interrupt time to exclude suspend intervals. Native system notifications own a background message loop and transient tray icons, released on reset and shutdown. ConsoleHost registers these services when it composes automation. See [Break Reminder](../guides/break-reminder.md).

ETW recovery owns a serialized lifecycle and process identity reconciliation, with an internal OS session/query boundary for deterministic testing. Adopted in-flight processes enter the same observation table before ETW starts. Durable snapshots include every process and its creation identity, refresh every ten seconds, and preserve completed rows when history writes fail. Monitor preset updates serialize separately from session bookkeeping, confirm writes by hardware readback, retry failures, and immediately re-evaluate configuration reloads. Sandboxed hosts never register system auto-start, and one-shot CLI commands leave it unchanged. See [ETW recovery behaviour](../guides/etw-recovery.md) for timing, cancellation, isolation, and known observation gaps.

- **Interactive startup entry**: auto-start and desktop entries run `GameHelper.ConsoleHost` with a normal visible window. The passive-launcher direction is retired; see [the decision](../adr/0001-normal-interactive-startup.md) and [startup behaviour](../guides/terminal-host.md).
- **Process Observation seam**: `IProcessMonitor` lets ETW, WMI, and no-op adapters satisfy one interface. ETW subscribes before capturing already running candidates, so games started before monitoring still enter automation; adopted PIDs are not emitted again, and queued starts older than the capture are ignored. `ProcessObservationPolicy` atomically configures candidate names and stop-event observation, so core automation never probes optional adapter capabilities. `IProcessPathResolver` keeps expensive live path lookup out of monitor callbacks; the ETW adapter also uses it at the gate to recover identity when non-ASCII image names garble in the ANSI kernel payload.
- **Game Configuration seam**: `IGameConfiguration` owns the complete `AppConfig` document through `Read` and atomic `Change`. YAML is the runtime adapter; JSON is a legacy conversion adapter. Catalog and settings changes cannot overwrite unrelated document fields. An optional `IConfigWatcher` seam (Windows adapter: `ConfigFileWatcher`) debounces file-system events on the config path and triggers `GameAutomationService.ReloadConfig`, so manual external edits hot-reload just like file-drop intake; the watcher skips absent-file windows and limits a failed reload to one retry, and monitor type plus sync-loop settings stay startup-bound.
- **Executable Identity and Game Catalog Intake**: `ExecutableIdentity` is one immutable stored value with read-only name/path projections. `IGameCatalogService` exposes list, preview, intake, batch intake, update, and remove outcomes; duplicate detection, `DataKey` allocation, and commit-time validation stay inside the module.
- **Playtime seam**: `IPlayTimeService` records sessions; `IPlaytimeSnapshotProvider` reads historical snapshots for statistics and session summaries. Both current adapters share one internal CSV codec for schema, escaping, parsing, and legacy JSON migration.
- **File-drop Intake module**: Console file drops are validated, resolved, batch-committed through Game Catalog Intake, and followed by one automation reload. It uses explicit dependencies and does not construct storage or catalog implementations itself.
- **Automation module**: `GameAutomationService` coordinates matching, session tracking, playtime, HDR, optional speed control, monitor preset modes, and stop-event control.
- **Game Speed Attachment module**: callers only request `ApplySpeed(pid, multiplier)` through `IGameSpeedController`; its result means an attach was initiated, not that the game's clock was verified. The CE adapter owns per-request table identity, atomic file publication, expiry cleanup, and process launch. Each request gets a unique table so queued or concurrent requests for one PID cannot overwrite each other. The private embedded Lua implementation owns Unity hook reuse and CE exit monitoring; tests exercise the same generated table through the controller interface, including opt-in execution in real CE.
- **Interactive launch module**: deployment and published startup tests directly load the same side-effect-free `scripts/start-checked-instance.ps1`. It owns real ConsoleHost PID adoption after terminal relocation, startup failure handling, and console visibility verification. Tests substitute OS commands at that seam rather than rewriting implementation text; there is no passive-launcher branch or focus-preservation contract.
- **Monitor preset mode seam**: `IMonitorModeController` lets no-op and real DDC/CI adapters satisfy one contract. `MonitorModePlan` is an immutable validated settings snapshot (ordered DDC/CI recipes plus a `DaylightEvaluator` over solar sunrise/sunset or fixed windows); the stateless `MonitorModeScheduler` reads the current VCP state and applies the recipe only on mismatch. DDC/CI bus I/O stays outside the automation state lock so a slow monitor cannot stall session bookkeeping. Daylight coordinates come from the manual config (highest priority), the Windows location service via `IUserLocationSource` when `location: auto` (detected once per start and cached through `ILocationCacheStore`, refreshed at most daily, falling back to fixed windows on failure), or the fixed time window. Defaults are calibrated for the Dell U2720QM and overridable per monitor. See [Monitor preset auto-switching](../guides/monitor-mode.md).
- **In-flight session continuity seam**: `IInFlightSessionStore` (atomic temp+rename JSON snapshot at `%AppData%\GameHelper\inflight-sessions.json`) persists live game sessions on every tracking-state change, so force-killed or crashed instances still recover them. On startup `GameAutomationService` re-adopts sessions whose process identity still matches (`IProcessIdentityProbe` verifies the recovered pid's image path against the persisted identity, guarding against PID reuse) and resumes them from the original start time through `IPlayTimeService.ResumeTracking`; sessions whose game died during downtime are closed at the last-known-alive timestamp via `WriteRecoveredSession`, with the recovered span capped at 12 hours. Manual stop keeps the immediate-flush semantics and clears the snapshot. This is the safety net that makes the direct-update deploy policy safe.
- **Stats sync module**: `ISyncService`/`SyncService` own interval debouncing, mtime-based dirty checks, content hashing, and state persistence; `IStatsUploadChannel` implementations (git.exe runner and GitHub Git Data API client, both in Infrastructure) only write inside the configured remote subdirectory.
- **Shell modules**: CLI commands should call core services rather than duplicate domain logic; interactive shell composition lives in a dedicated module so routing stays separate from module construction.

Monitor preset automation calls `IHdrController.TryGetEnabled` before any DDC availability probe or preset read/write. The Windows adapter refreshes DisplayConfig state under its HDR lock, including manual changes; enabled or unknown HDR suspends the entire preset update. Suspension invalidates the change gate so the first confirmed SDR update re-evaluates even unchanged game/daylight state. DisplayConfig failures are unknown, not a confirmed SDR result.

## Persistence Model

- `config.yml` is the primary configuration file under `%AppData%\GameHelper\`.
- `dataKey` is the single stable game identity in configuration and the statistics key written to playtime records.
- Each game stores one `executable` value. It can be a full path for path-first matching or a process file name for name-only matching; `ExecutableIdentity` derives read-only path and name views from that value. An optional positive `speed` value requests Cheat Engine speed control on first process activation; the `speedAttach` policy (default `auto`) defers that attach behind the mod loader's chainloader completion marker when a loader is detected beside the executable, falling back to a timed attach and guarding against PID reuse during the wait. Game exits shortly after an attach are correlated with WER crash reports in the automation log.
- `playtime.csv` is the current playtime history format; JSON exists only for legacy migration compatibility.
- `sync-state.json` records stats-sync bookkeeping (last success/attempt, content hash, target identity); it is written only after an upload attempt. The git upload method keeps its throwaway clone under `%AppData%\GameHelper\sync\`.
- Configuration changes reload the latest complete document, preserve unrelated fields, and commit through a temporary-file replacement. A failed change is not persisted.

## Testing Strategy

Use [Testing Strategy](./testing-strategy.md) as the live safety net definition for refactors. At minimum, run:

```powershell
dotnet build GameHelper.sln
dotnet test GameHelper.sln
```

## Supporting Standards

- [Coding Standards](./coding-standards.md)
- [Encoding](./encoding.md)
- [Tech Stack](./tech-stack.md)
- [Dependency Direction Rules](./dependency-direction.md)

Historical brownfield architecture chapters are archived under `docs/archives/architecture/brownfield/`; the retired WinUI shell design lives at `docs/archives/architecture/ui-shell-design.md`.
