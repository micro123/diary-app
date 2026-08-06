using Diary.Core.Configure;
using Diary.RedMine;
using Newtonsoft.Json.Linq;

namespace Diary.RedMineTests;

[TestClass]
public sealed class RedMineConfigurationMigrationTests
{
    [TestMethod]
    public void Migration_AddsRuleIdsAndPreservesUnknownFields()
    {
        var payload = new JObject
        {
            ["RootUnknown"] = "root",
            ["Instances"] = new JArray
            {
                new JObject
                {
                    ["InstanceId"] = "company",
                    ["InstanceUnknown"] = "instance",
                    ["TagRules"] = new JArray
                    {
                        new JObject
                        {
                            ["TagId"] = 7,
                            ["RuleUnknown"] = "rule",
                        },
                    },
                },
            },
        };

        var migrated = (JObject)new RedMineConfigurationMigration().Migrate(payload);
        var instance = (JObject)migrated["Instances"]![0]!;
        var rule = (JObject)instance["TagRules"]![0]!;

        Assert.AreEqual("root", (string?)migrated["RootUnknown"]);
        Assert.AreEqual("instance", (string?)instance["InstanceUnknown"]);
        Assert.AreEqual("rule", (string?)rule["RuleUnknown"]);
        Assert.IsFalse(string.IsNullOrWhiteSpace((string?)rule["RuleId"]), migrated.ToString());
    }

    [TestMethod]
    public void Plugin_DeclaresContinuousVersionTwoConfigurationSchema()
    {
        var migrations = new RedMinePlugin().GetConfigurationMigrations().ToArray();

        Assert.AreEqual(2, migrations.Length);
        CollectionAssert.AreEqual(new[] { 0, 1 }, migrations.Select(item => item.FromVersion).ToArray());
        CollectionAssert.AreEqual(new[] { 1, 2 }, migrations.Select(item => item.ToVersion).ToArray());
        Assert.IsTrue(migrations.All(item => item.PluginId == RedMinePluginConstants.PluginId));
    }

    [TestMethod]
    public void IconMigration_AddsDefaultAndPreservesConfiguredIcon()
    {
        var payload = new JObject
        {
            ["Instances"] = new JArray
            {
                new JObject { ["InstanceId"] = "default" },
                new JObject { ["InstanceId"] = "custom", ["Icon"] = "mdi-server" },
            },
        };

        var migrated = (JObject)new RedMineIconConfigurationMigration().Migrate(payload);

        Assert.AreEqual(RedMinePluginConstants.DefaultIcon, (string?)migrated["Instances"]![0]!["Icon"]);
        Assert.AreEqual("mdi-server", (string?)migrated["Instances"]![1]!["Icon"]);
    }

    [TestMethod]
    public void Instance_InvalidIconFallsBackToDefault()
    {
        var settings = new RedMineInstanceSettings { Icon = "invalid icon" };
        var instance = new RedMineInstance(new RedMineInstanceConfiguration(
            settings.InstanceId,
            settings.DisplayName,
            settings,
            null!));

        Assert.AreEqual(RedMinePluginConstants.DefaultIcon, instance.Icon);
    }

    [TestMethod]
    public void ConfigurationFileIsEncryptedAndApiKeyIsPasswordField()
    {
        var storage = typeof(RedMinePluginConfig)
            .GetCustomAttributes(typeof(StorageFileAttribute), false)
            .Cast<StorageFileAttribute>()
            .Single();
        var apiKey = typeof(RedMineConfig).GetProperty(nameof(RedMineConfig.RedMineApiKey))!;
        var configure = apiKey.GetCustomAttributes(typeof(ConfigureTextAttribute), false)
            .Cast<ConfigureTextAttribute>()
            .Single();

        Assert.IsTrue(storage.Encrypted);
        Assert.IsTrue(configure.IsPassword);
    }
}
