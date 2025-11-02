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
            new NavigateInfo("日记", "mdi-notebook", null ),
            new NavigateInfo("RedMine", "fa-cloud", null ),
            new NavigateInfo("统计", "fa-chart-pie", null ),
            new NavigateInfo("调查", "mdi-chat-processing-outline", null ),
            new NavigateInfo("设置", "mdi-cog-outline", null ),
        ];

        [ObservableProperty] private NavigateInfo? _selectedPage = null;

        partial void OnSelectedPageChanged(NavigateInfo? value)
        {
            CurrentPageModel = value?.ViewModel;
        }
        
        [ObservableProperty] private ViewModelBase? _currentPageModel = null;

        public MainWindowViewModel()
        {
            SelectedPage = Pages[0];
        }
    }
}
