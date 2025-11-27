using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Diary.App.Messages;
using Diary.App.Models;
using Diary.App.Utils;
using Diary.App.ViewModels;
using Diary.Core.Data.Display;
using Diary.Core.Data.RedMine;
using Diary.Database;
using Diary.RedMine;
using Diary.RedMine.Response;
using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.App.Pages;

[DiAutoRegister]
public partial class RedMineInfoViewModel : ViewModelBase
{
    private readonly ILogger _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly DbShareData _shareData;
    private DbInterfaceBase? Db => App.Current.UseDb;

    // 基本信息
    [ObservableProperty] private int _userId;
    [ObservableProperty] private string _userName = string.Empty;
    [ObservableProperty] private string _userLogin = string.Empty;

    // 活动列表
    public ObservableCollection<RedMineActivity> Activities => _shareData.RedMineActivities;
    
    // 问题列表
    public ObservableCollection<RedMineIssueDisplay> Issues => _shareData.RedMineIssues;

    [RelayCommand]
    private async Task SyncActivities()
    {
        var result = await Task.Run(() =>
        {
            RedMineApis.GetActivities(out var activities);
            // 更新数据库
            var all = activities?.Select(x => Db!.AddRedMineActivity(x.Id, x.Name)).ToArray();
            return all != null;
        });
        EventDispatcher.DbChanged(DbChangedEvent.RedMineActivity);
    }


    public RedMineInfoViewModel(ILogger logger, IServiceProvider serviceProvider, DbShareData shareData)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _shareData = shareData;

        Messenger.Register<ConfigUpdateEvent>(this, (r, m) => { UpdateUserInfo(); });

        Task.Run(UpdateUserInfo);
    }

    private void UpdateUserInfo()
    {
        RedMineApis.GetUserInfo(out var info);
        Dispatcher.UIThread.Post(() => { UpdateUserInfo(info); });
    }

    private void UpdateUserInfo(UserInfo? userInfo)
    {
        if (userInfo is not null)
        {
            UserName = $"{userInfo.LastName}{userInfo.FirstName}";
            UserId = userInfo.Id;
            UserLogin = userInfo.Login;
        }
        else
        {
            UserName = string.Empty;
            UserId = 0;
            UserLogin = string.Empty;
        }
    }

    [RelayCommand]
    private async Task SyncIssueState()
    {
        await Task.Run(() =>
        {
            var batches = Issues.Select((x, n) => new { o = x, i = n })
                .GroupBy(x => x.i / RedMineApis.PageSize)
                .Select(g => g.Select(x => x.o));
            foreach (var batch in batches)
            {
                var arr = batch.ToArray();
                string ids = string.Join(',', arr.Select(x => x.Id));
                var success =
                    RedMineApis.SearchIssueByIds(out IEnumerable<IssueInfo>? infos, out var _, false, false, 0, ids);
                if (success)
                {
                    // update db
                    foreach (var issue in infos!)
                    {
                        Db!.AddRedMineIssue(issue.Id, issue.Subject, issue.AssignedTo.Name, issue.Project.Id, issue.Status.IsClosed);
                    }
                }
            }
        });
        EventDispatcher.DbChanged(DbChangedEvent.RedMineIssue);
    }

    [RelayCommand]
    private async Task ReloadIssues()
    {
        await Dispatcher.UIThread.InvokeAsync(() => EventDispatcher.DbChanged(DbChangedEvent.RedMineIssue));
    }

    [RelayCommand]
    private async Task CloseIssue()
    {
        // TODO: 关闭问题
        await Task.Delay(500);
    }

    [RelayCommand]
    private async Task DeleteIssue()
    {
        // TODO: 删除问题，只影响本地数据
        await Task.Delay(500);
    }
}