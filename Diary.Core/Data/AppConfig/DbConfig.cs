using Diary.Core.Configure;
using Diary.Core.Constants;

namespace Diary.Core.Data.AppConfig;

public class DbConfig
{
    [ConfigureUser("数据库驱动", "DB_DRIVER")]
    public string DatabaseDriver { get; set; } = "SQLite";

    [ConfigureButton("配置数据库", "配置", CommandNames.ShowDbSettings, "配置驱动参数，会重新打开数据库连接。")]
    private object? ShowSettings { get; }

    [ConfigureButton("备份数据库", "创建备份", CommandNames.BackupDatabase, "创建包含核心数据和 Tracker 扩展表的数据库备份。")]
    private object? BackupDatabase { get; }

    [ConfigureButton("还原数据库", "选择备份", CommandNames.RestoreDatabase, "校验备份后暂存还原任务，下次启动时安全替换数据库。")]
    private object? RestoreDatabase { get; }

    [ConfigureButton("迁移旧数据", "迁移向导", CommandNames.ShowMigrateGuide, "从DiaryApp迁移数据，会丢失当前数据！")]
    private object? ShowMigrateGuide { get; }
}
