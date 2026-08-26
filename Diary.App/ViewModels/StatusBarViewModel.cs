using Avalonia.Threading;
using Avalonia.Controls.Notifications;
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

public sealed record StatusBarNotificationItemViewModel(
    Guid Id,
    string Title,
    string Message,
    string TimeText,
    string OccurrenceText,
    bool HasMessage,
    bool HasOccurrenceCount,
    bool IsUnread,
    bool IsInformation,
    bool IsSuccess,
    bool IsWarning,
    bool IsError,
    string ActionLabel,
    string ActionCommand,
    string? ActionArgument,
    bool HasAction);

[DiAutoRegister]
public partial class StatusBarViewModel : ViewModelBase
{
    private readonly AppStatusService _statusService;
    private readonly NotificationHistoryService _notificationHistory;

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

    [ObservableProperty]
    private IReadOnlyList<StatusBarNotificationItemViewModel> _notifications = [];

    [ObservableProperty]
    private bool _hasNotifications;

    public bool HasNoNotifications => !HasNotifications;

    partial void OnHasNotificationsChanged(bool value)
        => OnPropertyChanged(nameof(HasNoNotifications));

    [ObservableProperty]
    private bool _hasUnreadNotifications;

    [ObservableProperty]
    private string _unreadNotificationText = string.Empty;

    [ObservableProperty]
    private bool _notificationIsInformation;

    [ObservableProperty]
    private bool _notificationIsSuccess;

    [ObservableProperty]
    private bool _notificationIsWarning;

    [ObservableProperty]
    private bool _notificationIsError;

    public StatusBarViewModel(
        AppStatusService statusService,
        NotificationHistoryService notificationHistory)
    {
        _statusService = statusService;
        _notificationHistory = notificationHistory;
        _statusService.Changed += OnStatusChanged;
        _notificationHistory.Changed += OnNotificationHistoryChanged;
        ApplySnapshot(_statusService.GetSnapshot());
        ApplyNotifications(_notificationHistory.GetSnapshot());
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

    [RelayCommand]
    private void OpenNotificationCenter()
        => _notificationHistory.MarkAllRead();

    [RelayCommand]
    private void MarkAllNotificationsRead()
        => _notificationHistory.MarkAllRead();

    [RelayCommand]
    private void ClearReadNotifications()
        => _notificationHistory.ClearRead();

    [RelayCommand]
    private async Task ClearAllNotifications()
    {
        if (await EventDispatcher.Confirm("清空通知历史", "将删除全部会话和持久通知，是否继续？"))
            _notificationHistory.ClearAll();
    }

    [RelayCommand]
    private void DeleteNotification(StatusBarNotificationItemViewModel item)
        => _notificationHistory.Remove(item.Id);

    [RelayCommand]
    private void ExecuteNotificationAction(StatusBarNotificationItemViewModel item)
    {
        if (!item.HasAction)
            return;
        _notificationHistory.MarkRead(item.Id);
        if (item.ActionCommand == CommandNames.OpenPath)
        {
            if (string.IsNullOrWhiteSpace(item.ActionArgument))
                return;
            if (Directory.Exists(item.ActionArgument))
                ProcUtils.OpenDirectoryCrossPlatform(item.ActionArgument);
            else if (File.Exists(item.ActionArgument))
                ProcUtils.OpenFileCrossPlatform(item.ActionArgument);
            return;
        }
        if (IsAllowedNotificationCommand(item.ActionCommand))
            EventDispatcher.RunCommand(item.ActionCommand);
    }

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        var snapshot = _statusService.GetSnapshot();
        if (Dispatcher.UIThread.CheckAccess())
            ApplySnapshot(snapshot);
        else
            Dispatcher.UIThread.Post(() => ApplySnapshot(snapshot));
    }

    private void OnNotificationHistoryChanged(object? sender, EventArgs e)
    {
        var snapshot = _notificationHistory.GetSnapshot();
        if (Dispatcher.UIThread.CheckAccess())
            ApplyNotifications(snapshot);
        else
            Dispatcher.UIThread.Post(() => ApplyNotifications(snapshot));
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

    private void ApplyNotifications(IReadOnlyList<AppNotificationEntry> snapshot)
    {
        Notifications = snapshot.Select(ToNotificationViewModel).ToArray();
        HasNotifications = Notifications.Count > 0;
        var unread = snapshot.Where(entry => !entry.IsRead).ToArray();
        HasUnreadNotifications = unread.Length > 0;
        UnreadNotificationText = unread.Length switch
        {
            0 => string.Empty,
            > 99 => "99+",
            _ => unread.Length.ToString(),
        };
        var highest = unread.Select(entry => entry.Type).DefaultIfEmpty(NotificationType.Information)
            .MaxBy(GetNotificationPriority);
        NotificationIsError = unread.Length > 0 && highest == NotificationType.Error;
        NotificationIsWarning = unread.Length > 0 && highest == NotificationType.Warning;
        NotificationIsSuccess = unread.Length > 0 && highest == NotificationType.Success;
        NotificationIsInformation = unread.Length > 0
            && !NotificationIsError
            && !NotificationIsWarning
            && !NotificationIsSuccess;
    }

    private static StatusBarNotificationItemViewModel ToNotificationViewModel(AppNotificationEntry entry)
    {
        var actionAllowed = entry.Action is not null
            && (entry.Action.Command == CommandNames.OpenPath
                ? File.Exists(entry.Action.Argument) || Directory.Exists(entry.Action.Argument)
                : IsAllowedNotificationCommand(entry.Action.Command));
        return new StatusBarNotificationItemViewModel(
            entry.Id,
            entry.Title,
            entry.Message,
            FormatNotificationTime(entry.LastOccurredAt),
            entry.OccurrenceCount > 1 ? $"×{entry.OccurrenceCount}" : string.Empty,
            !string.IsNullOrWhiteSpace(entry.Message),
            entry.OccurrenceCount > 1,
            !entry.IsRead,
            entry.Type == NotificationType.Information,
            entry.Type == NotificationType.Success,
            entry.Type == NotificationType.Warning,
            entry.Type == NotificationType.Error,
            entry.Action?.Label ?? string.Empty,
            entry.Action?.Command ?? string.Empty,
            entry.Action?.Argument,
            actionAllowed);
    }

    private static bool IsAllowedNotificationCommand(string command)
        => command is CommandNames.ShowDbSettings
            or CommandNames.ShowTrackerSettings
            or CommandNames.CheckForUpdates
            or CommandNames.OpenCurrentLog;

    private static int GetNotificationPriority(NotificationType type) => type switch
    {
        NotificationType.Error => 4,
        NotificationType.Warning => 3,
        NotificationType.Success => 2,
        _ => 1,
    };

    private static string FormatNotificationTime(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        if (local.Date == today)
            return local.ToString("HH:mm");
        if (local.Date == today.AddDays(-1))
            return $"昨天 {local:HH:mm}";
        return local.ToString("M月d日 HH:mm");
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
        _notificationHistory.Changed -= OnNotificationHistoryChanged;
        base.Cleanup();
    }
}
