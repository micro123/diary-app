using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.Services;
using Diary.Core.Constants;
using Diary.GUIBase.Utils;
using Diary.GUIBase.ViewModels;
using Diary.Utils;

namespace Diary.App.ViewModels;

public sealed record StatusBarItemViewModel(string Text, string Detail, AppStatusLevel Level)
{
    public bool IsInformation => Level == AppStatusLevel.Information;
    public bool IsSuccess => Level == AppStatusLevel.Success;
    public bool IsWarning => Level == AppStatusLevel.Warning;
    public bool IsError => Level == AppStatusLevel.Error;

    public static StatusBarItemViewModel From(AppStatusItem item)
        => new(item.Text, item.Detail, item.Level);
}

public sealed record StatusBarTaskItemViewModel(
    string Name,
    string Detail,
    string ProgressText,
    double ProgressValue,
    bool HasProgress);

[DiAutoRegister]
public partial class StatusBarViewModel : ViewModelBase
{
    private readonly AppStatusService _statusService;

    [ObservableProperty]
    private string _date = FormatDate(DateTime.Now);

    [ObservableProperty]
    private StatusBarItemViewModel _database = new(
        "数据库",
        "正在读取数据库状态。",
        AppStatusLevel.Information);

    [ObservableProperty]
    private StatusBarItemViewModel _tracker = new(
        "Tracker",
        "正在读取 Tracker 状态。",
        AppStatusLevel.Information);

    [ObservableProperty]
    private StatusBarItemViewModel _message = new(string.Empty, string.Empty, AppStatusLevel.Neutral);

    [ObservableProperty]
    private StatusBarItemViewModel _update = new(string.Empty, string.Empty, AppStatusLevel.Neutral);

    [ObservableProperty]
    private StatusBarItemViewModel _taskSummary = new(string.Empty, string.Empty, AppStatusLevel.Information);

    [ObservableProperty]
    private IReadOnlyList<StatusBarTaskItemViewModel> _tasks = [];

    [ObservableProperty]
    private bool _hasMessage;

    [ObservableProperty]
    private bool _hasUpdate;

    [ObservableProperty]
    private bool _hasTasks;

    public StatusBarViewModel()
        : this(new AppStatusService())
    {
    }

    public StatusBarViewModel(AppStatusService statusService)
    {
        _statusService = statusService;
        _statusService.Changed += OnStatusChanged;
        ApplySnapshot(_statusService.GetSnapshot());
    }

    [RelayCommand]
    private void OpenDatabaseSettings()
        => EventDispatcher.RunCommand(CommandNames.ShowDbSettings);

    [RelayCommand]
    private void OpenTrackerSettings()
        => EventDispatcher.RunCommand(CommandNames.ShowTrackerSettings);

    [RelayCommand]
    private void CheckForUpdates()
        => EventDispatcher.RunCommand(CommandNames.CheckForUpdates);

    [RelayCommand]
    private void OpenCurrentLog()
        => EventDispatcher.RunCommand(CommandNames.OpenCurrentLog);

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        var snapshot = _statusService.GetSnapshot();
        if (Dispatcher.UIThread.CheckAccess())
            ApplySnapshot(snapshot);
        else
            Dispatcher.UIThread.Post(() => ApplySnapshot(snapshot));
    }

    private void ApplySnapshot(AppStatusSnapshot snapshot)
    {
        Database = StatusBarItemViewModel.From(snapshot.Database);
        Tracker = StatusBarItemViewModel.From(snapshot.Tracker);
        HasMessage = snapshot.Message is not null;
        Message = snapshot.Message is null
            ? new StatusBarItemViewModel(string.Empty, string.Empty, AppStatusLevel.Neutral)
            : StatusBarItemViewModel.From(snapshot.Message);
        HasUpdate = snapshot.Update is not null;
        Update = snapshot.Update is null
            ? new StatusBarItemViewModel(string.Empty, string.Empty, AppStatusLevel.Neutral)
            : StatusBarItemViewModel.From(snapshot.Update);
        Tasks = snapshot.Tasks.Select(ToTaskViewModel).ToArray();
        HasTasks = Tasks.Count > 0;
        TaskSummary = CreateTaskSummary(snapshot.Tasks);
        Date = FormatDate(DateTime.Now);
    }

    private static StatusBarTaskItemViewModel ToTaskViewModel(AppBackgroundTaskStatus task)
    {
        var progress = task.Progress is null ? 0 : task.Progress.Value * 100;
        return new StatusBarTaskItemViewModel(
            task.Name,
            task.Detail,
            task.Progress is null ? string.Empty : $"{task.Progress:P0}",
            progress,
            task.Progress is not null);
    }

    private static StatusBarItemViewModel CreateTaskSummary(IReadOnlyList<AppBackgroundTaskStatus> tasks)
    {
        if (tasks.Count == 0)
            return new StatusBarItemViewModel(string.Empty, string.Empty, AppStatusLevel.Information);
        var latest = tasks[^1];
        var text = tasks.Count == 1
            ? latest.Progress is null
                ? latest.Name
                : $"{latest.Name} {latest.Progress:P0}"
            : $"后台任务 {tasks.Count}";
        var detail = string.Join("\n", tasks.Select(task => $"{task.Name}：{task.Detail}"));
        return new StatusBarItemViewModel(text, detail, AppStatusLevel.Information);
    }

    private static string FormatDate(DateTime value) => $"{value.Month}月{value.Day}日";

    protected override void Cleanup()
    {
        _statusService.Changed -= OnStatusChanged;
        base.Cleanup();
    }
}
