using CommunityToolkit.Mvvm.ComponentModel;
using Diary.App.Models;
using Diary.GUIBase.ViewModels;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.App.ViewModels.Dialogs;

public partial class PeriodTrackerUploadProgressViewModel : ViewModelBase, IDialogContext
{
    public string Title { get; }
    public string RangeText { get; }

    [ObservableProperty] private bool _isIndeterminate = true;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = "正在读取工作事项和 Tracker 绑定……";
    [ObservableProperty] private int _completed;
    [ObservableProperty] private int _total;
    [ObservableProperty] private int _succeeded;
    [ObservableProperty] private int _skipped;

    public PeriodTrackerUploadProgressViewModel(
        string title,
        DateTime startDate,
        DateTime endDate)
    {
        Title = title;
        RangeText = $"{startDate:yyyy-MM-dd} 至 {endDate:yyyy-MM-dd}";
    }

    public void Begin(int total)
    {
        Total = total;
        IsIndeterminate = false;
        Progress = total == 0 ? 100 : 0;
        StatusText = total == 0 ? "范围内没有工作事项" : "已完成准备，开始检查可同步事项……";
    }

    public void Report(PeriodTrackerUploadProgress progress)
    {
        Completed = progress.Completed;
        Total = progress.Total;
        Succeeded = progress.Succeeded;
        Skipped = progress.Skipped;
        Progress = progress.Total == 0
            ? 100
            : progress.Completed * 100d / progress.Total;
        StatusText = progress.Message;
    }

    public void Complete(PeriodTrackerUploadSummary summary)
        => RequestClose?.Invoke(this, summary);

    public event EventHandler<object?>? RequestClose;

    public void Close()
    {
    }
}
