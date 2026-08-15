using Diary.Core;
using Diary.Database;
using Diary.Db.PostgreSQL;
using Diary.Db.SQLite;

namespace Diary.DbTests;

/// <summary>
/// 正式发布数据版本契约：当前已发布数据版本没有待执行迁移。
/// 提升 <see cref="DataVersion.VersionCode"/> 时必须先更新本契约，
/// 并为 SQLite 与 PostgreSQL 同步登记从上一正式数据版本开始的迁移。
/// </summary>
[TestClass]
public sealed class ProviderMigrationRegistrationTests
{
    private const uint LastReleasedDataVersion = 0x10000;

    [TestMethod]
    public void CurrentCoreDataVersion_RemainsAtLastReleasedVersion()
    {
        var currentVersion = (uint)typeof(DataVersion)
            .GetField(nameof(DataVersion.VersionCode))!
            .GetRawConstantValue()!;
        Assert.AreEqual(
            LastReleasedDataVersion,
            currentVersion,
            "提升核心数据版本前必须同步登记 SQLite/PostgreSQL 迁移并更新本契约测试。");
    }

    [TestMethod]
    public void ProductionProviders_HaveNoPendingMigrationForCurrentVersion()
    {
        IDbFactory[] factories = [new SQLiteFactory(), new PostgreSQLFactory()];

        foreach (var factory in factories)
        {
            Assert.IsNull(
                factory.GetMigration(DataVersion.VersionCode),
                $"{factory.Name} 在当前核心数据版本上不应登记待执行迁移。");
        }
    }
}
