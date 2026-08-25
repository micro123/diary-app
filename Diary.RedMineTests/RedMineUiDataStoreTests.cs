using Diary.RedMine.Models;
using Diary.RedMine.UI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diary.RedMineTests;

[TestClass]
[TestCategory("Unit")]
public sealed class RedMineUiDataStoreTests
{
    [TestMethod]
    public void UpdateIssueStatusReplacesIssueAndRefreshesOpenIssues()
    {
        var store = new RedMineUiDataStore(NullLogger.Instance);
        var first = CreateIssue(1, disabled: false);
        var second = CreateIssue(2, disabled: false);
        store.RedMineIssues.Add(first);
        store.RedMineIssues.Add(second);
        store.RedMineIssuesOpen.Add(first);
        store.RedMineIssuesOpen.Add(second);

        store.UpdateIssueStatus(1, disabled: true);

        Assert.IsTrue(store.RedMineIssues[0].Disabled);
        Assert.AreEqual(1, store.RedMineIssuesOpen.Count);
        Assert.AreEqual(2, store.RedMineIssuesOpen[0].Id);

        store.UpdateIssueStatus(1, disabled: false);

        Assert.IsFalse(store.RedMineIssues[0].Disabled);
        CollectionAssert.AreEqual(
            new[] { 1, 2 },
            store.RedMineIssuesOpen.Select(issue => issue.Id).ToArray());
    }

    private static RedMineIssueDisplay CreateIssue(int id, bool disabled) => new()
    {
        Id = id,
        Title = $"Issue {id}",
        AssignedTo = "Tester",
        Project = "DiaryApp",
        Disabled = disabled,
    };
}
