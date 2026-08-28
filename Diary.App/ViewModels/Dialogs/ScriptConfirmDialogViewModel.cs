using CommunityToolkit.Mvvm.Input;
using Diary.GUIBase.ViewModels;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.App.ViewModels.Dialogs;

public sealed partial class ScriptConfirmDialogViewModel : ViewModelBase, IDialogContext
{
    public string DialogTitle { get; }
    public string? Message { get; }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public string GuidanceText => "脚本正在等待确认，请选择是否继续。";

    public ScriptConfirmDialogViewModel(string? title, string? message)
    {
        DialogTitle = string.IsNullOrWhiteSpace(title) ? "脚本确认" : title.Trim();
        Message = string.IsNullOrWhiteSpace(message) ? null : message;
    }

    [RelayCommand]
    private void Confirm() => RequestClose?.Invoke(this, true);

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(this, false);

    public void Close() => Cancel();

    public event EventHandler<object?>? RequestClose;
}
