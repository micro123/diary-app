using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.Models;
using Diary.App.ViewModels.Dialogs;
using Diary.App.Services;
using Diary.Core.Data.Base;
using Diary.Database;
using Diary.GUIBase;
using Diary.GUIBase.Utils;
using Diary.GUIBase.ViewModels;
using Diary.PluginBase;
using Diary.PluginUI;
using Diary.ScriptBase;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;
using Ursa.Controls;

namespace Diary.App.ViewModels;

public sealed record WorkEditorScriptMenuItem(string Header, ICommand Command, bool IsEnabled);

public partial class TrackerEditorTabItem : ObservableObject
{
    public ITrackerEditorExtension Extension { get; }

    [ObservableProperty]
    private string _header;

    public string Identity => $"{Extension.Key.PluginId}/{Extension.InstanceId}";

    public TrackerEditorTabItem(ITrackerEditorExtension extension, string header)
    {
        Extension = extension;
        _header = header;
    }
}

public partial class WorkEditorViewModel : ViewModelBase
{
    private readonly DbShareData _shareData;
    private readonly IWorkItemPersistenceCoordinator _persistence;
    private readonly ITrackerUploadCoordinator _uploadCoordinator;
    private readonly ITagAutomationCoordinator _tagAutomation;
    private readonly ScriptAutomationScheduler? _scriptAutomationScheduler;
    private readonly TrackerUiContributionRegistry _trackerRegistry;
    private readonly List<(WorkTag Tag, TagAddSource Source, int Sequence)> _pendingTagAutomation = [];
    private IReadOnlyList<int> _recentTagIds = Array.Empty<int>();

    // db data fields
    private WorkItem? WorkItem { get; set; } // ref to existed db item, may null

    // tracker 扩展集合（RedMine 等，可多个）。无 tracker 时空集合，编辑器只渲染 generic 字段。
    public ObservableCollection<ITrackerEditorExtension> Extensions { get; } = new();
    public ObservableCollection<TrackerEditorTabItem> TrackerTabs { get; } = new();
    public ObservableCollection<TrackerUploadResult> UploadResults { get; } = new();
    public bool HasUploadResults => UploadResults.Count > 0;
    public ObservableCollection<WorkEditorScriptMenuItem> EditorScriptActions { get; } = new();
    [ObservableProperty] private TagAutomationResult? _lastTagAutomationResult;

    // generic data
    [ObservableProperty] private string _date;
    [ObservableProperty] private string _comment;
    [ObservableProperty] private string _note;
    [ObservableProperty] private string _timeExpression = string.Empty;
    [ObservableProperty] private double _time;
    [ObservableProperty] private WorkPriorities _priority;
    [ObservableProperty] private ObservableCollection<WorkTag> _workTags = new();
    [ObservableProperty] private ObservableCollection<WorkTag> _availableTags = new();
    private readonly ObservableCollection<WorkItemExtraFieldValue> _extraFieldValues = new();
    private IReadOnlyList<WorkItemExtraField> _extraFields = Array.Empty<WorkItemExtraField>();

    public bool HasAvailableTags => !IsLocked && AvailableTags.Count > 0;
    public bool HasExtraFields => !IsImportedReadOnly && _extraFields.Count > 0;
    public bool CanOpenExtraFields => HasExtraFields && !IsLocked;
    public string ExtraFieldsButtonText => IsImportedReadOnly ? "查看附加信息" : "附加信息";
    public string ExtraFieldsSummary => _extraFields.Count == 0
        ? "暂无附加信息"
        : string.Join(Environment.NewLine, _extraFields
            .Where(extraField => !string.IsNullOrWhiteSpace(extraField.Value))
            .GroupBy(extraField => extraField.TagName)
            .Select(group => $"{group.Key}: {string.Join("；", group.Select(extraField => $"{extraField.Label}={extraField.Value}"))}"))
        switch
        {
            { Length: > 600 } summary => summary[..600] + "…",
            var summary => summary.Length == 0 ? "暂无附加信息" : summary,
        };
    public IReadOnlyCollection<WorkItemExtraFieldValue> ExtraFieldValues => _extraFieldValues;
    public IReadOnlyCollection<WorkItemExtraField> GetExtraFieldsSnapshot() => _extraFields;

    public bool IsImportedReadOnly => WorkItem?.IsReadOnly == true;

    public bool HasUploadedTracker => Extensions.Any(extension => extension.IsLocked);

    /// <summary>是否锁住 generic 编辑字段（任一 tracker 区已上传到远程即锁定）。</summary>
    [ObservableProperty] private bool _isLocked;

    public string LocalSaveStatusText => IsNewItem ? "未保存" : "本地已保存";

    public WorkItemUploadStatus UploadStatus
    {
        get
        {
            var states = UploadResults.Count > 0
                ? UploadResults.Select(result => result.State).ToArray()
                : Extensions.Select(extension => extension.UploadState).ToArray();
            return WorkItemUploadStatusResolver.Resolve(
                !IsNewItem,
                Extensions.Count,
                Extensions.Count(extension => extension.IsLocked),
                UploadResults.Any(result => !result.Success && !result.Skipped)
                    || states.Contains(TrackerUploadState.Failed),
                states.Contains(TrackerUploadState.Uncertain));
        }
    }

    public string UploadStatusText => IsImportedReadOnly
        ? "迁移记录（只读）"
        : WorkItemUploadStatusResolver.GetDisplayText(UploadStatus);

    public string StatusSummary => $"{LocalSaveStatusText} · {UploadStatusText}";

    public ObservableCollection<WorkTag> AllTags => _shareData.WorkTags;

    // todo: plm?

    private DbInterfaceBase? Db => (BaseApp.Instance as App)?.UseDb;

    public static WorkEditorViewModel FromWorkItem(WorkItem workItem)
    {
        return new WorkEditorViewModel(
            App.Instance.Services.GetRequiredService<DbShareData>(),
            App.Instance.Services.GetRequiredService<IWorkItemPersistenceCoordinator>(),
            App.Instance.Services.GetRequiredService<ITrackerUploadCoordinator>())
        {
            WorkId = workItem.Id,
            WorkItem = workItem,
            Date = workItem.CreateDate,
            Comment = workItem.Comment,
            Time = workItem.Time,
            Priority = workItem.Priority,
        };
    }

    public WorkEditorViewModel(
        DbShareData shareData,
        IWorkItemPersistenceCoordinator? persistence = null,
        ITrackerUploadCoordinator? uploadCoordinator = null,
        TrackerUiContributionRegistry? trackerRegistry = null,
        string? defaultTaskTitle = null,
        ITagAutomationCoordinator? tagAutomation = null,
        ScriptAutomationScheduler? scriptAutomationScheduler = null)
    {
        _shareData = shareData;
        _persistence = persistence
            ?? App.Instance.Services.GetRequiredService<IWorkItemPersistenceCoordinator>();
        _uploadCoordinator = uploadCoordinator
            ?? App.Instance.Services.GetRequiredService<ITrackerUploadCoordinator>();
        _tagAutomation = tagAutomation
            ?? App.Instance.Services.GetRequiredService<ITagAutomationCoordinator>();
        _scriptAutomationScheduler = scriptAutomationScheduler
            ?? (Application.Current as App)?.Services.GetService<ScriptAutomationScheduler>();
        _trackerRegistry = trackerRegistry ?? App.Instance.Services
            .GetRequiredService<TrackerUiContributionRegistry>();
        Date = TimeTools.Today();
        Comment = defaultTaskTitle ?? App.Instance.AppConfig.WorkSettings.DefaultTaskTitle;
        Note = string.Empty;
        Time = 0.0;
        Priority = WorkPriorities.P0;

        // 解析全部已注册 tracker，为每个创建一个编辑器扩展。
        var trackers = _trackerRegistry.Contributions;
        foreach (var t in trackers)
        {
            try
            {
                var ext = t.CreateEditorExtension(t.Instance.InstanceId);
                if (ext is not null && Extensions.All(existing => existing.Key != ext.Key))
                {
                    Extensions.Add(ext);
                    TrackerTabs.Add(new TrackerEditorTabItem(
                        ext,
                        GetTrackerTabHeader(t.Instance)));
                }
            }
            catch (Exception ex)
            {
                LastTagAutomationResult = new TagAutomationResult([
                    new TagAutomationInstanceResult(
                        new TrackerKey(t.PluginId, t.Instance.InstanceId),
                        false,
                        Array.Empty<string>(),
                        Array.Empty<TrackerTagDefaultConflict>(),
                        Array.Empty<TrackerTagDefaultInvalidTarget>(),
                        $"创建编辑扩展失败: {ex.Message}")
                ]);
            }
        }

        WorkTags.CollectionChanged += (_, _) =>
        {
            if (_syncing_tags)
                return;
            UpdateAvailableTags();
        };
    }

    public void RefreshTrackerTabHeaders()
    {
        foreach (var tab in TrackerTabs)
        {
            var contribution = _trackerRegistry.Contributions.FirstOrDefault(item =>
                item.PluginId == tab.Extension.Key.PluginId
                && item.Instance.InstanceId == tab.Extension.InstanceId);
            if (contribution is not null)
                tab.Header = GetTrackerTabHeader(contribution.Instance);
        }
    }

    private static string GetTrackerTabHeader(ITrackerInstance instance)
        => string.IsNullOrWhiteSpace(instance.DisplayName)
            ? instance.InstanceId
            : instance.DisplayName;

    public bool IsDateChanged => WorkItem is not null && WorkItem.CreateDate != Date;

    public bool IsNewItem => WorkItem is null;

    // public int WorkId => WorkItem?.Id ?? 0;
    [ObservableProperty] private int _workId;

    public void SetEditorScriptActions(IEnumerable<WorkEditorScriptMenuItem> actions)
    {
        EditorScriptActions.Clear();
        foreach (var action in actions)
            EditorScriptActions.Add(action);
    }

    public void Save(out bool created)
    {
        var db = Db!;
        var result = _persistence.Save(db, new WorkItemSaveRequest(
            WorkItem, Date, Comment, Note, Time, Priority, WorkTags,
            _extraFieldValues, Extensions));
        created = result.Created;
        if (!result.Success || result.WorkItem is null)
        {
            EventDispatcher.ShowToast(result.Error ?? "保存失败了！");
            return;
        }

        WorkItem = result.WorkItem;
        WorkId = WorkItem.Id;
        if (created)
        {
            TriggerScriptAutomation(ScriptAutomationTriggerKind.WorkItemCreated, WorkItem);
            foreach (var pending in _pendingTagAutomation)
                TriggerScriptAutomation(ScriptAutomationTriggerKind.TagAdded, WorkItem, pending.Tag, pending.Source, pending.Sequence);
            _pendingTagAutomation.Clear();
        }
        else
        {
            TriggerScriptAutomation(ScriptAutomationTriggerKind.WorkItemSaved, WorkItem);
        }
        NotifyStatusChanged();
    }

    private void TriggerScriptAutomation(
        ScriptAutomationTriggerKind trigger,
        WorkItem item,
        WorkTag? tag = null,
        TagAddSource? tagSource = null,
        int? sequence = null)
    {
        var eventData = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workItemId"] = item.Id.ToString(CultureInfo.InvariantCulture),
            ["date"] = item.CreateDate,
            ["comment"] = item.Comment,
            ["time"] = item.Time.ToString(CultureInfo.InvariantCulture),
            ["priority"] = item.Priority.ToString(),
        };
        if (tag is not null)
        {
            eventData["tagId"] = tag.Id.ToString(CultureInfo.InvariantCulture);
            eventData["tagName"] = tag.Name;
            eventData["tagLevel"] = tag.Level.ToString();
            eventData["tagSource"] = tagSource?.ToString() ?? string.Empty;
            eventData["sequence"] = (sequence ?? 0).ToString(CultureInfo.InvariantCulture);
        }
        _ = _scriptAutomationScheduler?.TriggerAsync(trigger, eventData);
    }

    public void Delete()
    {
        // remove from db
        if (WorkItem is { Id: > 0 })
            Db!.DeleteWorkItem(WorkItem!);
        _pendingTagAutomation.Clear();
        WorkItem = null;
    }

    public bool CanDelete()
    {
        return Db is not null;
    }

    [RelayCommand]
    private void QuickDate(string what)
    {
        Date = what switch
        {
            "0" => TimeTools.Today(),
            "+1" => TimeTools.Tomorrow(),
            "-1" => TimeTools.Yestoday(),
            _ => Date
        };
    }

    [RelayCommand]
    private async Task EditExtraFields()
    {
        if (!CanOpenExtraFields)
            return;
        RefreshExtraFieldsSnapshot();
        var fields = _extraFields;
        if (fields.Count == 0)
            return;
        var dialog = new WorkItemExtraFieldsViewModel(
            Db!, WorkId, fields, isReadOnly: IsImportedReadOnly);
        var result = await OverlayDialog.ShowCustomModal<bool>(dialog, options: new OverlayDialogOptions
        {
            Title = dialog.Title,
            CanDragMove = false,
            CanResize = true,
            CanLightDismiss = false,
            Mode = DialogMode.None,
        });
        if (!result)
            return;

        var values = dialog.GetValues();
        if (WorkItem is { Id: > 0 } item && !Db!.SaveWorkItemExtraFieldValues(item.Id, values))
        {
            EventDispatcher.ShowToast("保存附加信息失败。");
            return;
        }

        _extraFieldValues.Clear();
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value.Value)))
            _extraFieldValues.Add(value with { WorkItemId = WorkId });
        RefreshExtraFieldsSnapshot();
    }

    private List<WorkItemExtraField> BuildExtraFields()
    {
        var db = Db;
        if (db is null)
            return [];
        if (WorkItem is { Id: > 0 } item)
            return db.GetWorkItemExtraFields(item).ToList();

        var values = _extraFieldValues.ToDictionary(value => value.FieldId, value => value.Value);
        var fields = new List<WorkItemExtraField>();
        foreach (var tag in WorkTags)
        {
            foreach (var definition in db.GetTagExtraFieldDefinitions(tag.Id))
            {
                fields.Add(new WorkItemExtraField
                {
                    FieldId = definition.FieldId,
                    FieldKey = definition.FieldKey,
                    TagId = tag.Id,
                    TagName = tag.Name,
                    Label = definition.Label,
                    Type = definition.Type,
                    Description = definition.Description,
                    SortOrder = definition.SortOrder,
                    Options = definition.Options,
                    Enabled = definition.Enabled,
                    Value = values.GetValueOrDefault(definition.FieldId, string.Empty),
                });
            }
        }
        return fields
            .OrderBy(field => field.TagId)
            .ThenBy(field => field.SortOrder)
            .ThenBy(field => field.FieldKey)
            .ToList();
    }

    private void SyncExtraFields()
    {
        _extraFieldValues.Clear();
        if (WorkItem is { Id: > 0 } item && Db is not null)
        {
            foreach (var field in Db.GetWorkItemExtraFields(item)
                         .Where(field => !string.IsNullOrWhiteSpace(field.Value)))
            {
                _extraFieldValues.Add(new WorkItemExtraFieldValue
                {
                    WorkItemId = item.Id,
                    FieldId = field.FieldId,
                    Value = field.Value,
                });
            }
        }
        RefreshExtraFieldsSnapshot();
    }

    private void RefreshExtraFieldsSnapshot()
    {
        _extraFields = BuildExtraFields();
        OnPropertyChanged(nameof(HasExtraFields));
        OnPropertyChanged(nameof(CanOpenExtraFields));
        OnPropertyChanged(nameof(ExtraFieldsSummary));
        OnPropertyChanged(nameof(ExtraFieldsButtonText));
    }

    public void SyncAll()
    {
        SyncNote();
        SyncTags();
        SyncExtraFields();
        foreach (var ext in Extensions)
            ext.Load(WorkItem);
        RecomputeIsLocked();
    }

    public void SyncFromBatch(
        Dictionary<int, string> notesById,
        Dictionary<int, ICollection<WorkTag>> tagsById,
        IReadOnlyDictionary<TrackerKey, IDictionary<int, object?>?>? bindingsByTracker)
    {
        if (WorkItem is not { Id: > 0 })
            return;

        var id = WorkItem.Id;

        if (notesById.TryGetValue(id, out var note))
            Note = note;
        else
            Note = string.Empty;

        // 无论该工作项是否已有标签，都要重建 WorkTags 并刷新可选标签列表，
        // 否则无标签项的 AvailableTags 永远是空的（添加标签列表列不出任何标签）。
        _syncing_tags = true;
        WorkTags.Clear();
        if (tagsById.TryGetValue(id, out var tags))
        {
            foreach (var tag in tags)
                WorkTags.Add(tag);
        }
        _syncing_tags = false;
        UpdateAvailableTags();
        SyncExtraFields();

        // 每个 tracker 扩展从 map 中按 InstanceId 取自己的 per-work 绑定
        foreach (var ext in Extensions)
        {
            object? binding = null;
            if (bindingsByTracker != null
                && bindingsByTracker.TryGetValue(ext.Key, out var perTracker)
                && perTracker != null
                && perTracker.TryGetValue(id, out var bv))
            {
                binding = bv;
            }
            ext.Load(WorkItem, binding);
        }
        RecomputeIsLocked();
    }

    private void SyncNote()
    {
        if (WorkItem is { Id: > 0 })
        {
            Note = Db!.WorkGetNote(WorkItem!) ?? string.Empty;
        }
    }

    private bool _syncing_tags;

    public void SyncTags()
    {
        _syncing_tags = true;
        if (WorkItem is { Id: > 0 })
        {
            WorkTags.Clear();
            var tags = Db!.GetWorkItemTags(WorkItem);
            foreach (var tag in tags)
            {
                WorkTags.Add(tag);
            }
        }

        UpdateAvailableTags();
        SyncExtraFields();
        _syncing_tags = false;
    }

    public WorkEditorViewModel Clone()
    {
        var result = new WorkEditorViewModel(
            _shareData,
            _persistence,
            _uploadCoordinator,
            _trackerRegistry,
            Comment,
            _tagAutomation)
        {
            WorkItem = null,
            Date = Date,
            Note = Note,
            Comment = Comment,
            Time = 0.0,
            Priority = Priority,
        };
        var targetExtensions = result.Extensions.ToDictionary(extension => extension.Key);
        foreach (var extension in Extensions)
        {
            if (targetExtensions.TryGetValue(extension.Key, out var target))
                extension.CloneTo(target);
        }
        result.AddTags(WorkTags, TagAddSource.Duplicate);
        foreach (var value in _extraFieldValues)
            result._extraFieldValues.Add(value with { WorkItemId = 0 });
        result.RefreshExtraFieldsSnapshot();

        return result;
    }

    public bool CanClone()
    {
        return WorkItem is { Id: > 0 }; // 克隆的前提是这个事件已经保存过了
    }

    public bool CanUpload()
        => !IsImportedReadOnly && Extensions.Any(extension => !extension.IsLocked);

    public void SetRecentTagIds(IEnumerable<int> tagIds)
    {
        _recentTagIds = tagIds.Distinct().ToArray();
        UpdateAvailableTags();
    }

    [RelayCommand]
    private void QuickTime(string value)
    {
        Time = value switch
        {
            "15m" => 0.25,
            "30m" => 0.5,
            "1h" => 1.0,
            "2h" => 2.0,
            "4h" => 4.0,
            "clear" => 0.0,
            _ => Time,
        };
    }

    [RelayCommand]
    private void ApplyTimeExpression()
    {
        if (!TimeExpressionParser.TryParse(TimeExpression, out var hours, out var error))
        {
            EventDispatcher.ShowToast(error);
            return;
        }

        Time = hours;
        TimeExpression = string.Empty;
    }

    [RelayCommand]
    private void AddTag(WorkTag tag)
        => AddTags([tag], TagAddSource.User);

    public void AddTags(IEnumerable<WorkTag> tags, TagAddSource source)
    {
        if (IsLocked)
            return;
        var sequence = 0;
        foreach (var tag in tags)
        {
            if (WorkTags.Any(existing => existing.Id == tag.Id))
                continue;
            _syncing_tags = true;
            if (WorkItem is { Id: > 0 })
            {
                if (Db is null || !Db.WorkItemAddTag(WorkItem, tag))
                {
                    _syncing_tags = false;
                    LastTagAutomationResult = new TagAutomationResult([
                        new TagAutomationInstanceResult(
                            new TrackerKey("core", "local"),
                            false,
                            Array.Empty<string>(),
                            Array.Empty<TrackerTagDefaultConflict>(),
                            Array.Empty<TrackerTagDefaultInvalidTarget>(),
                            "标签保存失败")
                    ]);
                    continue;
                }
            }
            WorkTags.Add(tag);
            _syncing_tags = false;
            var tagSequence = sequence++;
            LastTagAutomationResult = _tagAutomation.TagAdded(
                WorkItem,
                tag,
                new TagAutomationContext(source, tagSequence),
                Extensions);
            if (WorkItem is { Id: > 0 } persistedItem)
                TriggerScriptAutomation(ScriptAutomationTriggerKind.TagAdded, persistedItem, tag, source, tagSequence);
            else
                _pendingTagAutomation.Add((tag, source, tagSequence));
        }
        UpdateAvailableTags();
        RefreshExtraFieldsSnapshot();
    }

    [RelayCommand]
    private void DelTag(WorkTag tag)
    {
        if (IsLocked)
            return;
        _syncing_tags = true;
        if (WorkItem is { Id: > 0 })
        {
            if (tag.Level == TagLevels.Primary)
            {
                Db!.WorkItemCleanTags(WorkItem);
                WorkTags.Clear();
            }
            else
            {
                Db!.WorkItemRemoveTag(WorkItem, tag);
                WorkTags.Remove(tag);
            }
        }
        else
        {
            if (tag.Level == TagLevels.Primary)
                WorkTags.Clear();
            else
                WorkTags.Remove(tag);
        }
        _syncing_tags = false;
        UpdateAvailableTags();
        RefreshExtraFieldsSnapshot();
    }

    private void UpdateAvailableTags()
    {
        AvailableTags.Clear();
        if (WorkTags.Count > 0)
        {
            // show only secondary tags
            foreach (var tag in OrderByRecent(AllTags.Where(x => x is { Level: TagLevels.Secondary, Disabled: false })))
            {
                if (!WorkTags.Contains(tag))
                    AvailableTags.Add(tag);
            }
        }
        else
        {
            // show only primary tags
            foreach (var tag in OrderByRecent(AllTags.Where(x => x is { Level: TagLevels.Primary, Disabled: false })))
            {
                AvailableTags.Add(tag);
            }
        }
        OnPropertyChanged(nameof(HasAvailableTags));
        OnPropertyChanged(nameof(IsImportedReadOnly));
        OnPropertyChanged(nameof(HasUploadedTracker));
    }

    private IEnumerable<WorkTag> OrderByRecent(IEnumerable<WorkTag> tags)
    {
        var order = _recentTagIds
            .Select((id, index) => (id, index))
            .ToDictionary(item => item.id, item => item.index);
        return tags.OrderBy(tag => order.TryGetValue(tag.Id, out var index) ? index : int.MaxValue);
    }

    /// <summary>上传所有 tracker 扩展，聚合结果。任一失败即整体失败。</summary>
    public async Task<(bool, string?)> Upload()
    {
        if (WorkItem is null)
            return (false, "工作项尚未保存");
        var result = await _uploadCoordinator.UploadAsync(WorkItem, Extensions);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            UploadResults.Clear();
            foreach (var uploadResult in result.Results)
                UploadResults.Add(uploadResult);
            OnPropertyChanged(nameof(HasUploadResults));
            RecomputeIsLocked();
            NotifyStatusChanged();
        });
        return (result.Success, result.Error);
    }

    private void RecomputeIsLocked()
    {
        IsLocked = IsImportedReadOnly || Extensions.Any(e => e.IsLocked);
        NotifyStatusChanged();
    }

    private void NotifyStatusChanged()
    {
        OnPropertyChanged(nameof(LocalSaveStatusText));
        OnPropertyChanged(nameof(UploadStatus));
        OnPropertyChanged(nameof(UploadStatusText));
        OnPropertyChanged(nameof(StatusSummary));
        OnPropertyChanged(nameof(HasAvailableTags));
        OnPropertyChanged(nameof(CanOpenExtraFields));
        OnPropertyChanged(nameof(ExtraFieldsButtonText));
    }
}
