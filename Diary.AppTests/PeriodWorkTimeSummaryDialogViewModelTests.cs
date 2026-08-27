using Diary.App.Models;
using Diary.App.ViewModels.Dialogs;

namespace Diary.AppTests;

[TestClass]
public sealed class PeriodWorkTimeSummaryDialogViewModelTests
{
    [TestMethod]
    public void Initialize_FormatsRangeCountsAndHours()
    {
        var viewModel = new PeriodWorkTimeSummaryDialogViewModel();
        var summary = new PeriodWorkTimeSummary(
            new DateTime(2026, 8, 24),
            new DateTime(2026, 8, 30),
            new PeriodWorkTimeSummaryBucket(8, 36.5),
            new PeriodWorkTimeSummaryBucket(4, 20),
            new PeriodWorkTimeSummaryBucket(3, 14.5),
            new PeriodWorkTimeSummaryBucket(1, 2));

        viewModel.Initialize("周度工时概要", summary);

        Assert.AreEqual("周度工时概要", viewModel.Title);
        Assert.AreEqual("2026年8月24日 至 2026年8月30日", viewModel.RangeText);
        Assert.AreEqual("8 项 · 36.5 小时", viewModel.TotalText);
        Assert.AreEqual("4 项 · 20 小时", viewModel.SubmittedText);
        Assert.AreEqual("3 项 · 14.5 小时", viewModel.UnsubmittedText);
        Assert.AreEqual("1 项 · 2 小时", viewModel.BlockedOrFailedText);
    }
}
