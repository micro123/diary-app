using System.Text;
using Diary.Core.ConfigurationTools;

namespace Diary.UtilTests;

[TestClass]
public sealed class ConfigurationDocumentStoreTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "Diary.ConfigTool.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    [TestMethod]
    public void EncryptedDocument_RoundTripsAndCreatesBackup()
    {
        var descriptor = new ConfigurationFileDescriptor(
            "test.secure",
            "测试加密配置",
            "测试",
            "secure.json",
            "test-purpose");
        var store = new ConfigurationDocumentStore(_root);

        var first = store.Save(descriptor, "{\"secret\":\"first-value\"}", null);
        var second = store.Save(descriptor, "{\"secret\":\"second-value\"}", first.Fingerprint);

        Assert.AreEqual(ConfigurationDocumentStatus.Loaded, second.Status);
        StringAssert.Contains(second.Json!, "second-value");
        Assert.IsTrue(File.Exists(Path.Combine(_root, ".diary-master-key")));
        Assert.IsTrue(File.Exists(Path.Combine(_root, "secure.json.bak")));
        Assert.AreEqual(-1, File.ReadAllBytes(Path.Combine(_root, "secure.json"))
            .AsSpan().IndexOf(Encoding.UTF8.GetBytes("second-value")));
        var backup = store.Load(descriptor, useBackup: true);
        StringAssert.Contains(backup.Json!, "first-value");
    }

    [TestMethod]
    public void Save_RejectsExternalModification()
    {
        var descriptor = new ConfigurationFileDescriptor("test.plain", "测试", "测试", "plain.json");
        var store = new ConfigurationDocumentStore(_root);
        var loaded = store.Save(descriptor, "{\"value\":1}", null);
        File.WriteAllText(Path.Combine(_root, "plain.json"), "{\"value\":2}");

        var exception = Assert.ThrowsExactly<IOException>(() =>
            store.Save(descriptor, "{\"value\":3}", loaded.Fingerprint));

        StringAssert.Contains(exception.Message, "其他进程修改");
        StringAssert.Contains(File.ReadAllText(Path.Combine(_root, "plain.json")), "2");
    }

    [TestMethod]
    public void RestoreBackup_PreservesPreRestoreFile()
    {
        var descriptor = new ConfigurationFileDescriptor("test.plain", "测试", "测试", "plain.json");
        var store = new ConfigurationDocumentStore(_root);
        var first = store.Save(descriptor, "{\"value\":1}", null);
        store.Save(descriptor, "{\"value\":2}", first.Fingerprint);

        var recoveryPath = store.RestoreBackup(descriptor);

        Assert.IsTrue(File.Exists(recoveryPath));
        StringAssert.Contains(store.Load(descriptor).Json!, "1");
        StringAssert.Contains(File.ReadAllText(recoveryPath), "2");
    }

    [TestMethod]
    public void Transfer_ExportsPlaintextAndImportsWithSnapshot()
    {
        var descriptor = new ConfigurationFileDescriptor(
            "test.secure",
            "测试加密配置",
            "test",
            "secure.json",
            "test-purpose");
        var sourceRoot = Path.Combine(_root, "source");
        var targetRoot = Path.Combine(_root, "target");
        var exportRoot = Path.Combine(_root, "export");
        var source = new ConfigurationDocumentStore(sourceRoot);
        source.Save(descriptor, "{\"secret\":\"portable-value\"}", null);
        var transfer = new ConfigurationTransferService();

        var exported = transfer.Export(source, [descriptor], exportRoot);
        var imported = transfer.Import(
            new ConfigurationDocumentStore(targetRoot),
            new Dictionary<string, ConfigurationFileDescriptor> { [descriptor.Id] = descriptor },
            exportRoot);

        Assert.AreEqual(1, exported.ExportedCount);
        Assert.AreEqual(1, imported.ImportedCount);
        Assert.IsTrue(Directory.Exists(imported.SnapshotDirectory));
        var exportedJson = File.ReadAllText(Path.Combine(exportRoot, "test", "test-secure.json"));
        StringAssert.Contains(exportedJson, "portable-value");
        var target = new ConfigurationDocumentStore(targetRoot).Load(descriptor);
        StringAssert.Contains(target.Json!, "portable-value");
    }

    [TestMethod]
    public void ResetUnreadableMasterKey_PreservesOldKeyAndAllowsReimport()
    {
        var descriptor = new ConfigurationFileDescriptor(
            "test.secure",
            "测试加密配置",
            "测试",
            "secure.json",
            "test-purpose");
        var store = new ConfigurationDocumentStore(_root);
        store.Save(descriptor, "{\"secret\":\"old-value\"}", null);
        File.WriteAllBytes(Path.Combine(_root, ".diary-master-key"), new byte[31]);
        var unreadable = store.Load(descriptor);

        var reset = store.ResetUnreadableMasterKey([descriptor]);
        var imported = store.Save(descriptor, "{\"secret\":\"new-value\"}", unreadable.Fingerprint);

        Assert.IsTrue(File.Exists(reset.RetainedMasterKeyPath));
        Assert.IsTrue(Directory.Exists(reset.SnapshotDirectory));
        Assert.AreEqual(ConfigurationMasterKeyStatus.Readable, store.DiagnoseMasterKey().Status);
        StringAssert.Contains(imported.Json!, "new-value");
    }

    [TestMethod]
    public void WindowsDpapiMigration_IsRejectedOnOtherPlatforms()
    {
        if (OperatingSystem.IsWindows())
            return;

        var store = new ConfigurationDocumentStore(_root);
        var service = new WindowsDpapiMigrationService();
        Assert.IsEmpty(service.DiscoverPreviousSidCandidates());
        var exception = Assert.ThrowsExactly<PlatformNotSupportedException>(() =>
            service.Migrate(store, [], "S-1-5-21-1-2-3-1001", "old-password"));

        StringAssert.Contains(exception.Message, "仅支持 Windows");
    }
}
