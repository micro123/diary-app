using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.Models;
using Diary.App.Utils;
using Diary.App.ViewModels;
using Diary.Core.Utils;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.App.Dialogs;

[DiAutoRegister]
public partial class GenericConfigViewModel: ViewModelBase, IDialogContext
{
    [ObservableProperty] private SettingGroup _settingGroup = new("Root");
    [ObservableProperty] private string _title = string.Empty;
    
    private object? _settings;
    
    public void Close()
    {
        RequestClose?.Invoke(this, null);
    }

    public event EventHandler<object?>? RequestClose;

    [RelayCommand]
    void Save()
    {
        SettingGroup.Save();
        EasySaveLoad.Save(_settings!);
        RequestClose?.Invoke(this, true);
    }

    [RelayCommand]
    void Cancel()
    {
        SettingGroup.Load();
        RequestClose?.Invoke(this, false);
    }

    public void InitSettings(string title, object? settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        SettingTreeBuilder.BuildTree(SettingGroup, _settings);
        SettingGroup.Load();
        Title = title;
    }
}