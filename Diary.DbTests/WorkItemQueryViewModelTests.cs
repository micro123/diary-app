using Diary.App.Models;
using Diary.App.ViewModels;
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
}
