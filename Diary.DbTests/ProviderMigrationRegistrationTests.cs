using Diary.Core;
using Diary.Database;
using Diary.Db.PostgreSQL;
using Diary.Db.SQLite;

namespace Diary.DbTests;

/// <summary>
/// 正式发布数据版本契约：当前数据版本必须具备从上一正式版本升级的完整迁移链。
/// </summary>
[TestClass]
public sealed class ProviderMigrationRegistrationTests
{
    private const uint PreviousReleasedDataVersion = 0x10000;
    private const uint CurrentDataVersion = 0x10001;

    [TestMethod]
    public void CurrentCoreDataVersion_IsExpectedVersion()
    {
        var currentVersion = (uint)typeof(DataVersion)
            .GetField(nameof(DataVersion.VersionCode))!
            .GetRawConstantValue()!;
        Assert.AreEqual(
            CurrentDataVersion,
            currentVersion,
            "核心数据版本与正式迁移契约不一致。");
    }

    [TestMethod]
    public void ProductionProviders_RegisterMigrationFromPreviousVersion()
    {
        IDbFactory[] factories = [new SQLiteFactory(), new PostgreSQLFactory()];

        foreach (var factory in factories)
        {
            var migration = factory.GetMigration(PreviousReleasedDataVersion);
            Assert.IsNotNull(migration, $"{factory.Name} 缺少从上一正式版本开始的迁移。");
            Assert.AreEqual(PreviousReleasedDataVersion, migration.VersionFrom);
            Assert.AreEqual(CurrentDataVersion, migration.VersionTo);
            Assert.IsNull(factory.GetMigration(CurrentDataVersion),
                $"{factory.Name} 不应在当前版本之后登记未知迁移。");
        }
    }
}
