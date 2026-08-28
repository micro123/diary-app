using Diary.App.ViewModels.Dialogs;

namespace Diary.AppTests;

[TestClass]
public sealed class ScriptConfirmDialogViewModelTests
{
    [TestMethod]
    public void Initialize_NormalizesEmptyContent()
    {
        var viewModel = new ScriptConfirmDialogViewModel("  ", "  ");

        Assert.AreEqual("脚本确认", viewModel.DialogTitle);
        Assert.IsNull(viewModel.Message);
        Assert.IsFalse(viewModel.HasMessage);
    }

    [TestMethod]
    public void Confirm_ReturnsTrue()
    {
        var viewModel = new ScriptConfirmDialogViewModel("执行确认", "是否继续？");
        object? result = null;
        viewModel.RequestClose += (_, value) => result = value;

        viewModel.ConfirmCommand.Execute(null);

        Assert.IsTrue(viewModel.HasMessage);
        Assert.AreEqual(true, result);
    }

    [TestMethod]
    public void CancelAndClose_ReturnFalse()
    {
        var viewModel = new ScriptConfirmDialogViewModel("执行确认", "是否继续？");
        var results = new List<object?>();
        viewModel.RequestClose += (_, value) => results.Add(value);

        viewModel.CancelCommand.Execute(null);
        viewModel.Close();

        CollectionAssert.AreEqual(new object?[] { false, false }, results);
    }
}
