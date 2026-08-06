using Diary.App.Models;
using Diary.Core.Data.Base;
using Newtonsoft.Json.Linq;

namespace Diary.UtilTests;

[TestClass]
public sealed class SavedWorkItemQueryStoreTests
{
    [TestMethod]
    public void Add_SerializesOnlyNameAndQueryConditions()
    {
        var store = new SavedWorkItemQueryStore(false, false);
        var query = new WorkItemQuery
        {
            StartDate = "2026-08-01",
            EndDate = "2026-08-31",
            Text = "planning",
            TagIds = new[] { 3, 5, 3 },
            TagFilter = WorkItemTagFilter.All,
            Priority = WorkPriorities.P1,
        };

        Assert.IsTrue(store.TryAdd("August", query, out var error), error);

        var saved = store.Queries.Single();
        Assert.AreEqual("August", saved.Name);
        CollectionAssert.AreEqual(new[] { 3, 5 }, saved.TagIds);
        Assert.AreEqual(WorkItemTagFilter.All, saved.TagFilter);
        var json = JObject.FromObject(store).ToString();
        Assert.IsFalse(json.Contains("result", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("tracker", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void RenameUpdateDelete_UsesStableIdentityAndRejectsDuplicateNames()
    {
        var store = new SavedWorkItemQueryStore(false, false);
        Assert.IsTrue(store.TryAdd("First", new WorkItemQuery(), out _));
        Assert.IsTrue(store.TryAdd("Second", new WorkItemQuery(), out _));
        var firstId = store.Queries[0].Id;

        Assert.IsFalse(store.TryRename(firstId, "second", out var duplicateError));
        Assert.AreEqual("查询名称已存在", duplicateError);
        Assert.IsTrue(store.TryRename(firstId, "Renamed", out _));
        Assert.IsTrue(store.TryUpdate(firstId, new WorkItemQuery { Text = "changed" }, out _));
        Assert.AreEqual("Renamed", store.Queries.Single(saved => saved.Id == firstId).Name);
        Assert.AreEqual("changed", store.Queries.Single(saved => saved.Id == firstId).Text);
        Assert.IsTrue(store.TryDelete(firstId, out _));
        Assert.AreEqual("Second", store.Queries.Single().Name);
    }
}
