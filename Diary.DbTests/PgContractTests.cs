using Diary.Database;

namespace Diary.DbTests;

/// <summary>PostgreSQL（Testcontainers）跑全部契约场景。每测 new 一个 PgDb + DropData 清数据。</summary>
[TestClass]
public class PgContractTests : DbContractTests
{
    protected override DbInterfaceBase CreateDb()
    {
        var factory = PgContainerFixture.CreateFactory();
        if (factory is null)
        {
            Assert.Inconclusive("PostgreSQL 容器不可用（Docker 未运行？）");
            return null!;
        }
        var db = factory.Create();
        Assert.IsTrue(db.Connect(), "Pg Connect 失败");
        Assert.IsTrue(db.Initialized(), "Pg Initialized 失败");
        Assert.IsTrue(db.DropData(), "Pg DropData 失败（每测清空数据）");
        return db;
    }
}
