using Diary.RedMine;

namespace Diary.RedMineTests;

[TestClass]
public sealed class RedMineTagDefaultsTests
{
    [TestMethod]
    public void Apply_UsesHighestPriorityValidRulesForEmptyFields()
    {
        var rules = new[]
        {
            new RedMineTagRule { TagId = 1, ActivityId = 10, Priority = 10 },
            new RedMineTagRule { TagId = 1, ActivityId = 20, IssueId = 200, Priority = 100 },
            new RedMineTagRule { TagId = 1, IssueId = 100, Priority = 50 },
        };

        var result = RedMineTagDefaults.Apply(
            rules, 1, null, null, new HashSet<int> { 10, 20 }, new HashSet<int> { 100, 200 });

        Assert.AreEqual(20, result.ActivityId);
        Assert.AreEqual(200, result.IssueId);
    }

    [TestMethod]
    public void Apply_DoesNotOverrideExistingValues()
    {
        var rules = new[]
        {
            new RedMineTagRule { TagId = 1, ActivityId = 20, IssueId = 200 },
        };

        var result = RedMineTagDefaults.Apply(
            rules, 1, 10, 100, new HashSet<int> { 10, 20 }, new HashSet<int> { 100, 200 });

        Assert.AreEqual(10, result.ActivityId);
        Assert.AreEqual(100, result.IssueId);
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
    }
}
