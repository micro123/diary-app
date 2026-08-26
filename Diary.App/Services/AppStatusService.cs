using Diary.Utils;

namespace Diary.App.Services;

public enum AppStatusLevel
{
    Neutral,
    Information,
    Success,
    Warning,
    Error,
}

public sealed record AppStatusItem(
    string Text,
    string Detail,
    AppStatusLevel Level = AppStatusLevel.Neutral);

public sealed record AppBackgroundTaskStatus(
    Guid Id,
    string Name,
    string Detail,
    double? Progress,
    DateTimeOffset StartedAt);

public sealed record AppStatusSnapshot(
    AppStatusItem Database,
    AppStatusItem Tracker,
    AppStatusItem? Message,
    AppStatusItem? Update,
    IReadOnlyList<AppBackgroundTaskStatus> Tasks);

[DiAutoRegister(singleton: true)]
public sealed class AppStatusService
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, AppBackgroundTaskStatus> _tasks = [];
    private AppStatusItem _database = new("数据库", "正在读取数据库状态。", AppStatusLevel.Information);
    private AppStatusItem _tracker = new("Tracker", "正在读取 Tracker 状态。", AppStatusLevel.Information);
    private AppStatusItem? _message;
    private AppStatusItem? _update;
    private long _messageGeneration;

    public event EventHandler? Changed;

    public AppStatusSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return new AppStatusSnapshot(
                _database,
                _tracker,
                _message,
                _update,
                _tasks.Values.OrderBy(task => task.StartedAt).ToArray());
        }
    }

    public void SetDatabase(AppStatusItem status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_sync)
            _database = status;
        RaiseChanged();
    }

    public void SetTracker(AppStatusItem status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_sync)
            _tracker = status;
        RaiseChanged();
    }

    public void SetUpdate(AppStatusItem? status)
    {
        lock (_sync)
            _update = status;
        RaiseChanged();
    }

    public void ShowMessage(
        string text,
        AppStatusLevel level = AppStatusLevel.Information,
        string? detail = null,
        TimeSpan? duration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        long generation;
        lock (_sync)
        {
            _message = new AppStatusItem(text, detail ?? text, level);
            generation = ++_messageGeneration;
        }
        RaiseChanged();
        _ = ClearMessageLaterAsync(generation, duration ?? TimeSpan.FromSeconds(8));
    }

    public AppStatusTask BeginTask(string name, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var task = new AppBackgroundTaskStatus(
            Guid.NewGuid(),
            name,
            detail ?? "正在执行…",
            null,
            DateTimeOffset.Now);
        lock (_sync)
            _tasks.Add(task.Id, task);
        RaiseChanged();
        return new AppStatusTask(this, task.Id);
    }

    internal void UpdateTask(Guid id, double? progress, string? detail)
    {
        if (progress is < 0 or > 1 || double.IsNaN(progress ?? 0))
            throw new ArgumentOutOfRangeException(nameof(progress));
        lock (_sync)
        {
            if (!_tasks.TryGetValue(id, out var current))
                return;
            _tasks[id] = current with
            {
                Progress = progress,
                Detail = string.IsNullOrWhiteSpace(detail) ? current.Detail : detail,
            };
        }
        RaiseChanged();
    }

    internal void EndTask(Guid id)
    {
        lock (_sync)
        {
            if (!_tasks.Remove(id))
                return;
        }
        RaiseChanged();
    }

    private async Task ClearMessageLaterAsync(long generation, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return;
        await Task.Delay(duration).ConfigureAwait(false);
        lock (_sync)
        {
            if (generation != _messageGeneration)
                return;
            _message = null;
        }
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

public sealed class AppStatusTask(AppStatusService owner, Guid id) : IDisposable
{
    private int _disposed;

    public void Report(double? progress, string? detail = null)
    {
        if (Volatile.Read(ref _disposed) == 0)
            owner.UpdateTask(id, progress, detail);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            owner.EndTask(id);
    }
}
