using System.Text.Json;
using System.Text.Json.Nodes;
using Diary.Script.Runtime;
using Diary.ScriptBase;

namespace Diary.App.Models;

public static class ScriptMetadataEditor
{
    public static string GetMetadataPath(string sourcePath) => sourcePath + ".json";

    public static async ValueTask<JsonObject> ReadAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var metadataPath = GetMetadataPath(sourcePath);
        if (!File.Exists(metadataPath))
            return new JsonObject();
        var json = await File.ReadAllTextAsync(metadataPath, cancellationToken);
        return JsonNode.Parse(json) as JsonObject
            ?? throw new JsonException("脚本 metadata 必须是 JSON 对象。");
    }

    /// <summary>
    /// 读-改-写 metadata：更新运行配置并保留未知字段。
    /// C# 脚本的身份、作用域和入口类型由源码声明，保存时会移除对应 metadata 字段。
    /// </summary>
    public static async ValueTask WriteAsync(
        string sourcePath,
        string? name,
        string? description,
        string? schedule,
        bool runOnStartup,
        IReadOnlyCollection<ScriptAutomationTriggerKind>? triggers = null,
        IReadOnlyDictionary<string, string>? defaultArguments = null,
        int? timeoutSeconds = null,
        bool updateIdentity = true,
        CancellationToken cancellationToken = default)
    {
        var normalizedSchedule = string.IsNullOrWhiteSpace(schedule) ? null : schedule.Trim();
        if (normalizedSchedule is not null && !ScriptAutomationSchedule.TryParse(normalizedSchedule, out _))
            throw new ArgumentException("调度时间必须是 'daily HH:mm' 格式。", nameof(schedule));

        if (timeoutSeconds is <= 0 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "脚本超时必须在 1 到 3600 秒之间。");

        var root = await ReadAsync(sourcePath, cancellationToken);
        if (updateIdentity)
        {
            SetProperty(root, "Name", string.IsNullOrWhiteSpace(name) ? null : JsonValue.Create(name.Trim()));
            SetProperty(root, "Description", string.IsNullOrWhiteSpace(description) ? null : JsonValue.Create(description.Trim()));
        }
        else
        {
            foreach (var propertyName in new[]
                     {
                         "ApiVersion", "Id", "Name", "Description", "Engine", "Scope",
                         "SupportedEditorTargets", "EntryKind",
                     })
            {
                SetProperty(root, propertyName, null);
            }
        }
        SetProperty(root, "Schedule", normalizedSchedule is null ? null : JsonValue.Create(normalizedSchedule));
        SetProperty(root, "RunOnStartup", JsonValue.Create(runOnStartup));
        var normalizedTriggers = triggers?
            .Where(trigger => trigger is ScriptAutomationTriggerKind.WorkItemCreated
                or ScriptAutomationTriggerKind.WorkItemSaved
                or ScriptAutomationTriggerKind.TagAdded)
            .Distinct()
            .ToArray() ?? [];
        SetProperty(
            root,
            "Triggers",
            normalizedTriggers.Length == 0 ? null : JsonSerializer.SerializeToNode(normalizedTriggers));
        var normalizedArguments = defaultArguments?
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .ToDictionary(pair => pair.Key.Trim(), pair => pair.Value ?? string.Empty, StringComparer.Ordinal);
        SetProperty(
            root,
            "DefaultArguments",
            normalizedArguments is null or { Count: 0 }
                ? null
                : JsonSerializer.SerializeToNode(normalizedArguments));
        SetProperty(root, "TimeoutSeconds", timeoutSeconds is null ? null : JsonValue.Create(timeoutSeconds.Value));

        var metadataPath = GetMetadataPath(sourcePath);
        var tempPath = metadataPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                tempPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
            File.Move(tempPath, metadataPath, true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static void SetProperty(JsonObject root, string propertyName, JsonNode? value)
    {
        var existing = root.FirstOrDefault(pair =>
            string.Equals(pair.Key, propertyName, StringComparison.OrdinalIgnoreCase));
        if (existing.Key is not null)
            root.Remove(existing.Key);
        if (value is not null)
            root[propertyName] = value;
    }
}
