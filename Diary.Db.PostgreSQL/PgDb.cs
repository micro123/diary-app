using System.Diagnostics;
using Diary.Core.Data.Base;
using Diary.Core.Data.Display;
using Diary.Core.Data.RedMine;
using Diary.Core.Data.Statistics;
using Diary.Database;
using Npgsql;

namespace Diary.Db.PostgreSQL;

internal static class NpgsqlDataReaderExtensions
{
    public static string GetStringTrimmed(this NpgsqlDataReader reader, int ordinal)
    {
        var value = reader.GetString(ordinal);
        return value.TrimEnd();
    }
}

public sealed class PgDb(IDbFactory factory) : DbInterfaceBase, IDisposable, IAsyncDisposable
{
    private readonly IDbFactory _factory = factory;
    private NpgsqlDataSource? _dataSource;
    public override bool Connect()
    {
        var cfg = _factory.GetConfig() as Config;
        Debug.Assert(cfg != null);
        var csb = new NpgsqlConnectionStringBuilder()
        {
            ApplicationName = "DiaryAppNG",
            Host = cfg.Host,
            Port = cfg.Port,
            Database = cfg.Database,
            Username = cfg.User,
            Password = cfg.Password,
            CommandTimeout = 5,
        };
        
        try
        {
            var dsb = new NpgsqlSlimDataSourceBuilder(csb.ConnectionString);
            _dataSource = dsb.Build();
        }
        catch (Exception)
        {
            return false;
        }

        return _dataSource != null;
    }

    #region helpers

    private static WorkTag MapWorkTag(NpgsqlDataReader reader)
    {
        return new WorkTag()
        {
            Id = reader.GetInt32(0),
            Name = reader.GetStringTrimmed(1),
            Color = reader.GetInt32(2),
            Level = (TagLevels)reader.GetInt32(3),
            Disabled = reader.GetInt32(4) != 0,
        };
    }

    private static WorkItem MapWorkItem(NpgsqlDataReader reader)
    {
        return new WorkItem()
        {
            Id =  reader.GetInt32(0),
            CreateDate = reader.GetStringTrimmed(1),
            Comment = reader.GetStringTrimmed(2),
            Time = reader.GetFloat(3),
            Priority = (WorkPriorities)reader.GetInt32(4),
        };
    }
    
    private RedMineActivity MapRedMineActivity(NpgsqlDataReader reader)
    {
        return new RedMineActivity()
        {
            Id = reader.GetInt32(0),
            Title = reader.GetStringTrimmed(1),
        };
    }
    
    private RedMineProject MapRedMineProject(NpgsqlDataReader reader)
    {
        return new RedMineProject()
        {
            Id = reader.GetInt32(0),
            Title = reader.GetStringTrimmed(1),
            Description = reader.GetStringTrimmed(2),
            IsClosed = reader.GetInt32(3) != 0,
        };
    }
    
    private RedMineIssue MapRedMineIssue(NpgsqlDataReader reader)
    {
        return new RedMineIssue()
        {
            Id = reader.GetInt32(0),
            Title = reader.GetStringTrimmed(1),
            AssignedTo = reader.GetStringTrimmed(2),
            ProjectId = reader.GetInt32(3),
            IsClosed = reader.GetInt32(4) != 0,
        };
    }
    
    private WorkTimeEntry MapWorkTimeEntry(NpgsqlDataReader reader)
    {
        return new WorkTimeEntry()
        {
            WorkId = reader.GetInt32(0),
            EntryId = reader.GetInt32(1),
            ActivityId = reader.GetInt32(2),
            IssueId = reader.GetInt32(3),
        };
    }


    private NpgsqlCommand Command(string statement)
    {
        return _dataSource!.CreateCommand(statement);
    }
    #endregion
    
    public override bool Initialized()
    {
        var sql = """
                  CREATE TABLE IF NOT EXISTS WORK_TAGS (
                  	ID SERIAL PRIMARY KEY,
                  	TAG_NAME CHAR(64) NOT NULL UNIQUE,
                  	TAG_COLOR INTEGER NOT NULL DEFAULT 0,
                  	TAG_LEVEL INTEGER NOT NULL DEFAULT 0,
                  	IS_DISABLED INTEGER NOT NULL DEFAULT 0
                  );
                  
                  CREATE TABLE IF NOT EXISTS WORK_ITEMS (
                  	ID SERIAL PRIMARY KEY,
                  	CREATE_DATE CHAR(16) NOT NULL,
                  	COMMENT CHAR(128) NOT NULL,
                  	HOURS REAL DEFAULT 0.0,
                  	PRIORITY INTEGER DEFAULT 0
                  );
                  
                  CREATE TABLE IF NOT EXISTS WORK_NOTES (
                  	ID INTEGER PRIMARY KEY REFERENCES WORK_ITEMS (ID) ON DELETE CASCADE,
                  	NOTE TEXT NOT NULL
                  );
                  
                  CREATE TABLE IF NOT EXISTS WORK_ITEM_TAGS (
                  	WORK_ID INTEGER REFERENCES WORK_ITEMS (ID) ON DELETE CASCADE,
                  	TAG_ID INTEGER REFERENCES WORK_TAGS (ID) ON DELETE CASCADE,
                  	PRIMARY KEY (WORK_ID, TAG_ID)
                  );
                  
                  CREATE TABLE IF NOT EXISTS REDMINE_PROJECTS (
                  	ID INTEGER NOT NULL PRIMARY KEY,
                  	PROJECT_NAME CHAR(128) NOT NULL,
                  	PROJECT_DESC CHAR(1024) DEFAULT '',
                  	IS_CLOSED INTEGER DEFAULT 0
                  );
                  
                  CREATE TABLE IF NOT EXISTS REDMINE_ACTIVITIES (
                  	ID INTEGER PRIMARY KEY,
                  	ACT_NAME CHAR(32) NOT NULL
                  );
                  
                  CREATE TABLE IF NOT EXISTS REDMINE_ISSUES (
                  	ID INTEGER PRIMARY KEY,
                  	ISSUE_TITLE CHAR(128) NOT NULL,
                  	ASSIGNED_TO CHAR(16) DEFAULT '',
                  	PROJECT_ID INTEGER NOT NULL REFERENCES REDMINE_PROJECTS (ID) ON DELETE CASCADE,
                  	IS_CLOSED INTEGER DEFAULT 0
                  );
                  
                  CREATE TABLE IF NOT EXISTS REDMINE_TIME_ENTRIES (
                  	WORK_ID INTEGER PRIMARY KEY REFERENCES WORK_ITEMS (ID) ON DELETE CASCADE,
                  	ID INTEGER DEFAULT 0,
                  	ACT_ID INTEGER REFERENCES REDMINE_ACTIVITIES (ID) ON DELETE CASCADE,
                  	ISSUE_ID INTEGER REFERENCES REDMINE_ISSUES (ID) ON DELETE CASCADE
                  );
                  
                  CREATE TABLE IF NOT EXISTS DATA_VERSIONS (VERSION_CODE INTEGER PRIMARY KEY);
                  
                  -- default data version is 1.0.0 (0x1000000)
                  INSERT
                  	INTO DATA_VERSIONS
                  VALUES
                  	(0x10000)
                  ON CONFLICT (VERSION_CODE)
                  	DO NOTHING;
                  """;
        using var cmd = Command(sql);
        try
        {
            cmd.ExecuteNonQuery();
        }
        catch (Exception)
        {
            return false;
        }

        return true;
    }

    public override bool KeepAlive()
    {
        using var cmd = Command("select version();");
        return cmd.ExecuteNonQuery() > 0;
    }

    public override void Close()
    {
        _dataSource!.Dispose();
        _dataSource = null;
    }

    public override uint GetDataVersion()
    {
        using var cmd = Command("SELECT * FROM data_versions ORDER BY version_code DESC LIMIT 1;");
        var result = cmd.ExecuteScalar();
        return result != null ? Convert.ToUInt32(result) : 0;
    }

    public override bool UpdateTables(uint targetVersion)
    {
        var currentVersion = GetDataVersion();
        while (currentVersion != targetVersion)
        {
            var migration = _factory.GetMigration(currentVersion);
            if (migration == null)
                return false;
            migration.Up(this);
            currentVersion = GetDataVersion();
        }

        return true;
    }

    public override WorkTag CreateWorkTag(string name, bool primary, int color)
    {
        var sql = """
                  INSERT INTO work_tags(tag_name, tag_level, tag_color) values ($1, $2, $3) returning *;
                  """;
        using var cmd = Command(sql);
        cmd.Parameters.AddWithValue(name);
        cmd.Parameters.AddWithValue(primary ? 0 : 1);
        cmd.Parameters.AddWithValue(color);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return MapWorkTag(reader);
        }
        return new WorkTag();
    }

    public override bool UpdateWorkTag(WorkTag tag)
    {
        if (tag.Id == 0)
            return false;

        var sql = """
                  UPDATE work_tags SET tag_level=$1, tag_color=$2, is_disabled=$3
                  WHERE id=$4;
                  """;
        using var cmd = Command(sql);
        cmd.Parameters.AddWithValue((int)tag.Level);
        cmd.Parameters.AddWithValue(tag.Color);
        cmd.Parameters.AddWithValue(tag.Disabled ? 1 : 0);
        cmd.Parameters.AddWithValue(tag.Id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public override bool DeleteWorkTag(WorkTag tag)
    {
        if (tag.Id == 0)
            return false;

        var sql = """
                  DELETE FROM work_tags WHERE id=$1;
                  """;
        using var cmd = Command(sql);
        cmd.Parameters.AddWithValue(tag.Id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public override ICollection<WorkTag> AllWorkTags()
    {
        var sql = "SELECT * FROM work_tags ORDER BY is_disabled, tag_level, id;";
        
        var result = new List<WorkTag>();
        using var cmd = Command(sql);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(MapWorkTag(reader));
        }
        
        return result;
    }

    public override WorkItem CreateWorkItem(string date, string comment)
    {
        var sql = """
                  INSERT INTO work_items(create_date, comment) VALUES ($1, $2) returning *;
                  """;
        using var cmd = Command(sql);
        cmd.Parameters.AddWithValue(date);
        cmd.Parameters.AddWithValue(comment);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? MapWorkItem(reader) : new WorkItem();
    }

    public override bool UpdateWorkItem(WorkItem item)
    {
        if (item.Id == 0)
            return false;

        var sql = """
                  UPDATE work_items SET create_date=$1, comment=$2, hours=$3, priority=$4  WHERE id=$5;
                  """;
        using var cmd = Command(sql);
        cmd.Parameters.AddWithValue(item.CreateDate);
        cmd.Parameters.AddWithValue(item.Comment);
        cmd.Parameters.AddWithValue(item.Time);
        cmd.Parameters.AddWithValue((int)item.Priority);
        cmd.Parameters.AddWithValue(item.Id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public override bool DeleteWorkItem(WorkItem item)
    {
        if (item.Id == 0)
            return false;
        
        var sql = """
                  DELTE FROM work_items WHERE id=$1;
                  """;
        using var cmd = Command(sql);
        cmd.Parameters.AddWithValue(item.Id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public override ICollection<WorkItem> GetWorkItemByDateRange(string beginData, string endData)
    {
        var sql = """
                  SELECT *
                  FROM work_items
                  WHERE create_date BETWEEN $1 AND $2
                  ORDER BY priority;
                  """;
        using var cmd = Command(sql);
        cmd.Parameters.AddWithValue(beginData);
        cmd.Parameters.AddWithValue(endData);
        using var reader = cmd.ExecuteReader();
        var result = new List<WorkItem>();
        while (reader.Read())
        {
            result.Add(MapWorkItem(reader));
        }

        return result;
    }

    public override ICollection<WorkItem> GetWorkItemByDate(string date)
    {
        const string sql = @"SELECT * FROM work_items WHERE create_date=$1 ORDER BY priority;";
        var cmd = Command(sql);
        cmd.Parameters.AddWithValue(date);
        using var reader = cmd.ExecuteReader();
        List<WorkItem> items = new();
        while (reader.Read())
        {
            items.Add(MapWorkItem(reader));
        }

        return items;
    }

    public override void WorkUpdateNote(WorkItem work, string content)
    {
        if (work.Id == 0)
            throw new ArgumentNullException(nameof(work.Id));

        var sql = """
                  INSERT INTO work_notes(id, note) VALUES ($1, $2) ON CONFLICT(id) DO UPDATE SET note=$2;
                  """;
        using var cmd = Command(sql);
        cmd.Parameters.AddWithValue(work.Id);
        cmd.Parameters.AddWithValue(content);
        cmd.ExecuteNonQuery();
    }

    public override void WorkDeleteNote(WorkItem work)
    {
        if (work.Id == 0)
            throw new ArgumentNullException(nameof(work.Id));

        var sql = """
                  DELETE FROM work_notes WHERE id=$1;
                  """;
        using var cmd = Command(sql);
        cmd.Parameters.AddWithValue(work.Id);
        cmd.ExecuteNonQuery();
    }

    public override string? WorkGetNote(WorkItem work)
    {
        if (work.Id == 0)
            throw new ArgumentNullException(nameof(work.Id));

        var sql = """
                  SELECT note FROM work_notes WHERE id=$1;
                  """;
        using var cmd = Command(sql);
        cmd.Parameters.AddWithValue(work.Id);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            return reader.GetStringTrimmed(0);
        return null;
    }

    public override bool WorkItemAddTag(WorkItem item, WorkTag tag)
    {
        if (item.Id == 0 ||  tag.Id == 0)
            throw new ArgumentException($"{nameof(item.Id)} or {nameof(tag.Id)} is required");

        var sql = """
                  INSERT INTO work_item_tags(work_id, tag_id) VALUES ($1, $2);
                  """;
        using var cmd = Command(sql);
        cmd.Parameters.AddWithValue(item.Id);
        cmd.Parameters.AddWithValue(tag.Id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public override bool WorkItemRemoveTag(WorkItem item, WorkTag tag)
    {
        if (item.Id == 0 ||  tag.Id == 0)
            throw new ArgumentException($"{nameof(item.Id)} or {nameof(tag.Id)} is required");

        var sql = """
                  DELETE FROM work_item_tags WHERE work_id=$1 AND tag_id=$2;
                  """;
        using var cmd = Command(sql);
        cmd.Parameters.AddWithValue(item.Id);
        cmd.Parameters.AddWithValue(tag.Id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public override bool WorkItemCleanTags(WorkItem item)
    {
        if (item.Id == 0)
            throw new ArgumentNullException(nameof(item.Id));

        var sql = """
                  DELETE FROM work_item_tags WHERE work_id=$1;
                  """;
        using var cmd = Command(sql);
        cmd.Parameters.AddWithValue(item.Id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public override ICollection<WorkTag> GetWorkItemTags(WorkItem item)
    {
        if (item.Id == 0)
            throw new ArgumentNullException(nameof(item.Id));

        var sql = """
                  SELECT work_tags.* 
                  FROM work_item_tags INNER JOIN work_tags ON work_item_tags.tag_id=work_tags.id
                  WHERE work_item_tags.work_id = $1
                  ORDER BY work_tags.tag_level;
                  """;
        var result = new List<WorkTag>();
        using var cmd = Command(sql);
        cmd.Parameters.AddWithValue(item.Id);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(MapWorkTag(reader));
        }
        return result;
    }

    public override RedMineActivity AddRedMineActivity(int id, string title)
    {
        var sql = """
                  INSERT INTO redmine_activities(id, act_name) VALUES ($1,$2) ON CONFLICT (id) DO UPDATE SET act_name=$2 RETURNING *;
                  """;
        using var cmd = Command(sql);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(title);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return MapRedMineActivity(reader);
        }

        return new RedMineActivity();
    }

    public override RedMineIssue AddRedMineIssue(int id, string title, string assignedTo, int project, bool closed = false)
    {
        var sql = """
                  INSERT INTO redmine_issues(id, issue_title, assigned_to, project_id, is_closed)
                  VALUES ($1,$2,$3,$4,$5) ON CONFLICT(id) DO UPDATE SET
                  issue_title=$2,assigned_to=$3,project_id=$4,is_closed=$5 RETURNING *;
                  """;
        using var cmd = Command(sql);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(title);
        cmd.Parameters.AddWithValue(assignedTo);
        cmd.Parameters.AddWithValue(project);
        cmd.Parameters.AddWithValue(closed ? 1 : 0);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
            return MapRedMineIssue(reader);
        return new RedMineIssue();
    }
    
    public override void UpdateRedMineIssueStatus(int id, bool closed)
    {
        var sql = """
                  UPDATE redmine_issues SET is_closed=$2 WHERE id=$1;
                  """;
        using var cmd = Command(sql);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(closed ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public override RedMineProject AddRedMineProject(int id, string title, string description)
    {
        var sql = """
                  INSERT INTO redmine_projects(id, project_name, project_desc)
                  VALUES ($1,$2,$3) ON CONFLICT (id) DO UPDATE SET project_name=$2,project_desc=$3 RETURNING *;
                  """;
        using var cmd = Command(sql);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(title);
        cmd.Parameters.AddWithValue(description);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
            return MapRedMineProject(reader);
        return new RedMineProject();
    }

    public override void UpdateRedMineProjectStatus(int id, bool closed)
    {
        var sql = """
                  UPDATE redmine_projects SET is_closed=$2 WHERE id=$1;
                  """;
        using var cmd = Command(sql);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(closed ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public override WorkTimeEntry? WorkItemGetTimeEntry(WorkItem item)
    {
        if (item.Id == 0)
            throw new ArgumentNullException(nameof(item.Id));

        var sql = """
                  SELECT * FROM redmine_time_entries WHERE work_id=$1;
                  """;
        using var cmd = Command(sql);
        cmd.Parameters.AddWithValue(item.Id);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
            return MapWorkTimeEntry(reader);
        return null;
    }

    public override bool WorkItemWasUploaded(WorkItem item)
    {
        if (item.Id == 0)
            throw new ArgumentNullException(nameof(item.Id));

        var sql = """
                  SELECT * FROM redmine_time_entries WHERE work_id=$1 AND id>0;
                  """;
        var cmd = Command(sql);
        cmd.Parameters.AddWithValue(item.Id);
        return  cmd.ExecuteNonQuery() > 0;
    }

    public override ICollection<RedMineActivity> GetRedMineActivities()
    {
        var sql = """
                  SELECT * FROM redmine_activities;
                  """;
        using var cmd = Command(sql);
        using var reader = cmd.ExecuteReader();
        var activities = new List<RedMineActivity>();
        while (reader.Read())
            activities.Add(MapRedMineActivity(reader));
        return activities;
    }

    public override ICollection<RedMineIssueDisplay> GetRedMineIssues(RedMineProject? project)
    {
        static RedMineIssueDisplay MapRedMineIssueDisplay(NpgsqlDataReader reader)
        {
            return new RedMineIssueDisplay()
            {
                Id = reader.GetInt32(0),
                Title = reader.GetStringTrimmed(1),
                AssignedTo = reader.GetStringTrimmed(2),
                Project = reader.GetStringTrimmed(3),
                Disabled = reader.GetInt32(4) != 0,
            };
        }
        
        var issues = new List<RedMineIssueDisplay>();
        if (project == null)
        {
            var sql = """
                      SELECT redmine_issues.id,redmine_issues.issue_title,redmine_issues.assigned_to,redmine_projects.project_name,redmine_issues.is_closed
                      FROM redmine_issues INNER JOIN redmine_projects ON redmine_issues.project_id = redmine_projects.id
                      ORDER BY redmine_issues.is_closed, redmine_issues.id DESC;
                      """;
            using var cmd = Command(sql);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                issues.Add(MapRedMineIssueDisplay(reader));
            }
        }
        else
        {
            var sql = """
                      SELECT redmine_issues.id,redmine_issues.issue_title,redmine_issues.assigned_to,redmine_projects.project_name,redmine_issues.is_closed
                      FROM redmine_issues INNER JOIN redmine_projects ON redmine_issues.project_id = redmine_projects.id AND redmine_issues.project_id=$1
                      ORDER BY redmine_issues.is_closed, redmine_issues.id DESC;
                      """;
            using var cmd = Command(sql);
            cmd.Parameters.AddWithValue(project.Id);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                issues.Add(MapRedMineIssueDisplay(reader));
            }
        }
        return issues;
    }

    public override ICollection<RedMineProject> GetRedMineProjects()
    {
        var sql = """
                  SELECT * FROM redmine_projects ORDER BY id DESC;
                  """;
        using var cmd = Command(sql);
        using var reader = cmd.ExecuteReader();
        var projects = new List<RedMineProject>();
        while (reader.Read())
        {
            projects.Add(MapRedMineProject(reader));
        }
        return projects;
    }

    public override WorkTimeEntry? CreateWorkTimeEntry(int work, int activity, int issus)
    {
        var sql = """
                  INSERT INTO redmine_time_entries(work_id, act_id, issue_id) VALUES ($1, $2, $3)
                  ON CONFLICT (work_id) DO UPDATE SET act_id=$2, issue_id=$3 RETURNING *;
                  """;
        using var cmd = Command(sql);
        cmd.Parameters.AddWithValue(work);
        cmd.Parameters.AddWithValue(activity);
        cmd.Parameters.AddWithValue(issus);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? MapWorkTimeEntry(reader) : null;
    }

    public override bool UpdateWorkTimeEntry(WorkTimeEntry timeEntry)
    {
        if (timeEntry.WorkId == 0)
            throw new ArgumentException("Work time entry must have a valid id");

        var sql = """
                  UPDATE redmine_time_entries SET id=$1,act_id=$2,issue_id=$3 WHERE work_id=$4;
                  """;
        using var cmd = Command(sql);
        cmd.Parameters.AddWithValue(timeEntry.EntryId);
        cmd.Parameters.AddWithValue(timeEntry.ActivityId);
        cmd.Parameters.AddWithValue(timeEntry.IssueId);
        cmd.Parameters.AddWithValue(timeEntry.WorkId);
        return cmd.ExecuteNonQuery() > 0;
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
            var dateRangeQuery = "SELECT sum(hours) FROM work_items WHERE create_date BETWEEN $1 AND $2;";
            using var cmd = Command(dateRangeQuery);
            cmd.Parameters.AddWithValue(beginDate);
            cmd.Parameters.AddWithValue(endDate);
            using var reader = cmd.ExecuteReader();
            if (reader.Read() && !reader.IsDBNull(0))
                result.Total = reader.GetFloat(0);
        }
        
        if (result.Total > 0)
        {
            var sql = """
                      SELECT work_tags.id AS tid, sum(hours) AS total, tag_name 
                      FROM 
                      	((work_item_tags INNER JOIN
                      			(SELECT id,hours FROM work_items WHERE create_date BETWEEN $1 AND $2) AS T1
                      		ON work_item_tags.work_id=T1.id) AS T2
                      	INNER JOIN work_tags ON work_tags.id=T2.tag_id AND work_tags.tag_level=0)
                      GROUP BY tid;
                      """;

            // 一级标签
            using var cmd = Command(sql);
            cmd.Parameters.AddWithValue(beginDate);
            cmd.Parameters.AddWithValue(endDate);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.PrimaryTags.Add(new TagTime()
                {
                    TagId = reader.GetInt32(0),
                    Time = reader.GetFloat(1),
                    TagName = reader.GetStringTrimmed(2),
                    Nested = new List<TagTime>(),
                });
            }
        }

        foreach (var tag in result.PrimaryTags)
        {
            var sql = """
                      SELECT
                      	work_tags.id, sum(hours) as total, work_tags.tag_name
                      FROM
                      	((((SELECT work_id FROM work_item_tags WHERE tag_id=$3) AS T0 INNER JOIN
                      	work_item_tags ON t0.work_id=work_item_tags.work_id AND work_item_tags.tag_id!=$3) INNER JOIN
                      	(SELECT id, hours FROM work_items WHERE create_date BETWEEN $1 AND $2) AS T1 ON T0.work_id=T1.id) AS T2 INNER JOIN
                      	work_tags ON work_tags.id=T2.tag_id AND work_tags.tag_level!=0)
                      GROUP BY work_tags.id;
                      """;
            
            using var cmd = Command(sql);
            cmd.Parameters.AddWithValue(beginDate);
            cmd.Parameters.AddWithValue(endDate);
            cmd.Parameters.AddWithValue(tag.TagId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                tag.Nested.Add(new TagTime()
                {
                    TagId = reader.GetInt32(0),
                    Time = reader.GetFloat(1),
                    TagName = reader.GetStringTrimmed(2),
                });
            }
        }
        
        return result;
    }

    public override StatisticsResult GetStatistics()
    {
        // get date range
        var sql = "SELECT min(create_date), max(create_date) FROM work_items;";
        using var cmd = Command(sql);
        using var reader = cmd.ExecuteReader();
        if (reader.Read() && !reader.IsDBNull(0))
        {
            var beginDate = reader.GetStringTrimmed(0);
            var endDate = reader.GetStringTrimmed(1);
            return GetStatistics(beginDate, endDate);
        }

        // empty result
        return new StatisticsResult()
        {
            DateBegin = string.Empty,
            DateEnd = string.Empty,
            Total = 0,
            PrimaryTags = Array.Empty<TagTime>(),
        };
    }
    
    public override ICollection<WorkItem> GetWorkItemsByTagAndDate(string dateBegin, string dateEnd, int l1, int l2 = 0)
    {
        var result = new List<WorkItem>();
        if (l2 == 0)
        {
            var sql = """
                      SELECT work_items.* FROM
                      (work_items INNER JOIN work_item_tags on work_items.id = work_item_tags.work_id)
                      WHERE work_item_tags.tag_id = $1 AND work_items.create_date BETWEEN $2 AND $3
                      ORDER BY create_date, priority, id;
                      """;
            using var cmd = Command(sql);
            cmd.Parameters.AddWithValue(l1);
            cmd.Parameters.AddWithValue(dateBegin);
            cmd.Parameters.AddWithValue(dateEnd);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new WorkItem()
                {
                    Id = reader.GetInt32(0),
                    CreateDate = reader.GetStringTrimmed(1),
                    Comment = reader.GetStringTrimmed(2),
                    Time = reader.GetFloat(3),
                    Priority = (WorkPriorities)reader.GetInt32(4),
                });
            }
        }
        else
        {
            var sql = """
                      SELECT work_items.* FROM
                      work_items INNER JOIN
                      (SELECT work_item_tags.work_id FROM
                      	(SELECT work_id FROM work_item_tags WHERE tag_id=$3) AS T0
                      	INNER JOIN work_item_tags ON T0.work_id = work_item_tags.work_id AND work_item_tags.tag_id=$4) AS T1
                      	ON work_items.id=T1.work_id WHERE create_date BETWEEN $1 AND $2
                      ORDER BY create_date, priority, id;
                      """;
            using var cmd = Command(sql);
            cmd.Parameters.AddWithValue(dateBegin);
            cmd.Parameters.AddWithValue(dateEnd);
            cmd.Parameters.AddWithValue(l1);
            cmd.Parameters.AddWithValue(l2);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new WorkItem()
                {
                    Id = reader.GetInt32(0),
                    CreateDate = reader.GetStringTrimmed(1),
                    Comment = reader.GetStringTrimmed(2),
                    Time = reader.GetFloat(3),
                    Priority = (WorkPriorities)reader.GetInt32(4),
                });
            }
        }
        return result;
    }

    public void Dispose()
    {
        _dataSource?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_dataSource != null) await _dataSource.DisposeAsync();
    }
}
