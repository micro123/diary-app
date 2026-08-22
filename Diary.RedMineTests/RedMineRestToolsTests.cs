using Diary.RedMine;
using Diary.RedMine.Response;
using Newtonsoft.Json;

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

        using var firstClient = RestTools.BasicClient(first);
        using var secondClient = RestTools.BasicClient(second);
        var request = RestTools.HttpGet(second, "/users/current.json");

        Assert.IsNotNull(firstClient);
        Assert.IsNotNull(secondClient);
        Assert.AreNotSame(firstClient, secondClient);
        Assert.AreEqual(
            "second-key",
            request.Parameters.Single(x => x.Name == "X-Redmine-API-Key").Value?.ToString());
    }

    [TestMethod]
    public void CloseIssueRequestUsesPutAndClosedStatus()
    {
        var configuration = new RedMineConfig
        {
            RedMineServerUrl = "https://redmine.example/",
            RedMineApiKey = "key",
        };

        var request = RestTools.HttpPut(configuration, IssueInfo.Fetch(42));
        var payload = JsonConvert.SerializeObject(new IssueInfo.PutRes(5));

        Assert.AreEqual(RestSharp.Method.Put, request.Method);
        Assert.AreEqual("issues/42.json", request.Resource);
        StringAssert.Contains(payload, "\"status_id\":5");
    }

    [TestMethod]
    public void CloseIssueRejectsInvalidIdWithoutRequest()
    {
        Assert.IsFalse(new RedMineApi(new RedMineConfig()).CloseIssue(0));
    }
}
