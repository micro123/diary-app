using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Diary.GUIBase.Events;
using Diary.GUIBase.ViewModels;
using Diary.RedMine;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RedMineInfoViewModel = Diary.RedMine.UI.ViewModels.Pages.RedMineInfoViewModel;
using RedMineIssueManageViewModel = Diary.RedMine.UI.ViewModels.Pages.RedMineIssueManageViewModel;
using RedMineProjectViewModel = Diary.App.ViewModels.Pages.RedMineProjectViewModel;

namespace Diary.App.ViewModels;

public class RedMineTabItemModel
{
    public required string Title { get; set; }
    public required string Icon { get; set; }
    public required object Content { get; set; }
}

[DiAutoRegister(singleton: true)]
public partial class RedMineManageViewModel : ViewModelBase
{
    private readonly ILogger _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IRedMineApi _api;

    [ObservableProperty] private ObservableCollection<RedMineTabItemModel> _tabs = new();
    [ObservableProperty] private bool _serverOk;
    private readonly RedMineInfoViewModel _redmineInfo;

    public RedMineManageViewModel(ILogger logger, IServiceProvider serviceProvider, IRedMineApi api)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _api = api;
        _redmineInfo = _serviceProvider.GetRequiredService<RedMineInfoViewModel>();
        Tabs.Add(new RedMineTabItemModel()
        {
            Title = "基本信息",
            Icon = "mdi-information-slab-box-outline",
            Content = _redmineInfo,
        });
        Tabs.Add(new RedMineTabItemModel()
        {
            Title = "问题管理",
            Icon = "fa-exclamation",
            Content = _serviceProvider.GetRequiredService<RedMineIssueManageViewModel>(),
        });
        Tabs.Add(new RedMineTabItemModel()
        {
            Title = "项目管理",
            Icon = "fa-list-check",
            Content = _serviceProvider.GetRequiredService<RedMineProjectViewModel>(),
        });

        Task.Run(CheckServer);
        Messenger.Register<ConfigUpdateEvent>(this, (r, m) => { Task.Run(CheckServer); });
    }

    private void CheckServer()
    {
        ServerOk = _api.GetUserInfo(out var info);
        _logger.LogInformation("RedMine Server Ok? {Ok}", ServerOk);
        _redmineInfo.UpdateUserInfo(info);
    }
}
