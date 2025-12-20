using System.Data.SQLite;
using Diary.Core.Data.Base;
using Diary.Database;
using Diary.RedMine;
using Diary.RedMine.Response;

namespace Diary.MigrationTool.Impl;

internal class SqliteMigrator : IDisposable, IAsyncDisposable
{
    private readonly DbInterfaceBase _db;
    private readonly Action<bool, double, string> _processCallback;
    private readonly SQLiteConnection _connection;

    public SqliteMigrator(DbInterfaceBase db, string oldDatabase, Action<bool, double, string> processCallback)
    {
        _db = db;
        _processCallback = processCallback;
        var csb = new SQLiteConnectionStringBuilder()
        {
            DataSource = oldDatabase,
            ReadOnly = true,
        };
        _connection = new SQLiteConnection(csb.ConnectionString);
        _connection.Open();
    }

    private bool CheckVersion()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT version_code FROM _version_ ORDER BY version_code DESC LIMIT 1;";
        var result = command.ExecuteScalar();
        return result != null && (int)result == 0x50000;
    }

    private bool SyncActivities(double p)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT * FROM redmine_activities;";
        using var reader = command.ExecuteReader();
        var cnt = 1;
        while (reader.Read())
        {
            Ok(p, $"处理第{cnt++}条活动信息");
            _db.AddRedMineActivity(reader.GetInt32(0), reader.GetString(1));
        }

        return true;
    }

    private bool SyncIssues(double p)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT issue_id, is_closed FROM redmine_issues;";
        using var reader = command.ExecuteReader();
        var cnt = 1;
        while (reader.Read())
        {
            Ok(p, $"处理第{cnt++}条问题记录");
            var issueId = reader.GetInt32(0);
            var isClosed = reader.GetInt32(4) != 0;
            if (RedMineApis.GetIssue(out IssueInfo? info, issueId))
            {
                var project = info.Project;
                if (RedMineApis.GetProject(out ProjectInfo? projectInfo, project.Id))
                {
                    _db.AddRedMineProject(projectInfo.Id, projectInfo.Name, projectInfo.Description); // 需要先导入项目
                    _db.AddRedMineIssue(issueId, info.Subject, info.AssignedTo.Name, projectInfo.Id, isClosed);
                }
                else
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    private bool SyncWorks(double p)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT * FROM work_items;";
        using var reader = command.ExecuteReader();
        var dummyId = 1;
        var cnt = 1;
        while (reader.Read())
        {
            Ok(p, $"处理第{cnt++}条工作记录");
            var workId = reader.GetInt32(0);
            var time = reader.GetDouble(1);
            var comment = reader.GetString(2);
            var note = reader.GetString(3);
            var date = reader.GetString(4);
            var actId = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);
            var issueId = reader.IsDBNull(6) ? 0 : reader.GetInt32(6);
            var uploaded = reader.GetInt32(7) != 0;
            var priority = reader.GetInt32(8);

            var item = _db.CreateWorkItem(date, comment);
            if (item.Id != workId)
            {
                item.Priority = (WorkPriorities)priority;
                item.Time = time;
                _db.UpdateWorkItem(item);
                _db.UpdateWorkItemId(item.Id, workId);
                item.Id = workId;
            }

            if (!string.IsNullOrWhiteSpace(note))
            {
                _db.WorkUpdateNote(item, note);
            }

            if (actId != 0 && issueId != 0)
            {
                var entry = _db.CreateWorkTimeEntry(item.Id, actId, issueId);
                if (entry != null && uploaded)
                {
                    entry.EntryId = dummyId++; // 给个假的 ID
                    _db.UpdateWorkTimeEntry(entry);
                }
            }
        }

        return true;
    }

    private bool SyncTags(double p)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT * FROM tags;";
        using var reader = command.ExecuteReader();
        var cnt = 1;
        while (reader.Read())
        {
            Ok(p, $"处理第{cnt++}个标签");
            var tagId = reader.GetInt32(0);
            var disabled = reader.GetInt32(4) != 0;
            var tag = _db.CreateWorkTag(reader.GetString(1), reader.GetInt32(3) == 0, reader.GetInt32(2));
            if (disabled)
            {
                tag.Disabled = true;
                _db.UpdateWorkTag(tag);
            }

            if (tag.Id != tagId)
            {
                _db.UpdateWorkTagId(tag.Id, tagId);
            }
        }

        return true;
    }

    private bool SyncWorkTags(double p)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT * FROM work_item_tags;";
        using var reader = command.ExecuteReader();
        var cnt = 1;
        while (reader.Read())
        {
            Ok(p, $"处理第{cnt++}条标签组");
            _db.WorkItemAddTag(new WorkItem() { Id = reader.GetInt32(0) }, new WorkTag() { Id = reader.GetInt32(1) });
        }

        return true;
    }

    private void Ok(double progress, string message)
    {
        _processCallback(true, progress, message);
    }

    private void Fail(string message)
    {
        _processCallback(false, 1.0, message);
    }

    public bool DoMigrate()
    {
        Ok(0, "正在检查数据版本");
        if (!CheckVersion())
        {
            Fail("数据库版本错误，确保位于版本5.0.0");
            return false;
        }

        Ok(0.05, "数据版本校验通过，准备导入数据。");

        if (!_db.DropData())
        {
            Fail("清空当前数据失败");
            return false;
        }

        Ok(0.1, "数据已清空");

        /*
         * 需要处理的数据有：
         * 1. redmine_activities 活动
         * 2. redmine_issues 问题
         * 3. tags 标签
         * 4. work_items 事件
         * 5. work_item_tags 事件标签
         */
        Ok(0.15, "正在导入活动列表");
        if (!SyncActivities(0.15))
        {
            Fail("活动导入失败");
            return false;
        }

        Ok(0.3, "正在导入问题列表");
        if (!SyncIssues(0.3))
        {
            Fail("导入问题失败");
            return false;
        }

        Ok(0.5, "正在导入标签");
        if (!SyncTags(0.5))
        {
            Fail("导入标签失败");
            return false;
        }

        Ok(0.7, "正在导入事件");
        if (!SyncWorks(0.7))
        {
            Fail("导入事件失败");
            return false;
        }

        Ok(0.9, "正在导入事件标签");
        if (!SyncWorkTags(0.9))
        {
            Fail("导入事件标签失败");
            return false;
        }

        Ok(1.0, "迁移完成");
        return true;
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }
}