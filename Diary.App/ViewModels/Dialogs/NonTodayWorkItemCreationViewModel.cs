using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.GUIBase.ViewModels;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.App.ViewModels.Dialogs;

public sealed record NonTodayWorkItemCreationDecision(bool SuppressForToday);

public partial class NonTodayWorkItemCreationViewModel : ViewModelBase, IDialogContext
{
    [ObservableProperty]
    private bool _suppressForToday;

    public string TargetDateText { get; }

    public string WarningMessage { get; }

    public NonTodayWorkItemCreationViewModel(DateTime targetDate)
    {
        TargetDateText = targetDate.Date.ToString("yyyy-MM-dd");
        WarningMessage = $"当前选择的是 {TargetDateText}，不是今天。是否继续在该日期新建事项？";
    }

    [RelayCommand]
    private void Confirm() =>
        RequestClose?.Invoke(this, new NonTodayWorkItemCreationDecision(SuppressForToday));

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(this, null);

    public void Close() => Cancel();

    public event EventHandler<object?>? RequestClose;
}
