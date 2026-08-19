using Diary.App.Services;
using Diary.App.ViewModels;
using Diary.App.ViewModels.Dialogs;
using Diary.ScriptBase;

namespace Diary.AppTests;

[TestClass]
public sealed class ScriptShareDialogViewModelTests
{
    [TestMethod]
    public void ExportDialog_SelectsAllAndCanClearSelection()
    {
        var viewModel = new ScriptShareExportDialogViewModel();
        viewModel.Initialize(
        [
            CreateScript("a", "A"),
            CreateScript("b", "B"),
        ]);

        Assert.AreEqual(2, viewModel.SelectedCount);
        viewModel.ClearSelectionCommand.Execute(null);
        Assert.AreEqual(0, viewModel.SelectedCount);
        Assert.IsFalse(viewModel.HasSelection);
        viewModel.SelectAllCommand.Execute(null);
        Assert.AreEqual(2, viewModel.SelectedCount);
    }

    [TestMethod]
    public void ImportDialog_DefaultsConflictsToSkippedAndRequiresExplicitSelection()
    {
        var preview = new ScriptSharePackagePreview("shared.diaryscripts",
        [
            new("safe", "Safe", ScriptScope.Application, ScriptEntryKind.Application, "C#", "safe.cs", "safe.cs", null, false, "可以导入"),
            new("conflict", "Conflict", ScriptScope.Application, ScriptEntryKind.Application, "C#", "conflict.cs", "conflict.cs", "old.cs", true, "存在冲突"),
        ]);
        var viewModel = new ScriptShareImportDialogViewModel();
        viewModel.Initialize(preview);

        Assert.AreEqual(1, viewModel.SelectedCount);
        Assert.IsFalse(viewModel.Items.Single(item => item.Id == "conflict").IsSelected);

        viewModel.Items.Single(item => item.Id == "conflict").IsSelected = true;
        ScriptShareImportSelection? selection = null;
        viewModel.RequestClose += (_, value) => selection = value as ScriptShareImportSelection;
        viewModel.ImportCommand.Execute(null);

        Assert.IsNotNull(selection);
        Assert.AreEqual(2, selection.Decisions.Count);
        Assert.IsTrue(selection.Decisions.Single(item => item.Id == "conflict").ReplaceExisting);
        Assert.IsFalse(selection.Decisions.Single(item => item.Id == "safe").ReplaceExisting);
    }

    private static ScriptListItem CreateScript(string id, string name) => new(
        $"{id}.cs",
        id,
        name,
        ScriptScope.Application,
        true,
        "已加载",
        [],
        []);
}
