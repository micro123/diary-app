using Diary.Core.Configure;
using Diary.Core.Constants;

namespace Diary.Core.Data.AppConfig;

public class DbConfig
{
    [ConfigureUser("数据库驱动", "DB_DRIVER", "数据库驱动")]
    public string DatabaseDriver { get; set; } = "SQLite";

    [ConfigureButton("配置数据库", "配置", CommandNames.ShowDbSettings, "配置驱动参数，需要重启程序才能生效（一般）")]
    private int ShowSettings { get; set; } = 0;

    [ConfigureButton("迁移旧数据", "迁移向导", CommandNames.ShowMigrateGuide, "会丢失当前数据")]
    private int ShowMigrateGuide { get; set; } = 0;
}