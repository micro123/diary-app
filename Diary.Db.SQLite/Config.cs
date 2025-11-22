using Diary.Core.Configure;

namespace Diary.Db.SQLite;

[StorageFile("sqlite_config.dat")]
public class Config
{
    [ConfigurePath("存储路径")]
    public string FilePath { get; set; } = string.Empty;
}
