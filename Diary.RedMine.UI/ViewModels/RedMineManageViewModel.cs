using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Diary.GUIBase.Events;
using Diary.GUIBase.ViewModels;
using Diary.RedMine;
using Diary.RedMine.UI.ViewModels.Pages;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Diary.RedMine.UI.ViewModels;

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
    private readonly IServiceProvider _services;
    private readonly IRedMineApi _api;
    private readonly RedMineInfoViewModel _redmineInfo;

    [ObservableProperty] private ObservableCollection<RedMineTabItemModel> _tabs = new();
    [ObservableProperty] private bool _serverOk;

    public RedMineManageViewModel(
        ILogger logger,
        IServiceProvider services,
        IRedMineApi api,
        IRedMineUiData data,
        IRedMineDb database)
    {
        _logger = logger;
        _services = services;
        _api = api;
        _redmineInfo = ActivatorUtilities.CreateInstance<RedMineInfoViewModel>(
            services, data, api, database);
        Tabs.Add(new RedMineTabItemModel
        {
            Title = "基本信息",
            Icon = "mdi-information-slab-box-outline",
            Content = _redmineInfo,
        });
        Tabs.Add(new RedMineTabItemModel
        {
            Title = "问题管理",
            Icon = "fa-exclamation",
            Content = ActivatorUtilities.CreateInstance<RedMineIssueManageViewModel>(
                services, api, database),
        });
        Tabs.Add(new RedMineTabItemModel
        {
            Title = "项目管理",
            Icon = "fa-list-check",
            Content = ActivatorUtilities.CreateInstance<RedMineProjectViewModel>(
                services, api),
        });

        Task.Run(CheckServer);
        Messenger.Register<ConfigUpdateEvent>(this, (_, _) => Task.Run(CheckServer));
    }

    private void CheckServer()
    {
        ServerOk = _api.GetUserInfo(out var info);
        _logger.LogInformation("RedMine Server Ok? {Ok}", ServerOk);
        _redmineInfo.UpdateUserInfo(info);
    }
}
