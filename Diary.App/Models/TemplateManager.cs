using System.Text.Json;
using Diary.Core.Configure;
using Diary.Core.Data.App;
using Diary.Core.Utils;
using Diary.Utils;

namespace Diary.App.Models;

[StorageFile("templates.json")]
public class TemplateManager : SingletonBase<TemplateManager>
{
    private const string LegacyRedMinePluginId = "tracker.redmine";
    private const string LegacyRedMineInstanceId = "redmine.default";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private TemplateManager()
    {
        EasySaveLoad.Load(this);
        MigrateLegacyRedMineFields();
    }

    public ICollection<Template> Templates { get; set; } = Array.Empty<Template>();

    /// <summary>
    /// 旧 templates.json 用 DefaultActivity/DefaultIssue 携带 RedMine 默认值（核心模板直持 RedMine 语义）。
    /// 迁移为透明 Extensions payload（pluginId=tracker.redmine）。Extensions 已非空则跳过（已迁移过）。
    /// 旧字段保留供下次兼容读取，不删除（文档 §11.7）。
    /// </summary>
    private void MigrateLegacyRedMineFields()
    {
        foreach (var t in Templates)
        {
            if (t.Extensions.Any(x =>
                    x.PluginId == LegacyRedMinePluginId
                    && x.InstanceId == LegacyRedMineInstanceId))
                continue;
            if (t.DefaultActivity <= 0 && t.DefaultIssue <= 0)
                continue;

            var payload = JsonSerializer.Serialize(
                new { activityId = t.DefaultActivity, issueId = t.DefaultIssue }, JsonOpts);
            var list = (t.Extensions as List<TemplateExtensionData>) ?? new List<TemplateExtensionData>(t.Extensions);
            list.Add(new TemplateExtensionData
            {
                PluginId = LegacyRedMinePluginId,
                InstanceId = LegacyRedMineInstanceId,
                SchemaVersion = 1,
                PayloadJson = payload,
            });
            t.Extensions = list;
        }
    }
}
