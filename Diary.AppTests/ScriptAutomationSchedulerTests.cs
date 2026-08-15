using Diary.App.Services;
using Diary.Script.Runtime;
using Diary.ScriptBase;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diary.AppTests;

[TestClass]
public sealed class ScriptAutomationSchedulerTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 15, 10, 30, 0, TimeSpan.FromHours(8));
    [TestMethod]
    public async Task RunStartupCatchUp_RunsStartupScriptsOnce()
    {
        var manager = new RecordingScriptManager();
        var scheduler = new ScriptAutomationScheduler(manager, NullLogger<ScriptAutomationScheduler>.Instance, new FixedTimeProvider(FixedNow));
        scheduler.ApplyLoadResult(BuildResult(
            ("startup-script", null, RunOnStartup: true),
            ("scheduled-script", new TimeOnly(9, 0), RunOnStartup: false)));

        await scheduler.RunStartupCatchUpAsync();
        await scheduler.RunStartupCatchUpAsync();

        Assert.AreEqual(2, manager.Executions.Count);
        Assert.AreEqual(1, manager.Executions.Count(execution => execution.ScriptId == "startup-script"));
        Assert.AreEqual(1, manager.Executions.Count(execution => execution.ScriptId == "scheduled-script"));
        Assert.IsTrue(manager.Executions.All(execution => execution.Source == ScriptExecutionSource.Startup));
    }

    [TestMethod]
    public async Task RunStartupCatchUp_UsesStableIdempotencyKeys()
    {
        var manager = new RecordingScriptManager();
        var scheduler = new ScriptAutomationScheduler(manager, NullLogger<ScriptAutomationScheduler>.Instance, new FixedTimeProvider(FixedNow));
        scheduler.ApplyLoadResult(BuildResult(
            ("startup-script", null, RunOnStartup: true),
            ("scheduled-script", new TimeOnly(9, 0), RunOnStartup: false)));

        await scheduler.RunStartupCatchUpAsync();

        Assert.AreEqual(2, manager.Executions.Count);
        var startup = manager.Executions.Single(execution => execution.ScriptId == "startup-script");
        var scheduled = manager.Executions.Single(execution => execution.ScriptId == "scheduled-script");
        StringAssert.StartsWith(startup.IdempotencyKey, "startup:startup-script:");
        StringAssert.StartsWith(scheduled.IdempotencyKey, "auto:scheduled-script:");
    }

    [TestMethod]
    public async Task ApplyLoadResult_DropsRemovedScripts()
    {
        var manager = new RecordingScriptManager();
        var scheduler = new ScriptAutomationScheduler(manager, NullLogger<ScriptAutomationScheduler>.Instance, new FixedTimeProvider(FixedNow));
        scheduler.ApplyLoadResult(BuildResult(("script-a", null, RunOnStartup: true)));
        scheduler.ApplyLoadResult(BuildResult(("script-b", null, RunOnStartup: true)));

        await scheduler.RunStartupCatchUpAsync();

        Assert.AreEqual(1, manager.Executions.Count);
        Assert.AreEqual("script-b", manager.Executions.Single().ScriptId);
    }

    private static ScriptDirectoryLoadResult BuildResult(params (string ScriptId, TimeOnly? Time, bool RunOnStartup)[] plans) =>
        new(
            [.. plans.Select(plan => new ScriptDirectoryEntry(
                $"/scripts/{plan.ScriptId}",
                ScriptScope.Application,
                ScriptBuildResult.Success(new FakeProgram(plan.ScriptId)),
                new ScriptFileMetadata(
                    EntryKind: ScriptEntryKind.Automation,
                    Schedule: plan.Time is { } time ? $"daily {time:HH:mm}" : null,
                    RunOnStartup: plan.RunOnStartup)))],
            []);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone(
            "UTC+08:00",
            TimeSpan.FromHours(8),
            "UTC+08:00",
            "UTC+08:00");
    }

    private sealed class FakeProgram(string id) : IScriptProgramV1
    {
        public ScriptDescriptor Descriptor { get; } = new(
            id, id, ScriptApiVersion.V1, ScriptScope.Application, EntryKind: ScriptEntryKind.Automation);

        public ValueTask<ScriptExecutionResult> ExecuteAsync(
            ScriptExecutionRequest request,
            IScriptExecutionContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ScriptExecutionResult.Succeeded());
    }

    private sealed class RecordingScriptManager : IScriptManager
    {
        public List<(string ScriptId, ScriptExecutionSource Source, string? IdempotencyKey)> Executions { get; } = [];

        public ValueTask<ScriptBuildResult> BuildAndRegisterAsync(
            ScriptBuildRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ScriptBuildResult.Failure(new ScriptDiagnostic(
                "TEST_NOT_IMPLEMENTED", "测试未实现。", ScriptDiagnosticSeverity.Error, ScriptDiagnosticCategory.Runtime)));

        public ValueTask<ScriptExecutionOutcome> ExecuteAsync(
            string scriptId,
            ScriptExecutionRequest request,
            IScriptExecutionContext context,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default) =>
            Record(scriptId, request);

        public ValueTask<ScriptExecutionOutcome> ExecuteAsync(
            string scriptId,
            ScriptExecutionRequest request,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default) =>
            Record(scriptId, request);

        private ValueTask<ScriptExecutionOutcome> Record(string scriptId, ScriptExecutionRequest request)
        {
            Executions.Add((scriptId, request.Source, request.IdempotencyKey));
            return ValueTask.FromResult(new ScriptExecutionOutcome(
                Guid.NewGuid(),
                new ScriptExecutionResult(ScriptExecutionStatus.Succeeded, []),
                Source: request.Source));
        }
    }
}
