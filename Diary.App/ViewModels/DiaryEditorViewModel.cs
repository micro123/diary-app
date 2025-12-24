using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Diary.App.Messages;
using Diary.App.Models;
using Diary.App.Utils;
using Diary.Core.Constants;
using Diary.Core.Data.App;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Calendar = Avalonia.Controls.Calendar;

namespace Diary.App.ViewModels;


public sealed class DayMenuItem
{
    public required string Header { get; set; }
    public bool Enabled { get; set; } = false;
    public ICommand? Command { get; set; } = null;

    public static DayMenuItem Separator { get; } = new DayMenuItem() { Header = "-" };
}


[DiAutoRegister]
public partial class DiaryEditorViewModel : ViewModelBase
{
    private readonly ILogger _logger;
    private readonly IServiceProvider _serviceProvider;

    [ObservableProperty]
    private DateTime _selectedDate;
    [ObservableProperty]
    private DateTime _currentDate;
    
    private string CurrentDateString => TimeTools.FormatDateTime(CurrentDate);
    private bool _creating;

    [ObservableProperty] private ObservableCollection<Template> _templates = new();
    [ObservableProperty] private bool _canUseTemplates = false;

    private bool IsSurveyorEnabled => App.Current.AppConfig.SurveySettings.IsServerEnabled;
    
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
            SelectedWork.Comment =  template.DefaultTitle;
        if (template.DefaultTime > 0)
            SelectedWork.Time = template.DefaultTime;
        if (template.DefaultActivity >= 0)
            SelectedWork.SetRedMineActivity(template.DefaultActivity);
        if (template.DefaultIssue >= 0)
            SelectedWork.SetRedMineIssues(template.DefaultIssue);
        foreach (var tag in template.DefaultWorkTags)
        {
            var x = SelectedWork.AllTags.FirstOrDefault(x => x.Id == tag);
            if (x is not null)
                SelectedWork.WorkTags.Add(x);
        }
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
        var item = DailyWorks.FirstOrDefault(x=>x.WorkId == id);
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
    }

    private bool CanUpload => SelectedWork is { Uploaded: false };

    [RelayCommand(CanExecute = nameof(CanUploadAll))]
    private async Task UploadAll()
    {
        if (SelectedWork is {IsNewItem: false})
        {
            SaveWorkItem();
        }

        var sb = new StringBuilder();
        var skip = 0;
        var success = 0;
        var failed = 0;
        
        foreach (var work in DailyWorks)
        {
            if (!work.Uploaded)
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

    public DiaryEditorViewModel(ILogger logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
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
        
        FetchTemplates();
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
        var db = App.Current.UseDb;
        if (db != null)
        {
            var dbItems = db.GetWorkItemByDate(CurrentDateString);
            foreach (var item in dbItems)
            {
                var x = WorkEditorViewModel.FromWorkItem(item);
                x.SyncAll(); // load database data
                DailyWorks.Add(x);
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
            if (work.Uploaded)
                uploaded += work.Time;
        }

        TotalTime = sum;
        UploadedTime = uploaded;
    }

    [ObservableProperty] private ObservableCollection<DayMenuItem> _quickMenuItems = new();
    
    private enum CalendarWhat
    {
        None,
        Day,
        Month,
        Year,
    }
    
    [RelayCommand]
    private void Test(ContextRequestedEventArgs args)
    {
        Button? btn = null;
        Calendar? calendar = null;
        CalendarWhat what = CalendarWhat.None;
        DateTime? selectDate = null;
        
        bool isHeader = false;
        bool isGridButton = false;

        var control = args.Source as Control;
        while (control is not null)
        {
            if (btn is null)
            {
                if (control is CalendarDayButton d)
                {
                    what = CalendarWhat.Day;
                    btn = d;
                    selectDate = (DateTime)d.DataContext!;
                }
                else if (control is Button m && control.Name == "PART_HeaderButton")
                {
                    what = CalendarWhat.None;
                    btn = m;
                    isHeader = true;
                }
                else if (control is CalendarButton y)
                {
                    what = CalendarWhat.None;
                    btn = y;
                    isGridButton = true;
                }
            }

            if (control is Calendar c)
            {
                calendar = c;
                break;
            }
            
            control = control.Parent as Control;
        }

        if (what == CalendarWhat.None)
        {
            if (isHeader)
            {
                switch (calendar!.DisplayMode)
                {
                    case CalendarMode.Month:
                        what = CalendarWhat.Month;
                        selectDate = calendar.DisplayDate.AddDays(-calendar.DisplayDate.Day + 1);
                        break;
                    case CalendarMode.Year:
                        what = CalendarWhat.Year;
                        selectDate = new DateTime(calendar.DisplayDate.Year, 1, 1);
                        break;
                }
            }
            else if (isGridButton)
            {
                switch (calendar!.DisplayMode)
                {
                    case CalendarMode.Year:
                        what = CalendarWhat.Month;
                        break;
                    case CalendarMode.Decade:
                        what = CalendarWhat.Year;
                        break;
                }

                selectDate = (DateTime)btn!.DataContext!;
            }
        }

        if (what == CalendarWhat.None)
        {
            args.Handled = true; // ignore event
            return;
        }

        switch (what)
        {
            case CalendarWhat.Day:
                FillDayMenus((DateTime)selectDate!);
                break;
            case CalendarWhat.Month:
                FillMonthMenus((DateTime)selectDate!);
                break;
            case CalendarWhat.Year:
                FillYearMenus((DateTime)selectDate!);
                break;
        }
    }

    private void FillDayMenus(DateTime date)
    {
        if (date != SelectedDate)
            GoDate(date); // 切换到那天
        QuickMenuItems.Clear();
        // 固定项
        var sb = new StringBuilder();
        sb.Append(date.ToString("yyyy年MM月dd日"));
        sb.Append(' ');
        sb.Append(
            $"第{CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(date, CalendarWeekRule.FirstDay, DayOfWeek.Monday)}周");
        QuickMenuItems.Add(new DayMenuItem()
        {
            Header = sb.ToString(),
        });
        QuickMenuItems.Add(new DayMenuItem()
        {
            Header = $"今日总工时{TotalTime:0.##}小时，有{TotalTime-UploadedTime:0.##}小时未提交",
        });
        QuickMenuItems.Add(DayMenuItem.Separator);
        
        // 功能项
        QuickMenuItems.Add(new DayMenuItem()
        {
            Header = "提交本日工时",
            Command = UploadAllCommand,
            Enabled = true,
        });
        QuickMenuItems.Add(new DayMenuItem()
        {
            Header = "提交本周工时(尚未实现)",
            Command = UploadAllCommand,
            Enabled = false,
        });
        QuickMenuItems.Add(new DayMenuItem()
        {
            Header = "统计本周工时",
            Command = new RelayCommand(() =>
            {
                EventDispatcher.RouteToPage(PageNames.Statistics);
                EventDispatcher.Msg(new QuickStatisticsEvent(date, AdjustPart.Week));
            }),
            Enabled = true,
        });
        if (IsSurveyorEnabled)
        {
            QuickMenuItems.Add(DayMenuItem.Separator);
            QuickMenuItems.Add(new DayMenuItem()
            {
                Header = "调查本周工时情况",
                Command = new RelayCommand(() =>
                {
                    EventDispatcher.RouteToPage(PageNames.SurveyTool);
                    EventDispatcher.Msg(new QuickSurveyEvent(date, AdjustPart.Week));
                }),
                Enabled = true,
            });
        }
    }

    private void FillMonthMenus(DateTime date)
    {
        QuickMenuItems.Clear();
        // 固定项
        var sb = new StringBuilder();
        sb.Append(date.ToString("yyyy年MM月"));
        sb.Append(' ');
        sb.Append($"第{(date.Month-1)/3+1}季度");
        QuickMenuItems.Add(new DayMenuItem()
        {
            Header = sb.ToString(),
        });
        QuickMenuItems.Add(DayMenuItem.Separator);
        
        // 功能项
        QuickMenuItems.Add(new DayMenuItem()
        {
            Header = "提交本月工时(尚未实现)",
            Command = UploadAllCommand,
            Enabled = false,
        });
        QuickMenuItems.Add(new DayMenuItem()
        {
            Header = "统计本月工时",
            Command = new RelayCommand(() =>
            {
                EventDispatcher.RouteToPage(PageNames.Statistics);
                EventDispatcher.Msg(new QuickStatisticsEvent(date, AdjustPart.Month));
            }),
            Enabled = true,
        });
        QuickMenuItems.Add(new DayMenuItem()
        {
            Header = "统计本季度工时",
            Command = new RelayCommand(() =>
            {
                EventDispatcher.RouteToPage(PageNames.Statistics);
                EventDispatcher.Msg(new QuickStatisticsEvent(date, AdjustPart.Quarter));
            }),
            Enabled = true,
        });
        if (IsSurveyorEnabled)
        {
            QuickMenuItems.Add(DayMenuItem.Separator);
            QuickMenuItems.Add(new DayMenuItem()
            {
                Header = "调查本月工时情况",
                Command = new RelayCommand(() =>
                {
                    EventDispatcher.RouteToPage(PageNames.SurveyTool);
                    EventDispatcher.Msg(new QuickSurveyEvent(date, AdjustPart.Month));
                }),
                Enabled = true,
            });
            QuickMenuItems.Add(new DayMenuItem()
            {
                Header = "调查本季度工时情况",
                Command = new RelayCommand(() =>
                {
                    EventDispatcher.RouteToPage(PageNames.SurveyTool);
                    EventDispatcher.Msg(new QuickSurveyEvent(date, AdjustPart.Quarter));
                }),
                Enabled = true,
            });
        }
    }

    private void FillYearMenus(DateTime date)
    {
        QuickMenuItems.Clear();
        // 固定项
        QuickMenuItems.Add(new DayMenuItem()
        {
            Header = date.ToString("yyyy年"),
        });
        QuickMenuItems.Add(DayMenuItem.Separator);
        
        // 功能项
        QuickMenuItems.Add(new DayMenuItem()
        {
            Header = "统计此年工时",
            Command = new RelayCommand(() =>
            {
                EventDispatcher.RouteToPage(PageNames.Statistics);
                EventDispatcher.Msg(new QuickStatisticsEvent(date, AdjustPart.Week));
            }),
            Enabled = true,
        });
        if (IsSurveyorEnabled)
        {
            QuickMenuItems.Add(DayMenuItem.Separator);
            QuickMenuItems.Add(new DayMenuItem()
            {
                Header = "调查此年工时情况",
                Command = new RelayCommand(() =>
                {
                    EventDispatcher.RouteToPage(PageNames.SurveyTool);
                    EventDispatcher.Msg(new QuickSurveyEvent(date, AdjustPart.Week));
                }),
                Enabled = true,
            });
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