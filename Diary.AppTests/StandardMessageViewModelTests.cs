using Avalonia.Controls.Notifications;
using Diary.App.ViewModels.Dialogs;

namespace Diary.AppTests;

[TestClass]
public sealed class StandardMessageViewModelTests
{
    [TestMethod]
    public void InitializeMapsMessageTypesToVisualState()
    {
        AssertType(NotificationType.Information, "通知", vm => vm.IsInformation);
        AssertType(NotificationType.Success, "操作成功", vm => vm.IsSuccess);
        AssertType(NotificationType.Warning, "需要注意", vm => vm.IsWarning);
        AssertType(NotificationType.Error, "操作失败", vm => vm.IsError);
    }

    [TestMethod]
    public void InitializeUsesFallbacksForEmptyContent()
    {
        var viewModel = new StandardMessageViewModel();

        viewModel.Initialize(" ", "", NotificationType.Information);

        Assert.AreEqual("通知", viewModel.Title);
        Assert.AreEqual("没有更多详细信息。", viewModel.DisplayBody);
        Assert.IsFalse(viewModel.HasBody);
        Assert.IsFalse(viewModel.ShowMessageKind);
        Assert.AreEqual("没有提供更多消息内容。", viewModel.GuidanceText);
        Assert.IsFalse(viewModel.CopyBodyCommand.CanExecute(null));
    }

    [TestMethod]
    public void GenericErrorTitleUsesMessageKindWithoutDuplicatePill()
    {
        var viewModel = new StandardMessageViewModel();

        viewModel.Initialize("错误", "数据库连接失败。", NotificationType.Error);

        Assert.AreEqual("操作失败", viewModel.DisplayTitle);
        Assert.IsFalse(viewModel.ShowMessageKind);
    }

    [TestMethod]
    public void NonEmptyBodyCanBeCopied()
    {
        var viewModel = new StandardMessageViewModel();

        viewModel.Initialize("导出完成", "文件已经生成。", NotificationType.Success);

        Assert.AreEqual("导出完成", viewModel.Title);
        Assert.AreEqual("文件已经生成。", viewModel.DisplayBody);
        Assert.IsTrue(viewModel.HasBody);
        Assert.IsTrue(viewModel.CopyBodyCommand.CanExecute(null));
    }

    [TestMethod]
    public void ConfirmReturnsOkResult()
    {
        var viewModel = new StandardMessageViewModel();
        object? result = null;
        viewModel.RequestClose += (_, value) => result = value;

        viewModel.ConfirmCommand.Execute(null);

        Assert.AreEqual(Ursa.Controls.DialogResult.OK, result);
    }

    private static void AssertType(
        NotificationType type,
        string expectedKind,
        Func<StandardMessageViewModel, bool> state)
    {
        var viewModel = new StandardMessageViewModel();
        viewModel.Initialize("测试", "详情", type);

        Assert.AreEqual(type, viewModel.MessageType);
        Assert.AreEqual(expectedKind, viewModel.MessageKind);
        Assert.IsTrue(state(viewModel));
    }
}
