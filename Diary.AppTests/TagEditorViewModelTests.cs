using System.Collections.ObjectModel;
using Diary.App.Models;
using Diary.App.ViewModels.Dialogs;
using Diary.Core.Data.Base;

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

    private static EditableTagExtraField CreateField(string fieldKey, int sortOrder)
        => new(new TagExtraFieldDefinition
        {
            FieldKey = fieldKey,
            Label = fieldKey,
            SortOrder = sortOrder,
        });
}
