using Diary.Core.Configure;
using Diary.Core.Utils;
using Diary.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Diary.Core.Data.AppConfig;

[StorageFile("app_settings.json", "diary.core.data")]
public class AllConfig : SingletonBase<AllConfig>
{
    private AllConfig()
    {
        EasySaveLoad.Load(this);
    }

    [ConfigureGroup("视图设置", "配置默认颜色、托盘功能等。")]
    public ViewConfig ViewSettings { get; } = new();

    [ConfigureGroup("工作设置", "配置日记记录的一般信息")]
    public WorkConfig WorkSettings { get; } = new();

    [JsonExtensionData]
    public IDictionary<string, JToken> ExtensionData { get; set; } = new Dictionary<string, JToken>();

    [ConfigureGroup("数据库设置", "所有功能的基础都是先连接好数据库。")]
    public DbConfig DbSettings { get; } = new();

    [ConfigureGroup("调查统计功能设置", "”调查 - 回应“功能设置")]
    public SurveyConfig SurveySettings { get; } = new();

    [ConfigureGroup("脚本设置", "配置脚本的执行方式。")]
    public ScriptConfig ScriptSettings { get; } = new();
}
