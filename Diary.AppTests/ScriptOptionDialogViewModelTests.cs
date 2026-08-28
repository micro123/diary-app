using Diary.App.ViewModels.Dialogs;
using Diary.ScriptHost;

namespace Diary.AppTests;

[TestClass]
public sealed class ScriptOptionDialogViewModelTests
{
    [TestMethod]
    public void RequireChoiceBuildsDescribedOptionsAndReturnsSelection()
    {
        var viewModel = new ScriptOptionDialogViewModel(new OptionDialogRequest
        {
            Title = "导出完成",
            Message = "report.xlsx\n\n是否立即打开？",
            DismissPolicy = DialogDismissPolicy.RequireChoice,
            Options =
            [
                new DialogOption("open", "打开文件", "使用系统默认程序打开。"),
                new DialogOption("decline", "暂不打开", IsDestructive: true),
            ],
            DefaultOptionId = "open",
        });
        object? result = null;
        viewModel.RequestClose += (_, value) => result = value;

        viewModel.SelectCommand.Execute(viewModel.Options[0]);

        Assert.AreEqual("导出完成", viewModel.DialogTitle);
        Assert.IsTrue(viewModel.HasMessage);
        Assert.IsTrue(viewModel.RequireChoice);
        Assert.IsFalse(viewModel.CanCancel);
        Assert.IsTrue(viewModel.Options[0].IsDefault);
        Assert.IsTrue(viewModel.Options[0].HasDescription);
        Assert.IsTrue(viewModel.Options[1].IsDestructive);
        Assert.AreEqual("open", (result as OptionDialogResult)?.OptionId);
    }

    [TestMethod]
    public void OptionalDialogCanBeCancelledByCloseRequest()
    {
        var viewModel = new ScriptOptionDialogViewModel(new OptionDialogRequest
        {
            Title = "请选择",
            Options = [new DialogOption("continue", "继续")],
        });
        object? result = null;
        viewModel.RequestClose += (_, value) => result = value;

        viewModel.Close();

        Assert.IsFalse(viewModel.HasMessage);
        Assert.IsTrue(viewModel.CanCancel);
        Assert.AreEqual(OptionDialogStatus.Cancelled, (result as OptionDialogResult)?.Status);
    }

    [TestMethod]
    public void RequiredDialogIgnoresCloseRequest()
    {
        var viewModel = new ScriptOptionDialogViewModel(new OptionDialogRequest
        {
            Title = "必须选择",
            DismissPolicy = DialogDismissPolicy.RequireChoice,
            Options = [new DialogOption("continue", "继续")],
        });
        var closeRequested = false;
        viewModel.RequestClose += (_, _) => closeRequested = true;

        viewModel.Close();

        Assert.IsFalse(closeRequested);
    }
}
