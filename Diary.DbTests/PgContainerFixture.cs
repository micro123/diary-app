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
        catch (Exception exception)
        {
            _container = null;
            if (string.Equals(
                    Environment.GetEnvironmentVariable("DIARY_REQUIRE_POSTGRES_TESTS"),
                    "1",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "PostgreSQL 契约测试被设为必需，但测试容器启动失败。",
                    exception);
            }

            // 本地未启用 Docker 时允许 Pg 用例显示为 Inconclusive；CI Linux 门禁会设置必需标记。
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
