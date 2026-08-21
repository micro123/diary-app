using Diary.Update;

namespace Diary.UpdateTests;

[TestClass]
public sealed class UpdateTransactionExecutorTests
{
    [TestMethod]
    public async Task ApplyAsync_AddsReplacesDeletesAndWritesManifest()
    {
        using var fixture = await UpdateFixture.CreateAsync();
        var oldPath = fixture.InstallPath("old.txt");
        var deletePath = fixture.InstallPath("delete.txt");
        await File.WriteAllTextAsync(oldPath, "old-content");
        await File.WriteAllTextAsync(deletePath, "delete-content");
        var replacement = await fixture.StageAsync("payload/old.txt", "new-content");
        var addition = await fixture.StageAsync("payload/new.txt", "added-content");
        var plan = await fixture.CreatePlanAsync(
        [
            await fixture.OperationAsync(UpdateFileOperationKind.Replace, "old.txt", replacement, oldPath),
            await fixture.OperationAsync(UpdateFileOperationKind.Add, "new.txt", addition),
            await fixture.OperationAsync(UpdateFileOperationKind.Delete, "delete.txt", existingPath: deletePath),
        ]);

        var result = await new UpdateTransactionExecutor().ApplyAsync(
            UpdatePlanValidator.Validate(plan, fixture.PlanPath),
            restartApplication: false);

        Assert.AreEqual(UpdateTransactionState.Applied, result.State);
        Assert.AreEqual("new-content", await File.ReadAllTextAsync(oldPath));
        Assert.AreEqual("added-content", await File.ReadAllTextAsync(fixture.InstallPath("new.txt")));
        Assert.IsFalse(File.Exists(deletePath));
        Assert.AreEqual("manifest", await File.ReadAllTextAsync(fixture.InstallPath(".update/installed-manifest.json")));
    }

    [TestMethod]
    public async Task ApplyAsync_WhenLaterOperationFails_RollsBackEarlierReplacement()
    {
        using var fixture = await UpdateFixture.CreateAsync();
        var original = fixture.InstallPath("first.txt");
        await File.WriteAllTextAsync(original, "before");
        await File.WriteAllTextAsync(fixture.InstallPath("blocked"), "not-a-directory");
        var replacement = await fixture.StageAsync("payload/first.txt", "after");
        var blockedAddition = await fixture.StageAsync("payload/second.txt", "second");
        var plan = await fixture.CreatePlanAsync(
        [
            await fixture.OperationAsync(UpdateFileOperationKind.Replace, "first.txt", replacement, original),
            await fixture.OperationAsync(UpdateFileOperationKind.Add, "blocked/second.txt", blockedAddition),
        ]);

        await Assert.ThrowsAsync<IOException>(async () =>
            await new UpdateTransactionExecutor().ApplyAsync(
                UpdatePlanValidator.Validate(plan, fixture.PlanPath),
                restartApplication: false));

        Assert.AreEqual("before", await File.ReadAllTextAsync(original));
        var store = new UpdateTransactionStore(UpdatePlanValidator.Validate(plan, fixture.PlanPath));
        Assert.AreEqual(UpdateTransactionState.RolledBack, (await store.ReadStatusAsync())!.State);
    }

    [TestMethod]
    public async Task RecoverAsync_RemovesPreparedAddedFile()
    {
        using var fixture = await UpdateFixture.CreateAsync();
        var staged = await fixture.StageAsync("payload/new.txt", "new-content");
        var plan = await fixture.CreatePlanAsync(
        [
            await fixture.OperationAsync(UpdateFileOperationKind.Add, "new.txt", staged),
        ]);
        var validated = UpdatePlanValidator.Validate(plan, fixture.PlanPath);
        var store = new UpdateTransactionStore(validated);
        await File.WriteAllTextAsync(fixture.InstallPath("new.txt"), "partially-applied");
        await store.WriteStatusAsync(UpdateTransactionState.Applying);
        await store.AppendJournalAsync(new UpdateJournalEntry
        {
            Sequence = 0,
            Phase = UpdateJournalPhase.Prepared,
            Kind = UpdateFileOperationKind.Add,
            TargetPath = "new.txt",
            ExistedBefore = false,
            Timestamp = DateTimeOffset.UtcNow,
        });

        var state = await new UpdateTransactionExecutor().RecoverAsync(validated);

        Assert.AreEqual(UpdateTransactionState.RolledBack, state);
        Assert.IsFalse(File.Exists(fixture.InstallPath("new.txt")));
    }

    [TestMethod]
    public async Task RecoverAsync_FromHandoffState_DoesNotReplayJournal()
    {
        using var fixture = await UpdateFixture.CreateAsync();
        var plan = await fixture.CreatePlanAsync([]);
        var validated = UpdatePlanValidator.Validate(plan, fixture.PlanPath);
        var store = new UpdateTransactionStore(validated);
        var existingPath = fixture.InstallPath("keep.txt");
        await File.WriteAllTextAsync(existingPath, "keep-content");
        await store.WriteStatusAsync(UpdateTransactionState.HandingOff);
        await store.AppendJournalAsync(new UpdateJournalEntry
        {
            Sequence = 0,
            Phase = UpdateJournalPhase.Prepared,
            Kind = UpdateFileOperationKind.Add,
            TargetPath = "keep.txt",
            ExistedBefore = false,
            Timestamp = DateTimeOffset.UtcNow,
        });

        var state = await new UpdateTransactionExecutor().RecoverAsync(validated);

        Assert.AreEqual(UpdateTransactionState.RolledBack, state);
        Assert.AreEqual("keep-content", await File.ReadAllTextAsync(existingPath));
        Assert.IsEmpty(await store.ReadJournalAsync());
    }

    [TestMethod]
    public async Task Validate_RejectsTraversalAndCaseInsensitiveCollisionForWindowsPlan()
    {
        using var fixture = await UpdateFixture.CreateAsync(rid: "win-x64");
        var staged = await fixture.StageAsync("payload/file.txt", "content");
        var traversal = await fixture.CreatePlanAsync(
        [
            await fixture.OperationAsync(UpdateFileOperationKind.Add, "../outside.txt", staged),
        ]);
        Assert.Throws<InvalidDataException>(() => UpdatePlanValidator.Validate(traversal, fixture.PlanPath));

        var collision = await fixture.CreatePlanAsync(
        [
            await fixture.OperationAsync(UpdateFileOperationKind.Add, "Folder/File.txt", staged),
            await fixture.OperationAsync(UpdateFileOperationKind.Add, "folder/file.txt", staged),
        ]);
        Assert.Throws<InvalidDataException>(() => UpdatePlanValidator.Validate(collision, fixture.PlanPath));
    }

    [TestMethod]
    public async Task ReadStatusAsync_WhenTransactionTokenChanges_RejectsStatus()
    {
        using var fixture = await UpdateFixture.CreateAsync();
        var plan = await fixture.CreatePlanAsync([]);
        var store = new UpdateTransactionStore(UpdatePlanValidator.Validate(plan, fixture.PlanPath));
        await store.WriteStatusAsync(UpdateTransactionState.ReadyToApply);
        var changedPlan = plan with
        {
            TransactionToken = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())
                + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()),
        };
        var changedStore = new UpdateTransactionStore(UpdatePlanValidator.Validate(changedPlan, fixture.PlanPath));

        await Assert.ThrowsAsync<InvalidDataException>(async () => await changedStore.ReadStatusAsync());
    }

    [TestMethod]
    public async Task Validate_WhenUpdaterIsNotManagedExecutable_RejectsPlan()
    {
        using var fixture = await UpdateFixture.CreateAsync();
        var plan = await fixture.CreatePlanAsync([]);
        var updaterOperation = plan.Operations.Single(operation => operation.TargetPath == fixture.UpdaterTargetName);
        var invalidPlan = plan with
        {
            Operations = [updaterOperation with { Executable = false }],
        };

        Assert.Throws<InvalidDataException>(() => UpdatePlanValidator.Validate(invalidPlan, fixture.PlanPath));
    }

    [TestMethod]
    public async Task Validate_WhenOperationTargetsReservedUpdateDirectory_RejectsPlan()
    {
        using var fixture = await UpdateFixture.CreateAsync();
        var staged = await fixture.StageAsync("payload/manifest.json", "unexpected-manifest");
        var plan = await fixture.CreatePlanAsync(
        [
            await fixture.OperationAsync(
                UpdateFileOperationKind.Add,
                ".update/installed-manifest.json",
                staged),
        ]);

        Assert.Throws<InvalidDataException>(() => UpdatePlanValidator.Validate(plan, fixture.PlanPath));
    }

    [TestMethod]
    public async Task ApplyAsync_WhenRestartFails_KeepsAppliedFilesAndState()
    {
        using var fixture = await UpdateFixture.CreateAsync();
        var staged = await fixture.StageAsync("payload/new.txt", "new-content");
        var plan = await fixture.CreatePlanAsync(
        [
            await fixture.OperationAsync(UpdateFileOperationKind.Add, "new.txt", staged),
        ]);
        plan = plan with
        {
            Restart = new UpdateRestartOptions
            {
                ExecutablePath = "missing-app",
                Sha256 = new string('0', 64),
            },
        };

        var result = await new UpdateTransactionExecutor().ApplyAsync(
            UpdatePlanValidator.Validate(plan, fixture.PlanPath));

        Assert.AreEqual(UpdateTransactionState.Applied, result.State);
        Assert.IsFalse(result.RestartStarted);
        Assert.AreEqual("new-content", await File.ReadAllTextAsync(fixture.InstallPath("new.txt")));
        var status = await new UpdateTransactionStore(UpdatePlanValidator.Validate(plan, fixture.PlanPath)).ReadStatusAsync();
        Assert.AreEqual(UpdateTransactionState.Applied, status!.State);
        StringAssert.Contains(status.Message, "应用重启失败");
    }

    [TestMethod]
    public async Task ApplyAsync_WhenRollbackRuns_RestoresOriginalUnixMode()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix 文件权限测试仅适用于非 Windows 平台。");
            return;
        }

        using var fixture = await UpdateFixture.CreateAsync();
        var original = fixture.InstallPath("executable");
        await File.WriteAllTextAsync(original, "before");
        var originalMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.SetUnixFileMode(original, originalMode);
        await File.WriteAllTextAsync(fixture.InstallPath("blocked"), "not-a-directory");
        var replacement = await fixture.StageAsync("payload/executable", "after");
        var blockedAddition = await fixture.StageAsync("payload/second.txt", "second");
        var plan = await fixture.CreatePlanAsync(
        [
            await fixture.OperationAsync(UpdateFileOperationKind.Replace, "executable", replacement, original),
            await fixture.OperationAsync(UpdateFileOperationKind.Add, "blocked/second.txt", blockedAddition),
        ]);

        await Assert.ThrowsAsync<IOException>(async () =>
            await new UpdateTransactionExecutor().ApplyAsync(
                UpdatePlanValidator.Validate(plan, fixture.PlanPath),
                restartApplication: false));

        Assert.AreEqual(originalMode, File.GetUnixFileMode(original));
    }

    [TestMethod]
    public async Task HandleRolledBackStartupAsync_WhenOldAppStarts_CleansTransactionAndBootstrap()
    {
        using var fixture = await UpdateFixture.CreateAsync();
        var appName = OperatingSystem.IsWindows() ? "Diary.App.exe" : "Diary.App";
        var appPath = fixture.InstallPath(appName);
        await File.WriteAllTextAsync(appPath, "old-app");
        var appSha256 = await UpdateHash.ComputeSha256Async(appPath);
        var plan = await fixture.CreatePlanAsync([]) with
        {
            Restart = new UpdateRestartOptions
            {
                ExecutablePath = appName,
                Sha256 = appSha256,
                PreviousSha256 = appSha256,
            },
        };
        await UpdateTransactionStore.WritePlanAsync(fixture.PlanPath, plan);
        var store = new UpdateTransactionStore(UpdatePlanValidator.Validate(plan, fixture.PlanPath));
        await store.WriteStatusAsync(UpdateTransactionState.RolledBack);

        var handled = await new UpdateStartupManager().HandleRolledBackStartupAsync(
            fixture.PlanPath,
            startupSucceeded: true);

        Assert.IsTrue(handled);
        Assert.IsFalse(Directory.Exists(plan.TransactionDirectory));
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(plan.BootstrapUpdater.Path)!));
    }

    [TestMethod]
    public async Task HandleRolledBackStartupAsync_WhenOldAppStillFails_PreservesTransaction()
    {
        using var fixture = await UpdateFixture.CreateAsync();
        var plan = await fixture.CreatePlanAsync([]);
        var store = new UpdateTransactionStore(UpdatePlanValidator.Validate(plan, fixture.PlanPath));
        await store.WriteStatusAsync(UpdateTransactionState.RolledBack);

        var handled = await new UpdateStartupManager().HandleRolledBackStartupAsync(
            fixture.PlanPath,
            startupSucceeded: false);

        Assert.IsTrue(handled);
        Assert.IsTrue(Directory.Exists(plan.TransactionDirectory));
        Assert.IsTrue(File.Exists(plan.BootstrapUpdater.Path));
    }

    private sealed class UpdateFixture : IDisposable
    {
        private readonly string _root;
        private readonly string _rid;
        private readonly string _updatesRoot;
        private readonly string _staging;
        private readonly string _transaction;
        private readonly string _backup;
        private readonly string _bootstrap;
        private readonly string _targetUpdater;
        private readonly string _manifest;

        private UpdateFixture(string root, string rid)
        {
            _root = root;
            _rid = rid;
            InstallDirectory = Path.Combine(root, "install");
            _updatesRoot = Path.Combine(root, "updates");
            _staging = Path.Combine(_updatesRoot, "staging", "target");
            _transaction = Path.Combine(_updatesRoot, "transactions", Guid.NewGuid().ToString("N"));
            _backup = Path.Combine(_updatesRoot, "backup", Guid.NewGuid().ToString("N"));
            var updaterName = rid == "win-x64" ? "Diary.Updater.exe" : "Diary.Updater";
            _bootstrap = Path.Combine(_updatesRoot, "bootstrap", updaterName);
            _targetUpdater = Path.Combine(_staging, updaterName);
            _manifest = Path.Combine(_staging, "installed-manifest.json");
            PlanPath = Path.Combine(_transaction, "transaction.json");
        }

        public string InstallDirectory { get; }
        public string PlanPath { get; }
        public string UpdaterTargetName => _rid == "win-x64" ? "Diary.Updater.exe" : "Diary.Updater";

        public static async Task<UpdateFixture> CreateAsync(string? rid = null)
        {
            var fixture = new UpdateFixture(
                Path.Combine(Path.GetTempPath(), $"diary-update-tests-{Guid.NewGuid():N}"),
                rid ?? UpdateProtocol.CurrentRid);
            Directory.CreateDirectory(fixture.InstallDirectory);
            Directory.CreateDirectory(fixture._staging);
            Directory.CreateDirectory(fixture._transaction);
            Directory.CreateDirectory(Path.GetDirectoryName(fixture._bootstrap)!);
            await File.WriteAllTextAsync(fixture._bootstrap, "bootstrap-updater");
            await File.WriteAllTextAsync(fixture._targetUpdater, "target-updater");
            await File.WriteAllTextAsync(fixture._manifest, "manifest");
            return fixture;
        }

        public string InstallPath(string relativePath) =>
            Path.Combine(InstallDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));

        public async Task<string> StageAsync(string relativePath, string content)
        {
            var path = Path.Combine(_staging, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content);
            return path;
        }

        public async Task<UpdateFileOperation> OperationAsync(
            UpdateFileOperationKind kind,
            string targetPath,
            string? sourcePath = null,
            string? existingPath = null)
        {
            return new UpdateFileOperation
            {
                Kind = kind,
                TargetPath = targetPath,
                SourcePath = sourcePath is null ? null : Path.GetRelativePath(_staging, sourcePath).Replace('\\', '/'),
                SourceSize = sourcePath is null ? null : new FileInfo(sourcePath).Length,
                SourceSha256 = sourcePath is null ? null : await UpdateHash.ComputeSha256Async(sourcePath),
                ExistingSha256 = existingPath is null ? null : await UpdateHash.ComputeSha256Async(existingPath),
            };
        }

        public async Task<UpdateTransactionPlan> CreatePlanAsync(IReadOnlyList<UpdateFileOperation> operations)
        {
            var updaterTarget = _rid == "win-x64" ? "Diary.Updater.exe" : "Diary.Updater";
            var updaterOperation = await OperationAsync(UpdateFileOperationKind.Add, updaterTarget, _targetUpdater) with
            {
                Executable = true,
            };
            var plan = new UpdateTransactionPlan
            {
                TransactionId = Guid.NewGuid().ToString(),
                TransactionToken = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()) + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()),
                HandoffToken = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()) + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()),
                CurrentVersion = "1.0.0-old",
                TargetVersion = "1.0.0-new",
                Rid = _rid,
                InstallDirectory = InstallDirectory,
                UpdatesRootDirectory = _updatesRoot,
                TransactionDirectory = _transaction,
                StagingDirectory = _staging,
                BackupDirectory = _backup,
                Operations = [.. operations, updaterOperation],
                InstalledManifestSourcePath = Path.GetRelativePath(_staging, _manifest).Replace('\\', '/'),
                InstalledManifestSize = new FileInfo(_manifest).Length,
                InstalledManifestSha256 = await UpdateHash.ComputeSha256Async(_manifest),
                BootstrapUpdater = new UpdateUpdaterDescriptor
                {
                    Path = _bootstrap,
                    Sha256 = await UpdateHash.ComputeSha256Async(_bootstrap),
                    Rid = _rid,
                    ProtocolVersion = UpdateProtocol.UpdaterProtocolVersion,
                },
                TargetUpdater = new UpdateUpdaterDescriptor
                {
                    Path = _targetUpdater,
                    Sha256 = await UpdateHash.ComputeSha256Async(_targetUpdater),
                    Rid = _rid,
                    ProtocolVersion = UpdateProtocol.UpdaterProtocolVersion,
                },
            };
            await UpdateTransactionStore.WritePlanAsync(PlanPath, plan);
            return plan;
        }

        public void Dispose()
        {
            if (!Directory.Exists(_root))
                return;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.Delete(_root, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(50 * (attempt + 1));
                }
            }
        }
    }
}
