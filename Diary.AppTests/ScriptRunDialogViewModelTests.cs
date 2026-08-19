using Diary.App.ViewModels.Dialogs;
using Diary.Script.Runtime;

namespace Diary.AppTests;

[TestClass]
public sealed class ScriptRunDialogViewModelTests
{
    [TestMethod]
    public void Initialize_LoadsDefaultsAndTimeout()
    {
        var viewModel = new ScriptRunDialogViewModel();

        viewModel.Initialize("示例", new ScriptFileMetadata(
            DefaultArguments: new Dictionary<string, string> { ["project"] = "Diary", ["range"] = "today" },
            TimeoutSeconds: 45));

        Assert.AreEqual("示例", viewModel.ScriptName);
        Assert.AreEqual(45, viewModel.TimeoutSeconds);
        StringAssert.Contains(viewModel.ArgumentsText, "project=Diary");
        StringAssert.Contains(viewModel.ArgumentsText, "range=today");
    }

    [TestMethod]
    public void TryParseArguments_RejectsDuplicateKeys()
    {
        var succeeded = ScriptRunDialogViewModel.TryParseArguments(
            "range=today\nrange=week",
            out _,
            out var error);

        Assert.IsFalse(succeeded);
        StringAssert.Contains(error, "参数名重复");
    }
}
