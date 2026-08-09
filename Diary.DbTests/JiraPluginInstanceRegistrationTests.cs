using Diary.Jira;
using Diary.PluginBase;

namespace Diary.DbTests;

[TestClass]
public sealed class JiraPluginInstanceRegistrationTests
{
    [TestMethod]
    public void GetInstanceRegistrations_InitializesSqliteExtension()
    {
        using var db = TestDb.Create();
        var config = new JiraPluginConfig
        {
            Instances = new List<JiraInstanceSettings>
            {
                new()
                {
                    InstanceId = "jira.company",
                    DisplayName = "Company Jira",
                    Enabled = true,
                    ServerUrl = "https://jira.example",
                    UserName = "user@example.com",
                    ApiToken = "token",
                },
            },
        };

        var registrations = new JiraPlugin()
            .GetInstanceRegistrations(new PluginHostContext(db, config))
            .ToList();

        Assert.AreEqual(1, registrations.Count);
        Assert.AreEqual(TrackerInstanceState.Enabled, registrations[0].State);
        Assert.IsInstanceOfType(registrations[0].Configuration, typeof(JiraInstanceConfiguration));
        Assert.AreEqual(1u, ((JiraInstanceConfiguration)registrations[0].Configuration!).Database.SchemaVersion);
    }
}
