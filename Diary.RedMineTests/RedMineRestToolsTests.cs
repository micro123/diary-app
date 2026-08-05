using Diary.RedMine;

namespace Diary.RedMineTests;

[TestClass]
[TestCategory("Unit")]
public sealed class RedMineRestToolsTests
{
    [TestMethod]
    public void DifferentConfigurationsUseDifferentClientsAndApiKeys()
    {
        var first = new RedMineConfig
        {
            RedMineServerUrl = "https://first.example/",
            RedMineApiKey = "first-key",
        };
        var second = new RedMineConfig
        {
            RedMineServerUrl = "https://second.example/",
            RedMineApiKey = "second-key",
        };

        var firstClient = RestTools.BasicClient(first);
        var secondClient = RestTools.BasicClient(second);
        var request = RestTools.HttpGet(second, "/users/current.json");

        Assert.IsNotNull(firstClient);
        Assert.IsNotNull(secondClient);
        Assert.AreNotSame(firstClient, secondClient);
        Assert.AreEqual(
            "second-key",
            request.Parameters.Single(x => x.Name == "X-Redmine-API-Key").Value?.ToString());
    }
}
