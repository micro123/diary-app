using Diary.Database;

namespace Diary.DbTests;

/// <summary>
/// 核心数据库初始化不得创建任何 tracker 专用表（架构 §7.1）。
/// </summary>
[TestClass]
public sealed class CoreIsolationTests
{
    [TestMethod]
    public void CoreInit_CreatesNoTrackerTables()
    {
        using var db = TestDb.Create();
        var host = (IDbExtensionHost)db;

        Assert.IsFalse(host.Exists(
            "SELECT name FROM sqlite_master WHERE type='table' AND name='redmine_issues';"));
        Assert.IsFalse(host.Exists(
            "SELECT name FROM sqlite_master WHERE type='table' AND name='redmine_projects';"));
        Assert.IsFalse(host.Exists(
            "SELECT name FROM sqlite_master WHERE type='table' AND name='redmine_activities';"));
        Assert.IsFalse(host.Exists(
            "SELECT name FROM sqlite_master WHERE type='table' AND name='redmine_time_entries';"));
        // 插件版本表也由插件迁移创建，核心迁移不应建。
        Assert.IsFalse(host.Exists(
            "SELECT name FROM sqlite_master WHERE type='table' AND name='plugin_data_versions';"));
    }
}
