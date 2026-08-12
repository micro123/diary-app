using Diary.App.Models;
using Diary.App.ViewModels;
using Diary.Core.Data.Base;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diary.DbTests;

[TestClass]
public sealed class WorkItemQueryViewModelTests
{
    [TestMethod]
    public void QueryFailurePreservesLastSuccessfulResults()
    {
        using var db = TestDb.Create();
        db.CreateWorkItem("2026-08-06", "completed query");
        var shareData = new DbShareData(NullLogger.Instance);
        var viewModel = new WorkItemQueryViewModel(
            shareData,
            NullLogger.Instance,
            new SavedWorkItemQueryStore(false, false),
            () => db)
        {
            StartDate = new DateTime(2026, 8, 1),
            EndDate = new DateTime(2026, 8, 31),
        };

        viewModel.QueryCommand.Execute(null);
        Assert.AreEqual(1, viewModel.Results.Count);
        Assert.IsFalse(viewModel.HasQueryError);

        db.Close();
        viewModel.QueryCommand.Execute(null);

        Assert.AreEqual(1, viewModel.Results.Count);
        Assert.AreEqual("completed query", viewModel.Results[0].Comment);
        Assert.IsTrue(viewModel.HasQueryError);
        StringAssert.Contains(viewModel.ResultSummary, "已保留上次成功结果");
    }

    [TestMethod]
    public void QueryUsesExplicitResultLimitAndWarnsAboutPossibleTruncation()
    {
        using var db = TestDb.Create();
        for (var i = 0; i < WorkItemQueryViewModel.DefaultResultLimit + 1; i++)
            db.CreateWorkItem("2026-08-06", $"item {i}");
        var viewModel = CreateViewModel(db);

        viewModel.QueryCommand.Execute(null);

        Assert.AreEqual(WorkItemQueryViewModel.DefaultResultLimit, viewModel.Results.Count);
        StringAssert.Contains(viewModel.ResultSummary, "可能已截断");
    }

    [TestMethod]
    public void QueryBuildsDateAndPrimaryTagBreakdown()
    {
        using var db = TestDb.Create();
        var project = db.CreateWorkTag("项目 A", true, 0);
        var first = db.CreateWorkItem("2026-08-06", "first");
        first.Time = 1.5;
        Assert.IsTrue(db.UpdateWorkItem(first));
        Assert.IsTrue(db.WorkItemAddTag(first, project));
        var second = db.CreateWorkItem("2026-08-07", "second");
        second.Time = 2.0;
        Assert.IsTrue(db.UpdateWorkItem(second));
        Assert.IsTrue(db.WorkItemAddTag(second, project));

        var viewModel = CreateViewModel(db);
        viewModel.QueryCommand.Execute(null);

        StringAssert.Contains(viewModel.ResultBreakdown, "2026-08-07 2 小时");
        StringAssert.Contains(viewModel.ResultBreakdown, "项目 A 3.5 小时");
        StringAssert.Contains(viewModel.QuerySummaryText, "记录数：2");
        StringAssert.Contains(viewModel.QuerySummaryText, "总工时：3.5 小时");
    }

    [TestMethod]
    public void ApplySavedQuery_DoesNotSelectSameIdWithDifferentSnapshotIdentity()
    {
        using var db = TestDb.Create();
        var shareData = new DbShareData(NullLogger.Instance);
        shareData.WorkTags.Add(new WorkTag
        {
            Id = 7,
            Name = "current database name",
            Level = TagLevels.Primary,
        });
        var saved = new SavedWorkItemQuery
        {
            Name = "other database",
            TagFilter = WorkItemTagFilter.Any,
            Tags =
            [
                new() { Id = 7, Name = "other database name", Level = TagLevels.Primary },
            ],
        };
        var store = new SavedWorkItemQueryStore(false, false) { Queries = [saved] };
        var viewModel = new WorkItemQueryViewModel(
            shareData,
            NullLogger.Instance,
            store,
            () => db)
        {
            SelectedSavedQuery = saved,
        };

        viewModel.ApplySavedQueryCommand.Execute(null);

        Assert.IsFalse(viewModel.Tags.Single().Selected);
        StringAssert.Contains(viewModel.SavedQueryStatus, "名称/层级不一致");
    }

    [TestMethod]
    public async Task DeleteSavedQuery_RequiresConfirmationBoundary()
    {
        using var db = TestDb.Create();
        var store = new SavedWorkItemQueryStore(false, false);
        Assert.IsTrue(store.TryAdd("saved", new WorkItemQuery(), out _));
        var confirmations = 0;
        var viewModel = new WorkItemQueryViewModel(
            new DbShareData(NullLogger.Instance),
            NullLogger.Instance,
            store,
            () => db,
            (_, _) =>
            {
                confirmations++;
                return Task.FromResult(false);
            })
        {
            SelectedSavedQuery = store.Queries.Single(),
        };

        await viewModel.DeleteSavedQueryCommand.ExecuteAsync(null);

        Assert.AreEqual(1, confirmations);
        Assert.AreEqual(1, store.Queries.Count);
        Assert.AreEqual("已取消删除", viewModel.SavedQueryStatus);
    }

    [TestMethod]
    public void QueryRejectsInvalidPrioritySelectionWithoutReplacingResults()
    {
        using var db = TestDb.Create();
        var viewModel = CreateViewModel(db);
        viewModel.Results.Add(new WorkItemQueryResult(
            new WorkItem { Comment = "old result" },
            string.Empty));
        viewModel.PriorityIndex = -1;

        viewModel.QueryCommand.Execute(null);

        Assert.IsTrue(viewModel.HasQueryError);
        Assert.AreEqual("old result", viewModel.Results.Single().Comment);
    }

    private static WorkItemQueryViewModel CreateViewModel(Diary.Database.DbInterfaceBase db) => new(
        new DbShareData(NullLogger.Instance),
        NullLogger.Instance,
        new SavedWorkItemQueryStore(false, false),
        () => db)
    {
        StartDate = new DateTime(2026, 8, 1),
        EndDate = new DateTime(2026, 8, 31),
    };
}
