CREATE INDEX ix_play_sessions_key_sequence ON play_sessions(data_key, sequence);
CREATE INDEX ix_session_ratings_updated ON session_ratings(updated_at_utc, session_id);
CREATE INDEX ix_inflight_sessions_key ON inflight_sessions(data_key);
CREATE INDEX ix_inflight_processes_session ON inflight_processes(session_id);
