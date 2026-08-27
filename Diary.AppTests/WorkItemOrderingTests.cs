using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Diary.App.Models;
using Diary.Core.Data.Base;

namespace Diary.AppTests;

[TestClass]
public sealed class WorkItemOrderingTests
{
    [TestMethod]
    public void SortByPriorityAndId_MovesItemsWithoutResettingCollection()
    {
        var items = new ObservableCollection<TestItem>
        {
            new(8, WorkPriorities.P2),
            new(5, WorkPriorities.P1),
            new(2, WorkPriorities.P1),
            new(3, WorkPriorities.P0),
        };
        var collectionActions = new List<NotifyCollectionChangedAction>();
        items.CollectionChanged += (_, args) => collectionActions.Add(args.Action);

        WorkItemOrdering.SortByPriorityAndId(
            items,
            item => item.Priority,
            item => item.Id);

        CollectionAssert.AreEqual(new[] { 3, 2, 5, 8 }, items.Select(item => item.Id).ToArray());
        Assert.IsTrue(collectionActions.Count > 0);
        Assert.IsTrue(collectionActions.All(action => action == NotifyCollectionChangedAction.Move));
    }

    [TestMethod]
    public void ByPriorityAndId_CreatesOrderedSnapshotWithoutChangingSource()
    {
        var source = new[]
        {
            new TestItem(8, WorkPriorities.P2),
            new TestItem(5, WorkPriorities.P1),
            new TestItem(2, WorkPriorities.P1),
            new TestItem(3, WorkPriorities.P0),
        };

        var ordered = WorkItemOrdering.ByPriorityAndId(
            source,
            item => item.Priority,
            item => item.Id).ToArray();

        CollectionAssert.AreEqual(new[] { 3, 2, 5, 8 }, ordered.Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(new[] { 8, 5, 2, 3 }, source.Select(item => item.Id).ToArray());
    }

    private sealed record TestItem(int Id, WorkPriorities Priority);
}
