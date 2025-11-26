using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Diary.App.Pages;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.App.ViewModels;

[DiAutoRegister]
public partial class RedMineManageViewModel : ViewModelBase
{
    private readonly IServiceProvider _serviceProvider;

    public class TabItemModel
    {
        public required string Title { get; set; }
        public required string Icon { get; set; }
        public required object Content { get; set; }
    }
    
    [ObservableProperty] private ObservableCollection<TabItemModel> _tabs = new();

    public RedMineManageViewModel(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        Tabs.Add(new TabItemModel()
        {
            Title = "基本信息",
            Icon = "mdi-information-slab-box-outline",
            Content = _serviceProvider.GetRequiredService<RedMineInfoViewModel>(),
        });
        Tabs.Add(new TabItemModel()
        {
            Title = "问题管理",
            Icon = "fa-exclamation",
            Content = "222"
        });
        Tabs.Add(new TabItemModel()
        {
            Title = "项目搜索",
            Icon = "fa-list-check",
            Content = "333"
        });
    }
}