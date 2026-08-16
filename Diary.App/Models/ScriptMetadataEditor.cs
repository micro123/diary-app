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
    /// 读-改-写 metadata：只覆盖 Name/Description/Schedule/RunOnStartup/Triggers，
    /// 其余字段（含未知字段）原样保留；文件不存在时新建。schedule 非空时校验 "daily HH:mm"。
    /// </summary>
    public static async ValueTask WriteAsync(
        string sourcePath,
        string? name,
        string? description,
        string? schedule,
        bool runOnStartup,
        IReadOnlyCollection<ScriptAutomationTriggerKind>? triggers = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedSchedule = string.IsNullOrWhiteSpace(schedule) ? null : schedule.Trim();
        if (normalizedSchedule is not null && !ScriptAutomationSchedule.TryParse(normalizedSchedule, out _))
            throw new ArgumentException("调度时间必须是 'daily HH:mm' 格式。", nameof(schedule));

        var root = await ReadAsync(sourcePath, cancellationToken);
        SetProperty(root, "Name", string.IsNullOrWhiteSpace(name) ? null : JsonValue.Create(name.Trim()));
        SetProperty(root, "Description", string.IsNullOrWhiteSpace(description) ? null : JsonValue.Create(description.Trim()));
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
