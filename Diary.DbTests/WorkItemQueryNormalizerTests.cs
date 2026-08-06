using Diary.Core.Data.Base;

namespace Diary.DbTests;

[TestClass]
public sealed class WorkItemQueryNormalizerTests
{
    [TestMethod]
    public void Normalize_TrimsAndDeduplicatesReusableQueryInput()
    {
        var query = WorkItemQueryNormalizer.Normalize(new WorkItemQuery
        {
            StartDate = " 2026-08-01 ",
            EndDate = "2026-08-31",
            Text = " planning ",
            TagFilter = WorkItemTagFilter.Exact,
            TagIds = new[] { 2, 1, 2 },
            Limit = 200,
        });

        Assert.AreEqual("2026-08-01", query.StartDate);
        Assert.AreEqual("planning", query.Text);
        CollectionAssert.AreEqual(new[] { 2, 1 }, query.TagIds.ToArray());
    }

    [TestMethod]
    public void Normalize_RejectsMalformedDatesEnumsTagsAndPagination()
    {
        WorkItemQuery[] invalid =
        [
            new() { StartDate = "2026-8-01" },
            new() { StartDate = "2026-08-02", EndDate = "2026-08-01" },
            new() { TagFilter = (WorkItemTagFilter)99 },
            new() { Priority = (WorkPriorities)99 },
            new() { TagIds = null! },
            new() { TagIds = Enumerable.Range(1, WorkItemQueryNormalizer.MaxTagCount + 1).ToArray() },
            new() { TagIds = new[] { 0 } },
            new() { TagFilter = WorkItemTagFilter.Any },
            new() { Limit = 0 },
            new() { Limit = WorkItemQueryNormalizer.MaxLimit + 1 },
            new() { Offset = -1 },
            new() { Offset = 1 },
        ];

        foreach (var query in invalid)
            Assert.IsFalse(WorkItemQueryNormalizer.TryNormalize(query, out _, out _), query.ToString());
    }

    [TestMethod]
    public void Normalize_RemovesIrrelevantTagIdsForIgnoreAndNone()
    {
        foreach (var filter in new[] { WorkItemTagFilter.Ignore, WorkItemTagFilter.None })
        {
            var query = WorkItemQueryNormalizer.Normalize(new WorkItemQuery
            {
                TagFilter = filter,
                TagIds = new[] { 1, 2 },
            });
            Assert.AreEqual(0, query.TagIds.Count);
        }
    }
}
