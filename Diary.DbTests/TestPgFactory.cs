using Diary.Database;
using Diary.Db.PostgreSQL;

namespace Diary.DbTests;

/// <summary>
/// 测试用 PostgreSQL IDbFactory：用容器返回的连接参数构造 Config。
/// </summary>
internal sealed class TestPgFactory : IDbFactory
{
    private readonly Config _config;
    private readonly Func<uint, Migration?> _getMigration;

    public TestPgFactory(Config config, Func<uint, Migration?>? getMigration = null)
    {
        _config = config;
        _getMigration = getMigration ?? (_ => null);
    }

    public string Name => "PostgreSQL";
    public bool Usable => true;
    public DbInterfaceBase Create() => new PgDb(this);
    public Migration? GetMigration(uint version) => _getMigration(version);
    public object GetConfig() => _config;
}
