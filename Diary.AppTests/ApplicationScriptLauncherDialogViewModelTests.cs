using Diary.App.ViewModels;
using Diary.App.ViewModels.Dialogs;
using Diary.Script.Runtime;
using Diary.ScriptBase;

namespace Diary.AppTests;

[TestClass]
public sealed class ApplicationScriptLauncherDialogViewModelTests
{
    [TestMethod]
    public void Initialize_OnlyKeepsRunnableApplicationEntries()
    {
        var viewModel = new ApplicationScriptLauncherDialogViewModel();

        viewModel.Initialize(
        [
            CreateScript("z", "最后一个"),
            CreateScript("a", "第一个"),
            CreateScript("failed", "加载失败", buildSucceeded: false),
            CreateScript("editor", "编辑器", scope: ScriptScope.Editor),
            CreateScript("automation", "自动化", entryKind: ScriptEntryKind.Automation),
            CreateScript("query", "查询", entryKind: ScriptEntryKind.Query),
            CreateScript("pending", "待配置", configurationState: ScriptConfigurationState.NeedsConfiguration),
        ]);

        CollectionAssert.AreEqual(
            new[] { "a", "z" },
            viewModel.Scripts.Select(item => item.Id).ToArray());
        Assert.IsTrue(viewModel.HasScripts);
        Assert.AreEqual("a", viewModel.SelectedScript?.Id);
    }

    [TestMethod]
    public void RunCommand_ReturnsSelectedScript()
    {
        var viewModel = new ApplicationScriptLauncherDialogViewModel();
        var selected = CreateScript("sample", "示例脚本");
        ScriptListItem? result = null;
        viewModel.RequestClose += (_, value) => result = value as ScriptListItem;
        viewModel.Initialize([selected]);

        viewModel.RunCommand.Execute(null);

        Assert.AreSame(selected, result);
    }

    [TestMethod]
    public void Initialize_WithoutApplicationEntries_ShowsEmptyState()
    {
        var viewModel = new ApplicationScriptLauncherDialogViewModel();

        viewModel.Initialize([CreateScript("editor", "编辑器", scope: ScriptScope.Editor)]);

        Assert.IsFalse(viewModel.HasScripts);
        Assert.IsNull(viewModel.SelectedScript);
        Assert.IsFalse(viewModel.RunCommand.CanExecute(null));
    }

    private static ScriptListItem CreateScript(
        string id,
        string name,
        bool buildSucceeded = true,
        ScriptScope scope = ScriptScope.Application,
        ScriptEntryKind entryKind = ScriptEntryKind.Application,
        ScriptConfigurationState configurationState = ScriptConfigurationState.Ready) => new(
        $"{id}.cs",
        id,
        name,
        scope,
        buildSucceeded,
        buildSucceeded ? "已加载" : "加载失败",
        [],
        [],
        EntryKind: entryKind,
        ConfigurationState: configurationState);
}
