using Diary.Core.Configure;
using Diary.Core.Constants;

namespace Diary.Core.Data.AppConfig;

public class DbConfig
{
    [ConfigureUser("数据库驱动", "DB_DRIVER")] public string DatabaseDriver { get; set; } = "SQLite";
    [ConfigureButton("配置数据库",  "配置", CommandNames.ShowDbSettings)] private int ShowSettings { get; set; } = 0;
}