#if DEBUG
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Diagnostics.Cdp;
using Diary.Core.Data.AppConfig;
using Diary.Core.Data.Base;
using Diary.Core.Utils;
using Diary.Database;
using Diary.Utils;

namespace Diary.App;

internal static class DebugUiAutomation
{
    internal const string PortEnvironmentVariable = "DIARY_CDP_PORT";
    internal const string RootEnvironmentVariable = "DIARY_UI_TEST_ROOT";
    internal const string ScenarioEnvironmentVariable = "DIARY_UI_TEST_SCENARIO";
    private const string ExtraFieldsScenario = "extra-fields";
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
        }

        if (!EasySaveLoad.Save(config))
            throw new InvalidOperationException($"无法保存 UI 测试场景配置：{scenario}");
        Trace.WriteLine($"UI 测试场景已配置：{scenario}");
    }

    public static bool ApplyDatabaseScenario(DbInterfaceBase database)
    {
        if (_scenario != ExtraFieldsScenario)
            return false;

        const string tagName = "UI只读附加字段标签";
        const string fieldKey = "ui.readonly.note";
        const string workTitle = "UI只读附加字段事项";
        var tag = database.AllWorkTags().FirstOrDefault(item => item.Name == tagName)
                  ?? database.CreateWorkTag(tagName, true, 0x455A64);
        if (tag.Id <= 0)
            throw new InvalidOperationException("无法创建附加字段 UI 测试标签。");

        var definition = database.GetTagExtraFieldDefinitions(tag.Id, includeDisabled: true)
            .FirstOrDefault(item => item.FieldKey == fieldKey);
        if (definition is null)
        {
            definition = new TagExtraFieldDefinition
            {
                FieldKey = fieldKey,
                TagId = tag.Id,
                Label = "只读历史备注",
                Type = TagExtraFieldType.Text,
                Description = "用于验证迁移导入事项的附加字段只读展示。",
                SortOrder = 0,
                Enabled = true,
            };
            if (!database.CreateTagExtraFieldDefinition(definition))
                throw new InvalidOperationException("无法创建附加字段 UI 测试定义。");
        }

        var date = DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var existing = database.GetWorkItemByDate(date).FirstOrDefault(item => item.Comment == workTitle);
        if (existing is not null)
            return false;

        var workItem = database.CreateWorkItem(date, workTitle);
        workItem.Time = 0.5;
        if (workItem.Id <= 0
            || !database.UpdateWorkItem(workItem)
            || !database.WorkItemAddTag(workItem, tag)
            || !database.SaveWorkItemExtraFieldValues(workItem.Id,
                [new WorkItemExtraFieldValue
                {
                    WorkItemId = workItem.Id,
                    FieldId = definition.FieldId,
                    Value = "迁移历史值",
                }])
            || !database.MarkWorkItemReadOnly(workItem))
        {
            throw new InvalidOperationException("无法创建附加字段只读 UI 测试事项。");
        }

        Trace.WriteLine($"UI 测试场景已创建只读附加字段事项：{workItem.Id}");
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
