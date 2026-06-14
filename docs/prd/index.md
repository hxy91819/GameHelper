# GameHelper PRD

This PRD records current product intent and scope. Completed story detail has been archived and should not be treated as the active requirements source.

## Current Product Intent

GameHelper helps Windows players track game playtime and manage game-specific automation from either a CLI shell or a WinUI shell.

The maintained baseline is:

- Configuration-driven game catalog stored in `config.yml`.
- Stable `DataKey` for playtime records and statistics joins.
- Process monitoring through ETW by default, with WMI/no-op alternatives where appropriate.
- Path-first game matching with metadata/name fallback for legacy configs.
- CSV-backed playtime history and statistics display.
- Interactive CLI and file-drop workflows for adding or updating games.

## Out Of Scope For The Baseline

- Speed-control or game process mutation features.
- Monitor-layer pre-filtering as the primary correctness mechanism.
- Full product requirements copied from archived story cards.

## Related Documents

- Product goals: [1-goals-and-background.md](./1-goals-and-background.md)
- Current architecture: [../architecture/index.md](../architecture/index.md)
- CLI usage: [../guides/cli.md](../guides/cli.md)
- Archived story detail: [../archives/prd/2-epics-and-stories.md](../archives/prd/2-epics-and-stories.md)
