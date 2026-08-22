using System.Data.Common;
using System.Data.SQLite;
using System.Diagnostics;
using System.Text;
using Diary.Core.Data.Base;
using Diary.Core.Data.Statistics;
using Diary.Database;

namespace Diary.Db.SQLite;

public sealed partial class SQLiteDb(IDbFactory factory) : DbInterfaceBase(factory), IDisposable, IAsyncDisposable
{
    private const int WorkTagQueryBatchSize = 500;
    private SQLiteConnection? _connection;
    private SQLiteTransaction? _transaction;

    #region provider primitives

    protected override DbCommand CreateCommand(string sql)
    {
        var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        if (_transaction != null)
            cmd.Transaction = _transaction;
        return cmd;
    }

    protected override string ReadString(DbDataReader reader, int ordinal) => reader.GetString(ordinal);

    protected override void BindParameter(DbCommand cmd, string name, object? value)
        => ((SQLiteCommand)cmd).Parameters.AddWithValue(name, value ?? DBNull.Value);

    #endregion

    public override bool Connect()
    {
        var cfg = Factory.GetConfig() as Config;
        Debug.Assert(cfg != null);
        var csb = new SQLiteConnectionStringBuilder
        {
            DataSource = cfg.FilePath,
        };
        _connection = new SQLiteConnection(csb.ToString());
        _connection.Open();
        using (var foreignKeys = _connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
            foreignKeys.ExecuteNonQuery();
        }

        // query version
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "select sqlite_version();";
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            var version = reader.GetString(0);
            _sqliteVersion = version;
            return !string.IsNullOrWhiteSpace(version);
        }

        return false;
    }

    public override bool Initialized()
    {
        const string tableInitCmd = """
                                    CREATE TABLE IF NOT EXISTS
                                    	work_tags(
                                    		id INTEGER PRIMARY KEY AUTOINCREMENT,
                                    		tag_name CHAR(64) NOT NULL UNIQUE,
                                    		tag_color INTEGER NOT NULL DEFAULT 0,
                                    		tag_level INTEGER NOT NULL DEFAULT 0,
                                    is_disabled INTEGER NOT NULL DEFAULT 0,
                                    tag_metadata TEXT NOT NULL DEFAULT '{}'
                                    	);
                                    	
                                    CREATE TABLE IF NOT EXISTS
                                    	work_items(
                                    		id INTEGER PRIMARY KEY AUTOINCREMENT,
                                    		create_date CHAR(16) NOT NULL,
                                    		comment CHAR(256) NOT NULL,
                                    hours REAL DEFAULT 0.0,
                                    priority INTEGER DEFAULT 0,
                                    is_read_only INTEGER NOT NULL DEFAULT 0
                                    	);

                                    CREATE TABLE IF NOT EXISTS
                                    	work_notes(
                                    		id INTEGER PRIMARY KEY
                                    			REFERENCES work_items(id)
                                    			    ON DELETE CASCADE,
                                    		note TEXT NOT NULL
                                    	);

                                    	
                                    CREATE TABLE IF NOT EXISTS
                                    	work_item_tags(
                                    		work_id INTEGER REFERENCES work_items(id)
                                                ON DELETE CASCADE,
                                    		tag_id INTEGER REFERENCES work_tags(id)
                                                ON DELETE CASCADE,
                                    		PRIMARY KEY (work_id,tag_id)
                                    	);
                                    CREATE TABLE IF NOT EXISTS tag_extra_field_definitions(
                                       field_id TEXT PRIMARY KEY,
                                       field_key TEXT NOT NULL COLLATE NOCASE UNIQUE,
                                       tag_id INTEGER NOT NULL REFERENCES work_tags(id) ON DELETE CASCADE,
                                       label TEXT NOT NULL,
                                       field_type INTEGER NOT NULL,
                                       description TEXT NOT NULL DEFAULT '',
                                       sort_order INTEGER NOT NULL DEFAULT 0,
                                       options_json TEXT NOT NULL DEFAULT '[]',
                                       enabled INTEGER NOT NULL DEFAULT 1
                                    );
                                    CREATE TABLE IF NOT EXISTS work_item_extra_field_values(
                                       work_id INTEGER NOT NULL REFERENCES work_items(id) ON DELETE CASCADE,
                                       field_id TEXT NOT NULL REFERENCES tag_extra_field_definitions(field_id),
                                       value_json TEXT NOT NULL DEFAULT '',
                                       PRIMARY KEY (work_id, field_id)
                                    );
                                    CREATE UNIQUE INDEX IF NOT EXISTS ux_work_tags_name
                                       ON work_tags(tag_name);
                                    CREATE UNIQUE INDEX IF NOT EXISTS ux_tag_extra_fields_key
                                       ON tag_extra_field_definitions(field_key COLLATE NOCASE);
                                    CREATE INDEX IF NOT EXISTS idx_tag_extra_fields_tag
                                       ON tag_extra_field_definitions(tag_id, enabled, sort_order);
                                    CREATE INDEX IF NOT EXISTS idx_work_item_extra_fields_work
                                       ON work_item_extra_field_values(work_id);

                                    CREATE TABLE IF NOT EXISTS
                                    	data_versions(
                                    		version_code INTEGER PRIMARY KEY
                                    	);

                                    -- default data version is 1.0.0 (0x10000)
                                    INSERT OR IGNORE INTO data_versions VALUES(0x10000);
                                    
                                    CREATE INDEX IF NOT EXISTS idx_work_items_date ON work_items(create_date);
                                    CREATE INDEX IF NOT EXISTS idx_work_item_tags_tag ON work_item_tags(tag_id);
                                    CREATE INDEX IF NOT EXISTS idx_work_item_tags_work ON work_item_tags(work_id);

                                    CREATE TABLE IF NOT EXISTS diary_schema_metadata(
                                        id INTEGER PRIMARY KEY CHECK (id = 1),
                                        schema_version INTEGER NOT NULL,
                                        provider_id TEXT NOT NULL,
                                        schema_fingerprint TEXT NOT NULL,
                                        migration_state TEXT NOT NULL,
                                        last_migration_id TEXT,
                                        last_error TEXT,
                                        updated_at TEXT NOT NULL
                                    );
                                    CREATE TABLE IF NOT EXISTS diary_schema_migrations(
                                        migration_id TEXT PRIMARY KEY,
                                        version_from INTEGER NOT NULL,
                                        version_to INTEGER NOT NULL,
                                        checksum TEXT NOT NULL,
                                        applied_at TEXT NOT NULL,
                                        success INTEGER NOT NULL,
                                        error TEXT
                                    );
                                    """;
        using var transaction = _connection!.BeginTransaction();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = tableInitCmd;
            cmd.ExecuteNonQuery();

            using var columnCheck = _connection.CreateCommand();
            columnCheck.Transaction = transaction;
            columnCheck.CommandText = "SELECT COUNT(*) FROM pragma_table_info('work_items') WHERE name='is_read_only';";
            var hasReadOnlyColumn = Convert.ToInt32(columnCheck.ExecuteScalar()) > 0;
            if (!hasReadOnlyColumn)
            {
                using var addColumn = _connection.CreateCommand();
                addColumn.Transaction = transaction;
                addColumn.CommandText = "ALTER TABLE work_items ADD COLUMN is_read_only INTEGER NOT NULL DEFAULT 0;";
                addColumn.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch (SQLiteException)
        {
            transaction.Rollback();
            return false;
        }

        return true;
    }

    public override bool KeepAlive()
    {
        return true;
    }

    public override void Close()
    {
        _connection!.Close();
        _connection = null;
    }

    public override uint GetDataVersion()
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT * FROM data_versions ORDER BY version_code DESC LIMIT 1;";
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
            return (uint)reader.GetInt32(0);
        return 0;
    }

    public override bool TryCreateMigrationBackup(
        uint targetVersion,
        out string? backupPath,
        out string? error)
    {
        backupPath = null;
        error = null;
        var config = Factory.GetConfig() as Config;
        if (config is null
            || string.IsNullOrWhiteSpace(config.FilePath)
            || string.Equals(config.FilePath, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var sourcePath = Path.GetFullPath(config.FilePath);
        var sourceDirectory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            error = "无法确定 SQLite 数据库所在目录。";
            return false;
        }

        var backupDirectory = Path.Combine(sourceDirectory, "Backups");
        var currentVersion = GetDataVersion();
        var backupName =
            $"{Path.GetFileName(sourcePath)}.v{currentVersion:X8}-to-v{targetVersion:X8}." +
            $"{DateTimeOffset.Now:yyyyMMddHHmmssfff}.{Guid.NewGuid():N}.bak";
        var result = CreateBackup(Path.Combine(backupDirectory, backupName));
        backupPath = result.BackupPath;
        error = result.Error;
        return result.Success;
    }

    public override WorkTag CreateWorkTag(
        string name,
        bool primary,
        int color,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        const string sql =
            @"INSERT OR IGNORE INTO work_tags(tag_name,tag_level,tag_color,tag_metadata) VALUES ($value,$level,$color,$metadata) RETURNING *;";
        return QueryFirst(sql, MapWorkTag,
            ("$value", name),
            ("$level", primary ? 0 : 1),
            ("$color", color),
            ("$metadata", SerializeWorkTagMetadata(metadata ?? new Dictionary<string, string>())))
            ?? new WorkTag();
    }

    public override bool UpdateWorkTag(WorkTag tag)
    {
        if (tag.Id == 0)
            return false;
        const string sql =
            @"UPDATE OR FAIL work_tags
              SET tag_color=$color, tag_level=$level, is_disabled=$disabled, tag_metadata=$metadata
              WHERE id=$id;";
        return Execute(sql,
            ("$color", tag.Color),
            ("$level", tag.Level),
            ("$disabled", tag.Disabled ? 1 : 0),
            ("$metadata", SerializeWorkTagMetadata(tag.Metadata)),
            ("$id", tag.Id)) > 0;
    }

    public override bool DeleteWorkTag(WorkTag tag)
    {
        const string sql = @"DELETE FROM work_tags WHERE id=$id;";
        return Execute(sql, ("$id", tag.Id)) > 0;
    }

    public override ICollection<WorkTag> AllWorkTags()
    {
        const string sql = @"SELECT * FROM work_tags ORDER BY is_disabled ASC, tag_level ASC;";
        return Query(sql, MapWorkTag);
    }

    public override bool UpdateWorkTagId(int oldId, int newId)
    {
        const string sql = "UPDATE work_tags SET id=$new WHERE id=$old;";
        return Execute(sql, ("$old", oldId), ("$new", newId)) > 0;
    }

    public override ICollection<TagExtraFieldDefinition> GetTagExtraFieldDefinitions(
        int tagId, bool includeDisabled = false)
    {
        var sql = """
                  SELECT field_id, field_key, tag_id, label, field_type, description,
                         sort_order, options_json, enabled
                  FROM tag_extra_field_definitions
                  WHERE tag_id=$tag_id
                  """ + (includeDisabled ? string.Empty : " AND enabled=1") +
                  " ORDER BY sort_order, field_key;";
        return Query(sql, MapTagExtraFieldDefinition, ("$tag_id", tagId));
    }

    public override ICollection<TagExtraFieldDefinition> GetAllTagExtraFieldDefinitions(
        bool includeDisabled = false)
    {
        var sql = """
                  SELECT field_id, field_key, tag_id, label, field_type, description,
                         sort_order, options_json, enabled
                  FROM tag_extra_field_definitions
                  """ + (includeDisabled ? string.Empty : " WHERE enabled=1") +
                  " ORDER BY tag_id, sort_order, field_key;";
        return Query(sql, MapTagExtraFieldDefinition);
    }

    public override bool CreateTagExtraFieldDefinition(TagExtraFieldDefinition definition)
    {
        if (definition.TagId <= 0
            || !TagExtraFieldKeyRules.IsValid(definition.FieldKey)
            || string.IsNullOrWhiteSpace(definition.Label)
            || string.IsNullOrWhiteSpace(definition.FieldId)
            || !IsTagExtraFieldKeyAvailable(definition.FieldKey))
            return false;
        const string sql = """
                           INSERT OR IGNORE INTO tag_extra_field_definitions
                              (field_id, field_key, tag_id, label, field_type, description,
                               sort_order, options_json, enabled)
                           VALUES ($field_id, $field_key, $tag_id, $label, $field_type,
                                   $description, $sort_order, $options_json, $enabled);
                           """;
        return Execute(sql,
            ("$field_id", definition.FieldId),
            ("$field_key", TagExtraFieldKeyRules.Normalize(definition.FieldKey)),
            ("$tag_id", definition.TagId),
            ("$label", definition.Label.Trim()),
            ("$field_type", (int)definition.Type),
            ("$description", definition.Description ?? string.Empty),
            ("$sort_order", definition.SortOrder),
            ("$options_json", SerializeTagExtraFieldOptions(definition.Options)),
            ("$enabled", definition.Enabled ? 1 : 0)) > 0;
    }

    public override bool UpdateTagExtraFieldDefinition(TagExtraFieldDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.FieldId)
            || !TagExtraFieldKeyRules.IsValid(definition.FieldKey)
            || string.IsNullOrWhiteSpace(definition.Label))
            return false;
        var current = QueryFirst(
            "SELECT field_id, field_key, tag_id, label, field_type, description, sort_order, options_json, enabled " +
            "FROM tag_extra_field_definitions WHERE field_id=$field_id;",
            MapTagExtraFieldDefinition,
            ("$field_id", definition.FieldId));
        if (current is null
            || !string.Equals(current.FieldKey, TagExtraFieldKeyRules.Normalize(definition.FieldKey), StringComparison.OrdinalIgnoreCase)
            || current.Type != definition.Type
            || current.TagId != definition.TagId)
            return false;
        const string sql = """
                           UPDATE tag_extra_field_definitions
                           SET label=$label, description=$description, sort_order=$sort_order,
                               options_json=$options_json, enabled=$enabled
                           WHERE field_id=$field_id;
                           """;
        return Execute(sql,
            ("$label", definition.Label.Trim()),
            ("$description", definition.Description ?? string.Empty),
            ("$sort_order", definition.SortOrder),
            ("$options_json", SerializeTagExtraFieldOptions(definition.Options)),
            ("$enabled", definition.Enabled ? 1 : 0),
            ("$field_id", definition.FieldId)) > 0;
    }

    public override bool IsTagExtraFieldKeyAvailable(string fieldKey, string? excludingFieldId = null)
    {
        if (!TagExtraFieldKeyRules.IsValid(fieldKey))
            return false;
        var sql = "SELECT 1 FROM tag_extra_field_definitions WHERE lower(field_key)=lower($field_key)";
        if (!string.IsNullOrWhiteSpace(excludingFieldId))
            sql += " AND field_id<>$excluding_field_id";
        sql += ";";
        return !Exists(sql,
            ("$field_key", TagExtraFieldKeyRules.Normalize(fieldKey)),
            ("$excluding_field_id", excludingFieldId));
    }

    public override ICollection<WorkItemExtraField> GetWorkItemExtraFields(WorkItem item)
    {
        if (item.Id <= 0)
            return Array.Empty<WorkItemExtraField>();
        const string sql = """
                           SELECT d.field_id, d.field_key, d.tag_id, t.tag_name, d.label,
                                  d.field_type, d.description, d.sort_order, d.options_json,
                                  d.enabled, v.value_json
                           FROM work_item_tags wit
                           INNER JOIN tag_extra_field_definitions d
                              ON d.tag_id=wit.tag_id
                           INNER JOIN work_tags t ON t.id=d.tag_id
                           LEFT JOIN work_item_extra_field_values v
                              ON v.work_id=wit.work_id AND v.field_id=d.field_id
                           WHERE wit.work_id=$work_id
                             AND (d.enabled=1 OR v.value_json IS NOT NULL)
                           ORDER BY t.tag_level, t.id, d.sort_order, d.field_key;
                           """;
        return Query(sql, MapWorkItemExtraField, ("$work_id", item.Id));
    }

    public override bool SaveWorkItemExtraFieldValues(
        int workItemId, IReadOnlyCollection<WorkItemExtraFieldValue> values)
    {
        if (workItemId <= 0 || !IsWorkItemWritable(workItemId))
            return false;
        var ownsTransaction = _transaction is null;
        using var localTransaction = ownsTransaction ? _connection!.BeginTransaction() : null;
        try
        {
            using (var delete = CreateCommand("""
                DELETE FROM work_item_extra_field_values
                WHERE work_id=$work_id
                  AND field_id IN (
                      SELECT d.field_id
                      FROM work_item_tags wit
                      INNER JOIN tag_extra_field_definitions d ON d.tag_id=wit.tag_id
                      WHERE wit.work_id=$work_id AND d.enabled=1);
                """))
            {
                if (localTransaction is not null)
                    delete.Transaction = localTransaction;
                ((SQLiteCommand)delete).Parameters.AddWithValue("$work_id", workItemId);
                delete.ExecuteNonQuery();
            }

            foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value.FieldId)
                                                         && !string.IsNullOrWhiteSpace(value.Value)))
            {
                using var insert = CreateCommand("""
                    INSERT OR REPLACE INTO work_item_extra_field_values(work_id, field_id, value_json)
                    SELECT $work_id, d.field_id, $value
                    FROM tag_extra_field_definitions d
                    INNER JOIN work_item_tags wit ON wit.tag_id=d.tag_id
                    WHERE wit.work_id=$work_id AND d.field_id=$field_id AND d.enabled=1;
                    """);
                if (localTransaction is not null)
                    insert.Transaction = localTransaction;
                ((SQLiteCommand)insert).Parameters.AddWithValue("$work_id", workItemId);
                ((SQLiteCommand)insert).Parameters.AddWithValue("$field_id", value.FieldId);
                ((SQLiteCommand)insert).Parameters.AddWithValue("$value", value.Value);
                insert.ExecuteNonQuery();
            }

            localTransaction?.Commit();
            return true;
        }
        catch (Exception)
        {
            localTransaction?.Rollback();
            return false;
        }
    }

    public override WorkItem CreateWorkItem(string date, string comment)
    {
        const string sql =
            @"INSERT INTO work_items(create_date, comment) VALUES ($create_date, $comment) RETURNING *;";
        return QueryFirst(sql, MapWorkItem, ("$create_date", date), ("$comment", comment)) ?? new WorkItem();
    }

    public override bool UpdateWorkItem(WorkItem item)
    {
        if (item.Id == 0)
            return false;
        const string sql =
            @"UPDATE work_items SET create_date=$create_date, comment=$comment, hours=$time, priority=$priority WHERE id=$id AND is_read_only=0;";
        return Execute(sql,
            ("$create_date", item.CreateDate), ("$comment", item.Comment),
            ("$time", item.Time), ("$priority", (int)item.Priority), ("$id", item.Id)) > 0;
    }

    public override bool DeleteWorkItem(WorkItem item)
    {
        if (item.Id == 0)
            return false;
        const string sql = @"DELETE FROM work_items WHERE id=$id;";
        return Execute(sql, ("$id", item.Id)) > 0;
    }

    public override ICollection<WorkItem> GetWorkItemByDateRange(string beginData, string endData)
    {
        var sql = """
                  SELECT *
                  FROM work_items
                  WHERE create_date BETWEEN $beginDate AND $endDate;
                  """;
        return Query(sql, MapWorkItem, ("$beginDate", beginData), ("$endDate", endData));
    }

    public override ICollection<WorkItem> GetWorkItemByDate(string date)
    {
        const string sql = @"SELECT * FROM work_items WHERE create_date=$date ORDER BY priority ASC;";
        return Query(sql, MapWorkItem, ("$date", date));
    }

    public override bool UpdateWorkItemId(int oldId, int newId)
    {
        const string sql = "UPDATE work_items SET id=$new WHERE id=$old AND is_read_only=0;";
        return Execute(sql, ("$old", oldId), ("$new", newId)) > 0;
    }

    public override bool MarkWorkItemReadOnly(WorkItem item)
    {
        if (item.Id == 0)
            return false;
        const string sql = "UPDATE work_items SET is_read_only=1 WHERE id=$id;";
        var updated = Execute(sql, ("$id", item.Id)) > 0;
        if (updated)
            item.IsReadOnly = true;
        return updated;
    }

    public override void WorkUpdateNote(WorkItem work, string content)
    {
        if (work.Id == 0)
            throw new ArgumentException("work id is required");
        if (!IsWorkItemWritable(work.Id))
            throw new InvalidOperationException("只读工作项不可修改备注");
        const string sql =
            @"INSERT INTO work_notes(id, note) VALUES ($id, $note) ON CONFLICT (id) DO UPDATE SET note=$note RETURNING *;";
        Execute(sql, ("$id", work.Id), ("$note", content));
    }

    public override void WorkDeleteNote(WorkItem work)
    {
        if (work.Id == 0)
            throw new ArgumentException("work id is required");
        if (!IsWorkItemWritable(work.Id))
            throw new InvalidOperationException("只读工作项不可删除备注");
        const string sql = @"DELETE FROM work_notes WHERE id=$id;";
        Execute(sql, ("$id", work.Id));
    }

    public override string? WorkGetNote(WorkItem work)
    {
        const string sql = @"SELECT note FROM work_notes WHERE id=$id;";
        return QueryFirst(sql, r => ReadString(r, 0), ("$id", work.Id));
    }

    public override Dictionary<int, string> GetWorkNotesByDate(string date)
    {
        var sql = """
                  SELECT work_notes.id, work_notes.note
                  FROM work_notes INNER JOIN work_items ON work_notes.id = work_items.id
                  WHERE work_items.create_date = $date;
                  """;
        var rows = Query<(int Id, string Note)>(
            sql, r => (r.GetInt32(0), ReadString(r, 1)), ("$date", date));
        var result = new Dictionary<int, string>();
        foreach (var (id, note) in rows)
            result[id] = note;
        return result;
    }

    public override Dictionary<int, string> GetWorkNotesByWorkItemIds(
        IReadOnlyCollection<int> workItemIds)
    {
        ArgumentNullException.ThrowIfNull(workItemIds);
        var ids = workItemIds.Where(id => id != 0).Distinct().ToArray();
        var result = new Dictionary<int, string>();
        foreach (var batch in ids.Chunk(WorkTagQueryBatchSize))
        {
            var placeholders = new string[batch.Length];
            var args = new (string Name, object? Value)[batch.Length];
            for (var i = 0; i < batch.Length; i++)
            {
                placeholders[i] = $"$id{i}";
                args[i] = (placeholders[i], batch[i]);
            }
            var sql = $"SELECT id, note FROM work_notes WHERE id IN ({string.Join(", ", placeholders)});";
            foreach (var (id, note) in Query<(int Id, string Note)>(
                         sql, r => (r.GetInt32(0), ReadString(r, 1)), args))
                result[id] = note;
        }
        return result;
    }

    public override Dictionary<int, ICollection<WorkTag>> GetWorkTagsByDate(string date)
    {
        var sql = """
                  SELECT work_tags.*, work_item_tags.work_id
                  FROM work_item_tags
                  INNER JOIN work_tags ON work_item_tags.tag_id = work_tags.id
                  INNER JOIN work_items ON work_item_tags.work_id = work_items.id
                  WHERE work_items.create_date = $date
                  ORDER BY work_tags.tag_level;
                  """;
        var rows = Query<(WorkTag Tag, int WorkId)>(
            sql, r => (MapWorkTag(r), r.GetInt32(6)), ("$date", date));
        var result = new Dictionary<int, ICollection<WorkTag>>();
        foreach (var (tag, workId) in rows)
        {
            if (!result.TryGetValue(workId, out var list))
            {
                list = new List<WorkTag>();
                result[workId] = list;
            }
            list.Add(tag);
        }
        return result;
    }

    public override Dictionary<int, ICollection<WorkTag>> GetWorkTagsByWorkItemIds(
        IReadOnlyCollection<int> workItemIds)
    {
        ArgumentNullException.ThrowIfNull(workItemIds);
        var ids = workItemIds.Where(id => id != 0).Distinct().ToArray();
        if (ids.Length == 0)
            return new Dictionary<int, ICollection<WorkTag>>();

        var result = new Dictionary<int, ICollection<WorkTag>>();
        foreach (var batch in ids.Chunk(WorkTagQueryBatchSize))
        {
            var placeholders = new string[batch.Length];
            var args = new (string Name, object? Value)[batch.Length];
            for (var i = 0; i < batch.Length; i++)
            {
                placeholders[i] = $"$id{i}";
                args[i] = (placeholders[i], batch[i]);
            }
            var sql = $"""
                       SELECT work_tags.*, work_item_tags.work_id
                       FROM work_item_tags INNER JOIN work_tags ON work_item_tags.tag_id = work_tags.id
                       WHERE work_item_tags.work_id IN ({string.Join(", ", placeholders)})
                       ORDER BY work_item_tags.work_id, work_tags.tag_level, work_tags.id;
                       """;
            var rows = Query<(WorkTag Tag, int WorkId)>(
                sql, r => (MapWorkTag(r), r.GetInt32(6)), args);
            AddGroupedWorkTags(result, rows);
        }
        return result;
    }

    private static void AddGroupedWorkTags(
        Dictionary<int, ICollection<WorkTag>> result,
        IEnumerable<(WorkTag Tag, int WorkId)> rows)
    {
        foreach (var (tag, workId) in rows)
        {
            if (!result.TryGetValue(workId, out var tags))
            {
                tags = new List<WorkTag>();
                result[workId] = tags;
            }
            tags.Add(tag);
        }
    }

    public override bool WorkItemAddTag(WorkItem item, WorkTag tag)
    {
        if (!IsWorkItemWritable(item.Id))
            return false;
        const string sql = @"INSERT INTO work_item_tags VALUES($work_id, $tag_id) RETURNING *;";
        try
        {
            return Execute(sql, ("$work_id", item.Id), ("$tag_id", tag.Id)) > 0;
        }
        catch (SQLiteException)
        {
            return false;
        }
    }

    public override bool WorkItemRemoveTag(WorkItem item, WorkTag tag)
    {
        if (!IsWorkItemWritable(item.Id))
            return false;
        const string sql = @"DELETE from work_item_tags WHERE work_id=$work_id and tag_id=$tag_id;";
        return Execute(sql, ("$work_id", item.Id), ("$tag_id", tag.Id)) > 0;
    }

    public override bool WorkItemCleanTags(WorkItem item)
    {
        if (!IsWorkItemWritable(item.Id))
            return false;
        const string sql = @"DELETE from work_item_tags WHERE work_id=$work_id;";
        return Execute(sql, ("$work_id", item.Id)) > 0;
    }

    private bool IsWorkItemWritable(int id)
        => Exists("SELECT 1 FROM work_items WHERE id=$id AND is_read_only=0;", ("$id", id));

    public override ICollection<WorkTag> GetWorkItemTags(WorkItem item)
    {
        if (item.Id == 0)
            throw new ArgumentException("work id is required");
        var sql = """
                  SELECT work_tags.*
                  FROM work_item_tags INNER JOIN work_tags ON work_item_tags.tag_id=work_tags.id
                  WHERE work_item_tags.work_id = $work_id
                  ORDER BY work_tags.tag_level ASC;
                  """;
        return Query(sql, MapWorkTag, ("$work_id", item.Id));
    }

    public override StatisticsResult GetStatistics(string beginDate, string endDate)
    {
        var result = new StatisticsResult()
        {
            DateBegin = beginDate,
            DateEnd = endDate,
            PrimaryTags = new List<TagTime>(),
        };

        // total time
        {
            var dateRangeQuery = "SELECT sum(hours) FROM work_items WHERE create_date BETWEEN $beginDate AND $endDate;";
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = dateRangeQuery;
            cmd.Parameters.AddWithValue("$beginDate", beginDate);
            cmd.Parameters.AddWithValue("$endDate", endDate);
            using var reader = cmd.ExecuteReader();
            if (reader.Read() && !reader.IsDBNull(0))
                result.Total = reader.GetDouble(0);
        }

        if (result.Total > 0)
        {
            var sql = """
                      SELECT work_tags.id AS tid, sum(hours) AS total, tag_name 
                      FROM 
                      	((work_item_tags INNER JOIN
                      			(SELECT id,hours FROM work_items WHERE create_date BETWEEN $beginDate AND $endDate) AS T1
                      		ON work_item_tags.work_id=T1.id) AS T2
                      	INNER JOIN work_tags ON work_tags.id=T2.tag_id AND work_tags.tag_level=0)
                      GROUP BY tid;
                      """;

            // 一级标签
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$beginDate", beginDate);
            cmd.Parameters.AddWithValue("$endDate", endDate);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.PrimaryTags.Add(new TagTime()
                {
                    TagId = reader.GetInt32(0),
                    Time = reader.GetDouble(1),
                    TagName = reader.GetString(2),
                    Nested = new List<TagTime>(),
                });
            }
        }

        if (result.PrimaryTags.Count > 0)
        {
            var nestedSql = """
                            SELECT
                            	primary_tags.tag_id AS primary_id,
                            	work_tags.id,
                            	SUM(T1.hours) AS total,
                            	work_tags.tag_name
                            FROM
                            	(SELECT wit.work_id, wit.tag_id
                            	 FROM work_item_tags wit
                            	 INNER JOIN work_tags ON work_tags.id = wit.tag_id AND work_tags.tag_level = 0) AS primary_tags
                            	INNER JOIN work_item_tags AS nested_tags
                            		ON primary_tags.work_id = nested_tags.work_id AND nested_tags.tag_id != primary_tags.tag_id
                            	INNER JOIN work_tags ON work_tags.id = nested_tags.tag_id AND work_tags.tag_level != 0
                            	INNER JOIN (SELECT id, hours FROM work_items WHERE create_date BETWEEN $beginDate AND $endDate) AS T1
                            		ON primary_tags.work_id = T1.id
                            GROUP BY primary_tags.tag_id, work_tags.id, work_tags.tag_name;
                            """;

            using var nestedCmd = _connection!.CreateCommand();
            nestedCmd.CommandText = nestedSql;
            nestedCmd.Parameters.AddWithValue("$beginDate", beginDate);
            nestedCmd.Parameters.AddWithValue("$endDate", endDate);
            using var nestedReader = nestedCmd.ExecuteReader();

            var nestedMap = result.PrimaryTags.ToDictionary(t => t.TagId);
            while (nestedReader.Read())
            {
                var primaryId = nestedReader.GetInt32(0);
                if (nestedMap.TryGetValue(primaryId, out var primaryTag))
                {
                    primaryTag.Nested.Add(new TagTime()
                    {
                        TagId = nestedReader.GetInt32(1),
                        Time = nestedReader.GetDouble(2),
                        TagName = nestedReader.GetString(3),
                    });
                }
            }
        }

        return result;
    }

    public override ICollection<WorkItem> GetWorkItemsByTagAndDate(string dateBegin, string dateEnd, int l1, int l2 = 0)
    {
        if (l2 == 0)
        {
            var sql = """
                      SELECT work_items.* FROM
                      (work_items INNER JOIN work_item_tags on work_items.id = work_item_tags.work_id)
                      WHERE work_item_tags.tag_id = $id AND work_items.create_date BETWEEN $begin AND $end
                      ORDER BY create_date,work_items.id;
                      """;
            return Query(sql, MapWorkItem, ("$begin", dateBegin), ("$end", dateEnd), ("$id", l1));
        }

        {
            var sql = """
                      SELECT work_items.* FROM
                      work_items INNER JOIN
                      (SELECT work_item_tags.work_id FROM
                      	(SELECT work_id FROM work_item_tags WHERE tag_id=$primary) AS T0
                      	INNER JOIN work_item_tags ON T0.work_id = work_item_tags.work_id AND work_item_tags.tag_id=$secondary) AS T1
                      	ON work_items.id=T1.work_id WHERE create_date BETWEEN $begin AND $end
                      ORDER BY create_date,id;
                      """;
            return Query(sql, MapWorkItem,
                ("$begin", dateBegin), ("$end", dateEnd), ("$primary", l1), ("$secondary", l2));
        }
    }

    public override ICollection<WorkItem> QueryWorkItems(WorkItemQuery query)
    {
        query = WorkItemQueryNormalizer.Normalize(query);
        var tagIds = query.TagIds.Distinct().ToArray();
        if (query.TagFilter is WorkItemTagFilter.Any or WorkItemTagFilter.All && tagIds.Length == 0)
            return Array.Empty<WorkItem>();

        var sql = new StringBuilder("SELECT work_items.* FROM work_items WHERE 1=1");
        var args = new List<(string Name, object? Value)>();
        if (!string.IsNullOrWhiteSpace(query.StartDate))
        {
            sql.Append(" AND work_items.create_date >= $start");
            args.Add(("$start", query.StartDate));
        }
        if (!string.IsNullOrWhiteSpace(query.EndDate))
        {
            sql.Append(" AND work_items.create_date <= $end");
            args.Add(("$end", query.EndDate));
        }
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            sql.Append(" AND (instr(lower(work_items.comment), lower($text)) > 0 OR EXISTS (SELECT 1 FROM work_notes wn WHERE wn.id = work_items.id AND instr(lower(wn.note), lower($text)) > 0))");
            args.Add(("$text", query.Text));
        }
        if (query.Priority is not null)
        {
            sql.Append(" AND work_items.priority = $priority");
            args.Add(("$priority", (int)query.Priority.Value));
        }

        if (query.TagFilter == WorkItemTagFilter.None
            || query.TagFilter == WorkItemTagFilter.Exact && tagIds.Length == 0)
        {
            sql.Append(" AND NOT EXISTS (SELECT 1 FROM work_item_tags wit WHERE wit.work_id = work_items.id)");
        }
        else if (query.TagFilter is WorkItemTagFilter.Any or WorkItemTagFilter.All or WorkItemTagFilter.Exact)
        {
            var placeholders = new string[tagIds.Length];
            for (var i = 0; i < tagIds.Length; i++)
            {
                placeholders[i] = $"$tag{i}";
                args.Add((placeholders[i], tagIds[i]));
            }
            sql.Append(" AND (SELECT COUNT(DISTINCT wit.tag_id) FROM work_item_tags wit WHERE wit.work_id = work_items.id AND wit.tag_id IN (")
                .AppendJoin(", ", placeholders)
                .Append("))");
            sql.Append(query.TagFilter == WorkItemTagFilter.Any ? " > 0" : $" = {tagIds.Length}");
            if (query.TagFilter == WorkItemTagFilter.Exact)
                sql.Append($" AND (SELECT COUNT(*) FROM work_item_tags all_tags WHERE all_tags.work_id = work_items.id) = {tagIds.Length}");
        }

        sql.Append(" ORDER BY work_items.create_date, work_items.id");
        if (query.Limit is > 0)
        {
            sql.Append(" LIMIT $limit OFFSET $offset");
            args.Add(("$limit", query.Limit.Value));
            args.Add(("$offset", Math.Max(0, query.Offset)));
        }
        return Query(sql.ToString(), MapWorkItem, args.ToArray());
    }

    public override bool DropData()
    {
        var ownTransaction = _transaction is null;
        using var transaction = ownTransaction ? _connection!.BeginTransaction() : null;
        try
        {
            var sql = """
                      DELETE FROM work_item_tags;
                      DELETE FROM work_tags;
                      DELETE FROM work_notes;
                      DELETE FROM work_items;
                      """;
            using var cmd = CreateCommand(sql);
            if (transaction != null)
                cmd.Transaction = transaction;
            cmd.ExecuteNonQuery();
            if (ownTransaction)
                transaction!.Commit();
        }
        catch (Exception)
        {
            if (ownTransaction)
                transaction?.Rollback();
            return false;
        }

        return true;
    }

    public override bool BeginTransaction()
    {
        Debug.Assert(_transaction == null);

        try
        {
            _transaction = _connection!.BeginTransaction();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public override bool CommitTransaction()
    {
        Debug.Assert(_transaction != null);

        try
        {
            _transaction.Commit();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            _transaction.Dispose();
            _transaction = null;
        }
    }

    public override bool RollbackTransaction()
    {
        Debug.Assert(_transaction != null);

        try
        {
            _transaction.Rollback();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            _transaction.Dispose();
            _transaction = null;
        }
    }

    public override void Dispose()
    {
        _transaction?.Dispose();
        _transaction = null;
        _connection?.Dispose();
        _connection = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_transaction != null) await _transaction.DisposeAsync();
        _transaction = null;
        if (_connection != null) await _connection.DisposeAsync();
        _connection = null;
    }
}
