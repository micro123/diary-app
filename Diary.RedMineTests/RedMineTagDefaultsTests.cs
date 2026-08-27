using Diary.RedMine;

namespace Diary.RedMineTests;

[TestClass]
public sealed class RedMineTagDefaultsTests
{
    [TestMethod]
    public void Apply_UsesConfigurationOrderForEmptyFields()
    {
        var rules = new[]
        {
            new RedMineTagRule { TagId = 1, ActivityId = 10 },
            new RedMineTagRule { TagId = 1, ActivityId = 20, IssueId = 200 },
            new RedMineTagRule { TagId = 1, IssueId = 100 },
        };

        var result = RedMineTagDefaults.Apply(
            rules, 1, null, null, new HashSet<int> { 10, 20 }, new HashSet<int> { 100, 200 });

        Assert.AreEqual(10, result.ActivityId);
        Assert.AreEqual(200, result.IssueId);
        CollectionAssert.AreEqual(
             new[] { rules[0].RuleId, rules[1].RuleId },
            result.Winners.Select(winner => winner.RuleId).ToArray());
    }

    [TestMethod]
    public void Apply_DoesNotOverrideExistingValues()
    {
        var rules = new[]
        {
            new RedMineTagRule
            {
                TagId = 1,
                ActivityId = 20,
                IssueId = 200,
                ForceOverwrite = false,
            },
        };

        var result = RedMineTagDefaults.Apply(
            rules, 1, 10, 100, new HashSet<int> { 10, 20 }, new HashSet<int> { 100, 200 });

        Assert.AreEqual(10, result.ActivityId);
        Assert.AreEqual(100, result.IssueId);
    }

    [TestMethod]
    public void Apply_ForceOverwriteReplacesExistingValues()
    {
        var rule = new RedMineTagRule
        {
            TagId = 1,
            ActivityId = 20,
            IssueId = 200,
            ForceOverwrite = true,
        };

        var result = RedMineTagDefaults.Apply(
            [rule], 1, 10, 100, new HashSet<int> { 10, 20 }, new HashSet<int> { 100, 200 });

        Assert.AreEqual(20, result.ActivityId);
        Assert.AreEqual(200, result.IssueId);
        CollectionAssert.AreEqual(
            new[] { nameof(RedMineTagRule.ActivityId), nameof(RedMineTagRule.IssueId) },
            result.Winners.Select(winner => winner.Field).ToArray());
    }

    [TestMethod]
    public void Apply_IgnoresDisabledOtherTagAndUnavailableTargets()
    {
        var rules = new[]
        {
            new RedMineTagRule { TagId = 1, ActivityId = 20, Enabled = false },
            new RedMineTagRule { TagId = 2, ActivityId = 10 },
            new RedMineTagRule { TagId = 1, ActivityId = 99, IssueId = 999 },
        };

        var result = RedMineTagDefaults.Apply(
            rules, 1, null, null, new HashSet<int> { 10, 20 }, new HashSet<int> { 100, 200 });

        Assert.IsNull(result.ActivityId);
        Assert.IsNull(result.IssueId);
        Assert.AreEqual(2, result.InvalidTargets.Count);
    }

    [TestMethod]
    public void Apply_InvalidTargetFallsBackToNextValidTarget()
    {
        var invalid = new RedMineTagRule { TagId = 1, ActivityId = 99 };
        var valid = new RedMineTagRule { TagId = 1, ActivityId = 10 };

        var result = RedMineTagDefaults.Apply(
            [invalid, valid], 1, null, null, new HashSet<int> { 10 }, new HashSet<int>());

        Assert.AreEqual(10, result.ActivityId);
        Assert.AreEqual(valid.RuleId, result.Winners.Single().RuleId);
        Assert.AreEqual(invalid.RuleId, result.InvalidTargets.Single().RuleId);
    }

    [TestMethod]
    public void Apply_ConflictingTargetsUseFirstRuleAndReportConflict()
    {
        var first = new RedMineTagRule { TagId = 1, ActivityId = 10 };
        var second = new RedMineTagRule { TagId = 1, ActivityId = 20 };

        var result = RedMineTagDefaults.Apply(
            [first, second], 1, null, null, new HashSet<int> { 10, 20 }, new HashSet<int>());

        Assert.AreEqual(10, result.ActivityId);
        Assert.AreEqual(first.RuleId, result.Winners.Single().RuleId);
        Assert.AreEqual(first.RuleId, result.Conflicts.Single().WinningRuleId);
        CollectionAssert.AreEqual(
            new[] { first.RuleId, second.RuleId },
            result.Conflicts.Single().RuleIds.ToArray());
    }
}
