using Diary.Db.SQLite;
using Diary.PluginBase;
using Diary.RedMine;

namespace Diary.DbTests;

/// <summary>
/// 验证 RedMinePlugin 将已配置实例映射为 Enabled 注册项的成功路径
/// （失败→MigrationFailed 的映射由 SQLite_MigrationFailureThrowsAndLeavesNoVersionRow
/// 与 TrackerInstanceCoordinatorTests 间接覆盖）。
/// </summary>
[TestClass]
public sealed class RedMinePluginInstanceRegistrationTests
{
    [TestMethod]
    public void GetInstanceRegistrations_ReturnsEnabledForConfiguredInstance()
    {
        using var db = TestDb.Create();
        var config = new RedMinePluginConfig
        {
            Instances = new List<RedMineInstanceSettings>
            {
                new()
                {
                    InstanceId = "redmine.company",
                    DisplayName = "Company",
                    Enabled = true,
                },
                new()
                {
                    InstanceId = "redmine.disabled",
                    DisplayName = "Disabled",
                    Enabled = false,
                },
            },
        };

        var registrations = new RedMinePlugin()
            .GetInstanceRegistrations(new PluginHostContext(db, config))
            .ToList();

        // 禁用实例不参与注册
        Assert.AreEqual(1, registrations.Count);
        var reg = registrations[0];
        Assert.AreEqual("redmine.company", reg.InstanceId);
        Assert.AreEqual(TrackerInstanceState.Enabled, reg.State);

        Assert.IsInstanceOfType(reg.Configuration, typeof(RedMineInstanceConfiguration));
        var typed = (RedMineInstanceConfiguration)reg.Configuration!;
        Assert.AreEqual("redmine.company", typed.InstanceId);
        Assert.AreEqual("Company", typed.DisplayName);
        Assert.IsNotNull(typed.Database);
    }
}
