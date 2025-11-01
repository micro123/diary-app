using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.Models;

namespace Diary.App.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        public string VersionString { get; } = "1.0.5";

        [RelayCommand]
        public void CopyVersion()
        {
            Console.WriteLine("Copy Version");
        }

        [ObservableProperty] private ObservableCollection<NavigateInfo> _pages =
        [
            new NavigateInfo("日记", "", null ),
            new NavigateInfo("RedMine", "", null ),
            new NavigateInfo("统计", "", null ),
            new NavigateInfo("调查", "", null ),
            new NavigateInfo("设置", "", null ),
        ];
    }
}
