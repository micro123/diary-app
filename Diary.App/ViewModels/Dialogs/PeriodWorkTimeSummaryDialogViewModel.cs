using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.Models;
using Diary.GUIBase.ViewModels;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.App.ViewModels.Dialogs;

[DiAutoRegister]
public partial class PeriodWorkTimeSummaryDialogViewModel : ViewModelBase, IDialogContext
{
    [ObservableProperty] private string _title = "工时概要";
    [ObservableProperty] private string _rangeText = string.Empty;
    [ObservableProperty] private string _totalText = string.Empty;
    [ObservableProperty] private string _submittedText = string.Empty;
    [ObservableProperty] private string _unsubmittedText = string.Empty;
    [ObservableProperty] private string _blockedOrFailedText = string.Empty;

    public event EventHandler<object?>? RequestClose;

    public void Initialize(string title, PeriodWorkTimeSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        Title = title;
        RangeText = $"{summary.StartDate:yyyy年M月d日} 至 {summary.EndDate:yyyy年M月d日}";
        TotalText = Format(summary.Total);
        SubmittedText = Format(summary.Submitted);
        UnsubmittedText = Format(summary.Unsubmitted);
        BlockedOrFailedText = Format(summary.BlockedOrFailed);
    }

    public void Close() => RequestClose?.Invoke(this, null);

    [RelayCommand]
    private void Dismiss() => Close();

    private static string Format(PeriodWorkTimeSummaryBucket bucket)
        => $"{bucket.ItemCount} 项 · {bucket.Hours:0.##} 小时";
}
