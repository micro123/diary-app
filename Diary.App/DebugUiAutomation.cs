#if DEBUG
using System.Collections;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Diagnostics.Cdp;
using Diary.Core.Data.AppConfig;
using Diary.Core.Data.Base;
using Diary.Core.Utils;
using Diary.Database;
using Diary.PluginBase;
using Diary.Utils;
using PostgreSqlConfig = Diary.Db.PostgreSQL.Config;

namespace Diary.App;

internal static class DebugUiAutomation
{
    internal const string PortEnvironmentVariable = "DIARY_CDP_PORT";
    internal const string RootEnvironmentVariable = "DIARY_UI_TEST_ROOT";
    internal const string ScenarioEnvironmentVariable = "DIARY_UI_TEST_SCENARIO";
    internal const string PostgreSqlHostEnvironmentVariable = "DIARY_UI_TEST_PG_HOST";
    internal const string PostgreSqlPortEnvironmentVariable = "DIARY_UI_TEST_PG_PORT";
    internal const string PostgreSqlDatabaseEnvironmentVariable = "DIARY_UI_TEST_PG_DATABASE";
    internal const string PostgreSqlUserEnvironmentVariable = "DIARY_UI_TEST_PG_USER";
    internal const string PostgreSqlPasswordEnvironmentVariable = "DIARY_UI_TEST_PG_PASSWORD";
    internal const string RedmineServerUrlEnvironmentVariable = "DIARY_UI_TEST_REDMINE_URL";
    internal const string RedmineApiKeyEnvironmentVariable = "DIARY_UI_TEST_REDMINE_API_KEY";
    internal const string RedmineActivityIdsEnvironmentVariable = "DIARY_UI_TEST_REDMINE_ACTIVITY_IDS";
    internal const string RedmineIssueIdsEnvironmentVariable = "DIARY_UI_TEST_REDMINE_ISSUE_IDS";
    private const string ExtraFieldsScenario = "extra-fields";
    private const string DatePerformanceScenario = "date-performance";
    internal const string DatePerformanceTitlePrefix = "CDP日期性能";
    internal const int DatePerformanceDayCount = 540;
    internal const int DatePerformanceItemsPerDay = 48;
    internal const string DatePerformanceJiraInstanceId = "jira.cdp-performance";
    internal const string DatePerformanceRedmineInstanceId = "redmine.cdp-performance";
    private static bool _started;
    private static string _scenario = "default";

    public static string ConfigureProcess(string appId)
    {
        var configuredRoot = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredRoot))
            return appId;

        if (!Path.IsPathFullyQualified(configuredRoot))
            throw new ArgumentException("UI 测试根目录必须是绝对路径。", RootEnvironmentVariable);

        var root = Path.GetFullPath(configuredRoot);
        FsTools.SetApplicationRootForCurrentProcess(root);
        ApplyScenario(Environment.GetEnvironmentVariable(ScenarioEnvironmentVariable));
        Trace.WriteLine($"UI 测试数据已隔离到：{root}");
        return CreateIsolatedAppId(appId, root);
    }

    internal static string NormalizeScenario(string? value)
    {
        var scenario = value?.Trim().ToLowerInvariant();
        return scenario switch
        {
            null or "" or "default" => "default",
            "extended" => "extended",
            "survey" => "survey",
            "database-error" => "database-error",
            ExtraFieldsScenario => ExtraFieldsScenario,
            DatePerformanceScenario => DatePerformanceScenario,
            "plugins" => "plugins",
            _ => throw new ArgumentException($"未知 UI 测试场景：{value}", ScenarioEnvironmentVariable),
        };
    }

    private static void ApplyScenario(string? value)
    {
        var scenario = NormalizeScenario(value);
        _scenario = scenario;
        if (scenario == "default")
            return;

        var config = AllConfig.Instance;
        config.ViewSettings.HasCompletedOnboarding = true;
        config.UpdateSettings.AutoCheck = false;
        switch (scenario)
        {
            case "extended":
                config.ViewSettings.ShowDeveloperFeatures = true;
                break;
            case "survey":
                config.SurveySettings.Enabled = true;
                config.SurveySettings.AsServer = true;
                config.SurveySettings.ServerAddress = string.Empty;
                break;
            case "database-error":
                config.DbSettings.DatabaseDriver = "Diary.UiTest.MissingDatabase";
                break;
            case ExtraFieldsScenario:
            case "plugins":
                break;
            case DatePerformanceScenario:
                if (IsPostgreSqlDatePerformanceRequested(Environment.GetEnvironmentVariable))
                    config.DbSettings.DatabaseDriver = "PostgreSQL";
                break;
        }

        if (!EasySaveLoad.Save(config))
            throw new InvalidOperationException($"无法保存 UI 测试场景配置：{scenario}");
        Trace.WriteLine($"UI 测试场景已配置：{scenario}");
    }

    public static bool ApplyDatabaseConfiguration(string factoryName, object configuration)
        => ApplyPostgreSqlDatePerformanceConfiguration(
            _scenario,
            factoryName,
            configuration,
            Environment.GetEnvironmentVariable);

    internal static bool ApplyPostgreSqlDatePerformanceConfiguration(
        string scenario,
        string factoryName,
        object configuration,
        Func<string, string?> readEnvironment)
    {
        if (scenario != DatePerformanceScenario || !IsPostgreSqlDatePerformanceRequested(readEnvironment))
            return false;
        if (!string.Equals(factoryName, "PostgreSQL", StringComparison.Ordinal)
            || configuration is not PostgreSqlConfig config)
        {
            throw new InvalidOperationException("日期性能 UI 测试请求了 PostgreSQL，但当前数据库配置类型不匹配。");
        }

        config.Host = ReadRequiredEnvironment(readEnvironment, PostgreSqlHostEnvironmentVariable);
        config.Database = ReadRequiredEnvironment(readEnvironment, PostgreSqlDatabaseEnvironmentVariable);
        config.User = ReadRequiredEnvironment(readEnvironment, PostgreSqlUserEnvironmentVariable);
        config.Password = readEnvironment(PostgreSqlPasswordEnvironmentVariable) ?? string.Empty;
        var portText = readEnvironment(PostgreSqlPortEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(portText))
        {
            if (!ushort.TryParse(portText, out var port) || port == 0)
                throw new InvalidOperationException($"{PostgreSqlPortEnvironmentVariable} 必须是 1 到 65535 的端口。");
            config.Port = port;
        }

        Trace.WriteLine($"UI 日期性能场景使用 PostgreSQL：{config.Host}:{config.Port}/{config.Database}");
        return true;
    }

    public static bool ApplyTrackerConfiguration(string pluginId, object configuration)
    {
        if (_scenario != DatePerformanceScenario)
            return false;

        if (IsRedmineDatePerformanceRequested(Environment.GetEnvironmentVariable))
        {
            if (!string.Equals(pluginId, "tracker.redmine", StringComparison.Ordinal))
                return false;
            ConfigureSingleTrackerInstance(
                configuration,
                "Redmine",
                ("InstanceId", DatePerformanceRedmineInstanceId),
                ("DisplayName", "CDP Redmine 性能实例"),
                ("Icon", "fa-cloud"),
                ("Enabled", true),
                ("RedMineServerUrl", ReadRequiredEnvironment(
                    Environment.GetEnvironmentVariable,
                    RedmineServerUrlEnvironmentVariable)),
                ("RedMineApiKey", ReadRequiredEnvironment(
                    Environment.GetEnvironmentVariable,
                    RedmineApiKeyEnvironmentVariable)));
            Trace.WriteLine("UI 日期性能场景已启用 Redmine 测试实例。");
            return true;
        }

        if (!string.Equals(pluginId, "tracker.jira", StringComparison.Ordinal))
            return false;
        ConfigureSingleTrackerInstance(
            configuration,
            "Jira",
            ("InstanceId", DatePerformanceJiraInstanceId),
            ("DisplayName", "CDP Jira 性能实例"),
            ("Icon", "fa-tasks"),
            ("Enabled", true),
            ("ServerUrl", "http://127.0.0.1:9"),
            ("UserName", "cdp-offline"),
            ("ApiToken", "cdp-offline-placeholder"));
        Trace.WriteLine("UI 日期性能场景已启用离线 Jira 测试实例。");
        return true;
    }

    private static void ConfigureSingleTrackerInstance(
        object configuration,
        string trackerName,
        params (string Name, object Value)[] values)
    {
        var instancesProperty = configuration.GetType().GetProperty("Instances")
            ?? throw new InvalidOperationException($"{trackerName} 日期性能配置缺少 Instances 属性。");
        if (instancesProperty.GetValue(configuration) is not IList instances)
            throw new InvalidOperationException($"{trackerName} 日期性能配置的 Instances 不是可修改列表。");
        var instanceType = instancesProperty.PropertyType.GetGenericArguments().SingleOrDefault()
            ?? throw new InvalidOperationException($"无法确定 {trackerName} 日期性能实例类型。");
        var instance = Activator.CreateInstance(instanceType)
            ?? throw new InvalidOperationException($"无法创建 {trackerName} 日期性能实例配置。");
        foreach (var (name, value) in values)
            SetProperty(instance, name, value);
        instances.Clear();
        instances.Add(instance);
    }

    public static bool ApplyDatePerformanceTrackerScenario(
        DbInterfaceBase database,
        IEnumerable<ITrackerInstance> trackerInstances)
    {
        if (_scenario != DatePerformanceScenario)
            return false;

        var redmineEnabled = trackerInstances.Any(instance =>
            instance.PluginId == "tracker.redmine"
            && instance.InstanceId == DatePerformanceRedmineInstanceId);
        var jiraEnabled = trackerInstances.Any(instance =>
            instance.PluginId == "tracker.jira"
            && instance.InstanceId == DatePerformanceJiraInstanceId);
        if (!redmineEnabled && !jiraEnabled)
            return false;

        var isSqlite = database.ProviderName.Contains("SQLite", StringComparison.OrdinalIgnoreCase);
        return redmineEnabled
            ? ApplyDatePerformanceRedmineScenario(database, isSqlite)
            : ApplyDatePerformanceJiraScenario(database, isSqlite);
    }

    private static bool ApplyDatePerformanceJiraScenario(DbInterfaceBase database, bool isSqlite)
    {
        var host = (IDbExtensionHost)database;
        var expectedCount = DatePerformanceDayCount * DatePerformanceItemsPerDay / 5;
        var existingCount = Convert.ToInt32(host.ExecuteScalar(
            isSqlite
                ? "SELECT COUNT(*) FROM jira_work_entries WHERE instance_id=$instance_id;"
                : "SELECT COUNT(*) FROM jira_work_entries WHERE instance_id=$1;",
            (isSqlite ? "$instance_id" : "$1", DatePerformanceJiraInstanceId)));
        if (existingCount == expectedCount && HasExpectedSparseBindingDistribution(
                host, isSqlite, "jira_work_entries", DatePerformanceJiraInstanceId))
            return false;
        if (!database.BeginTransaction())
            throw new InvalidOperationException("无法开始日期性能 Tracker 测试数据事务。");

        var transactionCompleted = false;
        try
        {
            InsertJiraReferenceData(host, isSqlite);
            DeleteDatePerformanceBindings(host, isSqlite, "jira_work_entries", DatePerformanceJiraInstanceId);
            var sparseItems = BuildSparseWorkItemSelection(isSqlite ? "$title_like" : "$2");
            host.Execute(
                isSqlite
                    ? $"INSERT INTO jira_work_entries(instance_id, work_id, issue_key) SELECT $instance_id, id, 'CDP-' || ((id % 8) + 1) FROM ({sparseItems}) sparse_items;"
                    : $"INSERT INTO jira_work_entries(instance_id, work_id, issue_key) SELECT $1, id, 'CDP-' || ((id % 8) + 1) FROM ({sparseItems}) sparse_items;",
                (isSqlite ? "$instance_id" : "$1", DatePerformanceJiraInstanceId),
                (isSqlite ? "$title_like" : "$2", DatePerformanceTitlePrefix + " %"));
            var actualCount = Convert.ToInt32(host.ExecuteScalar(
                isSqlite
                    ? "SELECT COUNT(*) FROM jira_work_entries WHERE instance_id=$instance_id;"
                    : "SELECT COUNT(*) FROM jira_work_entries WHERE instance_id=$1;",
                (isSqlite ? "$instance_id" : "$1", DatePerformanceJiraInstanceId)));
            if (actualCount != expectedCount)
                throw new InvalidOperationException($"日期性能 Tracker 绑定数量不正确：{actualCount}/{expectedCount}。");
            var commitSuccess = database.CommitTransaction();
            transactionCompleted = true;
            if (!commitSuccess)
                throw new InvalidOperationException("无法提交日期性能 Tracker 测试数据事务。");
            Trace.WriteLine($"UI 日期性能场景已创建 {actualCount} 条稀疏 Jira 绑定。");
            return true;
        }
        finally
        {
            if (!transactionCompleted)
                database.RollbackTransaction();
        }
    }

    private static void InsertJiraReferenceData(IDbExtensionHost host, bool isSqlite)
    {
        host.Execute(
            isSqlite
                ? "INSERT INTO jira_projects(instance_id, project_key, project_name, project_desc) VALUES ($instance_id, 'CDP', 'CDP 性能项目', '离线性能测试') ON CONFLICT(instance_id, project_key) DO NOTHING;"
                : "INSERT INTO jira_projects(instance_id, project_key, project_name, project_desc) VALUES ($1, 'CDP', 'CDP 性能项目', '离线性能测试') ON CONFLICT(instance_id, project_key) DO NOTHING;",
            (isSqlite ? "$instance_id" : "$1", DatePerformanceJiraInstanceId));
        for (var id = 1; id <= 8; id++)
        {
            host.Execute(
                isSqlite
                    ? "INSERT INTO jira_issues(instance_id, issue_key, issue_title, project_key, project_name, status_name) VALUES ($instance_id, $key, $title, 'CDP', 'CDP 性能项目', '进行中') ON CONFLICT(instance_id, issue_key) DO NOTHING;"
                    : "INSERT INTO jira_issues(instance_id, issue_key, issue_title, project_key, project_name, status_name) VALUES ($1, $2, $3, 'CDP', 'CDP 性能项目', '进行中') ON CONFLICT(instance_id, issue_key) DO NOTHING;",
                (isSqlite ? "$instance_id" : "$1", DatePerformanceJiraInstanceId),
                (isSqlite ? "$key" : "$2", $"CDP-{id}"),
                (isSqlite ? "$title" : "$3", $"性能问题 #{id}"));
        }
    }

    private static bool ApplyDatePerformanceRedmineScenario(DbInterfaceBase database, bool isSqlite)
    {
        var activityIds = ReadPositiveIds(RedmineActivityIdsEnvironmentVariable);
        var issueIds = ReadPositiveIds(RedmineIssueIdsEnvironmentVariable);
        var host = (IDbExtensionHost)database;
        var expectedCount = DatePerformanceDayCount * DatePerformanceItemsPerDay / 5;
        var existingCount = Convert.ToInt32(host.ExecuteScalar(
            isSqlite
                ? "SELECT COUNT(*) FROM redmine_time_entries WHERE instance_id=$instance_id;"
                : "SELECT COUNT(*) FROM redmine_time_entries WHERE instance_id=$1;",
            (isSqlite ? "$instance_id" : "$1", DatePerformanceRedmineInstanceId)));
        if (existingCount == expectedCount && HasExpectedSparseBindingDistribution(
                host, isSqlite, "redmine_time_entries", DatePerformanceRedmineInstanceId))
            return false;
        if (!database.BeginTransaction())
            throw new InvalidOperationException("无法开始日期性能 Redmine 测试数据事务。");

        var transactionCompleted = false;
        try
        {
            InsertRedmineReferenceData(host, isSqlite, activityIds, issueIds);
            DeleteDatePerformanceBindings(host, isSqlite, "redmine_time_entries", DatePerformanceRedmineInstanceId);
            var activityCase = BuildModuloCase("id", activityIds);
            var issueCase = BuildModuloCase("id", issueIds);
            var sparseItems = BuildSparseWorkItemSelection(isSqlite ? "$title_like" : "$2");
            host.Execute(
                isSqlite
                    ? $"INSERT INTO redmine_time_entries(instance_id, work_id, act_id, issue_id) SELECT $instance_id, id, {activityCase}, {issueCase} FROM ({sparseItems}) sparse_items;"
                    : $"INSERT INTO redmine_time_entries(instance_id, work_id, act_id, issue_id) SELECT $1, id, {activityCase}, {issueCase} FROM ({sparseItems}) sparse_items;",
                (isSqlite ? "$instance_id" : "$1", DatePerformanceRedmineInstanceId),
                (isSqlite ? "$title_like" : "$2", DatePerformanceTitlePrefix + " %"));
            var actualCount = Convert.ToInt32(host.ExecuteScalar(
                isSqlite
                    ? "SELECT COUNT(*) FROM redmine_time_entries WHERE instance_id=$instance_id;"
                    : "SELECT COUNT(*) FROM redmine_time_entries WHERE instance_id=$1;",
                (isSqlite ? "$instance_id" : "$1", DatePerformanceRedmineInstanceId)));
            if (actualCount != expectedCount)
                throw new InvalidOperationException($"日期性能 Redmine 绑定数量不正确：{actualCount}/{expectedCount}。");
            var commitSuccess = database.CommitTransaction();
            transactionCompleted = true;
            if (!commitSuccess)
                throw new InvalidOperationException("无法提交日期性能 Redmine 测试数据事务。");
            Trace.WriteLine($"UI 日期性能场景已创建 {actualCount} 条稀疏 Redmine 绑定。");
            return true;
        }
        finally
        {
            if (!transactionCompleted)
                database.RollbackTransaction();
        }
    }

    private static void InsertRedmineReferenceData(
        IDbExtensionHost host,
        bool isSqlite,
        IReadOnlyList<int> activityIds,
        IReadOnlyList<int> issueIds)
    {
        host.Execute(
            isSqlite
                ? "INSERT INTO redmine_projects(instance_id, id, project_name, project_desc) VALUES ($instance_id, 1, 'DiaryApp API Test', 'CDP 性能测试') ON CONFLICT(instance_id, id) DO NOTHING;"
                : "INSERT INTO redmine_projects(instance_id, id, project_name, project_desc) VALUES ($1, 1, 'DiaryApp API Test', 'CDP 性能测试') ON CONFLICT(instance_id, id) DO NOTHING;",
            (isSqlite ? "$instance_id" : "$1", DatePerformanceRedmineInstanceId));
        foreach (var id in activityIds)
        {
            host.Execute(
                isSqlite
                    ? "INSERT INTO redmine_activities(instance_id, id, act_name) VALUES ($instance_id, $id, $name) ON CONFLICT(instance_id, id) DO NOTHING;"
                    : "INSERT INTO redmine_activities(instance_id, id, act_name) VALUES ($1, $2, $3) ON CONFLICT(instance_id, id) DO NOTHING;",
                (isSqlite ? "$instance_id" : "$1", DatePerformanceRedmineInstanceId),
                (isSqlite ? "$id" : "$2", id),
                (isSqlite ? "$name" : "$3", $"Redmine 活动 {id}"));
        }
        foreach (var id in issueIds)
        {
            host.Execute(
                isSqlite
                    ? "INSERT INTO redmine_issues(instance_id, id, issue_title, assigned_to, project_id) VALUES ($instance_id, $id, $title, 'Redmine Admin', 1) ON CONFLICT(instance_id, id) DO NOTHING;"
                    : "INSERT INTO redmine_issues(instance_id, id, issue_title, assigned_to, project_id) VALUES ($1, $2, $3, 'Redmine Admin', 1) ON CONFLICT(instance_id, id) DO NOTHING;",
                (isSqlite ? "$instance_id" : "$1", DatePerformanceRedmineInstanceId),
                (isSqlite ? "$id" : "$2", id),
                (isSqlite ? "$title" : "$3", $"Redmine 性能 Issue #{id}"));
        }
    }

    private static IReadOnlyList<int> ReadPositiveIds(string environmentVariable)
    {
        var value = ReadRequiredEnvironment(Environment.GetEnvironmentVariable, environmentVariable);
        var ids = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var id) && id > 0
                ? id
                : throw new InvalidOperationException($"{environmentVariable} 包含无效 ID：{part}"))
            .Distinct()
            .ToArray();
        return ids.Length > 0
            ? ids
            : throw new InvalidOperationException($"{environmentVariable} 至少需要一个 ID。");
    }

    private static string BuildModuloCase(string column, IReadOnlyList<int> ids)
        => $"CASE ({column} % {ids.Count}) "
           + string.Join(' ', ids.Select((id, index) => $"WHEN {index} THEN {id}"))
           + $" ELSE {ids[0]} END";

    internal static string BuildSparseWorkItemSelection(string titleParameter)
        => $"SELECT id FROM ("
           + $"SELECT id, ROW_NUMBER() OVER (ORDER BY create_date, id) - 1 AS sample_index "
           + $"FROM work_items WHERE comment LIKE {titleParameter}"
           + ") ranked_items WHERE sample_index % 5 = 0";

    private static bool HasExpectedSparseBindingDistribution(
        IDbExtensionHost host,
        bool isSqlite,
        string tableName,
        string instanceId)
    {
        var validDayCount = Convert.ToInt32(host.ExecuteScalar(
            $"SELECT COUNT(*) FROM (SELECT w.create_date FROM work_items w "
            + $"LEFT JOIN {tableName} t ON t.work_id=w.id AND t.instance_id={(isSqlite ? "$instance_id" : "$1")} "
            + $"WHERE w.comment LIKE {(isSqlite ? "$title_like" : "$2")} GROUP BY w.create_date "
            + "HAVING COUNT(t.work_id) BETWEEN 9 AND 10) valid_days;",
            (isSqlite ? "$instance_id" : "$1", instanceId),
            (isSqlite ? "$title_like" : "$2", DatePerformanceTitlePrefix + " %")));
        return validDayCount == DatePerformanceDayCount;
    }

    private static void DeleteDatePerformanceBindings(
        IDbExtensionHost host,
        bool isSqlite,
        string tableName,
        string instanceId)
        => host.Execute(
            $"DELETE FROM {tableName} WHERE instance_id={(isSqlite ? "$instance_id" : "$1")};",
            (isSqlite ? "$instance_id" : "$1", instanceId));

    private static void SetProperty(object target, string name, object value)
    {
        var property = target.GetType().GetProperty(name)
            ?? throw new InvalidOperationException($"Tracker 日期性能配置缺少属性 {name}。");
        property.SetValue(target, value);
    }

    private static bool IsPostgreSqlDatePerformanceRequested(Func<string, string?> readEnvironment)
        => !string.IsNullOrWhiteSpace(readEnvironment(PostgreSqlHostEnvironmentVariable));

    private static bool IsRedmineDatePerformanceRequested(Func<string, string?> readEnvironment)
        => !string.IsNullOrWhiteSpace(readEnvironment(RedmineServerUrlEnvironmentVariable));

    private static string ReadRequiredEnvironment(Func<string, string?> readEnvironment, string name)
    {
        var value = readEnvironment(name)?.Trim();
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"PostgreSQL 日期性能 UI 测试缺少环境变量 {name}。");
    }

    public static bool ApplyDatabaseScenario(DbInterfaceBase database)
    {
        if (_scenario == "extended")
            return ApplyAiContextScenario(database);
        if (_scenario == DatePerformanceScenario)
            return ApplyDatePerformanceScenario(database, DateTime.Today);
        if (_scenario != ExtraFieldsScenario)
            return false;

        const string workTitle = "UI只读附加字段事项";
        var date = DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var existing = database.GetWorkItemByDate(date).FirstOrDefault(item => item.Comment == workTitle);
        if (existing is not null)
            return false;

        var workItem = database.CreateWorkItem(date, workTitle);
        workItem.Time = 0.5;
        if (workItem.Id <= 0
            || !database.UpdateWorkItem(workItem)
            || !database.MarkWorkItemReadOnly(workItem))
        {
            throw new InvalidOperationException("无法创建附加字段只读 UI 测试事项。");
        }

        Trace.WriteLine($"UI 测试场景已创建只读附加字段事项：{workItem.Id}");
        return true;
    }

    internal static bool ApplyDatePerformanceScenario(DbInterfaceBase database, DateTime anchorDate)
    {
        var isSqlite = database.ProviderName.Contains("SQLite", StringComparison.OrdinalIgnoreCase);
        var isPostgreSql = database.ProviderName.Contains("PgDb", StringComparison.OrdinalIgnoreCase)
                           || database.ProviderName.Contains("PostgreSQL", StringComparison.OrdinalIgnoreCase);
        if (!isSqlite && !isPostgreSql)
            throw new InvalidOperationException($"日期性能 UI 测试场景不支持数据库 {database.ProviderName}。");

        var host = (IDbExtensionHost)database;
        var titleLike = DatePerformanceTitlePrefix + " %";
        var expectedCount = DatePerformanceDayCount * DatePerformanceItemsPerDay;
        var existingCount = Convert.ToInt32(host.ExecuteScalar(
            isSqlite
                ? "SELECT COUNT(*) FROM work_items WHERE comment LIKE $title_like;"
                : "SELECT COUNT(*) FROM work_items WHERE comment LIKE $1;",
            (isSqlite ? "$title_like" : "$1", titleLike)));
        if (existingCount == expectedCount)
            return false;
        if (existingCount != 0)
            throw new InvalidOperationException($"日期性能 UI 测试已有不完整数据：{existingCount}/{expectedCount}。");

        if (!database.BeginTransaction())
            throw new InvalidOperationException("无法开始日期性能 UI 测试数据事务。");

        var transactionCompleted = false;
        try
        {
            var existingTags = database.AllWorkTags();
            var primaryTag = existingTags.FirstOrDefault(item => item.Name == "CDP性能主标签")
                             ?? database.CreateWorkTag("CDP性能主标签", true, 0x3B82F6);
            var secondaryTagA = existingTags.FirstOrDefault(item => item.Name == "CDP性能类型A")
                                ?? database.CreateWorkTag("CDP性能类型A", false, 0x22C55E);
            var secondaryTagB = existingTags.FirstOrDefault(item => item.Name == "CDP性能类型B")
                                ?? database.CreateWorkTag("CDP性能类型B", false, 0xF59E0B);
            if (primaryTag.Id <= 0 || secondaryTagA.Id <= 0 || secondaryTagB.Id <= 0)
                throw new InvalidOperationException("无法创建日期性能 UI 测试标签。");

            var definition = database.GetTagExtraFieldDefinitions(primaryTag.Id, includeDisabled: true)
                .FirstOrDefault(item => item.FieldKey == "cdp.performance.ticket");
            if (definition is null)
            {
                definition = new TagExtraFieldDefinition
                {
                    FieldKey = "cdp.performance.ticket",
                    TagId = primaryTag.Id,
                    Label = "性能样本编号",
                    Type = TagExtraFieldType.Text,
                    Description = "用于大量日期切换 CDP 性能测试。",
                    SortOrder = 0,
                    Enabled = true,
                };
                if (!database.CreateTagExtraFieldDefinition(definition))
                    throw new InvalidOperationException("无法创建日期性能 UI 测试附加字段。");
            }

            var startDate = TimeTools.FormatDateTime(anchorDate.AddDays(-(DatePerformanceDayCount / 2)));
            host.Execute(
                isSqlite
                    ? """
                      WITH RECURSIVE
                      days(day_index, work_date) AS (
                          SELECT 0, date($start_date)
                          UNION ALL
                          SELECT day_index + 1, date(work_date, '+1 day')
                          FROM days
                          WHERE day_index + 1 < $day_count
                      ),
                      slots(slot_index) AS (
                          SELECT 0
                          UNION ALL
                          SELECT slot_index + 1
                          FROM slots
                          WHERE slot_index + 1 < $items_per_day
                      )
                      INSERT INTO work_items(create_date, comment, hours, priority)
                      SELECT work_date,
                             $title_prefix || ' ' || work_date || ' #' || printf('%02d', slot_index),
                             ((slot_index % 8) + 1) * 0.25,
                             slot_index % 4
                      FROM days CROSS JOIN slots;
                      """
                    : """
                      WITH days AS (
                          SELECT day_index, CAST($1 AS date) + day_index AS work_date
                          FROM generate_series(0, $2 - 1) AS generated(day_index)
                      ),
                      slots AS (
                          SELECT slot_index
                          FROM generate_series(0, $3 - 1) AS generated(slot_index)
                      )
                      INSERT INTO work_items(create_date, comment, hours, priority)
                      SELECT to_char(work_date, 'YYYY-MM-DD'),
                             $4 || ' ' || to_char(work_date, 'YYYY-MM-DD') || ' #' || lpad(slot_index::text, 2, '0'),
                             ((slot_index % 8) + 1) * 0.25,
                             slot_index % 4
                      FROM days CROSS JOIN slots;
                      """,
                (isSqlite ? "$start_date" : "$1", startDate),
                (isSqlite ? "$day_count" : "$2", DatePerformanceDayCount),
                (isSqlite ? "$items_per_day" : "$3", DatePerformanceItemsPerDay),
                (isSqlite ? "$title_prefix" : "$4", DatePerformanceTitlePrefix));
            host.Execute(
                isSqlite
                    ? "INSERT INTO work_notes(id, note) SELECT id, '性能备注 ' || id FROM work_items WHERE comment LIKE $title_like AND id % 4 = 0;"
                    : "INSERT INTO work_notes(id, note) SELECT id, '性能备注 ' || id FROM work_items WHERE comment LIKE $1 AND id % 4 = 0;",
                (isSqlite ? "$title_like" : "$1", titleLike));
            host.Execute(
                isSqlite
                    ? "INSERT INTO work_item_tags(work_id, tag_id) SELECT id, $tag_id FROM work_items WHERE comment LIKE $title_like AND id % 5 <> 0;"
                    : "INSERT INTO work_item_tags(work_id, tag_id) SELECT id, $1 FROM work_items WHERE comment LIKE $2 AND id % 5 <> 0;",
                (isSqlite ? "$tag_id" : "$1", primaryTag.Id),
                (isSqlite ? "$title_like" : "$2", titleLike));
            host.Execute(
                isSqlite
                    ? "INSERT INTO work_item_tags(work_id, tag_id) SELECT id, CASE WHEN id % 4 = 0 THEN $tag_a ELSE $tag_b END FROM work_items WHERE comment LIKE $title_like AND id % 2 = 0;"
                    : "INSERT INTO work_item_tags(work_id, tag_id) SELECT id, CASE WHEN id % 4 = 0 THEN $1 ELSE $2 END FROM work_items WHERE comment LIKE $3 AND id % 2 = 0;",
                (isSqlite ? "$tag_a" : "$1", secondaryTagA.Id),
                (isSqlite ? "$tag_b" : "$2", secondaryTagB.Id),
                (isSqlite ? "$title_like" : "$3", titleLike));
            host.Execute(
                isSqlite
                    ? "INSERT INTO work_item_extra_field_values(work_id, field_id, value_json) SELECT id, $field_id, printf('PERF-%08d', id) FROM work_items WHERE comment LIKE $title_like AND id % 3 = 0;"
                    : "INSERT INTO work_item_extra_field_values(work_id, field_id, value_json) SELECT id, $1, 'PERF-' || lpad(id::text, 8, '0') FROM work_items WHERE comment LIKE $2 AND id % 3 = 0;",
                (isSqlite ? "$field_id" : "$1", definition.FieldId),
                (isSqlite ? "$title_like" : "$2", titleLike));

            var actualCount = Convert.ToInt32(host.ExecuteScalar(
                isSqlite
                    ? "SELECT COUNT(*) FROM work_items WHERE comment LIKE $title_like;"
                    : "SELECT COUNT(*) FROM work_items WHERE comment LIKE $1;",
                (isSqlite ? "$title_like" : "$1", titleLike)));
            if (actualCount != expectedCount)
                throw new InvalidOperationException($"日期性能 UI 测试事项数量不正确：{actualCount}/{expectedCount}。");
            var commitSuccess = database.CommitTransaction();
            transactionCompleted = true;
            if (!commitSuccess)
                throw new InvalidOperationException("无法提交日期性能 UI 测试数据事务。");
            Trace.WriteLine($"UI 日期性能场景已创建 {actualCount} 条事项：{startDate} 起，共 {DatePerformanceDayCount} 天。");
            return true;
        }
        finally
        {
            if (!transactionCompleted)
                database.RollbackTransaction();
        }
    }

    private static bool ApplyAiContextScenario(DbInterfaceBase database)
    {
        const string tagName = "AI上下文示例项目";
        const string fieldKey = "sample.ticket";
        const string workTitle = "整理 AI 脚本上下文";
        var changed = false;
        var tag = database.AllWorkTags().FirstOrDefault(item => item.Name == tagName);
        if (tag is null)
        {
            tag = database.CreateWorkTag(tagName, true, 0x4F6BED);
            changed = true;
        }
        if (tag.Id <= 0)
            throw new InvalidOperationException("无法创建 AI 上下文 UI 测试标签。");

        var definition = database.GetTagExtraFieldDefinitions(tag.Id, includeDisabled: true)
            .FirstOrDefault(item => item.FieldKey == fieldKey);
        if (definition is null)
        {
            definition = new TagExtraFieldDefinition
            {
                FieldKey = fieldKey,
                TagId = tag.Id,
                Label = "示例工单编号",
                Type = TagExtraFieldType.Text,
                Description = "用于 AI 上下文 UI 测试和用户手册截图。",
                SortOrder = 0,
                Enabled = true,
            };
            if (!database.CreateTagExtraFieldDefinition(definition))
                throw new InvalidOperationException("无法创建 AI 上下文 UI 测试字段。");
            changed = true;
        }

        var date = DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (database.GetWorkItemByDate(date).Any(item => item.Comment == workTitle))
            return changed;

        var workItem = database.CreateWorkItem(date, workTitle);
        workItem.Time = 1.5;
        workItem.Priority = WorkPriorities.P1;
        if (workItem.Id <= 0
            || !database.UpdateWorkItem(workItem)
            || !database.WorkItemAddTag(workItem, tag)
            || !database.SaveWorkItemExtraFieldValues(workItem.Id,
                [new WorkItemExtraFieldValue
                {
                    WorkItemId = workItem.Id,
                    FieldId = definition.FieldId,
                    Value = "SAMPLE-42",
                }]))
        {
            throw new InvalidOperationException("无法创建 AI 上下文 UI 测试事项。");
        }
        database.WorkUpdateNote(workItem, "示例备注：仅用于隔离 UI 测试，不包含真实用户数据。");
        Trace.WriteLine($"UI 测试场景已创建 AI 上下文示例事项：{workItem.Id}");
        return true;
    }

    public static void Start()
    {
        if (_started || !TryGetPort(out var port))
            return;

        CdpServer.Start(port);
        _started = true;
        Trace.WriteLine($"Avalonia CDP 调试服务已启动：http://127.0.0.1:{port}");
    }

    public static void Stop()
    {
        if (!_started)
            return;

        CdpServer.Stop();
        _started = false;
    }

    internal static string CreateIsolatedAppId(string appId, string root)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..12];
        return $"{appId}.UiTest.{hash}";
    }

    internal static bool TryGetPort(out int port)
    {
        return TryParsePort(Environment.GetEnvironmentVariable(PortEnvironmentVariable), out port);
    }

    internal static bool TryParsePort(string? value, out int port)
    {
        return int.TryParse(value, out port) && port is >= 1024 and <= 65535;
    }
}
#endif
