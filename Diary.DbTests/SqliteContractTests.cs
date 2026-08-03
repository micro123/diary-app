using Diary.Database;

namespace Diary.DbTests;

/// <summary>SQLite（:memory:）跑全部契约场景。</summary>
[TestClass]
public class SqliteContractTests : DbContractTests
{
    protected override DbInterfaceBase CreateDb() => TestDb.Create();
}
