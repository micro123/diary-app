using Diary.Core.Configure;

namespace Diary.Core.Data.AppConfig;

public class DbConfig
{
    [ConfigureUser("数据库驱动", "DB_DRIVER")] public string DatabaseDriver { get; set; } = "SQLite";
    [ConfigureButton("配置数据库",  "配置", "SHOW_DB_SETTINGS")] private int ShowSettings { get; set; } = 0;
}