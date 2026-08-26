using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Diary.App.Models;
using Diary.Core.Configure;
using Diary.Core.Constants;
using Diary.Core.Utils;
using Diary.GUIBase.Events;
using Diary.GUIBase.Utils;
using Diary.GUIBase.ViewModels;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.App.ViewModels;

public class AddStatisticOptionItem
{
    public required string Name { get; set; }
    public required StatisticsType Type { get; set; }
    public required bool Enabled { get; set; }
}

[StorageFile("statistics.json")]
public class StatisticsManager : SingletonBase<StatisticsManager>
{
    private StatisticsManager()
    {
        EasySaveLoad.Load(this);
    }

    public static void Save()
    {
        EasySaveLoad.Save(Instance);
    }

    public ICollection<StatisticsType> StatisticsList { get; set; } = new List<StatisticsType>();
}

[DiAutoRegister(singleton: true)]
public partial class StatisticsViewModel : ViewModelBase
{
    public override bool IsViewCacheable => true;
    private bool _isShown;

    [ObservableProperty] private ObservableCollection<StatisticsTabData> _tabs = new();

    [ObservableProperty] private ObservableCollection<AddStatisticOptionItem> _addList = new();
    [ObservableProperty] private int _selectedTabIndex = 0;

    private ICollection<StatisticsType> StatisticsTypes => StatisticsManager.Instance.StatisticsList;

    public StatisticsViewModel()
    {
        foreach (var type in StatisticsTypes)
        {
            Tabs.Add(new StatisticsTabData(type));
        }
        Tabs.Add(new StatisticsTabData(StatisticsType.Custom));
        SyncOptions();

        Messenger.Register<QuickStatisticsEvent>(this, (r, m) =>
        {
            var data = Tabs.Last();
            data.DateBegin = m.Value.Item1;
            data.MakeRange((AdjustPart)m.Value.Item2, AdjustDirection.Current);
            SelectedTabIndex = Tabs.Count - 1; // 最后一个是自定义
        });
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        if (_isShown)
            ScheduleSelectedTabInitialization();
    }

    public override void OnShow()
    {
        _isShown = true;
        ScheduleSelectedTabInitialization();
    }

    public override void OnHide() => _isShown = false;

    private void ScheduleSelectedTabInitialization()
        => Dispatcher.UIThread.Post(
            () => _ = EnsureSelectedTabInitializedSafelyAsync(),
            DispatcherPriority.Background);

    private async Task EnsureSelectedTabInitializedSafelyAsync()
    {
        if (SelectedTabIndex < 0 || SelectedTabIndex >= Tabs.Count)
            return;
        try
        {
            await Tabs[SelectedTabIndex].EnsureInitializedAsync();
        }
        catch (Exception exception)
        {
            EventDispatcher.ShowToast($"加载统计失败：{exception.Message}");
        }
    }

    [RelayCommand]
    private void RetryDatabaseConnection()
    {
        var app = (App)App.Instance;
        if (app.TryReconnectDatabase(out var message))
        {
            EventDispatcher.ShowToast("数据库已恢复连接");
            ScheduleSelectedTabInitialization();
            return;
        }

        EventDispatcher.Notify(
            "数据库仍不可用",
            $"{message}\n\n本地记录不会因连接失败被删除。请检查数据库设置，或导出诊断日志后再联系维护者。");
    }

    [RelayCommand]
    private void OpenDatabaseSettings()
        => EventDispatcher.RunCommand(CommandNames.ShowDbSettings);

    [RelayCommand]
    private void ExportDiagnostics()
    {
        var path = App.Instance.Services.GetRequiredService<DiagnosticLogExportService>().Export();
        EventDispatcher.Notify(
            path is null ? "暂无诊断日志" : "诊断日志已导出",
            path is null ? "当前没有可导出的应用日志。" : path);
    }

    private void SyncOptions()
    {
        AddList.Clear();
        // fixed header
        AddList.Add(new AddStatisticOptionItem()
        {
            Name = "添加快速测量",
            Type = StatisticsType.Custom,
            Enabled = false
        });
        AddList.Add(new AddStatisticOptionItem()
        {
            Name = "-",
            Type = StatisticsType.Custom,
            Enabled = false,
        });

        foreach (var type in Enum.GetValues<StatisticsType>())
        {
            if (StatisticsTypes.Contains(type) || type == StatisticsType.Custom)
                continue;
            AddList.Add(new AddStatisticOptionItem()
            {
                Name = StatisticsTabData.GetTypeName(type),
                Type = type,
                Enabled = true,
            });
        }

        if (AddList.Count < 3)
        {
            AddList.Add(new AddStatisticOptionItem()
            {
                Name = "无可用项",
                Type = StatisticsType.Custom,
                Enabled = false
            });
        }
    }

    [RelayCommand]
    private void AddStatistic(AddStatisticOptionItem item)
    {
        StatisticsTypes.Add(item.Type);
        Tabs.Insert(Tabs.Count - 1, new StatisticsTabData(item.Type));
        StatisticsManager.Save();
        Dispatcher.UIThread.Post(SyncOptions);
    }

    [RelayCommand]
    private void DelStatistic(StatisticsType type)
    {
        // find index of statistic
        var data = Tabs.FirstOrDefault(x => x.Type == type);
        if (data is null)
            return;
        Tabs.Remove(data);
        StatisticsTypes.Remove(type);
        SyncOptions();
        StatisticsManager.Save();
    }
}
