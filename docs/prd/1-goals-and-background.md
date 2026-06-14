# Goals And Background

## Goals

- Prefer exact executable path matching when path data exists.
- Preserve name-based fallback matching for older configurations.
- Use `DataKey` as the stable identity for config, playtime records, and statistics.
- Keep game onboarding low-friction through CLI, interactive shell, and file-drop workflows.
- Default to ETW monitoring while preserving WMI fallback for non-admin or compatibility scenarios.

## Current State

These goals are implemented and are now maintained as baseline behaviour. Historical delivery records live under `docs/archives/`.

## Deferred

- Monitor pre-filtering remains a performance optimization, not a correctness dependency.
- Speed-control work remains a separate proposal under `docs/plans/`.
