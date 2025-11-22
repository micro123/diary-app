using CommunityToolkit.Mvvm.ComponentModel;
using Diary.Core.Data.Base;

namespace Diary.App.Dialogs;

public partial class EditableWorkTag(WorkTag tag) : ObservableObject
{
    private readonly WorkTag _tag = tag;

    public int Id => _tag.Id;
    [ObservableProperty] private string _name = tag.Name;
    [ObservableProperty] private int _color = tag.Color;
    [ObservableProperty] private bool _primary = tag.Level == TagLevels.Primary;
    [ObservableProperty] private bool _disabled = tag.Disabled;

    public void ApplyChanges()
    {
        // todo: check if changed
    }
}