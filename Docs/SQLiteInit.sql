-- 历史 SQLite schema，仅用于参考旧版数据库结构，不可用于当前版本初始化。
-- 当前核心表由 Diary.Db.SQLite/SQLiteDb.cs 创建；Redmine 表由
-- Diary.RedMine/RedMineInitialMigration.cs 以版本 0 -> 1 创建。
-- 当前 Redmine 表使用 instance_id 和复合主键，不能使用下面的旧表定义。
-- plugin_data_versions 表由 Diary.RedMine.SQLite/SQLiteRedMineDb.cs、
-- Diary.RedMine.PostgreSQL/PgRedMineDb.cs 及 Diary.Jira.SQLite/PgJiraDb
-- 各自创建（plugin_id CHAR(128) PRIMARY KEY, schema_version）。

CREATE TABLE IF NOT EXISTS
	WorkTags(
		Id INTEGER PRIMARY KEY AUTOINCREMENT,
		Name CHAR(64) NOT NULL UNIQUE,
		Color INTEGER NOT NULL DEFAULT 0,
		Level INTEGER NOT NULL DEFAULT 0,
		Disabled INTEGER NOT NULL DEFAULT 0
	);
	
CREATE TABLE IF NOT EXISTS
	WorkItems(
		Id INTEGER PRIMARY KEY AUTOINCREMENT,
		CreateDate CHAR(16) NOT NULL,
		Comment CHAR(128) NOT NULL,
		Hours REAL DEFAULT 0.0,
		Priority INTEGER DEFAULT 0
	);

CREATE TABLE IF NOT EXISTS
	WorkNotes(
		WorkId INTEGER PRIMARY KEY
			REFERENCES WorkItems(Id)
			ON DELETE CASCADE,
		Note TEXT NOT NULL
	);

	
CREATE TABLE IF NOT EXISTS
	WorkItemTags(
		WorkId INTEGER REFERENCES WorkItems(Id),
		TagId INTEGER REFERENCES WorkTags(Id),
		PRIMARY KEY (WorkId,TagId)
	);
	
CREATE TABLE IF NOT EXISTS
	RedMineProjects(
		Id INTEGER NOT NULL PRIMARY KEY,
		Title CHAR(128) NOT NULL,
		Description CHAR(1024) DEFAULT '',
		IsClosed INTEGER DEFAULT 0
	);
	
CREATE TABLE IF NOT EXISTS
	RedMineActivities(
		Id INTEGER PRIMARY KEY,
		Title CHAR(32) NOT NULL
	);
	
CREATE TABLE IF NOT EXISTS
	RedMineIssues(
		Id INTEGER PRIMARY KEY,
		Title CHAR(128) NOT NULL,
		AssignedTo CHAR(16) DEFAULT '',
		ProjectId INTEGER NOT NULL REFERENCES
			RedMineProjects(Id) ON DELETE CASCADE,
		IsClosed INTEGER default 0
	);
	
CREATE TABLE IF NOT EXISTS
	RedMineTimeEntries(
		WorkId INTEGER PRIMARY KEY
			REFERENCES WorkItems(Id) ON DELETE CASCADE,
		EntryId INTEGER DEFAULT 0,
		ActivityId INTEGER
			REFERENCES RedMineActivities(Id) ON DELETE SET NULL,
		IssueId INTEGER
			REFERENCES RedMineIssues(Id) ON DELETE SET NULL
	);

CREATE TABLE IF NOT EXISTS
	DataVersions(
		Code INTEGER PRIMARY KEY
	);

-- default data version is 1.0.0 (0x1000000)
INSERT INTO DataVersions VALUES(0x1000000) ON CONFLICT DO NOTHING;
