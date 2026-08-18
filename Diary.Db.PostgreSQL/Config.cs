using Diary.Core.Configure;

namespace Diary.Db.PostgreSQL;

[StorageFile("pgsql_config.dat", "diary_secret")]
public class Config
{
    [ConfigureText("服务器地址")]
    public string Host { get; set; } = "";

    [ConfigureIntegral("端口", 1, 65535)]
    public ushort Port { get; set; } = 5432;

    [ConfigureText("数据库")]
    public string Database { get; set; } = "";
    [ConfigureText("用户名")]
    public string User { get; set; } = "";

    [ConfigureText("密码", true)]
    public string Password { get; set; } = "";

    [ConfigurePath(
        "PostgreSQL 工具目录",
        true,
        "包含 pg_dump 和 pg_restore 的 bin 目录。Windows 必须配置；Linux 未配置时会搜索 PATH。")]
    public string ToolsBinPath { get; set; } = "";
}
