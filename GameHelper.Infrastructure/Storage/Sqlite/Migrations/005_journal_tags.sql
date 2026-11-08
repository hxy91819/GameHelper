-- Journal statuses became multi-select tags: one Chinese tag or a canonical comma-joined subset
-- (order fixed by the product list). 未开始 means "no tag" and never combines with others.
-- Notes grew from 80 to 500 UTF-16 code units. Rebuild so both CHECKs match the new contract.
ALTER TABLE journals RENAME TO journals_v4;
CREATE TABLE journals (
    scope_id INTEGER NOT NULL REFERENCES config_scopes(scope_id),
    data_key TEXT NOT NULL COLLATE GH_ORDINAL_IGNORE_CASE,
    status TEXT NOT NULL CHECK (status IN (
        '未开始',
        '在玩', '已通关', '全成就', '搁置',
        '在玩,已通关', '在玩,全成就', '在玩,搁置',
        '已通关,全成就', '已通关,搁置', '全成就,搁置',
        '在玩,已通关,全成就', '在玩,已通关,搁置', '在玩,全成就,搁置', '已通关,全成就,搁置',
        '在玩,已通关,全成就,搁置')),
    completed_on TEXT,
    note TEXT NOT NULL CHECK (length(note) <= 500 AND instr(note, char(10)) = 0 AND instr(note, char(13)) = 0),
    revision INTEGER NOT NULL,
    position INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (scope_id, data_key)
);
INSERT INTO journals SELECT scope_id,data_key,status,completed_on,note,revision,position FROM journals_v4;
DROP TABLE journals_v4;
