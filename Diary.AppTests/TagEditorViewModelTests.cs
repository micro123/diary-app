using System.Collections.ObjectModel;
using Avalonia.Media;
using Diary.App.Models;
using Diary.App.ViewModels.Dialogs;
using Diary.Core.Data.Base;
using Diary.GUIBase.Converters;

namespace Diary.AppTests;

[TestClass]
public sealed class TagEditorViewModelTests
{
    [TestMethod]
    public void MatchesTagFilterIgnoresCaseAndTrimsInput()
    {
        Assert.IsTrue(TagEditorViewModel.MatchesTagFilter("ZView项目", " view "));
        Assert.IsTrue(TagEditorViewModel.MatchesTagFilter("ZView项目", string.Empty));
        Assert.IsFalse(TagEditorViewModel.MatchesTagFilter("MTA项目", "zview"));
    }

    [TestMethod]
    public void SortExtraFieldsUsesSortOrderThenFieldKey()
    {
        var fields = new ObservableCollection<EditableTagExtraField>
        {
            CreateField("z.field", 0),
            CreateField("middle.field", 20),
            CreateField("a.field", 0),
            CreateField("first.field", 10),
        };

        TagEditorViewModel.SortExtraFields(fields);

        CollectionAssert.AreEqual(
            new[] { "a.field", "z.field", "first.field", "middle.field" },
            fields.Select(field => field.FieldKey).ToArray());
    }

    [TestMethod]
    public void ResolveNewTagColorRandomizesDefaultBlackAndPreservesSelectedColor()
    {
        var randomColor = TagEditorViewModel.ResolveNewTagColor(default, new Random(20260827));

        Assert.AreNotEqual(0, randomColor);
        var hsv = HsvColorConverter.ToHsv(randomColor);
        Assert.IsGreaterThanOrEqualTo(0.55, hsv.S);
        Assert.IsGreaterThanOrEqualTo(0.68, hsv.V);

        var selected = new HsvColor(1, 210, 0.65, 0.8);
        Assert.AreEqual(
            HsvColorConverter.FromHsv(selected),
            TagEditorViewModel.ResolveNewTagColor(selected, new Random(1)));
    }

    private static EditableTagExtraField CreateField(string fieldKey, int sortOrder)
        => new(new TagExtraFieldDefinition
        {
            FieldKey = fieldKey,
            Label = fieldKey,
            SortOrder = sortOrder,
        });
}
