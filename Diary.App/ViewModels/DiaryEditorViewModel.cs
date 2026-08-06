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
using Diary.Core.Constants;
using Diary.Core.Data.App;
using Diary.Core.Data.Base;
using Diary.GUIBase.Events;
using Diary.GUIBase.Utils;
using Diary.GUIBase.ViewModels;
using Diary.PluginBase;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Diary.App.ViewModels;


public sealed class DayMenuItem
{
    public required string Header { get; set; }
    public bool Enabled { get; set; } = false;
    public ICommand? Command { get; set; } = null;

    public static DayMenuItem Separator { get; } = new DayMenuItem() { Header = "-" };
}


[DiAutoRegister(singleton: true)]
public partial class DiaryEditorViewModel : ViewModelBase
{
    private readonly ILogger _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly TemplateCoordinator _templateCoordinator;

    [ObservableProperty]
    private DateTime _selectedDate;
    [ObservableProperty]
    private DateTime _currentDate;

    private string CurrentDateString => TimeTools.FormatDateTime(CurrentDate);
    private bool _creating;

    [ObservableProperty] private ObservableCollection<Template> _templates = new();
    [ObservableProperty] private bool _canUseTemplates = false;

    private bool IsSurveyorEnabled => App.Instance.AppConfig.SurveySettings.IsServerEnabled;

    [RelayCommand]
    private void NewWorkItem()
    {
        _creating = true;
        SelectedWork = null; // hack: clear selection
        SelectedWork = new WorkEditorViewModel(_serviceProvider.GetRequiredService<DbShareData>())
        {
            Date = CurrentDateString,
        };
        _creating = false;
        SelectedWork.SyncAll();
    }

    [RelayCommand]
    private void NewWithTemplate(Template template)
    {
        NewWorkItem();
        if (SelectedWork is null)
            return;
        // apply template
        if (!string.IsNullOrWhiteSpace(template.DefaultTitle))
            SelectedWork.Comment = template.DefaultTitle;
        if (template.DefaultTime > 0)
            SelectedWork.Time = template.DefaultTime;
        // tracker 扩展默认值（如 RedMine activity/issue）经协调器按 InstanceId 应用到对应扩展
        _templateCoordinator.Apply(template, SelectedWork);
        var tags = template.DefaultWorkTags
            .Select(tagId => SelectedWork.AllTags.FirstOrDefault(tag => tag.Id == tagId))
            .Where(tag => tag is not null)
            .Cast<WorkTag>();
        SelectedWork.AddTags(tags, TagAddSource.Template);
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void SaveWorkItem()
    {
        var newDate = SelectedWork!.IsDateChanged;
        SelectedWork.Save(out var created);
        if (created)
        {
            if (CurrentDateString == SelectedWork.Date)
            {
                // 新创建的事项在其他的日期，需要切换
                DailyWorks.Add(SelectedWork);
            }
        }

        if (newDate || created)
        {
            var date = SelectedWork.Date;
            var id = SelectedWork.WorkId;
            GoDate(TimeTools.FromFormatedDate(date)); // 这里会修改选中的对象
            SelectWorkById(id);
        }

        UpdateTimeInfos();
        DuplicateWorkItemCommand.NotifyCanExecuteChanged();
    }

    private void SelectWorkById(int id)
    {
        Debug.Assert(id != 0);
        var item = DailyWorks.FirstOrDefault(x => x.WorkId == id);
        if (item is not null)
            SelectedWork = item;
    }

    private bool CanSave => SelectedWork != null;

    [RelayCommand(CanExecute = nameof(CanDuplicate))]
    private void DuplicateWorkItem()
    {
        // duplicate but not save
        var item = SelectedWork!.Clone();
        SelectedWork = null;
        _creating = true;
        SelectedWork = item;
        _creating = false;
    }

    private bool CanDuplicate => SelectedWork != null && SelectedWork.CanClone();
    private bool _deleting;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void DeleteWorkItem()
    {
        _deleting = true;
        SelectedWork!.Delete();
        DailyWorks.Remove(SelectedWork!);
        SelectedWork = DailyWorks.FirstOrDefault();
        _deleting = false;
    }
    private bool CanDelete => SelectedWork != null && SelectedWork.CanDelete();

    [RelayCommand(CanExecute = nameof(CanUpload))]
    private async Task UploadTime()
    {
        SaveWorkItem();
        var (result, msg) = await SelectedWork!.Upload();
        ToastManager?.Show(result ? "提交成功" : $"提交失败: {msg}");

        // hack: update button state
        Dispatcher.UIThread.Post(() => UploadTimeCommand.NotifyCanExecuteChanged());
        DeleteWorkItemCommand.NotifyCanExecuteChanged();
        UploadAllCommand.NotifyCanExecuteChanged();
    }

    private bool CanUpload => SelectedWork?.CanUpload() == true;

    [RelayCommand(CanExecute = nameof(CanUploadAll))]
    private async Task UploadAll()
    {
        if (SelectedWork is { IsNewItem: false })
        {
            SaveWorkItem();
        }

        var sb = new StringBuilder();
        var skip = 0;
        var success = 0;
        var failed = 0;

        foreach (var work in DailyWorks)
        {
            if (work.CanUpload())
            {
                var (result, message) = await work.Upload();
                if (result)
                {
                    ++success;
                    sb.AppendLine($"#{work.WorkId} 提交成功");
                }
                else
                {
                    ++failed;
                    sb.AppendLine($"#{work.WorkId} 提交失败: {message}");
                }
            }
            else
            {
                ++skip;
                sb.AppendLine($"#{work.WorkId} 已跳过");
            }
        }

        var title = $"提交结果: 成功 {success}，失败 {failed}，跳过 {skip}";
        EventDispatcher.Notify(title, sb.ToString());

        UpdateTimeInfos();
        UploadAllCommand.NotifyCanExecuteChanged();
        DeleteWorkItemCommand.NotifyCanExecuteChanged();
    }

    private bool CanUploadAll => TotalTime != 0 && UploadedTime < TotalTime;

    [RelayCommand]
    private void SelectToday()
    {
        GoDate(DateTime.Today);
    }


    private void GoDate(DateTime date)
    {
        CurrentDate = date;
        SelectedDate = date;
    }

    partial void OnSelectedDateChanged(DateTime value)
    {
        _currentDate = value;
        _logger.LogDebug("date changed to {Date}", _currentDate);
        FetchWorks();
    }

    partial void OnSelectedWorkChanging(WorkEditorViewModel? value) // 指 即将 从 当前值 更改为 value
    {
        if (!_deleting && !_creating && SelectedWork is not null)
            SaveWorkItem();
        UpdateTimeInfos();
    }

    public DiaryEditorViewModel(ILogger logger, IServiceProvider serviceProvider, TemplateCoordinator templateCoordinator)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _templateCoordinator = templateCoordinator;
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

    private void FetchWorks()
    {
        DailyWorks.Clear();
        var db = App.Instance.UseDb;
        if (db != null)
        {
            var dbItems = db.GetWorkItemByDate(CurrentDateString);
            if (dbItems.Count > 0)
            {
                var notesById = db.GetWorkNotesByDate(CurrentDateString);
                var tagsById = db.GetWorkTagsByDate(CurrentDateString);
                var trackers = App.Instance.Services
                    .GetRequiredService<TrackerUiContributionRegistry>().Contributions;
                var bindingsByTracker = new Dictionary<TrackerKey, IDictionary<int, object?>?>();
                foreach (var t in trackers)
                {
                    var key = new TrackerKey(t.PluginId, t.Instance.InstanceId);
                    bindingsByTracker[key] = t.Instance.LoadBindingsByDate(CurrentDateString);
                }

                foreach (var item in dbItems)
                {
                    var x = WorkEditorViewModel.FromWorkItem(item);
                    x.SyncFromBatch(notesById, tagsById, bindingsByTracker);
                    DailyWorks.Add(x);
                }
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

    private void UpdateTimeInfos()
    {
        double sum = 0.0, uploaded = 0.0;
        foreach (var work in DailyWorks)
        {
            sum += work.Time;
            if (work.IsLocked)
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

    private void FillDayMenus(DateTime date)
    {
        if (date != SelectedDate)
            GoDate(date);

        QuickMenuItems.Clear();
        var weekOfYear = CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(date, CalendarWeekRule.FirstDay, DayOfWeek.Monday);
        AddMenuHeader($"{date:yyyy年MM月dd日} 第{weekOfYear}周");
        AddMenuHeader($"今日总工时{TotalTime:0.##}小时，有{TotalTime - UploadedTime:0.##}小时未提交");
        AddMenuSeparator();
        AddMenuAction("提交本日工时", UploadAllCommand);
        AddMenuAction("提交本周工时(尚未实现)", UploadAllCommand, false);
        AddMenuAction("统计本周工时", CreateStatisticsCommand(date, AdjustPart.Week));
        if (IsSurveyorEnabled)
        {
            AddMenuSeparator();
            AddMenuAction("调查本周工时情况", CreateSurveyCommand(date, AdjustPart.Week));
        }
    }

    private void FillMonthMenus(DateTime date)
    {
        QuickMenuItems.Clear();
        AddMenuHeader($"{date:yyyy年MM月} 第{(date.Month - 1) / 3 + 1}季度");
        AddMenuSeparator();
        AddMenuAction("提交本月工时(尚未实现)", UploadAllCommand, false);
        AddMenuAction("统计本月工时", CreateStatisticsCommand(date, AdjustPart.Month));
        AddMenuAction("统计本季度工时", CreateStatisticsCommand(date, AdjustPart.Quarter));
        if (IsSurveyorEnabled)
        {
            AddMenuSeparator();
            AddMenuAction("调查本月工时情况", CreateSurveyCommand(date, AdjustPart.Month));
            AddMenuAction("调查本季度工时情况", CreateSurveyCommand(date, AdjustPart.Quarter));
        }
    }

    private void FillYearMenus(DateTime date)
    {
        QuickMenuItems.Clear();
        AddMenuHeader(date.ToString("yyyy年"));
        AddMenuSeparator();
        AddMenuAction("统计此年工时", CreateStatisticsCommand(date, AdjustPart.Week));
        if (IsSurveyorEnabled)
        {
            AddMenuSeparator();
            AddMenuAction("调查此年工时情况", CreateSurveyCommand(date, AdjustPart.Week));
        }
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
