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
- `GameHelper.Core` owns models, contracts, orchestration, matching, settings, statistics, and monitor lifecycle coordination.
- `GameHelper.Infrastructure` owns concrete adapters for process monitors, YAML/CSV persistence, HDR control, Steam resolution, and Windows startup integration.
- Dependency direction is enforced by tests and documented in [Dependency Direction Rules](./dependency-direction.md).

## Primary Runtime Flows

- **Configuration**: shells call core catalog/settings services; infrastructure persists `config.yml`.
- **Monitoring**: `MonitorControlService` starts the process monitor and `GameAutomationService` as a lifecycle pair.
- **Automation**: process events are matched by path first, metadata second; active game sessions drive playtime tracking and HDR scheduling.
- **Statistics**: playtime records are read from local files and joined to current config by stable `DataKey`.
- **File drop**: duplicate app launches forward dropped files to the running console process, which updates config and reloads automation.

## Key Modules And Seams

- **Process monitor seam**: `IProcessMonitor` lets ETW, WMI, and no-op adapters satisfy the same core monitoring interface.
- **Configuration seam**: `IConfigProvider` and `IAppConfigProvider` isolate YAML/JSON storage from core services.
- **Playtime seam**: `IPlayTimeService` records sessions; `IPlaytimeSnapshotProvider` reads historical records for statistics.
- **Automation module**: `GameAutomationService` coordinates matching, session tracking, playtime, HDR, and stop-event control.
- **Shell modules**: CLI commands and WinUI view models should call core services rather than duplicate domain logic.

## Persistence Model

- `config.yml` is the primary configuration file under `%AppData%\GameHelper\`.
- `entryId` identifies a configuration row; `dataKey` is the stable statistics key written to playtime records.
- `playtime.csv` is the current playtime history format; JSON exists only for legacy migration compatibility.
- Configuration writes must preserve global app settings when replacing the game list.

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
