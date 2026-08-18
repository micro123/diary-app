using Diary.Core;
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
    public void Compatibility_EmptySQLiteDatabase_IsUninitialized()
    {
        using var db = new SQLiteDb(new FileSqliteFactory(":memory:"));
        Assert.IsTrue(db.Connect());

        var report = db.CheckCompatibility(DataVersion.VersionCode);

        Assert.AreEqual(DbCompatibilityState.Uninitialized, report.State, report.ToUserMessage());
        Assert.IsFalse(report.IsUsable);
        Assert.AreEqual(0u, report.DeclaredVersion);
    }

    [TestMethod]
    public void Compatibility_ForeignKeyViolation_IsDataIntegrityError()
    {
        using var db = CreateDb();
        Assert.IsTrue(db.ExecRaw(
            "PRAGMA foreign_keys=OFF; " +
            "INSERT INTO work_item_tags(work_id, tag_id) VALUES(2147483000, 2147483001); " +
            "PRAGMA foreign_keys=ON;"));

        try
        {
            var report = db.CheckCompatibility(DataVersion.VersionCode);
            Assert.AreEqual(DbCompatibilityState.DataIntegrityError, report.State, report.ToUserMessage());
            Assert.IsTrue(report.Issues.Any(issue => issue.Code == "DB-DATA-FOREIGN-KEY-VIOLATION"));
        }
        finally
        {
            Assert.IsTrue(db.ExecRaw("DELETE FROM work_item_tags WHERE work_id=2147483000; PRAGMA foreign_keys=ON;"));
        }
    }

    [TestMethod]
    public void MigrateTo_DefaultValidation_FailsOnDataIntegrityError()
    {
        Assert.IsTrue(CreateInvalidForeignKeyData(out var db));
        using (db)
        {
            var result = db.MigrateTo(
                0x10001,
                new DbMigrationOptions(CreateBackup: false, ValidateDataAfterMigration: true));

            Assert.IsFalse(result.Success);
            Assert.AreEqual(DbCompatibilityState.DataIntegrityError, result.FinalReport!.State);
            Assert.AreEqual(0x10001u, db.GetDataVersion());
        }
    }

    [TestMethod]
    public void MigrateTo_DisabledValidation_AllowsDataIntegrityWarning()
    {
        Assert.IsTrue(CreateInvalidForeignKeyData(out var db));
        using (db)
        {
            var result = db.MigrateTo(
                0x10001,
                new DbMigrationOptions(CreateBackup: false, ValidateDataAfterMigration: false));

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual(DbCompatibilityState.DataIntegrityError,
                db.CheckCompatibility(0x10001).State);
        }
    }

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

    [TestMethod]
    public void Maintenance_CreateBackup_ValidatesAndRestoresDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), $"diary-sqlite-maintenance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var currentPath = Path.Combine(root, "current.sqlite3");
            var backupSourcePath = Path.Combine(root, "backup-source.sqlite3");
            var backupPath = Path.Combine(root, "manual-backup.sqlite3");
            CreateFileDatabase(currentPath, "current-item");

            DbBackupResult backupResult;
            using (var backupSource = CreateFileDatabase(backupSourcePath, "backup-item"))
                backupResult = ((IDbMaintenanceProvider)backupSource).CreateBackup(backupPath);
            Assert.IsTrue(backupResult.Success, backupResult.Error);

            using var maintenance = new SQLiteDb(new FileSqliteFactory(currentPath));
            var provider = (IDbMaintenanceProvider)maintenance;
            var validation = provider.ValidateBackup(backupPath, DataVersion.VersionCode);
            Assert.IsTrue(validation.Success, validation.Error);
            Assert.AreEqual(DataVersion.VersionCode, validation.DataVersion);

            var restore = provider.RestoreBackup(backupPath, DataVersion.VersionCode);
            Assert.IsTrue(restore.Success, restore.Error);
            Assert.IsNotNull(restore.RecoveryPath);
            Assert.IsTrue(File.Exists(restore.RecoveryPath));

            using (var restored = OpenFileDatabase(currentPath))
            {
                Assert.IsTrue(restored.GetWorkItemByDate("2026-08-18")
                    .Any(item => item.Comment == "backup-item"));
                Assert.IsFalse(restored.GetWorkItemByDate("2026-08-18")
                    .Any(item => item.Comment == "current-item"));
            }

            Assert.IsTrue(provider.RollbackRestore(restore, out var rollbackError), rollbackError);
            using var rolledBack = OpenFileDatabase(currentPath);
            Assert.IsTrue(rolledBack.GetWorkItemByDate("2026-08-18")
                .Any(item => item.Comment == "current-item"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Maintenance_ValidateBackup_RejectsInvalidFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"diary-invalid-backup-{Guid.NewGuid():N}.sqlite3");
        try
        {
            File.WriteAllText(path, "not a sqlite database");
            using var db = new SQLiteDb(new FileSqliteFactory(path + ".target"));

            var validation = ((IDbMaintenanceProvider)db)
                .ValidateBackup(path, DataVersion.VersionCode);

            Assert.IsFalse(validation.Success);
            Assert.IsNotNull(validation.Error);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
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

    private static bool CreateInvalidForeignKeyData(out SQLiteDb db)
    {
        db = TestDb.Create(_ => new TestMigration(0x10000, 0x10001, MigrationResult.Success));
        return db.ExecRaw(
            "PRAGMA foreign_keys=OFF; " +
            "INSERT INTO work_item_tags(work_id, tag_id) VALUES(2147483000, 2147483001); " +
            "PRAGMA foreign_keys=ON;");
    }

    private static SQLiteDb CreateFileDatabase(string path, string comment)
    {
        var db = new SQLiteDb(new FileSqliteFactory(path));
        Assert.IsTrue(db.Connect());
        Assert.IsTrue(db.Initialized());
        db.CreateWorkItem("2026-08-18", comment);
        var compatibility = db.CheckCompatibility(DataVersion.VersionCode);
        Assert.IsTrue(compatibility.IsUsable, compatibility.ToUserMessage());
        Assert.IsTrue(db.PersistCompatibilityMetadata(compatibility));
        return db;
    }

    private static SQLiteDb OpenFileDatabase(string path)
    {
        var db = new SQLiteDb(new FileSqliteFactory(path));
        Assert.IsTrue(db.Connect());
        Assert.IsTrue(db.Initialized());
        return db;
    }
}
