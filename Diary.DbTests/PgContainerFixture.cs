using Diary.Database;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Diary.DbTests;

/// <summary>
/// 装配级 PostgreSQL 容器：整个测试程序集只起一次，跑完即弃。
/// Docker 不可用时 <see cref="_container"/> 为 null，Pg 用例走 Inconclusive。
/// </summary>
[TestClass]
public class PgContainerFixture
{
    private static PostgreSqlContainer? _container;

    [AssemblyInitialize]
    public static async Task InitAsync(TestContext _)
    {
        try
        {
            _container = new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("diarytest")
                .WithUsername("diary")
                .WithPassword("diary")
                .Build();
            await _container.StartAsync();
        }
        catch
        {
            // Docker 未运行 / 不可用：Pg 用例将 Inconclusive，不影响 SQLite
            _container = null;
        }
    }

    [AssemblyCleanup]
    public static async Task CleanupAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    public static IDbFactory? CreateFactory(Func<uint, Migration?>? getMigration = null)
    {
        if (_container is null)
            return null;
        var cs = new NpgsqlConnectionStringBuilder(_container.GetConnectionString());
        var cfg = new Diary.Db.PostgreSQL.Config
        {
            Host = cs.Host ?? "localhost",
            Port = (ushort)cs.Port,
            Database = cs.Database ?? "",
            User = cs.Username ?? "",
            Password = cs.Password ?? "",
        };
        return new TestPgFactory(cfg, getMigration);
    }
}
