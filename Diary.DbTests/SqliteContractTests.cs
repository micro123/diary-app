using Diary.Database;
using Diary.Db.SQLite;

namespace Diary.DbTests;

/// <summary>SQLite（:memory:）跑全部契约场景。</summary>
[TestClass]
public class SqliteContractTests : DbContractTests
{
    protected override DbInterfaceBase CreateDb(Func<uint, Migration?>? getMigration = null)
        => TestDb.Create(getMigration);

    [TestMethod]
    public void TryCreateMigrationBackup_CreatesRestorableSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"diary-sqlite-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "source.sqlite3");
            var sourceFactory = new FileSqliteFactory(sourcePath);
            string backupPath;
            using (var source = new SQLiteDb(sourceFactory))
            {
                Assert.IsTrue(source.Connect());
                Assert.IsTrue(source.Initialized());
                source.CreateWorkItem("2026-08-15", "backup-item");

                Assert.IsTrue(
                    source.TryCreateMigrationBackup(0x10001, out var createdPath, out var error),
                    error);
                Assert.IsNotNull(createdPath);
                backupPath = createdPath;
                Assert.IsTrue(File.Exists(backupPath));
            }

            using var restored = new SQLiteDb(new FileSqliteFactory(backupPath));
            Assert.IsTrue(restored.Connect());
            Assert.IsTrue(restored.Initialized());
            Assert.IsTrue(restored.GetWorkItemByDate("2026-08-15")
                .Any(item => item.Comment == "backup-item"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FileSqliteFactory(string filePath) : IDbFactory
    {
        private readonly Config _config = new() { FilePath = filePath };

        public string Name => "SQLite";
        public bool Usable => true;
        public DbInterfaceBase Create() => new SQLiteDb(this);
        public Migration? GetMigration(uint version) => null;
        public object GetConfig() => _config;
    }
}
