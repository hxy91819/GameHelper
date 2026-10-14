# GameHelper PRD

This PRD records current product intent and scope. Completed story detail has been archived and should not be treated as the active requirements source.

## Current Product Intent

GameHelper helps Windows players track game playtime and manage game-specific automation from a CLI shell. A former WinUI desktop shell was removed from the repository and archived at tag `archive/winui`.

The maintained baseline is:

- Configuration-driven game catalog stored in `config.yml`.
- Stable `DataKey` as the single game identity for config, playtime records, and statistics joins.
- Compact game entries with one `executable` value instead of separate stored path/name fields.
- Process monitoring through ETW by default, with WMI/no-op alternatives where appropriate.
- Path-first game matching with metadata/name fallback for legacy configs.
- CSV-backed playtime history and statistics display.
- Optional foreground-game break reminders: cross-game accumulation, continuous-break reset, and repeat system notifications. Defaults are 40 minutes of play, 5 minutes of break, and 10 minutes between repeats; configuration is opt-in.
- Interactive CLI and file-drop workflows for adding or updating games.
- Optional per-game speed control through a user-installed Cheat Engine, configured entirely in `config.yml` without hand-maintained tables.

## Out Of Scope For The Baseline

- Bundling Cheat Engine or implementing a native speedhack/injector inside GameHelper.
- Monitor-layer pre-filtering as the primary correctness mechanism.
- Full product requirements copied from archived story cards.

## Related Documents

- Product goals: [1-goals-and-background.md](./1-goals-and-background.md)
- Current architecture: [../architecture/index.md](../architecture/index.md)
- CLI usage: [../guides/cli.md](../guides/cli.md)
- Verification & deployment workflow: [../guides/verify-and-deploy-workflow.md](../guides/verify-and-deploy-workflow.md)
- Archived story detail: [../archives/prd/2-epics-and-stories.md](../archives/prd/2-epics-and-stories.md)
