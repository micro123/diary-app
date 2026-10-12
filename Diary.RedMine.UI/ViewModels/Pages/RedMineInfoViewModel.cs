using System.Collections.ObjectModel;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.GUIBase.Events;
using Diary.GUIBase.Utils;
using Diary.GUIBase.ViewModels;
using Diary.RedMine.Models;
using Diary.RedMine.Response;
using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.RedMine.UI.ViewModels.Pages;

[DiAutoRegister]
public partial class RedMineInfoViewModel : ViewModelBase
{
    private readonly ILogger _logger;
    private readonly IRedMineUiData _data;
    private readonly IRedMineApi _api;
    private readonly IRedMineDb _database;

    [ObservableProperty] private int _userId;
    [ObservableProperty] private string _userName = string.Empty;
    [ObservableProperty] private string _userLogin = string.Empty;

    public ObservableCollection<RedMineActivity> Activities => _data.RedMineActivities;
    public ObservableCollection<RedMineIssueDisplay> Issues => _data.RedMineIssues;

    public RedMineInfoViewModel(
        ILogger logger,
        IRedMineUiData data,
        IRedMineApi api,
        IRedMineDb database)
    {
        _logger = logger;
        _data = data;
        _api = api;
        _database = database;
    }

    [RelayCommand]
    private async Task SyncActivities()
    {
        try
        {
            var result = await Task.Run(() =>
            {
                if (!_api.GetActivities(out var activities) || activities is null)
                    return (Synchronized: false, Error: _api.DescribeLastFailure("Redmine 活动同步失败"));
                foreach (var activity in activities)
                    _database.AddRedMineActivity(activity.Id, activity.Name);
                return (Synchronized: true, Error: string.Empty);
            });
            if (result.Synchronized)
                EventDispatcher.DbChanged(RedMineUiEvents.ActivityChanged);
            else
                ShowApiFailure(result.Error);
        }
        catch (ObjectDisposedException exception)
        {
            _logger.LogInformation(exception, "RedMine 配置重载时活动同步已停止");
            ShowApiFailure("Redmine 配置正在重新加载，请稍后重试。");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "同步 Redmine 活动失败");
            ShowApiFailure($"Redmine 活动同步失败：{exception.Message}");
        }
    }

    public void UpdateUserInfo(UserInfo? userInfo)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            UserId = userInfo?.Id ?? 0;
            UserName = userInfo is null ? string.Empty : $"{userInfo.LastName} {userInfo.FirstName}";
            UserLogin = userInfo?.Login ?? string.Empty;
        });
    }

    [RelayCommand]
    private async Task SyncIssueState()
    {
        var result = await Task.Run(() =>
        {
            var batches = Issues.Where(x => !x.Disabled)
                .Select((issue, index) => new { Issue = issue, Index = index })
                .GroupBy(x => x.Index / _api.PageSize)
                .Select(group => group.Select(x => x.Issue));
            var result = false;
            string? error = null;
            foreach (var batch in batches)
            {
                var ids = string.Join(',', batch.Select(x => x.Id));
                if (!_api.SearchIssueByIds(out var infos, out _, false, false, 0, ids))
                {
                    error ??= _api.DescribeLastFailure("Redmine 问题状态同步失败");
                    continue;
                }
                foreach (var issue in infos!.Where(x => x.Status.IsClosed))
                {
                    result = true;
                    _database.AddRedMineIssue(
                        issue.Id, issue.Subject, issue.AssignedTo.Name, issue.Project.Id, issue.Status.IsClosed);
                }
            }
            return (Changed: result, Error: error);
        });
        if (result.Changed)
            EventDispatcher.DbChanged(RedMineUiEvents.IssueChanged);
        if (result.Error is not null)
            ShowApiFailure(result.Error);
    }

    [RelayCommand]
    private async Task ReloadIssues()
        => await Dispatcher.UIThread.InvokeAsync(() => EventDispatcher.DbChanged(RedMineUiEvents.IssueChanged));

    [RelayCommand]
    private async Task ToggleIssue(RedMineIssueDisplay issue)
    {
        var disabled = !issue.Disabled;
        await Task.Run(() => _database.UpdateRedMineIssueStatus(issue.Id, disabled));
        await Dispatcher.UIThread.InvokeAsync(() => _data.UpdateIssueStatus(issue.Id, disabled));
    }

    [RelayCommand]
    private void DeleteIssue(RedMineIssueDisplay issue) => EventDispatcher.ShowToast("暂时不支持删除！");

    private static void ShowApiFailure(string message) =>
        EventDispatcher.ShowToast(
            message,
            NotificationType.Error,
            NotificationRetention.Session);
}
