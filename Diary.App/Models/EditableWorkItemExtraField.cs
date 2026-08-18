using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Diary.Core.Data.Base;

namespace Diary.App.Models;

public sealed class ExtraFieldGroupViewModel
{
    public int TagId { get; }
    public string TagName { get; }
    public ObservableCollection<EditableWorkItemExtraField> Fields { get; } = new();

    public ExtraFieldGroupViewModel(int tagId, string tagName)
    {
        TagId = tagId;
        TagName = tagName;
    }
}

public partial class EditableWorkItemExtraField : ObservableObject
{
    public string FieldId { get; }
    public string FieldKey { get; }
    public string Label { get; }
    public TagExtraFieldType Type { get; }
    public IReadOnlyList<string> Options { get; }
    public string Description { get; }
    public bool IsReadOnly { get; }

    [ObservableProperty]
    private string _value;

    public EditableWorkItemExtraField(WorkItemExtraField field, bool isReadOnly = false)
    {
        FieldId = field.FieldId;
        FieldKey = field.FieldKey;
        Label = field.Label;
        Type = field.Type;
        Options = field.Options;
        Description = field.Description;
        IsReadOnly = isReadOnly;
        _value = field.Value;
    }
}
