CREATE TABLE storage_meta (
    singleton INTEGER NOT NULL PRIMARY KEY CHECK (singleton = 1),
    database_id TEXT NOT NULL,
    history_revision INTEGER NOT NULL CHECK (history_revision >= 0),
    data_revision INTEGER NOT NULL CHECK (data_revision >= 0),
    compatibility_mode TEXT NOT NULL
);
CREATE TABLE config_scopes (
    scope_id INTEGER NOT NULL PRIMARY KEY,
    config_path TEXT NOT NULL UNIQUE COLLATE GH_ORDINAL_IGNORE_CASE
);
CREATE TABLE history_keys (
    data_key TEXT NOT NULL PRIMARY KEY COLLATE GH_ORDINAL_IGNORE_CASE,
    first_sequence INTEGER NOT NULL UNIQUE
);
CREATE TABLE import_sources (
    source_id TEXT NOT NULL PRIMARY KEY,
    scope_id INTEGER REFERENCES config_scopes(scope_id),
    path TEXT NOT NULL COLLATE GH_ORDINAL_IGNORE_CASE,
    sha256 TEXT NOT NULL, size_bytes INTEGER NOT NULL CHECK (size_bytes >= 0),
    codec_version INTEGER NOT NULL, status TEXT NOT NULL,
    accepted_count INTEGER NOT NULL CHECK (accepted_count >= 0),
    skipped_count INTEGER NOT NULL CHECK (skipped_count >= 0),
    validation_summary TEXT NOT NULL
);
CREATE TABLE play_sessions (
    session_id TEXT NOT NULL PRIMARY KEY,
    data_key TEXT NOT NULL COLLATE GH_ORDINAL_IGNORE_CASE REFERENCES history_keys(data_key),
    sequence INTEGER NOT NULL UNIQUE,
    started_value TEXT NOT NULL, ended_value TEXT NOT NULL,
    started_kind INTEGER NOT NULL CHECK (started_kind IN (0,1,2)),
    ended_kind INTEGER NOT NULL CHECK (ended_kind IN (0,1,2)),
    duration_ticks INTEGER NOT NULL,
    duration_minutes INTEGER NOT NULL CHECK (duration_minutes >= 0),
    launch_source TEXT NOT NULL CHECK (launch_source IN ('library','external','unknown')),
    completion_source TEXT NOT NULL CHECK (completion_source IN ('observed','recovered','legacy')),
    created_at_utc TEXT NOT NULL,
    import_source_id TEXT REFERENCES import_sources(source_id), import_record_ordinal INTEGER,
    UNIQUE (import_source_id, import_record_ordinal),
    CHECK ((import_source_id IS NULL) = (import_record_ordinal IS NULL))
);
CREATE TABLE session_ratings (
    session_id TEXT NOT NULL PRIMARY KEY REFERENCES play_sessions(session_id) ON DELETE RESTRICT,
    stars INTEGER NOT NULL CHECK (typeof(stars) = 'integer' AND stars BETWEEN 1 AND 5), thought TEXT,
    created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, revision INTEGER NOT NULL
);
CREATE TABLE journals (
    scope_id INTEGER NOT NULL REFERENCES config_scopes(scope_id),
    data_key TEXT NOT NULL COLLATE GH_ORDINAL_IGNORE_CASE,
    status TEXT NOT NULL CHECK (status IN ('未开始','在玩','已通关','全成就','搁置')),
    completed_on TEXT,
    note TEXT NOT NULL CHECK (length(note) <= 80 AND instr(note, char(10)) = 0 AND instr(note, char(13)) = 0),
    revision INTEGER NOT NULL, PRIMARY KEY (scope_id, data_key)
);
CREATE TABLE genre_overrides (
    scope_id INTEGER NOT NULL REFERENCES config_scopes(scope_id),
    data_key TEXT NOT NULL COLLATE GH_ORDINAL_IGNORE_CASE,
    revision INTEGER NOT NULL, PRIMARY KEY (scope_id, data_key)
);
CREATE TABLE genre_override_items (
    scope_id INTEGER NOT NULL REFERENCES config_scopes(scope_id),
    data_key TEXT NOT NULL COLLATE GH_ORDINAL_IGNORE_CASE, genre TEXT NOT NULL,
    position INTEGER NOT NULL, PRIMARY KEY (scope_id, data_key, genre),
    FOREIGN KEY (scope_id, data_key) REFERENCES genre_overrides(scope_id, data_key)
);
CREATE TABLE artwork_links (
    scope_id INTEGER NOT NULL REFERENCES config_scopes(scope_id),
    data_key TEXT NOT NULL COLLATE GH_ORDINAL_IGNORE_CASE,
    steam_app_id INTEGER NOT NULL CHECK (steam_app_id BETWEEN 1 AND 4294967295),
    revision INTEGER NOT NULL, PRIMARY KEY (scope_id, data_key)
);
CREATE TABLE desktop_drafts (
    scope_id INTEGER NOT NULL REFERENCES config_scopes(scope_id), draft_id TEXT NOT NULL,
    data_key TEXT NOT NULL COLLATE GH_ORDINAL_IGNORE_CASE, position INTEGER NOT NULL,
    payload_version INTEGER NOT NULL, payload_json TEXT NOT NULL, revision INTEGER NOT NULL,
    PRIMARY KEY (scope_id, draft_id)
);
CREATE TABLE scan_roots (
    scope_id INTEGER NOT NULL REFERENCES config_scopes(scope_id),
    directory TEXT NOT NULL COLLATE GH_ORDINAL_IGNORE_CASE,
    position INTEGER NOT NULL CHECK (position BETWEEN 0 AND 5),
    PRIMARY KEY (scope_id, directory), UNIQUE (scope_id, position)
);
CREATE TABLE scan_ignored (
    scope_id INTEGER NOT NULL REFERENCES config_scopes(scope_id),
    executable TEXT NOT NULL COLLATE GH_ORDINAL_IGNORE_CASE, PRIMARY KEY (scope_id, executable)
);
CREATE TABLE desktop_preferences (
    scope_id INTEGER NOT NULL PRIMARY KEY REFERENCES config_scopes(scope_id),
    theme TEXT NOT NULL CHECK (theme IN ('light','dark')), revision INTEGER NOT NULL
);
CREATE TABLE runtime_snapshot (
    singleton INTEGER NOT NULL PRIMARY KEY CHECK (singleton = 1), snapshot_at TEXT NOT NULL,
    hdr_enabled_by_helper INTEGER NOT NULL CHECK (hdr_enabled_by_helper IN (0,1)), revision INTEGER NOT NULL
);
CREATE TABLE inflight_sessions (
    session_id TEXT NOT NULL PRIMARY KEY, data_key TEXT NOT NULL COLLATE GH_ORDINAL_IGNORE_CASE,
    started_value TEXT NOT NULL, started_kind INTEGER NOT NULL CHECK (started_kind IN (0,1,2)),
    launch_source TEXT NOT NULL CHECK (launch_source IN ('library','external','unknown')),
    recovery_end_value TEXT, recovery_end_kind INTEGER CHECK (recovery_end_kind IN (0,1,2)),
    CHECK ((recovery_end_value IS NULL) = (recovery_end_kind IS NULL))
);
CREATE TABLE inflight_processes (
    process_row_id INTEGER NOT NULL PRIMARY KEY,
    session_id TEXT NOT NULL REFERENCES inflight_sessions(session_id), pid INTEGER,
    normalized_name TEXT NOT NULL, normalized_path TEXT NOT NULL, process_started_at_utc TEXT NOT NULL
);
CREATE TABLE pending_launches (
    data_key TEXT NOT NULL PRIMARY KEY COLLATE GH_ORDINAL_IGNORE_CASE, deadline TEXT NOT NULL
);
CREATE TABLE import_records (
    source_id TEXT NOT NULL REFERENCES import_sources(source_id), record_ordinal INTEGER NOT NULL,
    disposition TEXT NOT NULL CHECK (disposition IN ('accepted','skipped')), reason TEXT NOT NULL,
    session_id TEXT REFERENCES play_sessions(session_id), PRIMARY KEY (source_id, record_ordinal)
);
CREATE TABLE compatibility_exports (
    target TEXT NOT NULL PRIMARY KEY COLLATE GH_ORDINAL_IGNORE_CASE,
    data_revision INTEGER NOT NULL, snapshot_revision INTEGER NOT NULL,
    content_hash TEXT NOT NULL, failure TEXT NOT NULL,
    dirty INTEGER NOT NULL CHECK (dirty IN (0,1))
);
