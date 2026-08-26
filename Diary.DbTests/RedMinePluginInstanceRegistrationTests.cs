using Diary.Core.Utils;
using Diary.PluginBase;
using Diary.RedMine;
using Newtonsoft.Json.Linq;

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

    [TestMethod]
    public void TrySetInstanceEnabled_ChangesOnlySelectedInstance()
    {
        var config = new RedMinePluginConfig
        {
            Instances = new List<RedMineInstanceSettings>
            {
                new() { InstanceId = "redmine.company", Enabled = true },
                new() { InstanceId = "redmine.personal", Enabled = true },
            },
        };
        var plugin = new RedMinePlugin();

        Assert.IsTrue(plugin.TrySetInstanceEnabled(config, "redmine.company", false));
        Assert.IsFalse(config.Instances[0].Enabled);
        Assert.IsTrue(config.Instances[1].Enabled);
    }

    [TestMethod]
    public void PluginConfigurationLoader_PreservesEnabledDefaultInstanceFromPackage()
    {
        var seed = new RedMinePluginConfig();
        var package = new JObject
        {
            ["PluginId"] = RedMinePluginConstants.PluginId,
            ["SchemaVersion"] = 1,
            ["Payload"] = new JObject
            {
                ["Instances"] = new JArray
                {
                    new JObject
                    {
                        ["InstanceId"] = RedMinePluginConstants.DefaultInstanceId,
                        ["Enabled"] = true,
                        ["RedMineServerUrl"] = "http://redmine.local",
                        ["RedMineApiKey"] = "api-key",
                    },
                },
            },
        };

        try
        {
            var configRoot = Diary.Utils.FsTools.GetApplicationConfigDirectory();
            var testRoot = Environment.GetEnvironmentVariable("DIARY_TEST_APPLICATION_ROOT");
            Assert.IsFalse(string.IsNullOrWhiteSpace(testRoot));
            Assert.IsTrue(Path.GetFullPath(configRoot).StartsWith(
                Path.GetFullPath(testRoot!),
                StringComparison.Ordinal));
            Assert.IsTrue(EasySaveLoad.SaveJson(seed, package));
            var configuration = (RedMinePluginConfig)new Diary.App.PluginConfigurationLoader()
                .Load(new RedMinePlugin());

            var instance = new RedMinePlugin().GetInstanceConfigurations(configuration).Single();
            Assert.IsTrue(instance.Enabled);
            Assert.AreEqual(RedMinePluginConstants.DefaultInstanceId, instance.InstanceId);
        }
        finally
        {
            var path = Path.Combine(Diary.Utils.FsTools.GetApplicationConfigDirectory(), "redmine_settings.json");
            if (File.Exists(path))
                File.Delete(path);
            if (File.Exists(path + ".bak"))
                File.Delete(path + ".bak");
        }
    }
}
