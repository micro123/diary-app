using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.GUIBase.ViewModels;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;
using Serilog.Events;

namespace Diary.App.ViewModels.Dialogs;

public sealed record LogLevelFilterOption(
    string Name,
    LogEventLevel? Minimum = null,
    LogEventLevel? Maximum = null)
{
    public bool Matches(LogEventLevel level) =>
        (Minimum is null || level >= Minimum) &&
        (Maximum is null || level <= Maximum);

    public override string ToString() => Name;
}

public sealed class LogViewerItemViewModel(InMemoryLogEntry entry)
{
    public long Sequence => entry.Sequence;
    public string TimeText => entry.Timestamp.ToLocalTime().ToString("MM-dd HH:mm:ss.fff");
    public string LevelText => entry.Level switch
    {
        LogEventLevel.Verbose => "跟踪",
        LogEventLevel.Debug => "调试",
        LogEventLevel.Information => "信息",
        LogEventLevel.Warning => "警告",
        LogEventLevel.Error => "错误",
        LogEventLevel.Fatal => "严重",
        _ => entry.Level.ToString(),
    };
    public string Source => entry.Source;
    public string Message => entry.Message;
    public string Details => entry.ToDisplayText();
    public bool IsDebug => entry.Level <= LogEventLevel.Debug;
    public bool IsInformation => entry.Level == LogEventLevel.Information;
    public bool IsWarning => entry.Level == LogEventLevel.Warning;
    public bool IsError => entry.Level >= LogEventLevel.Error;

    public bool Matches(string searchText, LogLevelFilterOption levelFilter)
    {
        if (!levelFilter.Matches(entry.Level))
            return false;
        if (string.IsNullOrWhiteSpace(searchText))
            return true;
        return Details.Contains(searchText.Trim(), StringComparison.CurrentCultureIgnoreCase);
    }
}

[DiAutoRegister]
public partial class LogViewerDialogViewModel : ViewModelBase, IDialogContext
{
    private static readonly IReadOnlyList<LogLevelFilterOption> AvailableLevelOptions =
    [
        new("全部级别"),
        new("跟踪与调试", LogEventLevel.Verbose, LogEventLevel.Debug),
        new("信息", LogEventLevel.Information, LogEventLevel.Information),
        new("警告", LogEventLevel.Warning, LogEventLevel.Warning),
        new("错误与严重", LogEventLevel.Error, LogEventLevel.Fatal),
    ];

    private readonly InMemoryLogStore _store;
    private IReadOnlyList<LogViewerItemViewModel> _snapshot = [];
    private bool _subscribed;
    private int _refreshPending;

    public LogViewerDialogViewModel() : this(Logging.MemoryLogs)
    {
    }

    internal LogViewerDialogViewModel(InMemoryLogStore store)
    {
        _store = store;
        SelectedLevel = AvailableLevelOptions[0];
    }

    public IReadOnlyList<LogLevelFilterOption> LevelOptions => AvailableLevelOptions;
    public ObservableCollection<LogViewerItemViewModel> Entries { get; } = [];

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private LogLevelFilterOption _selectedLevel;
    [ObservableProperty] private LogViewerItemViewModel? _selectedEntry;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private bool _hasEntries;
    [ObservableProperty] private bool _isRefreshPaused;
    [ObservableProperty] private bool _hasPendingLogs;

    public string RefreshToggleText => IsRefreshPaused ? "继续刷新" : "暂停刷新";

    public event EventHandler<object?>? RequestClose;

    public void Initialize()
    {
        IsRefreshPaused = false;
        HasPendingLogs = false;
        if (!_subscribed)
        {
            _store.Changed += OnStoreChanged;
            _subscribed = true;
        }
        Refresh();
    }

    public void Deactivate() => Unsubscribe();

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedLevelChanged(LogLevelFilterOption value) => ApplyFilter();

    partial void OnIsRefreshPausedChanged(bool value)
    {
        OnPropertyChanged(nameof(RefreshToggleText));
        if (!value)
            Refresh();
        else
            UpdateSummary();
    }

    [RelayCommand]
    private void Refresh()
    {
        _snapshot = _store.GetSnapshot()
            .OrderByDescending(entry => entry.Sequence)
            .Select(entry => new LogViewerItemViewModel(entry))
            .ToArray();
        HasPendingLogs = false;
        ApplyFilter();
    }

    [RelayCommand]
    private void ToggleRefresh() => IsRefreshPaused = !IsRefreshPaused;

    [RelayCommand]
    private void Clear()
    {
        _store.Clear();
        Refresh();
    }

    [RelayCommand]
    private async Task CopySelected()
    {
        if (SelectedEntry is null || !await CopyStringToClipboardAsync(SelectedEntry.Details))
            return;
        ToastManager?.Show("日志详情已复制");
    }

    [RelayCommand]
    private async Task CopyVisible()
    {
        var content = string.Join(
            Environment.NewLine + Environment.NewLine,
            Entries.Select(entry => entry.Details));
        if (!await CopyStringToClipboardAsync(content))
            return;
        ToastManager?.Show("当前列表已复制");
    }

    [RelayCommand]
    public void Close()
    {
        Unsubscribe();
        RequestClose?.Invoke(this, null);
    }

    protected override void Cleanup()
    {
        Unsubscribe();
        base.Cleanup();
    }

    private void ApplyFilter()
    {
        var selectedSequence = SelectedEntry?.Sequence;
        Entries.Clear();
        foreach (var entry in _snapshot.Where(entry => entry.Matches(SearchText, SelectedLevel)))
            Entries.Add(entry);

        SelectedEntry = selectedSequence is null
            ? Entries.FirstOrDefault()
            : Entries.FirstOrDefault(entry => entry.Sequence == selectedSequence) ?? Entries.FirstOrDefault();
        HasEntries = Entries.Count > 0;
        UpdateSummary();
    }

    private void UpdateSummary() => Summary = IsRefreshPaused
        ? $"已暂停 · 内存中 {_store.Count}/{_store.Capacity} 条，当前显示 {Entries.Count} 条" +
          (HasPendingLogs ? " · 有新日志" : string.Empty)
        : $"内存中 {_store.Count}/{_store.Capacity} 条，当前显示 {Entries.Count} 条";

    private void OnStoreChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _refreshPending, 1) != 0)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _refreshPending, 0);
            if (!_subscribed)
                return;
            if (IsRefreshPaused)
            {
                HasPendingLogs = true;
                UpdateSummary();
                return;
            }
            Refresh();
        }, DispatcherPriority.Background);
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
            return;
        _store.Changed -= OnStoreChanged;
        _subscribed = false;
    }
}
