using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Diary.GUIBase.ViewModels;

namespace Diary.App.ViewModels;

public sealed partial class McpStatusSetting(string title, string helpTip)
    : SettingItemModel(title, helpTip)
{
    [ObservableProperty] private string _status = string.Empty;
}

public sealed partial class McpActionSetting(
    string title,
    string helpTip,
    string text,
    ICommand command,
    bool primary = false)
    : SettingItemModel(title, helpTip)
{
    [ObservableProperty] private bool _isEnabled = true;

    public string Text { get; } = text;
    public ICommand Command { get; } = command;
    public bool Primary { get; } = primary;
}
