using Diary.App.Models;
using Diary.App.ViewModels.Dialogs;
using Diary.Core.Data.App;
using Diary.Core.Data.Base;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diary.AppTests;

[TestClass]
public sealed class TemplateEditorViewModelTests
{
    [TestMethod]
    public void AvailableTagsFollowPrimaryThenSecondarySelectionRules()
    {
        var shareData = new DbShareData(NullLogger.Instance);
        var primary = new WorkTag { Id = 1, Name = "主标签", Level = TagLevels.Primary };
        var disabledPrimary = new WorkTag
        {
            Id = 2,
            Name = "禁用主标签",
            Level = TagLevels.Primary,
            Disabled = true,
        };
        var secondary = new WorkTag { Id = 3, Name = "次标签", Level = TagLevels.Secondary };
        var disabledSecondary = new WorkTag
        {
            Id = 4,
            Name = "禁用次标签",
            Level = TagLevels.Secondary,
            Disabled = true,
        };
        foreach (var tag in new[] { primary, disabledPrimary, secondary, disabledSecondary })
            shareData.WorkTags.Add(tag);
        var viewModel = new TemplateViewModel(new Template { Name = "测试模板" }, shareData);

        CollectionAssert.AreEqual(new[] { primary }, viewModel.AvailableTags.ToArray());

        viewModel.AddTagCommand.Execute(primary);

        CollectionAssert.AreEqual(new[] { primary }, viewModel.Tags.ToArray());
        CollectionAssert.AreEqual(new[] { secondary }, viewModel.AvailableTags.ToArray());

        viewModel.AddTagCommand.Execute(secondary);

        CollectionAssert.AreEqual(new[] { primary, secondary }, viewModel.Tags.ToArray());
        Assert.IsFalse(viewModel.HasAvailableTags);
    }

    [TestMethod]
    public void AddTagRejectsDisabledAndUnexpectedLevels()
    {
        var shareData = new DbShareData(NullLogger.Instance);
        var primary = new WorkTag { Id = 1, Level = TagLevels.Primary };
        var disabledPrimary = new WorkTag { Id = 2, Level = TagLevels.Primary, Disabled = true };
        var secondary = new WorkTag { Id = 3, Level = TagLevels.Secondary };
        foreach (var tag in new[] { primary, disabledPrimary, secondary })
            shareData.WorkTags.Add(tag);
        var viewModel = new TemplateViewModel(new Template { Name = "测试模板" }, shareData);

        viewModel.AddTagCommand.Execute(disabledPrimary);
        viewModel.AddTagCommand.Execute(secondary);

        Assert.IsEmpty(viewModel.Tags);
        viewModel.AddTagCommand.Execute(primary);
        viewModel.RemoveTagCommand.Execute(primary);
        Assert.IsEmpty(viewModel.Tags);
        CollectionAssert.AreEqual(new[] { primary }, viewModel.AvailableTags.ToArray());
    }

    [TestMethod]
    public void RemovingPrimaryClearsSecondaryAndAllowsPrimaryToBeAddedAgain()
    {
        var shareData = new DbShareData(NullLogger.Instance);
        var primary = new WorkTag { Id = 1, Name = "主标签", Level = TagLevels.Primary };
        var secondary = new WorkTag { Id = 2, Name = "次标签", Level = TagLevels.Secondary };
        shareData.WorkTags.Add(primary);
        shareData.WorkTags.Add(secondary);
        var viewModel = new TemplateViewModel(new Template
        {
            Name = "已有标签模板",
            DefaultWorkTags = [primary.Id, secondary.Id],
        }, shareData);

        viewModel.RemoveTagCommand.Execute(primary);

        Assert.IsEmpty(viewModel.Tags);
        CollectionAssert.AreEqual(new[] { primary }, viewModel.AvailableTags.ToArray());

        viewModel.AddTagCommand.Execute(primary);

        CollectionAssert.AreEqual(new[] { primary }, viewModel.Tags.ToArray());
        CollectionAssert.AreEqual(new[] { secondary }, viewModel.AvailableTags.ToArray());
    }

    [TestMethod]
    public void SaveFailureKeepsDialogOpenAndRestoresTemplates()
    {
        var manager = TemplateManager.Instance;
        var originalTemplates = manager.Templates;
        var storedTemplates = new[] { new Template { Name = "原模板" } };
        manager.Templates = storedTemplates;
        try
        {
            var viewModel = new TemplateEditorViewModel(
                new DbShareData(NullLogger.Instance),
                NullLogger.Instance,
                _ => false);
            var closeRequested = false;
            viewModel.RequestClose += (_, _) => closeRequested = true;
            viewModel.Templates.Add(new TemplateViewModel(
                new Template { Name = "新模板" },
                new DbShareData(NullLogger.Instance)));

            viewModel.SaveCommand.Execute(null);

            Assert.IsFalse(closeRequested);
            Assert.AreSame(storedTemplates, manager.Templates);
        }
        finally
        {
            manager.Templates = originalTemplates;
        }
    }
}
