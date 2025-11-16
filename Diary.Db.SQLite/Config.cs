using Diary.Core.Configure;

namespace Diary.Db.SQLite;

[StorageFile("sqlite_config.dat")]
public class Config
{
    [ConfigureText("文件路径")]
    public required string FilePath { get; set; }
}
