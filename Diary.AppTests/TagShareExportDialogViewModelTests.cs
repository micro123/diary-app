using Diary.App.ViewModels.Dialogs;
using Diary.Core.Data.Base;

namespace Diary.AppTests;

[TestClass]
public sealed class TagShareExportDialogViewModelTests
{
    [TestMethod]
    public void Initialize_SelectsAllTagsAndSortsByLevelThenName()
    {
        var viewModel = new TagShareExportDialogViewModel();

        viewModel.Initialize(
        [
            CreateTag(3, "次标签", TagLevels.Secondary),
            CreateTag(2, "主标签B", TagLevels.Primary),
            CreateTag(1, "主标签A", TagLevels.Primary),
        ]);

        CollectionAssert.AreEqual(
            new[] { 1, 2, 3 },
            viewModel.Items.Select(item => item.Id).ToArray());
        Assert.AreEqual(3, viewModel.SelectedCount);
        Assert.IsTrue(viewModel.HasSelection);
    }

    [TestMethod]
    public void Export_ReturnsOnlySelectedTagIds()
    {
        var viewModel = new TagShareExportDialogViewModel();
        viewModel.Initialize(
        [
            CreateTag(1, "标签A", TagLevels.Primary),
            CreateTag(2, "标签B", TagLevels.Secondary),
        ]);
        viewModel.Items.Single(item => item.Id == 2).IsSelected = false;
        TagShareExportSelection? selection = null;
        viewModel.RequestClose += (_, value) => selection = value as TagShareExportSelection;

        viewModel.ExportCommand.Execute(null);

        Assert.IsNotNull(selection);
        CollectionAssert.AreEquivalent(new[] { 1 }, selection.TagIds.ToArray());
    }

    [TestMethod]
    public void ClearSelection_DisablesExportUntilAtLeastOneTagIsSelected()
    {
        var viewModel = new TagShareExportDialogViewModel();
        viewModel.Initialize([CreateTag(1, "标签A", TagLevels.Primary)]);

        viewModel.ClearSelectionCommand.Execute(null);

        Assert.AreEqual(0, viewModel.SelectedCount);
        Assert.IsFalse(viewModel.HasSelection);
        Assert.IsFalse(viewModel.ExportCommand.CanExecute(null));
        viewModel.Items[0].IsSelected = true;
        Assert.IsTrue(viewModel.ExportCommand.CanExecute(null));
    }

    private static WorkTag CreateTag(int id, string name, TagLevels level) => new()
    {
        Id = id,
        Name = name,
        Level = level,
    };
}
