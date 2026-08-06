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
    public void Plugin_DeclaresVersionOneConfigurationSchema()
    {
        var migration = new RedMinePlugin().GetConfigurationMigrations().Single();

        Assert.AreEqual(RedMinePluginConstants.PluginId, migration.PluginId);
        Assert.AreEqual(0, migration.FromVersion);
        Assert.AreEqual(1, migration.ToVersion);
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
