using Diary.RedMine;
using Diary.RedMine.UI;
using Diary.RedMine.UI.ViewModels;

namespace Diary.RedMineTests;

[TestClass]
public sealed class RedMineConfigurationEditSessionTests
{
    [TestMethod]
    public void IndependentSessionsCommitOnlyTheirOwnChanges()
    {
        var source = CreateSettings();
        var service = new RedMineConfigurationEditService();
        var settingsSession = service.Open(new RedMinePluginConfig { Instances = [source] });
        var ruleSession = service.Open(source);
        settingsSession.WorkingCopy.Instances.Single().DisplayName = "renamed";
        ruleSession.WorkingCopy.TagRules.Add(new RedMineTagRule { TagId = 2 });

        settingsSession.Commit();

        Assert.AreEqual("renamed", source.DisplayName);
        Assert.AreEqual(1, source.TagRules.Count);
        ruleSession.Commit();
        Assert.AreEqual("renamed", source.DisplayName);
        Assert.AreEqual(2, source.TagRules.Count);
    }

    [TestMethod]
    public void ReloadDiscardsUncommittedChanges()
    {
        var source = CreateSettings();
        var session = new RedMineConfigurationEditService().Open(source);
        session.WorkingCopy.DisplayName = "draft";
        session.WorkingCopy.TagRules.Clear();

        session.Reload();

        Assert.AreEqual("source", session.WorkingCopy.DisplayName);
        Assert.AreEqual(1, session.WorkingCopy.TagRules.Count);
        Assert.AreEqual("source", source.DisplayName);
    }

    [TestMethod]
    public void ReloadSynchronizesChangesCommittedByAnotherSession()
    {
        var source = CreateSettings();
        var service = new RedMineConfigurationEditService();
        var first = service.Open(source);
        var second = service.Open(source);
        first.WorkingCopy.DisplayName = "committed";
        first.Commit();

        second.Reload();

        Assert.AreEqual("committed", second.WorkingCopy.DisplayName);
    }

    [TestMethod]
    public void SessionsMergeChangesToDifferentRules()
    {
        var source = CreateSettings();
        source.TagRules.Add(new RedMineTagRule { TagId = 2 });
        var service = new RedMineConfigurationEditService();
        var first = service.Open(source);
        var second = service.Open(source);
        first.WorkingCopy.TagRules[0].Priority = 10;
        second.WorkingCopy.TagRules[1].ActivityId = 42;

        first.Commit();
        second.Commit();

        Assert.AreEqual(10, source.TagRules[0].Priority);
        Assert.AreEqual(42, source.TagRules[1].ActivityId);
    }

    [TestMethod]
    public void RuleViewModelIncludesInvalidTargetsInItemsSource()
    {
        var rule = new RedMineTagRule { TagId = 7, ActivityId = 42, IssueId = 84 };
        var viewModel = new RedMineTagRuleViewModel(
            rule,
            [new RedMineTagRuleOption(7, "tag")],
            [new RedMineTagRuleOption(0, "none")],
            [new RedMineTagRuleOption(0, "none")]);

        Assert.AreEqual(42, viewModel.SelectedActivity!.Id);
        Assert.AreEqual(84, viewModel.SelectedIssue!.Id);
        Assert.IsTrue(viewModel.Activities.Any(option => option.Id == 42));
        Assert.IsTrue(viewModel.Issues.Any(option => option.Id == 84));
    }

    private static RedMineInstanceSettings CreateSettings()
        => new()
        {
            InstanceId = "redmine.test",
            DisplayName = "source",
            TagRules = [new RedMineTagRule { TagId = 1 }],
        };
}
