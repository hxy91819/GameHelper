-- Old JSON dictionaries/lists have observable insertion order. Keep it explicitly.
ALTER TABLE journals ADD COLUMN position INTEGER NOT NULL DEFAULT 0;
ALTER TABLE genre_overrides ADD COLUMN position INTEGER NOT NULL DEFAULT 0;
ALTER TABLE artwork_links ADD COLUMN position INTEGER NOT NULL DEFAULT 0;
ALTER TABLE scan_ignored ADD COLUMN position INTEGER NOT NULL DEFAULT 0;
-- Null process identity fields and per-process launch flags are legal in old snapshots.
ALTER TABLE inflight_processes RENAME TO inflight_processes_v2;
CREATE TABLE inflight_processes (
    process_row_id INTEGER NOT NULL PRIMARY KEY,
    session_id TEXT NOT NULL REFERENCES inflight_sessions(session_id), pid INTEGER,
    normalized_name TEXT, normalized_path TEXT, process_started_at_utc TEXT,
    launched_by_helper INTEGER CHECK (launched_by_helper IN (0,1))
);
INSERT INTO inflight_processes SELECT process_row_id, session_id, pid, normalized_name,
    normalized_path, process_started_at_utc, NULL FROM inflight_processes_v2;
DROP TABLE inflight_processes_v2;
CREATE INDEX ix_inflight_processes_session ON inflight_processes(session_id);
-- Activation and offline downgrade generations are facts in the DB, never external flags.
CREATE TABLE migration_state (
    name TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL
);
ALTER TABLE inflight_processes ADD COLUMN data_key TEXT;

ALTER TABLE pending_launches ADD COLUMN position INTEGER NOT NULL DEFAULT 0;
