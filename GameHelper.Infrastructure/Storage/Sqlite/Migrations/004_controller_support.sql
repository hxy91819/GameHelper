CREATE TABLE controller_support_cache (
 app_id INTEGER PRIMARY KEY CHECK(app_id>0), status TEXT NOT NULL CHECK(status IN ('full','partial','unlisted','unknown')),
 dualshock INTEGER NOT NULL CHECK(dualshock IN (0,1)), dualsense INTEGER NOT NULL CHECK(dualsense IN (0,1)),
 retry_at INTEGER NOT NULL, failures INTEGER NOT NULL CHECK(failures>=0));
CREATE TABLE controller_support_overrides (
 scope_id INTEGER NOT NULL REFERENCES config_scopes(scope_id), data_key TEXT NOT NULL COLLATE GH_ORDINAL_IGNORE_CASE,
 supported INTEGER NOT NULL CHECK(supported IN (0,1)), PRIMARY KEY(scope_id,data_key));
