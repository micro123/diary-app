using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Diary.App.Messages;
using Diary.App.Models;
using Diary.Core.Configure;
using Diary.Core.Utils;
using Diary.Utils;

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

[DiAutoRegister]
public partial class StatisticsViewModel : ViewModelBase
{
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
            Enabled =  false,
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