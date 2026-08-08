using System.Collections.Immutable;
using Diary.ScriptHost;
using Diary.ScriptBase;

namespace Diary.ScriptTests;

[TestClass]
public sealed class WorkItemStreamScriptApiTests
{
    [TestMethod]
    public async Task StreamAsync_ReadsAllPagesWithoutRetainingFullResult()
    {
        IWorkItemQueryScriptApi api = new PagedQueryApi(1_205);
        var items = new List<ScriptWorkItem>();

        await foreach (var item in api.StreamAsync(new ScriptWorkItemQuery
        {
            StartDate = "2026-01-01",
            EndDate = "2026-12-31",
        }, pageSize: 500))
            items.Add(item);

        Assert.AreEqual(1_205, items.Count);
        Assert.AreSequenceEqual(Enumerable.Range(1, 1_205), items.Select(item => item.Id));
        CollectionAssert.AreEqual(new[] { 0, 500, 1000 }, ((PagedQueryApi)api).Offsets.ToArray());
    }

    [TestMethod]
    public async Task StreamAsync_RejectsPageLargerThanTransportSafeLimit()
    {
        IWorkItemQueryScriptApi api = new PagedQueryApi(1);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
        {
            await foreach (var _ in api.StreamAsync(new ScriptWorkItemQuery(), pageSize: 501)) { }
        });
    }

    [TestMethod]
    public async Task StreamAsync_ReadsYearRangeFromSqliteAcrossPages()
    {
        using var database = TestDatabase.Create();
        for (var index = 0; index < 1_205; index++)
            database.CreateWorkItem($"2026-{index % 12 + 1:00}-{index % 28 + 1:00}", $"year item {index + 1}");
        IWorkItemQueryScriptApi api = new WorkItemQueryScriptApi(() => database);
        var count = 0;

        await foreach (var item in api.StreamAsync(new ScriptWorkItemQuery
        {
            StartDate = "2026-01-01",
            EndDate = "2026-12-31",
        }, pageSize: 200))
        {
            Assert.IsTrue(item.Date.StartsWith("2026-", StringComparison.Ordinal));
            count++;
        }

        Assert.AreEqual(1_205, count);
    }

    private sealed class PagedQueryApi(int total) : IWorkItemQueryScriptApi
    {
        public List<int> Offsets { get; } = [];

        public ValueTask<ScriptWorkItemQueryResult> QueryAsync(
            ScriptWorkItemQuery query, CancellationToken cancellationToken = default)
        {
            Offsets.Add(query.Offset);
            var count = Math.Min(query.Limit ?? 100, Math.Max(0, total - query.Offset));
            var items = Enumerable.Range(query.Offset + 1, count)
                .Select(id => new ScriptWorkItem(id, "2026-01-01", $"item {id}", 1, 0, null,
                    ImmutableArray<ScriptWorkTag>.Empty))
                .ToImmutableArray();
            return ValueTask.FromResult(ScriptWorkItemQueryResult.Success(items, query));
        }
    }
}
