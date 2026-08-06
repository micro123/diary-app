using Diary.Database;
using Diary.Db.SQLite;

namespace Diary.DbTests;

/// <summary>
/// 最小 IDbFactory：用 SQLite 内存库（Data Source=:memory:）。
/// SQLiteDb 全程持有单一 _connection，内存库的 schema/数据在该连接生命周期内持久。
/// </summary>
internal sealed class DbTestFactory : IDbFactory
{
    private readonly Func<uint, Migration?> _getMigration;

    public DbTestFactory(Func<uint, Migration?>? getMigration = null)
        => _getMigration = getMigration ?? (_ => null);

    public string Name => "SQLite";
    public bool Usable => true;
    private readonly Config _config = new() { FilePath = ":memory:" };
    public DbInterfaceBase Create() => new SQLiteDb(this);
    public Migration? GetMigration(uint version) => _getMigration(version);
    public object GetConfig() => _config;
}

/// <summary>
/// 每个测试新建一个独立内存库：Connect + Initialized 后即可用；Dispose 关闭连接。
/// </summary>
internal static class TestDb
{
    public static SQLiteDb Create(Func<uint, Migration?>? getMigration = null)
    {
        var db = new SQLiteDb(new DbTestFactory(getMigration));
        Assert.IsTrue(db.Connect(), "Connect 失败");
        Assert.IsTrue(db.Initialized(), "Initialized 失败");
        return db;
    }
}
