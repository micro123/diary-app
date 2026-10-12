using System.Collections.ObjectModel;
using System.Security.Principal;
using CommunityToolkit.Mvvm.ComponentModel;
using Diary.Core.ConfigurationTools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Diary.ConfigTool.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly ConfigurationTransferService _transferService = new();
    private readonly WindowsDpapiMigrationService _dpapiMigrationService = new();
    private ConfigurationDocumentStore _store;
    private ConfigurationDocument? _document;

    public MainWindowViewModel()
    {
        _configDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Diary.App");
        _store = new ConfigurationDocumentStore(_configDirectory);
        Configurations = new ObservableCollection<ConfigurationItemViewModel>(CreateDescriptors()
            .Select(descriptor => new ConfigurationItemViewModel(descriptor)));
        CurrentIdentity = GetCurrentIdentity();
        ReloadDirectory();
        SelectedConfiguration = Configurations.FirstOrDefault(item => item.Exists)
                                ?? Configurations.FirstOrDefault();
    }

    public ObservableCollection<ConfigurationItemViewModel> Configurations { get; }
    public ObservableCollection<JsonPropertyRowViewModel> PropertyRows { get; } = [];

    [ObservableProperty]
    private string _configDirectory;

    [ObservableProperty]
    private ConfigurationItemViewModel? _selectedConfiguration;

    [ObservableProperty]
    private string _jsonText = string.Empty;

    [ObservableProperty]
    private string _originalJsonText = string.Empty;

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private string _documentDetails = "请选择配置。";

    [ObservableProperty]
    private bool _showSensitiveValues;

    [ObservableProperty]
    private bool _canSave;

    [ObservableProperty]
    private string _masterKeyStatusText = string.Empty;

    [ObservableProperty]
    private bool _canMigrateWindowsDpapi;

    public string CurrentIdentity { get; }

    public bool IsWindows => OperatingSystem.IsWindows();

    public IReadOnlyList<WindowsDpapiSidCandidate> PreviousSidCandidates
        => _dpapiMigrationService.DiscoverPreviousSidCandidates();

    partial void OnSelectedConfigurationChanged(ConfigurationItemViewModel? value)
        => LoadSelected();

    partial void OnShowSensitiveValuesChanged(bool value)
    {
        foreach (var row in PropertyRows)
            row.SetRevealSensitive(value);
    }

    public void ChangeDirectory(string directory)
    {
        ConfigDirectory = Path.GetFullPath(directory);
        _store = new ConfigurationDocumentStore(ConfigDirectory);
        ReloadDirectory();
        SelectedConfiguration = Configurations.FirstOrDefault(item => item.Exists)
                                ?? Configurations.FirstOrDefault();
        LoadSelected();
    }

    public void ReloadDirectory()
    {
        foreach (var item in Configurations)
            item.Update(_store.Load(item.Descriptor));
        var masterKey = _store.DiagnoseMasterKey();
        MasterKeyStatusText = $"主密钥：{masterKey.Message}";
        CanMigrateWindowsDpapi = OperatingSystem.IsWindows()
                                 && masterKey.Status == ConfigurationMasterKeyStatus.Unreadable;
        StatusText = $"已检查 {Configurations.Count} 项配置。";
    }

    public void ReloadSelected() => LoadSelected();

    public void FormatAndValidateJson()
    {
        try
        {
            JsonText = ConfigurationDocumentStore.ValidateAndNormalizeJson(JsonText);
            RebuildPropertyRows(JsonText);
            CanSave = _document?.Status is ConfigurationDocumentStatus.Loaded or ConfigurationDocumentStatus.Missing;
            StatusText = "JSON 校验通过。";
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            CanSave = false;
            StatusText = $"JSON 校验失败：{exception.Message}";
        }
    }

    public void ApplyPropertyRows()
    {
        try
        {
            var root = JObject.Parse(JsonText);
            foreach (var row in PropertyRows)
                ReplaceByPointer(root, row.Pointer, row.ToToken());
            JsonText = root.ToString(Formatting.Indented);
            CanSave = true;
            StatusText = "属性表修改已应用到 JSON。";
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            CanSave = false;
            StatusText = $"应用属性失败：{exception.Message}";
        }
    }

    public void SaveSelected()
    {
        var selected = SelectedConfiguration ?? throw new InvalidOperationException("未选择配置。");
        if (_document?.Status == ConfigurationDocumentStatus.Unreadable
            && (!selected.IsEncrypted || _store.MasterKeyExists))
        {
            throw new InvalidOperationException("原配置无法读取，已阻止覆盖。");
        }
        _document = _store.Save(selected.Descriptor, JsonText, _document?.Fingerprint);
        selected.Update(_document);
        SetDocument(_document);
        StatusText = "配置已保存，原文件已写入 .bak。";
    }

    public void RestoreSelectedBackup()
    {
        var selected = SelectedConfiguration ?? throw new InvalidOperationException("未选择配置。");
        var recoveryPath = _store.RestoreBackup(selected.Descriptor);
        LoadSelected();
        StatusText = string.IsNullOrEmpty(recoveryPath)
            ? "已从 .bak 恢复。"
            : $"已从 .bak 恢复；恢复前文件保存在 {Path.GetFileName(recoveryPath)}。";
    }

    public void ExportCurrent(string path)
    {
        if (_document?.Status != ConfigurationDocumentStatus.Loaded || _document.Json is null)
            throw new InvalidOperationException("当前配置不可导出。");
        File.WriteAllText(path, _document.Json + Environment.NewLine, new System.Text.UTF8Encoding(false));
        StatusText = $"已明文导出到 {path}。";
    }

    public void ImportCurrent(string path)
    {
        JsonText = ConfigurationDocumentStore.ValidateAndNormalizeJson(File.ReadAllText(path));
        RebuildPropertyRows(JsonText);
        CanSave = _document?.Status != ConfigurationDocumentStatus.Unreadable
                  || (SelectedConfiguration?.IsEncrypted == true && !_store.MasterKeyExists);
        StatusText = "已载入明文 JSON；确认内容后点击保存。";
    }

    public ConfigurationMasterKeyResetResult ResetEncryptionIdentity()
    {
        var result = _store.ResetUnreadableMasterKey(Configurations.Select(item => item.Descriptor));
        ReloadDirectory();
        LoadSelected();
        StatusText = $"旧主密钥已保留为 {Path.GetFileName(result.RetainedMasterKeyPath)}；"
                     + $"快照位于 {result.SnapshotDirectory}。现在可导入明文配置包。";
        return result;
    }

    public WindowsDpapiMigrationResult MigrateWindowsDpapi(string oldSid, string oldPassword)
    {
        var result = _dpapiMigrationService.Migrate(
            _store,
            Configurations.Select(item => item.Descriptor),
            oldSid,
            oldPassword);
        ReloadDirectory();
        LoadSelected();
        StatusText = result.DiaryMasterKeyReadable
            ? $"DPAPI 迁移完成：成功 {result.SuccessCount} 个，失败 {result.FailureCount} 个；Diary 配置已恢复读取。"
            : $"DPAPI 迁移完成：成功 {result.SuccessCount} 个，失败 {result.FailureCount} 个；"
              + $"Diary 主密钥仍不可读：{result.VerificationMessage}";
        return result;
    }

    public ConfigurationExportResult ExportAll(string directory)
    {
        var result = _transferService.Export(_store, Configurations.Select(item => item.Descriptor), directory);
        StatusText = result.Failures.Count == 0
            ? $"已明文导出 {result.ExportedCount} 项配置。"
            : $"已导出 {result.ExportedCount} 项，{result.Failures.Count} 项失败。";
        return result;
    }

    public ConfigurationImportResult ImportAll(string directory)
    {
        var descriptors = Configurations.ToDictionary(item => item.Id, item => item.Descriptor, StringComparer.Ordinal);
        var result = _transferService.Import(_store, descriptors, directory);
        ReloadDirectory();
        LoadSelected();
        StatusText = result.Failures.Count == 0
            ? $"已导入 {result.ImportedCount} 项配置；原配置快照位于 {result.SnapshotDirectory}。"
            : $"已导入 {result.ImportedCount} 项，{result.Failures.Count} 项失败；快照位于 {result.SnapshotDirectory}。";
        return result;
    }

    private void LoadSelected()
    {
        if (SelectedConfiguration is null)
        {
            _document = null;
            JsonText = string.Empty;
            OriginalJsonText = string.Empty;
            PropertyRows.Clear();
            CanSave = false;
            return;
        }
        _document = _store.Load(SelectedConfiguration.Descriptor);
        SelectedConfiguration.Update(_document);
        SetDocument(_document);
    }

    private void SetDocument(ConfigurationDocument document)
    {
        JsonText = document.Json ?? string.Empty;
        OriginalJsonText = document.Json ?? string.Empty;
        CanSave = document.Status is ConfigurationDocumentStatus.Loaded or ConfigurationDocumentStatus.Missing
                  || (document.Status == ConfigurationDocumentStatus.Unreadable
                      && document.Descriptor.IsEncrypted
                      && !_store.MasterKeyExists
                      && !string.IsNullOrWhiteSpace(JsonText));
        DocumentDetails = BuildDetails(document);
        RebuildPropertyRows(JsonText);
        StatusText = document.Status switch
        {
            ConfigurationDocumentStatus.Loaded => "配置加载成功。",
            ConfigurationDocumentStatus.Missing => "配置尚不存在；可导入 JSON 后创建。",
            ConfigurationDocumentStatus.Unreadable => $"配置无法读取：{document.Error}",
            _ => "未知状态。",
        };
    }

    private void RebuildPropertyRows(string json)
    {
        PropertyRows.Clear();
        if (string.IsNullOrWhiteSpace(json))
            return;
        try
        {
            var root = JToken.Parse(json);
            AddScalarRows(root, "$", string.Empty);
            foreach (var row in PropertyRows)
                row.SetRevealSensitive(ShowSensitiveValues);
        }
        catch (JsonException)
        {
            // JSON 编辑期间允许暂时不完整，校验按钮会给出具体错误。
        }
    }

    private void AddScalarRows(JToken token, string displayPath, string pointer)
    {
        switch (token)
        {
            case JObject obj:
                foreach (var property in obj.Properties())
                {
                    AddScalarRows(
                        property.Value,
                        displayPath == "$" ? property.Name : $"{displayPath}.{property.Name}",
                        pointer + "/" + EscapePointer(property.Name));
                }
                break;
            case JArray array:
                for (var index = 0; index < array.Count; index++)
                    AddScalarRows(array[index]!, $"{displayPath}[{index}]", pointer + "/" + index);
                break;
            case JValue value:
                PropertyRows.Add(new JsonPropertyRowViewModel(
                    displayPath,
                    pointer,
                    value.Type,
                    value.Type == JTokenType.Null
                        ? string.Empty
                        : Convert.ToString(value.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty));
                break;
        }
    }

    private static void ReplaceByPointer(JToken root, string pointer, JToken value)
    {
        var segments = pointer.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(UnescapePointer)
            .ToArray();
        if (segments.Length == 0)
            throw new InvalidOperationException("不能替换配置根节点。");
        JToken current = root;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            current = current switch
            {
                JObject obj => obj[segments[index]] ?? throw new InvalidOperationException($"属性不存在：{segments[index]}"),
                JArray array when int.TryParse(segments[index], out var arrayIndex) => array[arrayIndex],
                _ => throw new InvalidOperationException("属性路径无效。"),
            };
        }
        var last = segments[^1];
        if (current is JObject parentObject)
            parentObject[last] = value;
        else if (current is JArray parentArray && int.TryParse(last, out var itemIndex))
            parentArray[itemIndex] = value;
        else
            throw new InvalidOperationException("属性路径无效。");
    }

    private static string BuildDetails(ConfigurationDocument document)
    {
        var writeTime = document.LastWriteTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "—";
        return $"名称：{document.Descriptor.DisplayName}\n"
               + $"路径：{document.Descriptor.RelativePath}\n"
               + $"类别：{document.Descriptor.Category}\n"
               + $"格式：{document.Format}\n"
               + $"加密：{(document.Descriptor.IsEncrypted ? "是" : "否")}\n"
               + $"备份：{(document.HasBackup ? "存在 .bak" : "无")}\n"
               + $"修改时间：{writeTime}\n"
               + (string.IsNullOrWhiteSpace(document.Descriptor.Description)
                   ? string.Empty
                   : $"说明：{document.Descriptor.Description}\n")
               + (string.IsNullOrWhiteSpace(document.Error) ? string.Empty : $"错误：{document.Error}");
    }

    private static string GetCurrentIdentity()
    {
        if (!OperatingSystem.IsWindows())
            return $"当前用户：{Environment.UserName}（非 Windows，主密钥依赖文件权限）";
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return $"当前身份：{identity.Name}，SID：{identity.User?.Value ?? "未知"}";
        }
        catch
        {
            return $"当前用户：{Environment.UserName}（无法读取 SID）";
        }
    }

    private static IReadOnlyList<ConfigurationFileDescriptor> CreateDescriptors()
        =>
        [
            new("app.settings", "程序设置", "程序", "app_settings.json", "diary.core.data", "界面、工作、数据库、调查与更新设置。"),
            new("database.sqlite", "SQLite 连接", "数据库", "sqlite_config.dat", Description: "SQLite 数据库文件位置。"),
            new("database.postgresql", "PostgreSQL 连接", "数据库", "pgsql_config.dat", "diary_secret", "PostgreSQL 连接与密码。"),
            new("tracker.redmine", "Redmine", "Tracker", "redmine_settings.json", "diary.redmine"),
            new("tracker.jira", "Jira", "Tracker", "jira_settings.json", "diary.jira"),
            new("agent.credentials", "AI 凭据", "AI 助手", "ai-agent-credentials.dat", "Diary.Agent.Credentials.v1", "包含模型、代理和 MCP 的本地凭据。"),
            new("agent.settings", "AI 助手设置", "AI 助手", Path.Combine("ai-agent", "settings.json")),
            new("modules.states", "可选模块状态", "模块", "module-states.json"),
            new("modules.remote-mcp", "远程 MCP 设置", "模块", Path.Combine("remote-mcp", "settings.json")),
            new("statistics", "统计页面设置", "程序", "statistics.json"),
            new("templates", "事项模板", "程序", "templates.json"),
            new("saved-queries", "保存的事项查询", "程序", "work_item_queries.json"),
        ];

    private static string EscapePointer(string value) => value.Replace("~", "~0").Replace("/", "~1");
    private static string UnescapePointer(string value) => value.Replace("~1", "/").Replace("~0", "~");
}
