using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Diary.App.Messages;
using Diary.App.Pages;
using Diary.RedMine;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Diary.App.ViewModels;

[DiAutoRegister]
public partial class RedMineManageViewModel : ViewModelBase
{
    private readonly ILogger _logger;
    private readonly IServiceProvider _serviceProvider;

    public class TabItemModel
    {
        public required string Title { get; set; }
        public required string Icon { get; set; }
        public required object Content { get; set; }
    }
    
    [ObservableProperty] private ObservableCollection<TabItemModel> _tabs = new();
    [ObservableProperty] private bool _serverOk;

    public RedMineManageViewModel(ILogger logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
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
            Content = _serviceProvider.GetRequiredService<RedMineIssueManageViewModel>(),
        });
        Tabs.Add(new TabItemModel()
        {
            Title = "项目管理",
            Icon = "fa-list-check",
            Content = _serviceProvider.GetRequiredService<RedMineProjectViewModel>(),
        });

        Task.Run(CheckServer);
        Messenger.Register<ConfigUpdateEvent>(this, (r,m)=>{ Task.Run(CheckServer); });
    }

    private void CheckServer()
    {
        ServerOk = RedMineApis.GetUserInfo(out _);
        _logger.LogInformation("RedMine Server Ok? {0}", ServerOk);
    }
}