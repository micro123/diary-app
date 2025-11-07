using Diary.Core.Configure;
using Diary.Utils;

namespace Diary.Core.Data.AppConfig;

[StorageFile("app_settings.json", "diary.core.data")]
public class AllConfig: SingletonBase<AllConfig>
{
    private AllConfig()
    {
        
    }
    
    [ConfigureGroup("视图设置")]
    public ViewConfig ViewSettings { get; } = new();
    
    [ConfigureGroup("RedMine设置")]
    public RedMineConfig RedMineSettings { get; } = new();
}
