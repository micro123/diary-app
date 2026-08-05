using Diary.PluginBase;
using Diary.RedMine;
using Newtonsoft.Json.Linq;

namespace Diary.DbTests;

[TestClass]
public sealed class RedMineConfigurationMigrationTests
{
    [TestMethod]
    public void LegacySingleInstance_IsConvertedWithoutDroppingUnknownFields()
    {
        var payload = new JObject
        {
            ["RedMineServerUrl"] = "https://redmine.example",
            ["RedMineApiKey"] = "secret",
            ["UnknownField"] = new JObject { ["value"] = 42 },
        };

        var migrated = (JObject)new RedMineConfigurationMigration().Migrate(payload);
        var instance = (JObject)((JArray)migrated["Instances"]!)[0]!;

        Assert.AreEqual(RedMinePluginConstants.DefaultInstanceId, (string?)instance["InstanceId"]);
        Assert.AreEqual("https://redmine.example", (string?)instance["RedMineServerUrl"]);
        Assert.AreEqual("secret", (string?)instance["RedMineApiKey"]);
        Assert.IsTrue((bool?)instance["Enabled"]);
        Assert.AreEqual(42, (int?)migrated["UnknownField"]!["value"]);
    }

    [TestMethod]
    public void EmptyInstances_CreateEnabledDefaultInstance()
    {
        var payload = new JObject
        {
            ["Instances"] = new JArray(),
            ["RedMineServerUrl"] = "https://redmine.example",
            ["RedMineApiKey"] = "secret",
        };

        var migrated = (JObject)new RedMineConfigurationMigration().Migrate(payload);
        var instance = (JObject)((JArray)migrated["Instances"]!)[0]!;

        Assert.AreEqual(RedMinePluginConstants.DefaultInstanceId, (string?)instance["InstanceId"]);
        Assert.IsTrue((bool?)instance["Enabled"]);
    }

    [TestMethod]
    public void ExistingInstances_GetStableIdsAndDefaults()
    {
        var payload = new JObject
        {
            ["Instances"] = new JArray(
                new JObject { ["InstanceId"] = "redmine.company" },
                new JObject { ["InstanceId"] = "redmine.company" },
                new JObject()),
        };

        var migrated = (JObject)new RedMineConfigurationMigration().Migrate(payload);
        var instances = (JArray)migrated["Instances"]!;

        Assert.AreEqual("redmine.company", (string?)instances[0]!["InstanceId"]);
        Assert.AreEqual("redmine.default", (string?)instances[1]!["InstanceId"]);
        Assert.AreEqual("redmine.default.1", (string?)instances[2]!["InstanceId"]);
        Assert.IsFalse((bool?)instances[2]!["Enabled"]);
    }
}
