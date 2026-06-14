# Refactor Safety Plan

This document records the working decisions made before the large refactor. It is intentionally concise: durable behaviour and design rules stay here; implementation details should stay in code and tests.

## Current Decision Log

- Work is done on branch `refactor-safety-docs`; each small stage should end with tests passing and a commit.
- No project `CONTEXT.md` or ADR directory exists yet, so current architecture review uses `docs/architecture/`, `docs/prd/`, and the code as the source of truth.
- First safety priority is behaviour that crosses seams: configuration persistence, process monitoring orchestration, playtime tracking, CLI workflows, and document navigation.
- Documentation cleanup should archive obsolete process/history material instead of continuously updating detailed historical plans.
- Active docs should describe stable repository design, responsibilities, seams, and test strategy; they should not duplicate every class or command detail.

## Safety Net Inventory

Already covered:

- Core service orchestration around `GameAutomationService`, including path matching, fuzzy matching, HDR scheduling, and multi-process reference counting.
- Monitor lifecycle symmetry in `MonitorControlService`.
- Config provider roundtrips for YAML and JSON game entries.
- File-drop request serialization and reload behaviour.
- Statistics aggregation and display-name resolution.
- Dependency direction from infrastructure and shells toward core.

Added in this stage:

- YAML `Save()` preserves global app settings when replacing the game list.
- `GameAutomationService` is covered with the current CSV-backed playtime adapter, proving matched `DataKey` values are persisted to `playtime.csv`.
- Local Markdown links in `docs/` and `README.md` must remain valid while documents are archived or rewritten.

Still recommended before deeper refactors:

- A non-interactive console-host smoke test for command dispatch, after startup side effects are isolated from registry auto-start updates.
- A composition-root test once host construction is extracted from top-level `Program.cs`.
- Documentation structure tests for the final active/archive split after the docs cleanup pass.

## Documentation Convergence Rules

- Keep `README.md` user-facing and short.
- Keep `docs/index.md` as the navigation root.
- Keep live architecture docs focused on current design and stable seams.
- Move stale plans, review notes, sprint process, historical reports, and completed implementation notes to `docs/archives/`.
- If a detail is easy to infer from code and likely to drift during refactor, do not preserve it in active docs.
