using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.Utils;
using Diary.Core.Data.Base;

namespace Diary.App.Models;

public partial class EditableWorkTag(WorkTag tag) : ObservableObject
{
    private readonly WorkTag _tag = tag;

    public int Id => _tag.Id;
    [ObservableProperty] private string _name = tag.Name;
    [ObservableProperty] private int _color = tag.Color;
    [ObservableProperty] private bool _primary = tag.Level == TagLevels.Primary;
    [ObservableProperty] private bool _disabled = tag.Disabled;

    public bool ApplyChanges()
    {
        if (Color != _tag.Color || (Primary != (_tag.Level == TagLevels.Primary)) || Disabled != _tag.Disabled)
        {
            _tag.Color = Color;
            _tag.Level = Primary ? TagLevels.Primary : TagLevels.Secondary;
            _tag.Disabled = Disabled;
            App.Current.UseDb!.UpdateWorkTag(_tag);
            return true;
        }

        return false;
    }

    [RelayCommand]
    void Delete()
    {
        if (App.Current.UseDb!.DeleteWorkTag(_tag))
        {
            EventDispatcher.DbChanged();
        }
    }
}