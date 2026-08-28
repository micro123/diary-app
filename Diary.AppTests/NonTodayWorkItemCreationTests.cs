using Diary.App.ViewModels;
using Diary.App.ViewModels.Dialogs;

namespace Diary.AppTests;

[TestClass]
public sealed class NonTodayWorkItemCreationTests
{
    private static readonly DateTime Today = new(2026, 8, 28);

    [TestMethod]
    public void TodayDoesNotRequireWarning()
    {
        Assert.IsFalse(DiaryEditorViewModel.ShouldWarnBeforeCreatingWorkItem(
            Today,
            Today,
            string.Empty));
    }

    [TestMethod]
    public void NonTodayDateRequiresWarningWithoutSuppression()
    {
        Assert.IsTrue(DiaryEditorViewModel.ShouldWarnBeforeCreatingWorkItem(
            Today.AddDays(-1),
            Today,
            string.Empty));
    }

    [TestMethod]
    public void SuppressionOnlyAppliesToMatchingDay()
    {
        Assert.IsFalse(DiaryEditorViewModel.ShouldWarnBeforeCreatingWorkItem(
            Today.AddDays(1),
            Today,
            "2026-08-28"));
        Assert.IsTrue(DiaryEditorViewModel.ShouldWarnBeforeCreatingWorkItem(
            Today.AddDays(1),
            Today,
            "2026-08-27"));
    }

    [TestMethod]
    public void ConfirmReturnsSuppressionChoice()
    {
        var viewModel = new NonTodayWorkItemCreationViewModel(Today.AddDays(-1))
        {
            SuppressForToday = true,
        };
        object? result = null;
        viewModel.RequestClose += (_, value) => result = value;

        viewModel.ConfirmCommand.Execute(null);

        var decision = result as NonTodayWorkItemCreationDecision;
        Assert.IsNotNull(decision);
        Assert.IsTrue(decision.SuppressForToday);
        Assert.AreEqual("2026-08-27", viewModel.TargetDateText);
    }

    [TestMethod]
    public void CancelDoesNotConfirmCreation()
    {
        var viewModel = new NonTodayWorkItemCreationViewModel(Today.AddDays(1));
        object? result = new object();
        viewModel.RequestClose += (_, value) => result = value;

        viewModel.CancelCommand.Execute(null);

        Assert.IsNull(result);
    }
}
