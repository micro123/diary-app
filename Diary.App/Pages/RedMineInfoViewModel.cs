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
    private DbInterfaceBase? Db => App.Current.UseDb;

    // 基本信息
    [ObservableProperty] private int _userId;
    [ObservableProperty] private string _userName = string.Empty;
    [ObservableProperty] private string _userLogin = string.Empty;

    // 活动列表
    [ObservableProperty] private ObservableCollection<RedMineActivity> _activities = new();
    
    // 问题列表
    [ObservableProperty] private ObservableCollection<RedMineIssueDisplay> _issues = new();

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
        if (result)
        {
            FetchActivitiesFromDb();
        }
    }

    private void FetchActivitiesFromDb()
    {
        if (Db is null)
            return;

        Activities.Clear();
        foreach (var act in Db!.GetRedMineActivities())
        {
            Activities.Add(act);
        }
    }

    public RedMineInfoViewModel(ILogger logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        FetchActivitiesFromDb();

        Messenger.Register<ConfigUpdateEvent>(this, (r, m) =>
        {
            UpdateUserInfo();
        });

        Task.Run(UpdateUserInfo);
        Task.Run(UpdateIssueList);
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

    private async Task UpdateIssueList()
    {
        if (Db is null)
            return;

        var issues = await Task.Run(() => Db.GetRedMineIssues(null));
        await Dispatcher.UIThread.InvokeAsync(()=>SetIssues(issues));
    }

    private void SetIssues(IEnumerable<RedMineIssueDisplay> issues)
    {
        Issues.Clear();
        foreach (var issue in issues)
        {
            Issues.Add(issue);
        }
    }

    [RelayCommand]
    private async Task SyncIssueState()
    {
        // TODO: 抓取所有数据库中的问题，更新问题的描述、关闭状态、指派目标等
        await Task.Delay(500);
    }
    
    [RelayCommand]
    private async Task ReloadIssues()
    {
        await UpdateIssueList();
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