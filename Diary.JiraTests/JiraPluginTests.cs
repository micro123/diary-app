using Diary.Jira;
using Diary.PluginBase;

namespace Diary.JiraTests;

[TestClass]
public sealed class JiraPluginTests
{
    [TestMethod]
    public void Manifest_DeclaresMultiInstanceJiraPlugin()
    {
        var manifest = new JiraPlugin().Manifest;

        Assert.AreEqual(JiraPluginConstants.PluginId, manifest.Id);
        Assert.IsTrue(manifest.SupportsMultipleInstances);
        Assert.IsTrue(manifest.RequiredCapabilities.Contains(PluginCapabilities.ForeignKeys));
    }

    [TestMethod]
    public void Configuration_RequiresServerAndCredentials()
    {
        Assert.IsFalse(new JiraConfig().Valid());
        Assert.IsTrue(new JiraConfig
        {
            ServerUrl = "https://jira.example",
            UserName = "user@example.com",
            ApiToken = "token",
        }.Valid());
        Assert.IsTrue(new JiraConfig
        {
            ServerUrl = "https://jira.example",
            UseBearerToken = true,
            ApiToken = "token",
        }.Valid());
    }
}
