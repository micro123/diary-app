using Diary.App.ViewModels.Dialogs;

namespace Diary.AppTests;

[TestClass]
public sealed class CopyDayViewModelTests
{
    [TestMethod]
    public void ConfirmRejectsSameSourceAndTargetDate()
    {
        var target = new DateTime(2026, 8, 12);
        var viewModel = new CopyDayViewModel(target)
        {
            SourceDate = target,
        };
        object? result = null;
        viewModel.RequestClose += (_, value) => result = value;

        viewModel.ConfirmCommand.Execute(null);

        Assert.IsNull(result);
        StringAssert.Contains(viewModel.ValidationMessage, "不能与目标日期相同");
    }

    [TestMethod]
    public void ConfirmReturnsSelectedSourceDate()
    {
        var viewModel = new CopyDayViewModel(new DateTime(2026, 8, 12))
        {
            SourceDate = new DateTime(2026, 8, 8),
        };
        object? result = null;
        viewModel.RequestClose += (_, value) => result = value;

        viewModel.ConfirmCommand.Execute(null);

        var selection = result as CopyDaySelection;
        Assert.IsNotNull(selection);
        Assert.AreEqual(new DateTime(2026, 8, 8), selection.SourceDate);
    }
}
