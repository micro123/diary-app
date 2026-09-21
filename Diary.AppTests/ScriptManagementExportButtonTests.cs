using Diary.App.ViewModels;
using Diary.ScriptBase;

namespace Diary.AppTests;

[TestClass]
public sealed class ScriptManagementExportButtonTests
{
    [TestMethod]
    public void ExportCommandTracksDirectoryLoadAndExecution()
    {
        var model = new ScriptManagementViewModel(
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);
        var states = new List<bool>();
        model.ExportScriptsCommand.CanExecuteChanged += (_, _) =>
            states.Add(model.ExportScriptsCommand.CanExecute(null));
        Assert.IsFalse(model.ExportScriptsCommand.CanExecute(null));

        model.Loading = true;
        model.Scripts.Add(new ScriptListItem("example.cs", "example", "示例", ScriptScope.Application,
            true, "已加载", [], []));
        model.Loading = false;
        Assert.IsTrue(model.ExportScriptsCommand.CanExecute(null));

        model.IsExecuting = true;
        Assert.IsFalse(model.ExportScriptsCommand.CanExecute(null));
        model.IsExecuting = false;
        Assert.IsTrue(model.ExportScriptsCommand.CanExecute(null));
        CollectionAssert.AreEqual(new[] { false, true, false, true }, states);
    }
}
