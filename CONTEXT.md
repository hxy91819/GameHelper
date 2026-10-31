# GameHelper Domain Context

This file defines the domain language used when naming modules and interfaces.

## Executable Identity

The single stored value that identifies a game's executable. It is either an absolute executable path or an executable file name. Executable path and executable name are derived views, not independently mutable state.

## Game Configuration

The complete persisted application document containing monitor settings, startup settings, and the Game Catalog. Changes preserve unrelated fields and commit the document atomically.

## Game Catalog Intake

The workflow that inspects an executable candidate, resolves duplicate identity and DataKey constraints, and commits a catalog entry. Shells own prompting and display; this module owns catalog invariants.

## Playtime History

The persisted sequence of completed Play Sessions keyed by DataKey. History has one authoritative source; compatibility exports and synchronized reports are derived views, not independent records. [ADR-0004](docs/adr/0004-sqlite-user-data-and-session-ratings.md) records the storage decision.

## Play Session

One period of tracked play for a DataKey, from the first matching process entering tracking to the last matching process leaving. Several processes can belong to one session. Restart continuity and adjustments that exclude sleep do not create a new session.

## Session Rating

The user's optional assessment of one completed Play Session: one to five stars and an optional thought. An unrated session has no rating; it is not a zero-star session. The time of rating does not change when the play occurred.

## Game Journal

The user's per-game progress status, optional completion date and short note in a selected library. It describes the game as a whole, independently of ratings of individual Play Sessions.

## Process Observation

The stream of candidate process start and stop events consumed by game automation. ETW, WMI, and no-op implementations are adapters at this seam and must share filtering and lifecycle semantics.

## Game Speed Attachment

An attempt to apply a configured speed multiplier to one running game process. It is distinct from a playtime session and from confirmation that the game has adopted the requested speed.

## Game Launch Entry

The target, arguments and working directory used to start a game through its original executable, shortcut or Steam entry. This is distinct from Executable Identity, which identifies the actual observed game process.

## Pending Game Launch

A bounded interval after launch preparation and before matching Process Observation. It holds HDR ownership without creating a playtime session. Process observation transfers it to gameplay; creation failure or expiry restores owned settings. Recovery preserves the deadline without replaying the launch command.

## Game Library

The graphical catalog and launch workflow over the existing automation runtime. Desktop presentation and local drafts are separate from Game Configuration; only saved catalog changes enter automation. Named-pipe and in-process callers share the same library interface.

## File-drop Intake

The ConsoleHost workflow that validates dropped files, resolves executable targets, imports them through Game Catalog Intake, and reloads automation after a successful batch.
