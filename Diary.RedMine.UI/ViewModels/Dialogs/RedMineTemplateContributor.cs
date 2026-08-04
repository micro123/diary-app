using System.Text.Json;
using Diary.GUIBase.ViewModels;
using Diary.PluginUI;
using Diary.RedMine.UI;
using Diary.Utils;

namespace Diary.RedMine.UI.ViewModels.Dialogs;

public sealed record RedMineTemplateData
{
    public int ActivityId { get; set; } = -1;
    public int IssueId { get; set; } = -1;
}

[DiAutoRegister(singleton: true, serviceType: typeof(ITrackerTemplateContributor))]
public sealed class RedMineTemplateContributor(IRedMineUiData data) : ITrackerTemplateContributor
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public string PluginId => RedMinePluginConstants.PluginId;
    public string InstanceId => RedMinePluginConstants.DefaultInstanceId;
    public int CurrentSchemaVersion => 1;

    public object CreateDefaultData() => new RedMineTemplateData();

    public ViewModelBase CreateEditor(object? value, TemplateEditorContext context)
    {
        var template = value as RedMineTemplateData ?? new RedMineTemplateData();
        return new RedMineTemplateEditorRegionViewModel(template, data)
        {
            PluginId = PluginId,
            InstanceId = InstanceId,
        };
    }

    public object ExtractData(ViewModelBase editor)
        => editor is RedMineTemplateEditorRegionViewModel region
            ? region.ToData()
            : CreateDefaultData();

    public string Serialize(object value) => JsonSerializer.Serialize((RedMineTemplateData)value, JsonOpts);

    public object? Deserialize(string payloadJson, int schemaVersion)
    {
        try { return JsonSerializer.Deserialize<RedMineTemplateData>(payloadJson, JsonOpts); }
        catch { return null; }
    }

    public void ApplyTo(object value, ITrackerEditorExtension target)
    {
        if (value is RedMineTemplateData template)
        {
            target.ApplyTemplateData(template);
        }
    }
}
