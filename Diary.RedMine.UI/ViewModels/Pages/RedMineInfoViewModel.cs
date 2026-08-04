using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.Database;
using Diary.GUIBase.Events;
using Diary.GUIBase;
using Diary.GUIBase.Utils;
using Diary.GUIBase.ViewModels;
using Diary.RedMine;
using Diary.RedMine.Models;
using Diary.RedMine.Response;
using Diary.RedMine.UI;
using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.RedMine.UI.ViewModels.Pages;

[DiAutoRegister]
public partial class RedMineInfoViewModel : ViewModelBase
{
    private readonly ILogger _logger;
    private readonly IRedMineUiData _data;
    private readonly IRedMineApi _api;
    private DbInterfaceBase? Db => BaseApp.Instance.UseDb;

    [ObservableProperty] private int _userId;
    [ObservableProperty] private string _userName = string.Empty;
    [ObservableProperty] private string _userLogin = string.Empty;

    public ObservableCollection<RedMineActivity> Activities => _data.RedMineActivities;
    public ObservableCollection<RedMineIssueDisplay> Issues => _data.RedMineIssues;

    public RedMineInfoViewModel(ILogger logger, IRedMineUiData data, IRedMineApi api)
    {
        _logger = logger;
        _data = data;
        _api = api;
    }

    [RelayCommand]
    private async Task SyncActivities()
    {
        await Task.Run(() =>
        {
            _api.GetActivities(out var activities);
            if (activities is not null && Db is not null)
            {
                foreach (var activity in activities)
                    Db.GetExtension<IRedMineDb>()!.AddRedMineActivity(activity.Id, activity.Name);
            }
        });
        EventDispatcher.DbChanged(RedMineUiEvents.ActivityChanged);
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
        var changed = await Task.Run(() =>
        {
            if (Db is null) return false;
            var batches = Issues.Where(x => !x.Disabled)
                .Select((issue, index) => new { Issue = issue, Index = index })
                .GroupBy(x => x.Index / _api.PageSize)
                .Select(group => group.Select(x => x.Issue));
            var result = false;
            foreach (var batch in batches)
            {
                var ids = string.Join(',', batch.Select(x => x.Id));
                if (!_api.SearchIssueByIds(out var infos, out _, false, false, 0, ids)) continue;
                foreach (var issue in infos!.Where(x => x.Status.IsClosed))
                {
                    result = true;
                    Db.GetExtension<IRedMineDb>()!.AddRedMineIssue(
                        issue.Id, issue.Subject, issue.AssignedTo.Name, issue.Project.Id, issue.Status.IsClosed);
                }
            }
            return result;
        });
        if (changed) EventDispatcher.DbChanged(RedMineUiEvents.IssueChanged);
    }

    [RelayCommand]
    private async Task ReloadIssues()
        => await Dispatcher.UIThread.InvokeAsync(() => EventDispatcher.DbChanged(RedMineUiEvents.IssueChanged));

    [RelayCommand]
    private async Task ToggleIssue(RedMineIssueDisplay issue)
    {
        await Task.Run(() => Db!.GetExtension<IRedMineDb>()!.UpdateRedMineIssueStatus(issue.Id, !issue.Disabled));
        EventDispatcher.DbChanged(RedMineUiEvents.IssueChanged);
    }

    [RelayCommand]
    private void DeleteIssue(RedMineIssueDisplay issue) => EventDispatcher.ShowToast("暂时不支持删除！");
}
