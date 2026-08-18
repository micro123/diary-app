using Diary.Core;
using Diary.Db.PostgreSQL;

namespace Diary.DbTests;

[TestClass]
public sealed class PostgreSqlToolLocatorTests
{
    [TestMethod]
    public void Resolve_WindowsWithoutConfiguredDirectory_IsUnsupportedEvenWhenPathContainsTools()
    {
        using var tools = FakeTools.Create(windows: true);

        var result = PostgreSqlToolLocator.Resolve(
            new Config(),
            isWindows: true,
            isLinux: false,
            tools.DirectoryPath);

        Assert.IsFalse(result.Supported);
        StringAssert.Contains(result.UnavailableReason, "Windows 必须");
    }

    [TestMethod]
    public void Resolve_WindowsConfiguredDirectory_FindsTools()
    {
        using var tools = FakeTools.Create(windows: true);

        var result = PostgreSqlToolLocator.Resolve(
            new Config { ToolsBinPath = tools.DirectoryPath },
            isWindows: true,
            isLinux: false,
            pathEnvironment: null);

        Assert.IsTrue(result.Supported, result.UnavailableReason);
        Assert.AreEqual(tools.DirectoryPath, result.BinDirectory);
    }

    [TestMethod]
    public void Resolve_LinuxWithoutConfiguredDirectory_SearchesPath()
    {
        using var tools = FakeTools.Create(windows: false);

        var result = PostgreSqlToolLocator.Resolve(
            new Config(),
            isWindows: false,
            isLinux: true,
            tools.DirectoryPath);

        Assert.IsTrue(result.Supported, result.UnavailableReason);
        Assert.AreEqual(tools.DirectoryPath, result.BinDirectory);
    }

    [TestMethod]
    public void Resolve_MissingRestoreTool_IsUnsupported()
    {
        using var tools = FakeTools.Create(windows: false, includeRestore: false);

        var result = PostgreSqlToolLocator.Resolve(
            new Config { ToolsBinPath = tools.DirectoryPath },
            isWindows: false,
            isLinux: true,
            pathEnvironment: string.Empty);

        Assert.IsFalse(result.Supported);
        StringAssert.Contains(result.UnavailableReason, "未找到");
    }

    [TestMethod]
    public void PgRestoreList_ValidatesCustomArchiveWithoutOpeningDatabase()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("该进程测试只在 Linux 上运行。");

        using var tools = FakeTools.Create(windows: false);
        var backupPath = Path.Combine(tools.DirectoryPath, "backup.dump");
        File.WriteAllText(backupPath, "fake custom archive");
        var config = new Config
        {
            Database = "diary-test",
            User = "diary",
            Password = "secret",
            ToolsBinPath = tools.DirectoryPath,
        };
        using var db = new PgDb(new TestPgFactory(config));

        var result = db.ValidateBackup(backupPath, DataVersion.VersionCode);

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("PostgreSQL", result.ProviderName);
        Assert.AreEqual(0u, result.DataVersion);
    }

    private sealed class FakeTools : IDisposable
    {
        private FakeTools(string directoryPath) => DirectoryPath = directoryPath;

        public string DirectoryPath { get; }

        public static FakeTools Create(bool windows, bool includeRestore = true)
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                $"diary-pg-tools-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var suffix = windows ? ".exe" : string.Empty;
            File.WriteAllText(
                Path.Combine(directory, $"pg_dump{suffix}"),
                windows ? string.Empty : ToolScript("pg_dump"));
            if (includeRestore)
            {
                File.WriteAllText(
                    Path.Combine(directory, $"pg_restore{suffix}"),
                    windows ? string.Empty : ToolScript("pg_restore"));
            }
            if (!windows && OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(
                    Path.Combine(directory, "pg_dump"),
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                if (includeRestore)
                {
                    File.SetUnixFileMode(
                        Path.Combine(directory, "pg_restore"),
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }
            return new FakeTools(directory);
        }

        private static string ToolScript(string toolName)
            => string.Join(
                Environment.NewLine,
                "#!/bin/sh",
                "if [ \"$1\" = \"--version\" ]; then",
                $"  echo \"{toolName} (PostgreSQL) 16.0\"",
                "  exit 0",
                "fi",
                "if [ \"$1\" = \"--list\" ]; then",
                "  echo \"         0; 0  TABLE public work_items diary\"",
                "  exit 0",
                "fi",
                "exit 0");

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}
