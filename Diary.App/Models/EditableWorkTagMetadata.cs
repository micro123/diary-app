using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Diary.App.Models;

public partial class EditableWorkTagMetadata : ObservableObject
{
    private readonly Action<EditableWorkTagMetadata> _remove;

    [ObservableProperty] private string _key;
    [ObservableProperty] private string _value;

    public EditableWorkTagMetadata(string key, string value, Action<EditableWorkTagMetadata> remove)
    {
        _key = key;
        _value = value;
        _remove = remove;
        RemoveCommand = new RelayCommand(() => _remove(this));
    }

    public IRelayCommand RemoveCommand { get; }
}
