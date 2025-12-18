using Diary.Core.Configure;
using Diary.Core.Utils;
using Diary.Utils;

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

    [ConfigureGroup("RedMine设置", "配置远程RedMine服务器")]
    public RedMineConfig RedMineSettings { get; } = new();

    [ConfigureGroup("数据库设置")] public DbConfig DbSettings { get; } = new();

    [ConfigureGroup("调查统计功能设置", "”调查 - 回应“功能设置")]
    public SurveyConfig SurveySettings { get; } = new();
}