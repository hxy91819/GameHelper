# GameHelper Architecture

GameHelper is a Windows-focused .NET 8 application for game process monitoring, playtime tracking, and configuration-driven automation. The repository has two shells over the same core behaviour: a console host and a WinUI desktop shell.

## System Shape

```text
GameHelper.ConsoleHost    GameHelper.WinUI
          |                    |
          +--------+-----------+
                   |
             GameHelper.Core
                   |
         GameHelper.Infrastructure
```

- Shells own presentation, command routing, and desktop-specific user experience.
- `GameHelper.Core` owns models, contracts, orchestration, catalog matching policy, settings, statistics, and monitor lifecycle coordination.
- `GameHelper.Infrastructure` owns concrete adapters for process monitors, YAML/CSV persistence, HDR control, Steam resolution, and Windows startup integration.
- Dependency direction is enforced by tests and documented in [Dependency Direction Rules](./dependency-direction.md).

## Primary Runtime Flows

- **Configuration**: shells call core catalog/settings services; infrastructure persists the compact `config.yml` shape (`monitor`, `startup`, and `games`).
- **Monitoring**: `MonitorControlService` starts the process monitor and `GameAutomationService` as a lifecycle pair. Runtime process filters are derived from enabled game candidates and refreshed on config reload. ETW and WMI both keep expensive event enrichment behind this candidate-name gate. The ETW monitor embeds its owner PID in the session name and self-recovers (with PID path-cache preserved) when its kernel session is stopped externally — without this, a dead session left the instance silently deaf and active playtime sessions were lost. Stale-session cleanup only removes sessions whose owner process is gone.
- **Automation**: process events first pass a cheap executable-name candidate gate; full path resolution is lazy and only used for configured candidates that need path matching or disambiguation. WMI process detail lookup follows the same rule and is not performed for non-candidate start events. Metadata/fuzzy matching is limited to that candidate set. Active game sessions drive playtime tracking and HDR scheduling. HDR scheduling only rolls back HDR changes made by GameHelper itself.
- **Statistics**: playtime records are read from local files and joined to current config by stable `DataKey`.
- **Stats sync**: `SyncService` aggregates playtime into a report + daily CSV and pushes it through an upload channel (GitHub via local git.exe or REST API). A console background loop checks interval/mtime gates every 15 minutes; session-end paths perform no extra disk writes.
- **File drop**: duplicate app launches forward dropped files to the running console process, which updates config and reloads automation.

## Key Modules And Seams

- **Process Observation seam**: `IProcessMonitor` lets ETW, WMI, and no-op adapters satisfy one interface. `ProcessObservationPolicy` atomically configures candidate names and stop-event observation, so core automation never probes optional adapter capabilities. `IProcessPathResolver` keeps expensive live path lookup out of monitor callbacks.
- **Game Configuration seam**: `IGameConfiguration` owns the complete `AppConfig` document through `Read` and atomic `Change`. YAML is the runtime adapter; JSON is a legacy conversion adapter. Catalog and settings changes cannot overwrite unrelated document fields.
- **Executable Identity and Game Catalog Intake**: `ExecutableIdentity` is one immutable stored value with read-only name/path projections. `IGameCatalogService` exposes list, preview, intake, batch intake, update, and remove outcomes; duplicate detection, `DataKey` allocation, and commit-time validation stay inside the module.
- **Playtime seam**: `IPlayTimeService` records sessions; `IPlaytimeSnapshotProvider` reads historical snapshots for statistics and session summaries. Both current adapters share one internal CSV codec for schema, escaping, parsing, and legacy JSON migration.
- **File-drop Intake module**: Console file drops are validated, resolved, batch-committed through Game Catalog Intake, and followed by one automation reload. It uses explicit dependencies and does not construct storage or catalog implementations itself.
- **Automation module**: `GameAutomationService` coordinates matching, session tracking, playtime, HDR, and stop-event control.
- **Stats sync module**: `ISyncService`/`SyncService` own interval debouncing, mtime-based dirty checks, content hashing, and state persistence; `IStatsUploadChannel` implementations (git.exe runner and GitHub Git Data API client, both in Infrastructure) only write inside the configured remote subdirectory.
- **Shell modules**: CLI commands and WinUI view models should call core services rather than duplicate domain logic; interactive shell composition lives in a dedicated module so routing stays separate from module construction.

## Persistence Model

- `config.yml` is the primary configuration file under `%AppData%\GameHelper\`.
- `dataKey` is the single stable game identity in configuration and the statistics key written to playtime records.
- Each game stores one `executable` value. It can be a full path for path-first matching or a process file name for name-only matching; `ExecutableIdentity` derives read-only path and name views from that value.
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
- [WinUI Shell Design](./ui-shell-design.md)

Historical brownfield architecture chapters are archived under `docs/archives/architecture/brownfield/`.
