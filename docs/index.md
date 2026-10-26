# GameHelper Docs

`docs/` is the source of truth for current product behaviour, repository design, and long-lived engineering standards. Historical plans, reviews, and implementation notes belong under `docs/archives/`.

## Active Documents

- [Architecture](./architecture/index.md): current repository design, module seams, dependency direction, and testing strategy.
- [CLI Guide](./guides/cli.md): user-facing command-line usage.
- [Game Speed](./guides/game-speed.md): per-game Cheat Engine speed configuration and safety boundary.
- [Terminal Host](./guides/terminal-host.md): why the console window may fall back to conhost, and how the program moves itself into Windows Terminal.
- [Break Reminder](./guides/break-reminder.md): foreground gaming time, continuous breaks, and system notifications.
- [PRD](./prd/index.md): product goals and current scope.
- [Refactor Safety Plan](./refactor-safety.md): decisions and test gaps for the current large refactor.
- [Plans](./plans/index.md): only active, not-yet-landed proposals.
- [Steward Workbook](./steward/desktop-and-capabilities.md): active roadmap, decisions, and user todos for ongoing multi-stage delivery.
- [Archives](./archives/index.md): historical material retained for traceability, not current truth.

## Maintenance Rules

- Keep active docs short and stable; prefer design intent, ownership, and constraints over class-by-class inventories.
- Do not duplicate implementation details that are easy to read from code and likely to drift during refactors.
- Move completed plans, review rounds, sprint notes, and stale TODOs to `docs/archives/`.
- Update `README.md` and `docs/guides/cli.md` for user-visible CLI or configuration changes.
- Update `docs/architecture/` for durable changes to module seams, dependency direction, persistence, lifecycle, or testing policy.
