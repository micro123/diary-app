using System.Text.Json;
using Diary.App.Models;
using Diary.GUIBase.ViewModels;
using Diary.PluginUI;
using Diary.Utils;

namespace Diary.App.ViewModels.Dialogs;

/// <summary>
/// RedMine 模板扩展数据（payload）。稳定业务标识（activityId/issueId），不存 UI 索引。
/// </summary>
public sealed record RedMineTemplateData
{
    public int ActivityId { get; set; } = -1;
    public int IssueId { get; set; } = -1;
}

/// <summary>
/// RedMine 的 <see cref="ITrackerTemplateContributor"/> 实现。负责模板扩展数据的
/// 创建/编辑/序列化/反序列化/应用到编辑器扩展。核心模板协调器按 PluginId+InstanceId 委托。
/// </summary>
[DiAutoRegister(singleton: true)]
public class RedMineTemplateContributor : ITrackerTemplateContributor
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly DbShareData _shareData;

    public RedMineTemplateContributor(DbShareData shareData)
    {
        _shareData = shareData;
    }

    public string PluginId => "tracker.redmine";
    public string InstanceId => "redmine.default";
    public int CurrentSchemaVersion => 1;

    public object CreateDefaultData() => new RedMineTemplateData();

    public ViewModelBase CreateEditor(object? data, TemplateEditorContext context)
    {
        var d = data as RedMineTemplateData ?? new RedMineTemplateData();
        return new RedMineTemplateEditorRegionViewModel(d, _shareData)
        {
            PluginId = PluginId,
            InstanceId = InstanceId,
        };
    }

    public object ExtractData(ViewModelBase editor) =>
        editor is RedMineTemplateEditorRegionViewModel r ? r.ToData() : CreateDefaultData();

    public string Serialize(object data) => JsonSerializer.Serialize((RedMineTemplateData)data, JsonOpts);

    public object? Deserialize(string payloadJson, int schemaVersion)
    {
        try { return JsonSerializer.Deserialize<RedMineTemplateData>(payloadJson, JsonOpts); }
        catch { return null; } // payload 损坏：返回 null，调用方保留原 payload（§11.8）
    }

    public void ApplyTo(object data, ITrackerEditorExtension target)
    {
        if (data is not RedMineTemplateData d)
            return;
        target.SetActivity(d.ActivityId);
        target.SetIssue(d.IssueId);
    }
}
