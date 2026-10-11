using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Diary.App.Models;
using Diary.Core.Data.Base;
using Diary.Database;
using Diary.PluginBase;
using Diary.Script.Runtime;
using Diary.ScriptBase;
using Diary.ScriptHost;
using Diary.Utils;

namespace Diary.App.Services;

public sealed class AgentCapabilityApi : IAgentCapabilityApi, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Func<DbInterfaceBase?> _databaseProvider;
    private readonly PluginInstanceRegistry _instances;
    private readonly TrackerUiContributionRegistry _trackerUi;
    private readonly IScriptCatalog _scripts;
    private readonly IScriptManager _scriptManager;
    private readonly ScriptDirectoryLoadState _scriptLoadState;
    private readonly IUserInteractionScriptApi _interaction;
    private readonly string _reminderPath;
    private readonly string _submissionPath;
    private readonly object _gate = new();
    private readonly List<AgentReminder> _reminders;
    private readonly Dictionary<string, AgentTrackerTimeSubmitResult> _submissions;
    private readonly SemaphoreSlim _reminderDelivery = new(1, 1);
    private readonly Timer _timer;

    public AgentCapabilityApi(
        Func<DbInterfaceBase?> databaseProvider,
        PluginInstanceRegistry instances,
        TrackerUiContributionRegistry trackerUi,
        IScriptCatalog scripts,
        IScriptManager scriptManager,
        ScriptDirectoryLoadState scriptLoadState,
        IUserInteractionScriptApi interaction,
        string stateDirectory)
    {
        _databaseProvider = databaseProvider;
        _instances = instances;
        _trackerUi = trackerUi;
        _scripts = scripts;
        _scriptManager = scriptManager;
        _scriptLoadState = scriptLoadState;
        _interaction = interaction;
        Directory.CreateDirectory(stateDirectory);
        _reminderPath = Path.Combine(stateDirectory, "reminders.json");
        _submissionPath = Path.Combine(stateDirectory, "tracker-submissions.json");
        _reminders = LoadState<List<AgentReminder>>(_reminderPath) ?? [];
        _submissions = LoadState<Dictionary<string, AgentTrackerTimeSubmitResult>>(_submissionPath)
            ?? new Dictionary<string, AgentTrackerTimeSubmitResult>(StringComparer.Ordinal);
        RecoverOverdueReminders();
        _timer = new Timer(_ => _ = DeliverDueRemindersAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    public IReadOnlyList<AgentSavedQuery> ListSavedQueries()
    {
        var database = _databaseProvider();
        var tags = database?.AllWorkTags().ToArray() ?? [];
        return new SavedWorkItemQueryStore(availableTags: tags).Queries.Select(ToSavedQuery).ToArray();
    }

    public async ValueTask<AgentSavedQueryResult> RunSavedQueryAsync(
        string queryId,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var database = _databaseProvider();
        if (database is null)
            return new(false, null, [], "database_unavailable", "数据库尚未连接。");
        var store = new SavedWorkItemQueryStore(availableTags: database.AllWorkTags().ToArray());
        var saved = store.Queries.FirstOrDefault(item => item.Id.ToString("D") == queryId);
        if (saved is null)
            return new(false, null, [], "saved_query_not_found", "保存查询不存在。");
        var descriptor = ToSavedQuery(saved);
        if (descriptor.HasUnresolvedTags)
            return new(false, descriptor, [], "saved_query_tag_unresolved", "保存查询包含已失效标签，请先在查询页面修复。");
        var query = saved.ToQuery();
        if (limit is not null)
            query = query with { Limit = Math.Clamp(limit.Value, 1, 500) };
        IWorkItemQueryScriptApi api = new WorkItemQueryScriptApi(() => database, includeLocalNotes: false);
        var items = new List<ScriptWorkItem>();
        await foreach (var item in api.StreamAsync(new ScriptWorkItemQuery
        {
            StartDate = query.StartDate,
            EndDate = query.EndDate,
            TagIds = query.TagIds.ToImmutableArray(),
            TagFilter = (ScriptWorkItemTagFilter)(int)query.TagFilter,
            Text = query.Text,
            Priority = query.Priority is null ? null : (int)query.Priority.Value,
            Limit = query.Limit,
            Offset = query.Offset,
        }, cancellationToken: cancellationToken))
        {
            items.Add(item);
        }
        return new(true, descriptor, items);
    }

    public IReadOnlyList<AgentTrackerItem> SearchTrackerItems(
        string? pluginId,
        string? instanceId,
        string? query,
        int limit) => _instances.Instances
        .Where(instance => instance is ITrackerLocalCatalog)
        .Where(instance => string.IsNullOrWhiteSpace(pluginId)
            || string.Equals(instance.PluginId, pluginId, StringComparison.Ordinal))
        .Where(instance => string.IsNullOrWhiteSpace(instanceId)
            || string.Equals(instance.InstanceId, instanceId, StringComparison.Ordinal))
        .SelectMany(instance => ((ITrackerLocalCatalog)instance).SearchLocalItems(query, Math.Clamp(limit, 1, 100))
            .Select(item => ToTrackerItem(instance, item)))
        .Take(Math.Clamp(limit, 1, 100))
        .ToArray();

    public AgentTrackerItem? GetTrackerItem(string pluginId, string instanceId, string itemId)
    {
        var instance = _instances.Get(pluginId, instanceId);
        var item = (instance as ITrackerLocalCatalog)?.GetLocalItem(itemId);
        return instance is null || item is null ? null : ToTrackerItem(instance, item);
    }

    public IReadOnlyList<AgentScriptContract> ListScripts()
    {
        var contracts = _scripts.GetAll()
            .Select(program => ToScriptContract(program.Descriptor))
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        if (_scriptLoadState.LatestResult is { } latest)
        {
            foreach (var entry in latest.Entries)
            {
                var contract = ToScriptContract(entry, latest.Diagnostics);
                contracts[contract.Id] = contract;
            }
        }
        return contracts.Values
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public AgentScriptContract? GetScriptContract(string scriptId) =>
        ListScripts().FirstOrDefault(item => string.Equals(item.Id, scriptId, StringComparison.Ordinal));

    public async ValueTask<AgentScriptExecutionResult> ExecuteSavedScriptAsync(
        string scriptId,
        IReadOnlyDictionary<string, string> arguments,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (!_scripts.TryGet(scriptId, out var program) || program is null
            || !_scripts.TryGetSource(scriptId, out var source) || source is null
            || !IsInsideSavedScriptDirectory(source.SourcePath))
        {
            return new(false, scriptId, "Rejected", [], null, "script_not_saved", "只能执行脚本目录中已保存并成功加载的脚本。");
        }
        var outcome = await _scriptManager.ExecuteAsync(
            scriptId,
            new ScriptExecutionRequest(
                Arguments: arguments.ToImmutableDictionary(StringComparer.Ordinal),
                Source: ScriptExecutionSource.Manual,
                IdempotencyKey: idempotencyKey),
            timeout: TimeSpan.FromMinutes(2),
            cancellationToken: cancellationToken);
        return new(
            outcome.Result.Status == ScriptExecutionStatus.Succeeded,
            scriptId,
            outcome.Result.Status.ToString(),
            outcome.Result.Diagnostics,
            outcome.Result.Effects,
            outcome.Result.Status == ScriptExecutionStatus.Succeeded ? null : "script_execution_failed",
            outcome.Result.Status == ScriptExecutionStatus.Succeeded ? null : "脚本执行未成功。");
    }

    public IReadOnlyList<AgentReminder> ListReminders()
    {
        lock (_gate)
            return _reminders.OrderByDescending(item => item.CreatedAt).ToArray();
    }

    public ValueTask<AgentOperationResult> ScheduleReminderAsync(
        string title,
        string body,
        DateTimeOffset dueAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (dueAt <= DateTimeOffset.Now)
            return ValueTask.FromResult(new AgentOperationResult(false, ErrorCode: "invalid_due_time", ErrorMessage: "提醒时间必须晚于当前时间。"));
        var reminder = new AgentReminder(
            Guid.NewGuid().ToString("N"),
            title.Trim(),
            body.Trim(),
            dueAt,
            AgentReminderState.Scheduled,
            DateTimeOffset.Now);
        lock (_gate)
        {
            _reminders.Add(reminder);
            SaveState(_reminderPath, _reminders);
        }
        return ValueTask.FromResult(new AgentOperationResult(true, reminder.Id, Summary: $"提醒已安排在 {dueAt:yyyy-MM-dd HH:mm:ss zzz}。"));
    }

    public ValueTask<AgentTrackerTimePreview> PreviewTrackerTimeEntriesAsync(
        IReadOnlyList<int> workItemIds,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var database = _databaseProvider();
        if (database is null)
            return ValueTask.FromResult(new AgentTrackerTimePreview(false, [], 0, null, "database_unavailable", "数据库尚未连接。"));
        var ids = workItemIds.Where(id => id > 0).Distinct().Take(20).ToArray();
        if (ids.Length == 0)
            return ValueTask.FromResult(new AgentTrackerTimePreview(false, [], 0, null, "invalid_arguments", "至少需要一个事项 ID。"));
        var items = ids.Select(id => database.QueryWorkItems(new WorkItemQuery
        {
            WorkItemId = id,
            Limit = 1,
        }).SingleOrDefault())
            .Where(item => item is not null)
            .Cast<WorkItem>()
            .OrderBy(item => item.Id)
            .ToArray();
        if (items.Length != ids.Length)
            return ValueTask.FromResult(new AgentTrackerTimePreview(false, [], 0, null, "work_item_not_found", "部分事项不存在。"));
        var entries = BuildTrackerEntries(items);
        var version = Hash(entries);
        return ValueTask.FromResult(new AgentTrackerTimePreview(
            true,
            entries,
            entries.Sum(item => item.Hours),
            version));
    }

    public async ValueTask<AgentTrackerTimeSubmitResult> SubmitTrackerTimeEntriesAsync(
        IReadOnlyList<int> workItemIds,
        string idempotencyKey,
        string previewVersion,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_submissions.TryGetValue(idempotencyKey, out var previous))
                return previous with { Duplicate = true };
        }
        var preview = await PreviewTrackerTimeEntriesAsync(workItemIds, cancellationToken);
        if (!preview.Succeeded || preview.PreviewVersion != previewVersion)
            return new(false, [], false, preview.PreviewVersion, "preview_stale", "Tracker 提交预览已变化，请重新确认。");
        if (preview.Entries.Any(entry => !entry.CanSubmit))
            return new(false, [], false, previewVersion, "tracker_entry_invalid", "预览中存在不能提交的 Tracker 条目。");

        var database = _databaseProvider()!;
        var items = workItemIds.Distinct()
            .Select(id => database.QueryWorkItems(new WorkItemQuery { WorkItemId = id, Limit = 1 }).Single())
            .OrderBy(item => item.Id)
            .ToArray();
        var results = new List<AgentOperationResult>();
        foreach (var item in items)
        {
            foreach (var contribution in _trackerUi.Contributions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var extension = contribution.CreateEditorExtension(contribution.Instance.InstanceId);
                if (extension is null)
                    continue;
                try
                {
                    extension.Load(item);
                    if (extension.IsLocked)
                        continue;
                    var validation = extension.ValidateUpload(item);
                    if (!validation.CanUpload)
                    {
                        results.Add(new(false, ErrorCode: "validation_failed", ErrorMessage: validation.Error, Summary: $"事项 {item.Id} / {extension.Key}"));
                        return SaveSubmission(idempotencyKey, new(false, results, false, previewVersion, "partial_failure", "提交已在首个失败处停止。"));
                    }
                    var upload = await extension.UploadAsync(item);
                    var verified = upload.Success
                        && upload.State == TrackerUploadState.Succeeded
                        && (extension.IsLocked || extension.UploadState == TrackerUploadState.Succeeded);
                    var error = verified
                        ? null
                        : upload.Error ?? "远程调用返回后，本地同步状态未能核验为成功。";
                    results.Add(new(verified, upload.RemoteId, verified ? null : "tracker_submit_unverified", error, $"事项 {item.Id} / {extension.DescribeUploadTarget()}"));
                    if (!verified)
                        return SaveSubmission(idempotencyKey, new(false, results, false, previewVersion, "partial_failure", "提交已在首个失败处停止；成功项不会自动回滚。"));
                }
                finally
                {
                    (extension as IDisposable)?.Dispose();
                }
            }
        }
        return SaveSubmission(idempotencyKey, new(true, results, false, previewVersion));
    }

    public void Dispose()
    {
        _timer.Dispose();
        _reminderDelivery.Dispose();
    }

    private IReadOnlyList<AgentTrackerTimeEntry> BuildTrackerEntries(IReadOnlyList<WorkItem> items)
    {
        var entries = new List<AgentTrackerTimeEntry>();
        foreach (var item in items)
        {
            foreach (var contribution in _trackerUi.Contributions)
            {
                var extension = contribution.CreateEditorExtension(contribution.Instance.InstanceId);
                if (extension is null)
                    continue;
                try
                {
                    extension.Load(item);
                    if (extension.IsLocked)
                        continue;
                    var validation = extension.ValidateUpload(item);
                    entries.Add(new(
                        item.Id,
                        item.CreateDate,
                        item.Comment,
                        item.Time,
                        extension.Key.PluginId,
                        extension.Key.InstanceId,
                        contribution.Instance.DisplayName,
                        extension.DescribeUploadTarget(),
                        extension.UploadState.ToString(),
                        validation.CanUpload,
                        validation.Error));
                }
                finally
                {
                    (extension as IDisposable)?.Dispose();
                }
            }
        }
        return entries;
    }

    private AgentTrackerTimeSubmitResult SaveSubmission(string key, AgentTrackerTimeSubmitResult result)
    {
        lock (_gate)
        {
            _submissions[key] = result;
            SaveState(_submissionPath, _submissions);
        }
        return result;
    }

    private async Task DeliverDueRemindersAsync()
    {
        if (!await _reminderDelivery.WaitAsync(0))
            return;
        try
        {
            AgentReminder[] due;
            lock (_gate)
                due = _reminders.Where(item => item.State == AgentReminderState.Scheduled && item.DueAt <= DateTimeOffset.Now).ToArray();
            foreach (var reminder in due)
            {
                try
                {
                    await _interaction.NotifyAsync(reminder.Title, reminder.Body);
                    CompleteReminder(reminder.Id, AgentReminderState.Delivered);
                }
                catch
                {
                    // 保留 Scheduled，下一次计时器重试。
                }
            }
        }
        finally
        {
            _reminderDelivery.Release();
        }
    }

    private void RecoverOverdueReminders()
    {
        var cutoff = DateTimeOffset.Now.AddHours(-24);
        lock (_gate)
        {
            for (var index = 0; index < _reminders.Count; index++)
            {
                var reminder = _reminders[index];
                if (reminder.State == AgentReminderState.Scheduled && reminder.DueAt < cutoff)
                    _reminders[index] = reminder with { State = AgentReminderState.Missed, CompletedAt = DateTimeOffset.Now };
            }
            SaveState(_reminderPath, _reminders);
        }
    }

    private void CompleteReminder(string id, AgentReminderState state)
    {
        lock (_gate)
        {
            var index = _reminders.FindIndex(item => item.Id == id);
            if (index < 0)
                return;
            _reminders[index] = _reminders[index] with { State = state, CompletedAt = DateTimeOffset.Now };
            SaveState(_reminderPath, _reminders);
        }
    }

    private static AgentSavedQuery ToSavedQuery(SavedWorkItemQuery query) => new(
        query.Id.ToString("D"),
        query.Name,
        query.StartDate,
        query.EndDate,
        query.Tags?.Select(tag => tag.Id).ToArray() ?? query.TagIds ?? [],
        query.Tags?.Select(tag => tag.Name).ToArray() ?? [],
        query.TagFilter.ToString(),
        query.Text,
        query.Priority is null ? null : (int)query.Priority.Value,
        query.Limit,
        query.Offset,
        query.Tags?.Any(tag => tag.Unresolved) == true);

    private static AgentTrackerItem ToTrackerItem(ITrackerInstance instance, TrackerLocalItem item) => new(
        instance.PluginId,
        instance.InstanceId,
        instance.DisplayName,
        item.ItemId,
        item.Title,
        item.Project,
        item.Status,
        item.Closed,
        item.Metadata);

    private AgentScriptContract ToScriptContract(ScriptDescriptor descriptor)
    {
        _scripts.TryGetSource(descriptor.Id, out var source);
        return new(
            descriptor.Id,
            descriptor.Name,
            descriptor.Description,
            source?.EngineName ?? Path.GetExtension(source?.SourcePath ?? string.Empty).TrimStart('.'),
            descriptor.Scope.ToString(),
            (descriptor.EntryKind ?? ScriptEntryKind.Application).ToString(),
            descriptor.Parameters ?? [],
            source is null ? "Registered" : "Ready",
            []);
    }

    internal static AgentScriptContract ToScriptContract(
        ScriptDirectoryEntry entry,
        ImmutableArray<ScriptDiagnostic> loadDiagnostics)
    {
        var descriptor = entry.DiscoveredDescriptor ?? entry.BuildResult?.Program?.Descriptor;
        var metadata = entry.Metadata;
        var id = descriptor?.Id ?? metadata?.Id;
        if (string.IsNullOrWhiteSpace(id))
            id = $"invalid-{Hash(entry.SourcePath)[..16].ToLowerInvariant()}";
        var diagnostics = entry.BuildResult?.Diagnostics ?? [];
        diagnostics = diagnostics.AddRange(entry.ConfigurationDiagnostics.IsDefault
            ? []
            : entry.ConfigurationDiagnostics);
        diagnostics = diagnostics.AddRange(loadDiagnostics.Where(diagnostic =>
            IsDiagnosticForEntry(diagnostic.SourcePath, entry.SourcePath)));
        var state = entry.ConfigurationState switch
        {
            ScriptConfigurationState.NeedsConfiguration => "NeedsConfiguration",
            _ when entry.BuildResult?.Succeeded == true => "Ready",
            _ => "Invalid",
        };
        return new(
            id,
            descriptor?.Name ?? metadata?.Name ?? Path.GetFileNameWithoutExtension(entry.SourcePath),
            descriptor?.Description ?? metadata?.Description,
            entry.BuildResult?.EngineName
                ?? metadata?.Engine
                ?? Path.GetExtension(entry.SourcePath).TrimStart('.'),
            (descriptor?.Scope ?? metadata?.Scope ?? entry.Scope).ToString(),
            (descriptor?.EntryKind ?? metadata?.EntryKind ?? ScriptEntryKind.Application).ToString(),
            descriptor?.Parameters ?? metadata?.Parameters ?? [],
            state,
            diagnostics
                .Distinct()
                .Select(SanitizeDiagnostic)
                .ToArray());
    }

    private static bool IsDiagnosticForEntry(string? diagnosticPath, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(diagnosticPath))
            return false;
        var diagnosticFullPath = Path.GetFullPath(diagnosticPath);
        var sourceFullPath = Path.GetFullPath(sourcePath);
        return string.Equals(diagnosticFullPath, sourceFullPath, PathComparison)
            || string.Equals(diagnosticFullPath, sourceFullPath + ".json", PathComparison);
    }

    private static ScriptDiagnostic SanitizeDiagnostic(ScriptDiagnostic diagnostic) =>
        diagnostic with
        {
            SourcePath = string.IsNullOrWhiteSpace(diagnostic.SourcePath)
                ? null
                : Path.GetFileName(diagnostic.SourcePath),
        };

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static bool IsInsideSavedScriptDirectory(string sourcePath)
    {
        var root = Path.GetFullPath(Path.Combine(FsTools.GetApplicationConfigDirectory(), "scripts"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(sourcePath);
        return path.StartsWith(root, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);
    }

    private static string Hash(object value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    private static T? LoadState<T>(string path)
    {
        try
        {
            if (!File.Exists(path))
                return default;
            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("schemaVersion", out _))
            {
                var envelope = JsonSerializer.Deserialize<StateEnvelope<T>>(json, JsonOptions);
                return envelope?.SchemaVersion == 1 ? envelope.Value : default;
            }
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { return default; }
    }

    private static void SaveState<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(new StateEnvelope<T>(1, value), JsonOptions), Encoding.UTF8);
        File.Move(temporary, path, true);
    }

    private sealed record StateEnvelope<T>(int SchemaVersion, T Value);
}
