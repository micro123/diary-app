using Diary.Jira;
using Diary.Jira.UI.ViewModels;
using Diary.RedMine;
using Diary.RedMine.UI.ViewModels;

namespace Diary.AppTests;

[TestClass]
public sealed class TrackerConfigurationProviderTests
{
    [TestMethod]
    public void Validate_DisabledEmptyInstances_DoesNotBlockSaving()
    {
        var jira = new JiraPluginConfig
        {
            Instances = new List<JiraInstanceSettings> { new() { Enabled = false } },
        };
        var redMine = new RedMinePluginConfig
        {
            Instances = new List<RedMineInstanceSettings> { new() { Enabled = false } },
        };

        Assert.IsTrue(CreateJiraProvider().Validate(jira, out var jiraError), jiraError);
        Assert.IsTrue(CreateRedMineProvider().Validate(redMine, out var redMineError), redMineError);
    }

    [TestMethod]
    public void Validate_EnabledValidInstances_Succeeds()
    {
        var jira = new JiraPluginConfig
        {
            Instances = new List<JiraInstanceSettings>
            {
                new()
                {
                    Enabled = true,
                    ServerUrl = "https://jira.example.test/",
                    UserName = "admin@example.test",
                    ApiToken = "token",
                },
            },
        };
        var redMine = new RedMinePluginConfig
        {
            Instances = new List<RedMineInstanceSettings>
            {
                new()
                {
                    Enabled = true,
                    RedMineServerUrl = "https://redmine.example.test/",
                    RedMineApiKey = "key",
                },
            },
        };

        Assert.IsTrue(CreateJiraProvider().Validate(jira, out var jiraError), jiraError);
        Assert.IsTrue(CreateRedMineProvider().Validate(redMine, out var redMineError), redMineError);
    }

    [TestMethod]
    public void Validate_EnabledInvalidInstance_Fails()
    {
        var jira = new JiraPluginConfig
        {
            Instances = new List<JiraInstanceSettings> { new() { Enabled = true } },
        };
        var redMine = new RedMinePluginConfig
        {
            Instances = new List<RedMineInstanceSettings> { new() { Enabled = true } },
        };

        Assert.IsFalse(CreateJiraProvider().Validate(jira, out _));
        Assert.IsFalse(CreateRedMineProvider().Validate(redMine, out _));
    }

    [TestMethod]
    public void Validate_AnyEnabledInvalidInstance_Fails()
    {
        var jira = new JiraPluginConfig
        {
            Instances = new List<JiraInstanceSettings>
            {
                new()
                {
                    Enabled = true,
                    ServerUrl = "https://jira.example.test/",
                    UserName = "admin@example.test",
                    ApiToken = "token",
                },
                new() { Enabled = true },
            },
        };
        var redMine = new RedMinePluginConfig
        {
            Instances = new List<RedMineInstanceSettings>
            {
                new()
                {
                    Enabled = true,
                    RedMineServerUrl = "https://redmine.example.test/",
                    RedMineApiKey = "key",
                },
                new() { Enabled = true },
            },
        };

        Assert.IsFalse(CreateJiraProvider().Validate(jira, out _));
        Assert.IsFalse(CreateRedMineProvider().Validate(redMine, out _));
    }

    private static JiraConfigurationProvider CreateJiraProvider() => new(null!, null!);

    private static RedMineConfigurationProvider CreateRedMineProvider() => new(null!, null!);
}
