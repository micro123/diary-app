using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Diary.App.Models;
using Diary.App.ViewModels.Dialogs;
using Diary.Core.Constants;
using Diary.Core.Data.App;
using Diary.Core.Data.Base;
using Diary.Core.Utils;
using Diary.GUIBase.Events;
using Diary.GUIBase.Utils;
using Diary.GUIBase.ViewModels;
using Diary.PluginBase;
using Diary.Script.Runtime;
using Diary.ScriptBase;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ursa.Controls;

namespace Diary.App.ViewModels;


public sealed class DayMenuItem
{
    public required string Header { get; set; }
    public bool Enabled { get; set; } = false;
    public ICommand? Command { get; set; } = null;
    public ObservableCollection<DayMenuItem> Children { get; } = new();

    public static DayMenuItem Separator { get; } = new DayMenuItem() { Header = "-" };
}


[DiAutoRegister(singleton: true)]
public partial class DiaryEditorViewModel : ViewModelBase
{
    private readonly ILogger _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IScriptCatalog _scriptCatalog;
    private readonly IScriptManager _scriptManager;

    [ObservableProperty]
    private DateTime _selectedDate;
    [ObservableProperty]
    private DateTime _currentDate;

    private string CurrentDateString => TimeTools.FormatDateTime(CurrentDate);
    private bool _creating;
    private bool _sortingDailyWorks;
    private bool _loadingWorks;
    private bool _restoringSelectedDate;

    [ObservableProperty] private ObservableCollection<Template> _templates = new();
    [ObservableProperty] private bool _canUseTemplates = false;

    private bool IsSurveyorEnabled => App.Instance.AppConfig.SurveySettings.IsServerEnabled;

    [RelayCommand]
    private void NewWorkItem() => TryCreateNewWorkItem();

    private bool TryCreateNewWorkItem()
    {
        if (!PrepareSelectedWorkForReplacement())
            return false;

        _creating = true;
        try
        {
            SelectedWork = null;
            var newWork = new WorkEditorViewModel(_serviceProvider.GetRequiredService<DbShareData>())
            {
                Date = CurrentDateString,
            };
            newWork.SetRecentTagIds(GetRecentTagIds());
            ConfigureEditorScriptActions(newWork);
            SelectedWork = newWork;
            newWork.SyncAll();
            return true;
        }
        finally
        {
            _creating = false;
        }
    }

    [RelayCommand]
    private void NewWithTemplate(Template template)
    {
        if (!TryCreateNewWorkItem() || SelectedWork is null)
            return;
        // apply template
        if (!string.IsNullOrWhiteSpace(template.DefaultTitle))
            SelectedWork.Comment = template.DefaultTitle;
        if (template.DefaultTime > 0)
            SelectedWork.Time = template.DefaultTime;
        var tags = template.DefaultWorkTags
            .Select(tagId => SelectedWork.AllTags.FirstOrDefault(tag => tag.Id == tagId))
            .Where(tag => tag is not null)
            .Cast<WorkTag>();
        SelectedWork.AddTags(tags, TagAddSource.Template);
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void SaveWorkItem() => TrySaveWorkItem();

    private bool TrySaveWorkItem(bool navigateToSavedDate = true)
    {
        var selected = SelectedWork;
        if (selected is null)
            return true;

        var newDate = selected.IsDateChanged;
        if (!selected.Save(out var created))
            return false;

        ConfigureEditorScriptActions(selected);
        RememberRecentPrimaryTags(selected.WorkTags);
        if (created && CurrentDateString == selected.Date)
        {
            DailyWorks.Add(selected);
        }

        if (navigateToSavedDate && (newDate || created))
        {
            var date = selected.Date;
            var id = selected.WorkId;
            GoDate(TimeTools.FromFormatedDate(date));
            SelectWorkById(id);
        }
        SortDailyWorks();

        UpdateTimeInfos();
        DuplicateWorkItemCommand.NotifyCanExecuteChanged();
        return true;
    }

    private bool PrepareSelectedWorkForReplacement()
    {
        if (SelectedWork is not { } selected)
            return true;
        if (selected.ShouldPersistBeforeReplacement)
            return TrySaveWorkItem();

        _creating = true;
        try
        {
            SelectedWork = null;
            return true;
        }
        finally
        {
            _creating = false;
        }
    }

    private void SelectWorkById(int id)
    {
        Debug.Assert(id != 0);
        var item = DailyWorks.FirstOrDefault(x => x.WorkId == id);
        if (item is not null)
            SelectedWork = item;
    }

    private void SortDailyWorks()
    {
        _sortingDailyWorks = true;
        try
        {
            WorkItemOrdering.SortByPriorityAndId(
                DailyWorks,
                work => work.Priority,
                work => work.WorkId);
        }
        finally
        {
            _sortingDailyWorks = false;
        }
    }

    private bool CanSave => SelectedWork is { IsLocked: false };

    [RelayCommand]
    private async Task CopyPreviousDay()
    {
        if (App.Instance.UseDb is not { } db)
            return;
        if (!PrepareSelectedWorkForReplacement())
            return;

        var previousDate = TimeTools.FormatDateTime(CurrentDate.AddDays(-1));
        var sourceItems = db.GetWorkItemByDate(previousDate).ToArray();
        if (sourceItems.Length == 0)
        {
            EventDispatcher.Notify("没有可复制的记录", $"{previousDate} 没有已保存的工作记录。");
            return;
        }

        var total = sourceItems.Sum(item => item.Time);
        if (!await EventDispatcher.Confirm(
                "复制昨天的记录",
                $"将复制 {sourceItems.Length} 条记录，共 {total:0.##} 小时到 {CurrentDateString}。只复制本地字段和标签，不复制远程 Tracker 绑定。继续吗？"))
            return;

        var notesById = db.GetWorkNotesByDate(previousDate);
        var tagsById = db.GetWorkTagsByDate(previousDate);
        var copied = 0;
        foreach (var sourceItem in sourceItems)
        {
            if (CopyWorkItemToCurrentDate(sourceItem, notesById, tagsById))
                ++copied;
        }

        var lastCopied = DailyWorks.LastOrDefault();
        SortDailyWorks();
        SelectedWork = lastCopied;
        UpdateTimeInfos();
        EventDispatcher.Notify("复制完成", $"已复制 {copied}/{sourceItems.Length} 条记录。");
    }

    [RelayCommand]
    private async Task CopyRecentWorkItem()
    {
        if (App.Instance.UseDb is not { } db)
            return;
        if (!PrepareSelectedWorkForReplacement())
            return;

        var endDate = CurrentDate.Date.AddDays(-1);
        var startDate = endDate.AddDays(-365);
        var sourceItem = db.GetWorkItemByDateRange(
                TimeTools.FormatDateTime(startDate),
                TimeTools.FormatDateTime(endDate))
            .OrderByDescending(item => item.CreateDate)
            .ThenByDescending(item => item.Id)
            .FirstOrDefault();
        if (sourceItem is null)
        {
            EventDispatcher.Notify("没有可复制的记录", "当前日期之前近一年内没有已保存的工作记录。");
            return;
        }

        if (!await EventDispatcher.Confirm(
                "复制最近记录",
                $"将复制 {sourceItem.CreateDate} 的“{sourceItem.Comment}”（{sourceItem.Time:0.##} 小时）到 {CurrentDateString}。只复制本地字段和标签，不复制远程 Tracker 绑定。继续吗？"))
            return;

        var notesById = db.GetWorkNotesByWorkItemIds([sourceItem.Id]);
        var tagsById = db.GetWorkTagsByWorkItemIds([sourceItem.Id]);
        if (!CopyWorkItemToCurrentDate(sourceItem, notesById, tagsById))
        {
            EventDispatcher.Notify("复制失败", "最近记录未能保存到当前日期。");
            return;
        }

        var copiedWork = DailyWorks.LastOrDefault();
        SortDailyWorks();
        SelectedWork = copiedWork;
        UpdateTimeInfos();
        EventDispatcher.Notify("复制完成", $"已将 {sourceItem.CreateDate} 的最近记录复制到 {CurrentDateString}。");
    }

    [RelayCommand]
    private async Task CopyWholeDay()
    {
        if (App.Instance.UseDb is not { } db)
            return;
        if (!PrepareSelectedWorkForReplacement())
            return;

        var selection = await OverlayDialog.ShowCustomModal<CopyDaySelection>(
            new CopyDayViewModel(CurrentDate),
            options: new OverlayDialogOptions
            {
                CanDragMove = false,
                CanResize = false,
                CanLightDismiss = false,
                IsCloseButtonVisible = false,
            });
        if (selection is null)
            return;

        var sourceDate = TimeTools.FormatDateTime(selection.SourceDate);
        var sourceItems = db.GetWorkItemByDate(sourceDate).ToArray();
        if (sourceItems.Length == 0)
        {
            EventDispatcher.Notify("没有可复制的记录", $"{sourceDate} 没有已保存的工作记录。");
            return;
        }

        var total = sourceItems.Sum(item => item.Time);
        if (!await EventDispatcher.Confirm(
                "确认复制整天记录",
                $"将复制 {sourceItems.Length} 条记录，共 {total:0.##} 小时，从 {sourceDate} 到 {CurrentDateString}。只复制本地字段和标签，不复制远程 Tracker 绑定。继续吗？"))
            return;

        var notesById = db.GetWorkNotesByDate(sourceDate);
        var tagsById = db.GetWorkTagsByDate(sourceDate);
        var copied = 0;
        foreach (var sourceItem in sourceItems)
        {
            if (CopyWorkItemToCurrentDate(sourceItem, notesById, tagsById))
                ++copied;
        }

        var lastCopied = DailyWorks.LastOrDefault();
        SortDailyWorks();
        SelectedWork = lastCopied;
        UpdateTimeInfos();
        EventDispatcher.Notify("复制完成", $"已从 {sourceDate} 复制 {copied}/{sourceItems.Length} 条记录到 {CurrentDateString}。");
    }

    private bool CopyWorkItemToCurrentDate(
        WorkItem sourceItem,
        Dictionary<int, string> notesById,
        Dictionary<int, ICollection<WorkTag>> tagsById)
    {
        var source = WorkEditorViewModel.FromWorkItem(sourceItem);
        source.SyncFromBatch(notesById, tagsById, null);
        var copy = source.Clone(includeTrackerBindings: false);
        copy.Date = CurrentDateString;
        copy.Save(out var created);
        if (!created)
            return false;

        ConfigureEditorScriptActions(copy);
        DailyWorks.Add(copy);
        RememberRecentPrimaryTags(copy.WorkTags);
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanDuplicate))]
    private void DuplicateWorkItem()
    {
        // duplicate but not save
        var item = SelectedWork!.Clone();
        SelectedWork = null;
        _creating = true;
        SelectedWork = item;
        ConfigureEditorScriptActions(SelectedWork);
        _creating = false;
    }

    private bool CanDuplicate => SelectedWork != null && SelectedWork.CanClone();
    private bool _deleting;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteWorkItem()
    {
        var selected = SelectedWork!;
        var message = selected.IsImportedReadOnly
            ? "该记录是迁移导入的只读统计记录。删除只会移除本地记录，确认继续吗？"
            : selected.IsLocked
                ? "该记录已经上传到外部系统。删除只会移除本地记录，远程工时不会被删除。确认继续吗？"
            : "该记录尚未产生远程上传。确认删除这条工作记录吗？此操作不可恢复。";
        if (!await EventDispatcher.Confirm("删除工作记录", message))
            return;

        _deleting = true;
        try
        {
            if (!selected.Delete())
                return;
            DailyWorks.Remove(selected);
            SelectedWork = DailyWorks.FirstOrDefault();
        }
        finally
        {
            _deleting = false;
        }
    }
    private bool CanDelete => SelectedWork != null && SelectedWork.CanDelete();

    [RelayCommand(CanExecute = nameof(CanUpload))]
    private async Task UploadTime()
    {
        SaveWorkItem();
        var (result, msg) = await SelectedWork!.Upload();
        var toast = SelectedWork.UploadStatus == WorkItemUploadStatus.Uncertain
            ? "同步结果待确认：请先在远程系统核对，避免重复同步。"
            : result
                ? "同步成功"
                : $"同步失败：{msg}";
        ToastManager?.Show(toast);

        // hack: update button state
        Dispatcher.UIThread.Post(() => UploadTimeCommand.NotifyCanExecuteChanged());
        DeleteWorkItemCommand.NotifyCanExecuteChanged();
        UploadAllCommand.NotifyCanExecuteChanged();
    }

    private bool CanUpload => SelectedWork?.CanUpload() == true;

    private IEnumerable<int> GetRecentTagIds()
        => RecentPrimaryTagHistory.Merge(
            DailyWorks
                .SelectMany(work => work.WorkTags)
                .Select(tag => tag.Id),
            App.Instance.AppConfig.ViewSettings.RecentPrimaryTagIds);

    private void RememberRecentPrimaryTags(IEnumerable<WorkTag> tags)
    {
        var merged = RecentPrimaryTagHistory.Merge(
            tags.Where(tag => tag.Level == TagLevels.Primary).Select(tag => tag.Id),
            App.Instance.AppConfig.ViewSettings.RecentPrimaryTagIds);
        var current = App.Instance.AppConfig.ViewSettings.RecentPrimaryTagIds;
        if (current.SequenceEqual(merged))
            return;

        App.Instance.AppConfig.ViewSettings.RecentPrimaryTagIds = merged.ToList();
        EasySaveLoad.Save(App.Instance.AppConfig);
    }

    [RelayCommand]
    private void RetryDatabaseConnection()
    {
        var app = (App)App.Instance;
        if (app.TryReconnectDatabase(out var message))
        {
            FetchWorks();
            UpdateTimeInfos();
            EventDispatcher.ShowToast("数据库已恢复连接");
            return;
        }

        EventDispatcher.Notify(
            "数据库仍不可用",
            $"{message}\n\n本地记录不会因连接失败被删除。请检查数据库设置，或导出诊断日志后再联系维护者。\n\n可恢复操作：重试连接、打开数据库设置、导出诊断日志。");
    }

    [RelayCommand]
    private void OpenDatabaseSettings()
        => EventDispatcher.RunCommand(CommandNames.ShowDbSettings);

    [RelayCommand]
    private void ExportDiagnostics()
    {
        var path = _serviceProvider.GetRequiredService<DiagnosticLogExportService>().Export();
        EventDispatcher.Notify(
            path is null ? "暂无诊断日志" : "诊断日志已导出",
            path is null ? "当前没有可导出的应用日志。" : path);
    }

    [RelayCommand(CanExecute = nameof(CanUploadAll))]
    private async Task UploadAll()
    {
        if (SelectedWork is { IsNewItem: false })
        {
            SaveWorkItem();
        }

        var preview = new BatchUploadPreviewViewModel(DailyWorks);
        var selection = await OverlayDialog.ShowCustomModal<BatchUploadSelection>(
            preview,
            options: new OverlayDialogOptions
            {
                CanDragMove = false,
                CanResize = true,
                CanLightDismiss = false,
                IsCloseButtonVisible = false,
            });
        if (selection is null || selection.Items.Count == 0)
            return;

        var failed = await UploadWorksAsync(selection.Items);
        if (failed.Count > 0 && await EventDispatcher.Confirm(
                "部分同步失败",
                $"有 {failed.Count} 条记录同步失败。是否仅重试失败项？结果待确认的记录不会自动重试。"))
        {
            await UploadWorksAsync(failed);
        }

        UpdateTimeInfos();
        UploadAllCommand.NotifyCanExecuteChanged();
        DeleteWorkItemCommand.NotifyCanExecuteChanged();
    }

    private async Task<IReadOnlyList<WorkEditorViewModel>> UploadWorksAsync(
        IReadOnlyCollection<WorkEditorViewModel> works)
    {
        var sb = new StringBuilder();
        var success = 0;
        var failed = 0;
        var uncertain = 0;
        var failedWorks = new List<WorkEditorViewModel>();

        foreach (var work in works)
        {
            var (result, message) = await work.Upload();
            if (result)
            {
                ++success;
                sb.AppendLine($"#{work.WorkId} 同步成功");
            }
            else
            {
                if (work.UploadStatus == WorkItemUploadStatus.Uncertain)
                {
                    ++uncertain;
                    sb.AppendLine($"#{work.WorkId} 结果待确认: {message}");
                }
                else
                {
                    ++failed;
                    failedWorks.Add(work);
                    sb.AppendLine($"#{work.WorkId} 同步失败: {message}");
                }
            }
        }

        var summary = uncertain == 0
            ? $"同步结果: 成功 {success}，失败 {failed}"
            : $"同步结果: 成功 {success}，失败 {failed}，结果待确认 {uncertain}";
        EventDispatcher.Notify(summary, sb.ToString());
        return failedWorks;
    }

    private bool CanUploadAll => TotalTime != 0 && UploadedTime < TotalTime;

    [RelayCommand]
    private void SelectToday()
    {
        GoDate(DateTime.Today);
    }


    private void GoDate(DateTime date)
    {
        SelectedDate = date;
    }

    partial void OnSelectedDateChanged(DateTime value)
    {
        if (_restoringSelectedDate)
            return;

        var previousDate = CurrentDate;
        if (SelectedWork is { ShouldPersistBeforeReplacement: true }
            && !TrySaveWorkItem(navigateToSavedDate: false))
        {
            _restoringSelectedDate = true;
            try
            {
                SelectedDate = previousDate;
            }
            finally
            {
                _restoringSelectedDate = false;
            }
            return;
        }

        _currentDate = value;
        _logger.LogDebug("date changed to {Date}", _currentDate);
        FetchWorks();
    }

    partial void OnSelectedWorkChanging(WorkEditorViewModel? value) // 指 即将 从 当前值 更改为 value
    {
        if (!_deleting && !_creating && !_sortingDailyWorks && !_loadingWorks
            && SelectedWork is { ShouldPersistBeforeReplacement: true })
            TrySaveWorkItem();
        UpdateTimeInfos();
    }

    public DiaryEditorViewModel(
        ILogger logger,
        IServiceProvider serviceProvider,
        IScriptCatalog scriptCatalog,
        IScriptManager scriptManager)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _scriptCatalog = scriptCatalog;
        _scriptManager = scriptManager;
        SelectedDate = DateTime.Today;

        Messenger.Register<DbChangedEvent>(this, (r, m) =>
        {
            if ((m.Value & DbChangedEvent.ShareData) != 0)
                Dispatcher.UIThread.Post(FetchWorks);
        });

        Messenger.Register<TemplateChangedEvent>(this, (r, m) =>
        {
            Dispatcher.UIThread.Post(FetchTemplates);
        });

        Messenger.Register<OpenWorkItemEvent>(this, (r, m) =>
        {
            Dispatcher.UIThread.Post(() => OpenWorkItem(m.Value.Date, m.Value.WorkItemId));
        });

        FetchTemplates();
    }

    private void OpenWorkItem(string date, int workItemId)
    {
        try
        {
            GoDate(TimeTools.FromFormatedDate(date));
            SelectWorkById(workItemId);
            if (SelectedWork?.WorkId != workItemId)
                EventDispatcher.ShowToast("未找到要打开的事项，数据可能已变更");
        }
        catch (FormatException)
        {
            _logger.LogWarning("Cannot open work item {WorkItemId}: invalid date", workItemId);
            EventDispatcher.ShowToast("无法定位到该事项");
        }
    }

    private void FetchTemplates()
    {
        Templates.Clear();
        foreach (var template in TemplateManager.Instance.Templates)
        {
            Templates.Add(template);
        }

        CanUseTemplates = Templates.Count > 0;
    }

    public void RefreshTrackerTabHeaders()
    {
        foreach (var work in DailyWorks)
            work.RefreshTrackerTabHeaders();
        if (SelectedWork is not null && !DailyWorks.Contains(SelectedWork))
            SelectedWork.RefreshTrackerTabHeaders();
    }

    private void FetchWorks()
    {
        _loadingWorks = true;
        try
        {
            DailyWorks.Clear();
            SelectedWork = null;
            var db = App.Instance.UseDb;
            if (db != null)
            {
                var dbItems = db.GetWorkItemByDate(CurrentDateString);
                if (dbItems.Count > 0)
                {
                    var workItemIds = dbItems.Select(item => item.Id).ToArray();
                    var notesById = db.GetWorkNotesByWorkItemIds(workItemIds);
                    var tagsById = db.GetWorkTagsByWorkItemIds(workItemIds);
                    var extraFieldsById = db.GetWorkItemExtraFieldsByWorkItemIds(workItemIds);
                    var trackers = App.Instance.Services
                        .GetRequiredService<TrackerUiContributionRegistry>().Contributions;
                    var bindingsByTracker = new Dictionary<TrackerKey, IDictionary<int, object?>?>();
                    foreach (var t in trackers)
                    {
                        var key = new TrackerKey(t.PluginId, t.Instance.InstanceId);
                        bindingsByTracker[key] = t.Instance.LoadBindingsByDate(
                            CurrentDateString, workItemIds);
                    }

                    foreach (var item in dbItems)
                    {
                        var x = WorkEditorViewModel.FromWorkItem(item);
                        ConfigureEditorScriptActions(x);
                        x.SyncFromBatch(notesById, tagsById, bindingsByTracker, extraFieldsById);
                        DailyWorks.Add(x);
                    }
                    SortDailyWorks();
                }
            }
            else
            {
                _logger.LogWarning("db is null");
            }

            if (DailyWorks.Count > 0)
            {
                SelectedWork = DailyWorks[0];
            }
        }
        finally
        {
            _loadingWorks = false;
        }
    }

    private void UpdateTimeInfos()
    {
        double sum = 0.0, uploaded = 0.0;
        foreach (var work in DailyWorks)
        {
            sum += work.Time;
            if (work.HasUploadedTracker)
                uploaded += work.Time;
        }

        TotalTime = sum;
        UploadedTime = uploaded;
    }

    [ObservableProperty] private ObservableCollection<DayMenuItem> _quickMenuItems = new();

    public enum CalendarWhat
    {
        None,
        Day,
        Month,
        Year,
    }

    public void ShowCalendarContextMenu(DateTime selectDate, CalendarWhat what)
    {
        switch (what)
        {
            case CalendarWhat.Day:
                FillDayMenus(selectDate);
                break;
            case CalendarWhat.Month:
                FillMonthMenus(selectDate);
                break;
            case CalendarWhat.Year:
                FillYearMenus(selectDate);
                break;
        }
    }

    private void AddMenuHeader(string text) =>
        QuickMenuItems.Add(new DayMenuItem { Header = text });

    private void AddMenuSeparator() =>
        QuickMenuItems.Add(DayMenuItem.Separator);

    private void AddMenuAction(string text, ICommand command, bool enabled = true) =>
        QuickMenuItems.Add(new DayMenuItem { Header = text, Command = command, Enabled = enabled });

    private RelayCommand CreateStatisticsCommand(DateTime date, AdjustPart part) =>
        new RelayCommand(() =>
        {
            EventDispatcher.RouteToPage(PageNames.Statistics);
            EventDispatcher.Msg(new QuickStatisticsEvent(date, part));
        });

    private RelayCommand CreateSurveyCommand(DateTime date, AdjustPart part) =>
        new RelayCommand(() =>
        {
            EventDispatcher.RouteToPage(PageNames.SurveyTool);
            EventDispatcher.Msg(new QuickSurveyEvent(date, part));
        });

    private IAsyncRelayCommand CreateEditorScriptCommand(
        string scriptId,
        ScriptEditorTarget target) =>
        new AsyncRelayCommand(async () =>
        {
            var request = EditorScriptMenuPolicy.CreateRequest(target);
            var outcome = await Task.Run(async () => await _scriptManager.ExecuteAsync(
                scriptId,
                request));
            EventDispatcher.ShowToast(
                outcome.Result.Status switch
                {
                    ScriptExecutionStatus.Succeeded => $"脚本 {scriptId} 执行成功",
                    ScriptExecutionStatus.Cancelled => $"脚本 {scriptId} 已取消",
                    ScriptExecutionStatus.TimedOut => $"脚本 {scriptId} 执行超时",
                    _ => $"脚本 {scriptId} 执行失败：{outcome.Result.Diagnostics.FirstOrDefault()?.Message ?? "请查看脚本诊断"}",
                });
        });

    private void ConfigureEditorScriptActions(WorkEditorViewModel workItem)
    {
        var actions = EditorScriptMenuPolicy.GetRunnableScripts(_scriptCatalog, ScriptEditorTargetKind.WorkItem)
            .Select(program => new WorkEditorScriptMenuItem(
                workItem.WorkId > 0 ? program.Descriptor.Name : $"{program.Descriptor.Name}（请先保存）",
                CreateEditorScriptCommand(
                    program.Descriptor.Id,
                    ScriptEditorTarget.ForWorkItem(ToScriptWorkItem(workItem))),
                workItem.WorkId > 0))
            .ToArray();
        workItem.SetEditorScriptActions(actions);
    }

    private void AddEditorScriptActions(DateTime startDate, DateTime endDate)
    {
        var target = GetEditorTarget(startDate, endDate);
        if (target is null)
            return;
        AddEditorScriptActions(target);
    }

    private void AddEditorScriptActions(ScriptEditorTarget target, string menuHeader = "脚本")
    {
        var scripts = EditorScriptMenuPolicy.GetRunnableScripts(_scriptCatalog, target.Kind);
        if (scripts.Count == 0)
            return;

        var scriptMenu = new DayMenuItem { Header = menuHeader, Enabled = true };
        foreach (var script in scripts)
        {
            scriptMenu.Children.Add(new DayMenuItem
            {
                Header = $"对{EditorScriptMenuPolicy.GetRangeLabel(target.Kind)}运行：{script.Descriptor.Name}",
                Command = CreateEditorScriptCommand(script.Descriptor.Id, target),
                Enabled = true,
            });
        }
        QuickMenuItems.Add(scriptMenu);
    }

    private static DateTime StartOfWeek(DateTime date)
    {
        var day = (int)date.DayOfWeek;
        if (day == 0)
            day = 7;
        return date.Date.AddDays(-day + 1);
    }

    private static ScriptEditorTarget? GetEditorTarget(DateTime startDate, DateTime endDate)
    {
        if (startDate.Date == endDate.Date)
            return ScriptEditorTarget.ForDay(TimeTools.FormatDateTime(startDate));
        if (startDate.Day == 1 && endDate.Year == startDate.Year && endDate.Month == startDate.Month
            && endDate.Day == DateTime.DaysInMonth(endDate.Year, endDate.Month))
            return ScriptEditorTarget.ForMonth(startDate.Year, startDate.Month);
        if (startDate.Month is 1 or 4 or 7 or 10 && startDate.Day == 1
            && endDate == startDate.AddMonths(3).AddDays(-1))
            return ScriptEditorTarget.ForQuarter(startDate.Year, (startDate.Month - 1) / 3 + 1);
        if (startDate.Month == 1 && startDate.Day == 1 && endDate.Month == 12 && endDate.Day == 31
            && startDate.Year == endDate.Year)
            return ScriptEditorTarget.ForYear(startDate.Year);
        if (startDate.DayOfWeek == DayOfWeek.Monday && endDate == startDate.AddDays(6))
            return ScriptEditorTarget.ForWeek(TimeTools.FormatDateTime(startDate));
        return null;
    }

    private static ScriptWorkItem ToScriptWorkItem(WorkEditorViewModel workItem) =>
        new(
            workItem.WorkId,
            workItem.Date,
            workItem.Comment,
            workItem.Time,
            (int)workItem.Priority,
            workItem.Note,
            [.. workItem.WorkTags.Select(tag => new ScriptWorkTag(
                tag.Id,
                tag.Name,
                tag.Color,
                (int)tag.Level,
                tag.Disabled)
            { Metadata = new Dictionary<string, string>(tag.Metadata, StringComparer.Ordinal) })])
        {
            ExtraFields = [.. workItem.GetExtraFieldsSnapshot().Select(field => new ScriptWorkItemExtraField(
                field.FieldId, field.FieldKey, field.TagId, field.TagName, field.Label, field.Type, field.Value))],
        };

    private void FillDayMenus(DateTime date)
    {
        if (date != SelectedDate)
            GoDate(date);

        QuickMenuItems.Clear();
        var weekOfYear = CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(date, CalendarWeekRule.FirstDay, DayOfWeek.Monday);
        AddMenuHeader($"{date:yyyy年MM月dd日} 第{weekOfYear}周");
        AddMenuHeader($"今日总工时{TotalTime:0.##}小时，有{TotalTime - UploadedTime:0.##}小时未同步");
        AddMenuSeparator();
        AddMenuAction("同步本日工时", UploadAllCommand);
        AddMenuAction("统计本周工时", CreateStatisticsCommand(date, AdjustPart.Week));
        if (IsSurveyorEnabled)
        {
            AddMenuSeparator();
            AddMenuAction("调查本周工时情况", CreateSurveyCommand(date, AdjustPart.Week));
        }
        AddEditorScriptActions(date.Date, date.Date);
        var weekStart = StartOfWeek(date);
        AddEditorScriptActions(
            ScriptEditorTarget.ForWeek(TimeTools.FormatDateTime(weekStart)),
            "脚本（本周）");
        AddEditorScriptActions(
            ScriptEditorTarget.ForWeek(TimeTools.FormatDateTime(weekStart.AddDays(-7))),
            "脚本（上周）");
    }

    private void FillMonthMenus(DateTime date)
    {
        QuickMenuItems.Clear();
        AddMenuHeader($"{date:yyyy年MM月} 第{(date.Month - 1) / 3 + 1}季度");
        AddMenuSeparator();
        AddMenuAction("统计本月工时", CreateStatisticsCommand(date, AdjustPart.Month));
        AddMenuAction("统计本季度工时", CreateStatisticsCommand(date, AdjustPart.Quarter));
        if (IsSurveyorEnabled)
        {
            AddMenuSeparator();
            AddMenuAction("调查本月工时情况", CreateSurveyCommand(date, AdjustPart.Month));
            AddMenuAction("调查本季度工时情况", CreateSurveyCommand(date, AdjustPart.Quarter));
        }
        AddEditorScriptActions(
            new DateTime(date.Year, date.Month, 1),
            new DateTime(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month)));
        AddEditorScriptActions(
            ScriptEditorTarget.ForQuarter(date.Year, (date.Month - 1) / 3 + 1),
            $"脚本（{date.Year} 年第{(date.Month - 1) / 3 + 1}季度）");
    }

    private void FillYearMenus(DateTime date)
    {
        QuickMenuItems.Clear();
        AddMenuHeader(date.ToString("yyyy年"));
        AddMenuSeparator();
        AddMenuAction("统计此年工时", CreateStatisticsCommand(date, AdjustPart.Year));
        if (IsSurveyorEnabled)
        {
            AddMenuSeparator();
            AddMenuAction("调查此年工时情况", CreateSurveyCommand(date, AdjustPart.Year));
        }
        AddEditorScriptActions(new DateTime(date.Year, 1, 1), new DateTime(date.Year, 12, 31));
    }

    public override void OnHide()
    {
        if (SelectedWork is not null)
            SaveWorkItem();
        SelectedWork = null;
    }

    public override void OnShow()
    {

    }

    #region 编辑器数据

    [ObservableProperty] private ObservableCollection<WorkEditorViewModel> _dailyWorks = new();
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UploadAllCommand))]
    private double _totalTime;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UploadAllCommand))]
    private double _uploadedTime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItem))]
    [NotifyCanExecuteChangedFor(nameof(SaveWorkItemCommand))]
    [NotifyCanExecuteChangedFor(nameof(DuplicateWorkItemCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteWorkItemCommand))]
    [NotifyCanExecuteChangedFor(nameof(UploadTimeCommand))]
    private WorkEditorViewModel? _selectedWork;

    public bool HasItem => SelectedWork != null;

    #endregion
}
