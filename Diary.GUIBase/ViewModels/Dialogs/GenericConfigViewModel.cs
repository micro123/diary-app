using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.Core.Utils;
using Diary.GUIBase.Utils;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.GUIBase.ViewModels.Dialogs;

[DiAutoRegister]
public partial class GenericConfigViewModel : ViewModelBase, IDialogContext
{
    [ObservableProperty] private SettingGroup _settingGroup = new("Root");
    [ObservableProperty] private string _title = string.Empty;
    private object? _settings;

    public event EventHandler<object?>? RequestClose;

    public void Close() => RequestClose?.Invoke(this, null);

    [RelayCommand]
    private void Save()
    {
        SettingGroup.Save();
        EasySaveLoad.Save(_settings!);
        RequestClose?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel()
    {
        SettingGroup.Load();
        RequestClose?.Invoke(this, false);
    }

    public void InitSettings(string title, object settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        SettingTreeBuilder.BuildTree(SettingGroup, settings, BaseApp.Instance);
        SettingGroup.Load();
        Title = title;
    }
}
