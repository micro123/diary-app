using Diary.Core.Configure;

namespace Diary.Core.Data.AppConfig;

[StorageFile("app_settings.json", "diary.core.data")]
public class AllConfig
{
    [ConfigureGroup("View Setting")]
    public ViewConfig ViewSettings { get; } = new ViewConfig();
    
    [ConfigureGroup("RedMine Setting")]
    public RedMineConfig RedMineSettings { get; } = new RedMineConfig();
}
