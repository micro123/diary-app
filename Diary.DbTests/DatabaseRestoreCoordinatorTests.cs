using Diary.App.Services;
using Diary.Core;
using Diary.Database;
using Diary.Db.SQLite;

namespace Diary.DbTests;

[TestClass]
public sealed class DatabaseRestoreCoordinatorTests
{
    [TestMethod]
    public void PendingRestore_AppliesAndCompletesAfterValidation()
    {
        using var environment = RestoreTestEnvironment.Create();
        var coordinator = new DatabaseRestoreCoordinator(environment.RestoreDirectory);
        var stage = coordinator.Stage("SQLite", environment.BackupPath);
        Assert.IsTrue(stage.Success, stage.Error);

        using var maintenance = new SQLiteDb(environment.CurrentFactory);
        Assert.IsTrue(coordinator.TryApplyPending(
            "SQLite",
            maintenance,
            DataVersion.VersionCode,
            out var context,
            out var error), error);
        Assert.IsNotNull(context);

        Assert.IsTrue(maintenance.Connect());
        Assert.IsTrue(maintenance.Initialized());
        Assert.IsTrue(maintenance.GetWorkItemByDate("2026-08-18")
            .Any(item => item.Comment == "backup-item"));
        maintenance.Dispose();

        coordinator.Complete(context);
        Assert.IsFalse(Directory.EnumerateFiles(environment.RestoreDirectory).Any());
    }

    [TestMethod]
    public void PendingRestore_RollbackRestoresOriginalDatabase()
    {
        using var environment = RestoreTestEnvironment.Create();
        var coordinator = new DatabaseRestoreCoordinator(environment.RestoreDirectory);
        Assert.IsTrue(coordinator.Stage("SQLite", environment.BackupPath).Success);

        using var maintenance = new SQLiteDb(environment.CurrentFactory);
        Assert.IsTrue(coordinator.TryApplyPending(
            "SQLite",
            maintenance,
            DataVersion.VersionCode,
            out var context,
            out var error), error);
        Assert.IsNotNull(context);
        Assert.IsTrue(coordinator.Rollback(context, out var rollbackError), rollbackError);

        using var current = new SQLiteDb(environment.CurrentFactory);
        Assert.IsTrue(current.Connect());
        Assert.IsTrue(current.Initialized());
        Assert.IsTrue(current.GetWorkItemByDate("2026-08-18")
            .Any(item => item.Comment == "current-item"));
    }

    private sealed class RestoreTestEnvironment : IDisposable
    {
        private RestoreTestEnvironment(
            string root,
            FileSqliteFactory currentFactory,
            string backupPath)
        {
            Root = root;
            CurrentFactory = currentFactory;
            BackupPath = backupPath;
            RestoreDirectory = Path.Combine(root, "restore-state");
        }

        public string Root { get; }
        public FileSqliteFactory CurrentFactory { get; }
        public string BackupPath { get; }
        public string RestoreDirectory { get; }

        public static RestoreTestEnvironment Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"diary-restore-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var currentFactory = new FileSqliteFactory(Path.Combine(root, "current.sqlite3"));
            using (CreateDatabase(currentFactory, "current-item"))
            {
            }

            var backupSourceFactory = new FileSqliteFactory(Path.Combine(root, "backup-source.sqlite3"));
            var backupPath = Path.Combine(root, "backup.sqlite3");
            using (var backupSource = CreateDatabase(backupSourceFactory, "backup-item"))
            {
                var result = ((IDbMaintenanceProvider)backupSource).CreateBackup(backupPath);
                Assert.IsTrue(result.Success, result.Error);
            }

            return new RestoreTestEnvironment(root, currentFactory, backupPath);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private static SQLiteDb CreateDatabase(FileSqliteFactory factory, string comment)
        {
            var db = new SQLiteDb(factory);
            Assert.IsTrue(db.Connect());
            Assert.IsTrue(db.Initialized());
            db.CreateWorkItem("2026-08-18", comment);
            var compatibility = db.CheckCompatibility(DataVersion.VersionCode);
            Assert.IsTrue(compatibility.IsUsable, compatibility.ToUserMessage());
            Assert.IsTrue(db.PersistCompatibilityMetadata(compatibility));
            return db;
        }
    }

    private sealed class FileSqliteFactory(string path) : IDbFactory
    {
        private readonly Config _config = new() { FilePath = path };

        public string Name => "SQLite";
        public bool Usable => true;
        public DbInterfaceBase Create() => new SQLiteDb(this);
        public Migration? GetMigration(uint version) => null;
        public object GetConfig() => _config;
    }
}
