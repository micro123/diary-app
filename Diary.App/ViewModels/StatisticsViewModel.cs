using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.Models;
using Diary.Utils;

namespace Diary.App.ViewModels;

[DiAutoRegister]
public partial class StatisticsViewModel : ViewModelBase
{
    [ObservableProperty] private ObservableCollection<StatisticsTabData> _tabs = new();
    
    public StatisticsViewModel()
    {
        Tabs.Add(new StatisticsTabData(StatisticsType.Custom));
        // foreach (var e in Enum.GetValues<StatisticsType>())
        // {
        //     Tabs.Add(new StatisticsTabData(e));
        // }
    }
}