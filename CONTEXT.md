# GameHelper Domain Context

This file defines the domain language used when naming modules and interfaces.

## Executable Identity

The single stored value that identifies a game's executable. It is either an absolute executable path or an executable file name. Executable path and executable name are derived views, not independently mutable state.

## Game Configuration

The complete persisted application document containing monitor settings, startup settings, and the Game Catalog. Changes preserve unrelated fields and commit the document atomically.

## Game Catalog Intake

The workflow that inspects an executable candidate, resolves duplicate identity and DataKey constraints, and commits a catalog entry. Shells own prompting and display; this module owns catalog invariants.

## Playtime History

The persisted sequence of completed play sessions keyed by DataKey. The current format is CSV; legacy JSON is import-only.

## Process Observation

The stream of candidate process start and stop events consumed by game automation. ETW, WMI, and no-op implementations are adapters at this seam and must share filtering and lifecycle semantics.

## File-drop Intake

The ConsoleHost workflow that validates dropped files, resolves executable targets, imports them through Game Catalog Intake, and reloads automation after a successful batch.
