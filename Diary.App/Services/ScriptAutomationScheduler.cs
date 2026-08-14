using Avalonia.Threading;
using Diary.Script.Runtime;
using Diary.ScriptBase;
using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.App.Services;

[DiAutoRegister(singleton: true)]
public sealed class ScriptAutomationScheduler(
    IScriptManager scriptManager,
    ILogger<ScriptAutomationScheduler> logger) : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    private DispatcherTimer? _timer;
    private readonly SemaphoreSlim _executionLock = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<string, AutomationPlan> _plans = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastRun = new(StringComparer.Ordinal);
    private bool _startupCatchUpDone;
    private bool _timerStarted;

    private sealed record AutomationPlan(
        string ScriptId,
        TimeOnly? Time,
        bool RunOnStartup);

    public void ApplyLoadResult(ScriptDirectoryLoadResult result)
    {
        lock (_sync)
        {
            _plans.Clear();
            foreach (var entry in result.Entries)
            {
                if (entry.Metadata is not { } metadata
                    || entry.BuildResult?.Succeeded != true
                    || metadata.EntryKind != ScriptEntryKind.Automation
                    || entry.BuildResult.Program is null)
                    continue;
                ScriptAutomationSchedule.TryParse(metadata.Schedule, out var time);
                _plans[entry.BuildResult.Program.Descriptor.Id] =
                    new AutomationPlan(entry.BuildResult.Program.Descriptor.Id, metadata.Schedule is null ? null : time, metadata.RunOnStartup);
            }
        }
    }

    public void Start()
    {
        if (_timerStarted)
            return;
        _timerStarted = true;
        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public async Task RunStartupCatchUpAsync()
    {
        if (_startupCatchUpDone)
            return;
        _startupCatchUpDone = true;
        var now = DateTimeOffset.Now;
        List<AutomationPlan> due;
        lock (_sync)
            due = _plans.Values.Where(plan =>
                    plan.RunOnStartup
                    || (plan.Time is { } time && ScriptAutomationSchedule.GetNextDue(time, now, null) <= now))
                .ToList();
        foreach (var plan in due)
        {
            var key = plan.Time is { } time
                ? $"auto:{plan.ScriptId}:{now:yyyy-MM-dd HH:mm}"
                : $"startup:{plan.ScriptId}:{now:yyyy-MM-dd}";
            await EnqueueAsync(plan, ScriptExecutionSource.Startup, key, now);
        }
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        try
        {
            await TickAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "自动化脚本调度检查失败。");
        }
    }

    private async Task TickAsync()
    {
        var now = DateTimeOffset.Now;
        List<(AutomationPlan Plan, DateTimeOffset Occurrence)> due;
        lock (_sync)
        {
            due = [];
            foreach (var plan in _plans.Values)
            {
                if (plan.Time is not { } time)
                    continue;
                var nextDue = ScriptAutomationSchedule.GetNextDue(
                    time, now, _lastRun.TryGetValue(plan.ScriptId, out var lastRun) ? lastRun : null);
                if (nextDue <= now)
                    due.Add((plan, nextDue));
            }
        }
        foreach (var (plan, occurrence) in due)
            await EnqueueAsync(plan, ScriptExecutionSource.Automation,
                $"auto:{plan.ScriptId}:{occurrence:yyyy-MM-dd HH:mm}", occurrence);
    }

    private async Task EnqueueAsync(
        AutomationPlan plan,
        ScriptExecutionSource source,
        string idempotencyKey,
        DateTimeOffset occurrence)
    {
        await _executionLock.WaitAsync();
        try
        {
            lock (_sync)
            {
                if (_lastRun.TryGetValue(plan.ScriptId, out var lastRun) && lastRun >= occurrence)
                    return;
                _lastRun[plan.ScriptId] = occurrence;
            }
            logger.LogInformation("开始执行自动化脚本 {ScriptId}（来源：{Source}）。", plan.ScriptId, source);
            var outcome = await Task.Run(async () => await scriptManager.ExecuteAsync(
                plan.ScriptId,
                new ScriptExecutionRequest(
                    Source: source,
                    IdempotencyKey: idempotencyKey),
                TimeSpan.FromMinutes(5),
                CancellationToken.None));
            logger.LogInformation(
                "自动化脚本 {ScriptId} 执行完成：{Status}（{DiagnosticCount} 条诊断）。",
                plan.ScriptId, outcome.Result.Status, outcome.Result.Diagnostics.Length);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "自动化脚本 {ScriptId} 执行失败。", plan.ScriptId);
        }
        finally
        {
            _executionLock.Release();
        }
    }

    public void Dispose()
    {
        if (_timer is { } timer)
        {
            timer.Tick -= OnTick;
            timer.Stop();
        }
        _executionLock.Dispose();
    }
}
