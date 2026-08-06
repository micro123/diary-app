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
        WorkTag[] tags =
        [
            new() { Id = 3, Name = "primary", Level = TagLevels.Primary },
            new() { Id = 5, Name = "secondary", Level = TagLevels.Secondary },
        ];

        Assert.IsTrue(store.TryAdd("August", query, out var error, tags), error);

        var saved = store.Queries.Single();
        Assert.AreEqual("August", saved.Name);
        CollectionAssert.AreEqual(new[] { 3, 5 }, saved.Tags!.Select(tag => tag.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "primary", "secondary" }, saved.Tags!.Select(tag => tag.Name).ToArray());
        Assert.AreEqual(WorkItemTagFilter.All, saved.TagFilter);
        Assert.AreEqual(SavedWorkItemQueryStore.CurrentSchemaVersion, store.SchemaVersion);
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

    [TestMethod]
    public void NormalizeLoadedQueries_MigratesLegacyAndRepairsOrDropsBadRecords()
    {
        var duplicateId = Guid.NewGuid();
        var store = new SavedWorkItemQueryStore(false, false)
        {
            SchemaVersion = 1,
            Queries =
            [
                new() { Id = Guid.Empty, Name = " Query ", TagIds = [7, 7], TagFilter = WorkItemTagFilter.Any },
                new() { Id = duplicateId, Name = "Query", Tags = null, TagIds = [] },
                new() { Id = duplicateId, Name = "Other", Tags = null, TagIds = [] },
                new() { Name = " ", Tags = null, TagIds = [] },
                new() { Name = "invalid enum", TagFilter = (WorkItemTagFilter)99, Tags = null, TagIds = [] },
                null!,
            ],
        };
        WorkTag[] availableTags =
        [
            new() { Id = 7, Name = "snapshot", Level = TagLevels.Secondary },
        ];

        Assert.IsTrue(store.NormalizeLoadedQueries(availableTags));

        Assert.AreEqual(3, store.Queries.Count);
        Assert.IsTrue(store.Queries.All(query => query.Id != Guid.Empty));
        Assert.AreEqual(3, store.Queries.Select(query => query.Id).Distinct().Count());
        CollectionAssert.AreEqual(new[] { "Query", "Query (2)", "Other" }, store.Queries.Select(query => query.Name).ToArray());
        Assert.AreEqual("snapshot", store.Queries[0].Tags!.Single().Name);
        Assert.IsNull(store.Queries[0].TagIds);
        StringAssert.Contains(store.LoadWarning, "忽略 3");
    }

    [TestMethod]
    public void NormalizeLoadedQueries_DropsInvalidSnapshotsAndNullTagCollections()
    {
        var store = new SavedWorkItemQueryStore(false, false)
        {
            SchemaVersion = SavedWorkItemQueryStore.CurrentSchemaVersion,
            Queries =
            [
                new() { Name = "null tags", Tags = null },
                new() { Name = "empty tag name", Tags = [new() { Id = 1, Name = "" }] },
                new() { Name = "valid", Tags = [] },
            ],
        };

        store.NormalizeLoadedQueries();

        Assert.AreEqual("valid", store.Queries.Single().Name);
    }

    [TestMethod]
    public void NormalizeLoadedQueries_PreservesUnresolvedLegacyTagWithoutUnsafeMatching()
    {
        var store = new SavedWorkItemQueryStore(false, false)
        {
            SchemaVersion = 1,
            Queries =
            [
                new()
                {
                    Name = "missing legacy tag",
                    Tags = null,
                    TagIds = [99],
                    TagFilter = WorkItemTagFilter.Any,
                },
            ],
        };

        store.NormalizeLoadedQueries();
        store.NormalizeLoadedQueries();

        var tag = store.Queries.Single().Tags!.Single();
        Assert.AreEqual(99, tag.Id);
        Assert.IsTrue(tag.Unresolved);
        Assert.AreEqual(string.Empty, tag.Name);
    }

    [TestMethod]
    public void NormalizeLoadedQueries_DoesNotDowngradeUnknownFutureSchema()
    {
        var store = new SavedWorkItemQueryStore(false, false)
        {
            SchemaVersion = SavedWorkItemQueryStore.CurrentSchemaVersion + 1,
            Queries = [new() { Name = "future" }],
        };

        Assert.IsFalse(store.NormalizeLoadedQueries());

        Assert.AreEqual(0, store.Queries.Count);
        StringAssert.Contains(store.LoadWarning, "高于当前支持版本");
    }
}
