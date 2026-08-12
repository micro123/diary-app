using Diary.App.Models;

namespace Diary.AppTests;

[TestClass]
public sealed class RecentPrimaryTagHistoryTests
{
    [TestMethod]
    public void MergePrefersNewIdsAndRemovesDuplicates()
    {
        var result = RecentPrimaryTagHistory.Merge([3, 2, 3], [1, 2, 0]);

        CollectionAssert.AreEqual(new[] { 3, 2, 1 }, result.ToArray());
    }

    [TestMethod]
    public void MergeRespectsMaximum()
    {
        var result = RecentPrimaryTagHistory.Merge([8, 7, 6], [5, 4], maximum: 3);

        CollectionAssert.AreEqual(new[] { 8, 7, 6 }, result.ToArray());
    }

    [TestMethod]
    public void MergeWithNonPositiveMaximumReturnsEmpty()
    {
        var result = RecentPrimaryTagHistory.Merge([1], [2], maximum: 0);

        Assert.AreEqual(0, result.Count);
    }
}
