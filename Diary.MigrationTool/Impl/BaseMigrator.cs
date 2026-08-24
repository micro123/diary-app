using System.Data;
using Diary.Core.Data.Base;
using Diary.Database;

namespace Diary.MigrationTool.Impl;

internal abstract class BaseMigrator : IDisposable, IAsyncDisposable
{
    protected readonly DbInterfaceBase Db;
    private readonly Action<bool, double, string> _processCallback;
    private readonly IDbConnection _connection;
    private readonly HashSet<int> _importedWorkIds = new();
    private readonly HashSet<int> _importedTagIds = new();

    protected BaseMigrator(DbInterfaceBase db, IDbConnection connection, Action<bool, double, string> processCallback)
    {
        Db = db;
        _connection = connection;
        _processCallback = processCallback;
    }

    protected IDbCommand CreateCommand(string sql)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }

    protected abstract string ReadDate(IDataReader reader, int ordinal);
    protected abstract long ReadColorValue(IDataReader reader, int ordinal);

    /// <summary>
    /// DiaryToolpp 使用 ImGui 的 IM_COL32，将颜色以 AABBGGRR 形式存入数据库；
    /// Diary Tools NG 的核心模型使用 0xRRGGBB，因此需要在迁移时转换通道顺序。
    /// </summary>
    protected static int ConvertLegacyColor(long value)
    {
        var packed = unchecked((uint)value);
        var red = packed & 0xFF;
        var green = packed & 0xFF00;
        var blue = (packed >> 16) & 0xFF;
        return (int)((red << 16) | green | blue);
    }

    protected void Ok(double progress, string message)
    {
        _processCallback(true, progress, message);
    }

    protected void Fail(string message)
    {
        _processCallback(false, 1.0, message);
    }

    private bool CheckVersion()
    {
        using var command = CreateCommand("SELECT version_code FROM _version_ ORDER BY version_code DESC LIMIT 1;");
        var result = command.ExecuteScalar();
        return result != null && Convert.ToInt32(result) == 0x50000;
    }

    private bool SyncWorks(double p)
    {
        using var command = CreateCommand(
            "SELECT work_id, hour, comment, note, create_date, priority FROM work_items;");
        using var reader = command.ExecuteReader();
        var cnt = 1;
        while (reader.Read())
        {
            Ok(p, $"处理第{cnt++}条工作记录");
            var workId = reader.GetInt32(0);
            var time = reader.GetDouble(1);
            var comment = reader.GetString(2);
            var note = reader.IsDBNull(3) ? null : reader.GetString(3);
            var date = ReadDate(reader, 4);
            var priority = reader.GetInt32(5);

            var item = Db.CreateWorkItem(date, comment);
            item.Time = time;
            item.Priority = (WorkPriorities)priority;
            if (!Db.UpdateWorkItem(item))
                return false;

            if (item.Id != workId)
            {
                if (!Db.UpdateWorkItemId(item.Id, workId))
                    return false;
                item.Id = workId;
            }

            if (!string.IsNullOrWhiteSpace(note))
                Db.WorkUpdateNote(item, note);

            _importedWorkIds.Add(item.Id);
        }

        return true;
    }

    private bool SyncTags(double p)
    {
        using var command = CreateCommand(
            "SELECT tag_id, tag_name, tag_color, tag_level, tag_disabled FROM tags;");
        using var reader = command.ExecuteReader();
        var cnt = 1;
        while (reader.Read())
        {
            Ok(p, $"处理第{cnt++}个标签");
            var tagId = reader.GetInt32(0);
            var disabled = reader.GetInt32(4) != 0;
            var tag = Db.CreateWorkTag(reader.GetString(1), reader.GetInt32(3) == 0,
                ConvertLegacyColor(ReadColorValue(reader, 2)));
            if (tag.Id == 0)
                return false;

            if (disabled)
            {
                tag.Disabled = true;
                if (!Db.UpdateWorkTag(tag))
                    return false;
            }

            if (tag.Id != tagId && !Db.UpdateWorkTagId(tag.Id, tagId))
                return false;

            _importedTagIds.Add(tagId);
        }

        return true;
    }

    private bool SyncWorkTags(double p)
    {
        using var command = CreateCommand("SELECT work_id, tag_id FROM work_item_tags;");
        using var reader = command.ExecuteReader();
        var cnt = 1;
        var skipped = 0;
        var missingWorks = 0;
        var missingTags = 0;
        while (reader.Read())
        {
            Ok(p, $"处理第{cnt++}条标签组");
            var workId = reader.GetInt32(0);
            var tagId = reader.GetInt32(1);
            var missingWork = !_importedWorkIds.Contains(workId);
            var missingTag = !_importedTagIds.Contains(tagId);
            if (missingWork || missingTag)
            {
                skipped++;
                missingWorks += missingWork ? 1 : 0;
                missingTags += missingTag ? 1 : 0;
                continue;
            }

            if (!Db.WorkItemAddTag(new WorkItem { Id = workId }, new WorkTag { Id = tagId }))
                return false;
        }

        if (skipped > 0)
        {
            Ok(p,
                $"已跳过{skipped}条悬空标签关联（缺失工作记录{missingWorks}条，缺失标签{missingTags}条）");
        }

        return true;
    }

    private bool MarkImportedWorksReadOnly()
    {
        foreach (var workId in _importedWorkIds)
        {
            if (!Db.MarkWorkItemReadOnly(new WorkItem { Id = workId }))
                return false;
        }

        return true;
    }

    public bool DoMigrate()
    {
        Ok(0, "正在检查数据版本");
        if (!CheckVersion())
        {
            Fail("数据库版本错误，确保位于版本5.0.0");
            return false;
        }

        Ok(0.05, "数据版本校验通过，准备导入统计数据。");
        if (!Db.DropData())
        {
            Fail("清空当前数据失败");
            return false;
        }

        Ok(0.1, "数据已清空");
        Ok(0.2, "正在导入标签");
        if (!SyncTags(0.2))
        {
            Fail("导入标签失败");
            return false;
        }

        Ok(0.5, "正在导入工作记录");
        if (!SyncWorks(0.5))
        {
            Fail("导入工作记录失败");
            return false;
        }

        Ok(0.75, "正在导入工作记录标签");
        if (!SyncWorkTags(0.75))
        {
            Fail("导入工作记录标签失败");
            return false;
        }

        Ok(0.9, "正在标记迁移记录为只读");
        if (!MarkImportedWorksReadOnly())
        {
            Fail("标记迁移记录为只读失败");
            return false;
        }

        Ok(1.0, "迁移完成，记录可用于统计且不可编辑");
        return true;
    }

    public virtual void Dispose()
    {
        _connection.Dispose();
    }

    public virtual async ValueTask DisposeAsync()
    {
        if (_connection is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else
            _connection.Dispose();
    }
}
