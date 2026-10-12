using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.GUIBase.Events;
using Diary.GUIBase.Utils;
using Diary.GUIBase.ViewModels;
using Diary.RedMine.Response;
using Diary.Utils;

namespace Diary.RedMine.UI.ViewModels.Pages;

[DiAutoRegister]
public partial class RedMineIssueManageViewModel : PaginatedSearchViewModel<IssueInfo>
{
    public const string SearchById = "SearchById";
    public const string SearchByKeyword = "SearchByKeyword";

    [ObservableProperty] private string _searchTerm = string.Empty;
    [ObservableProperty] private bool _onlyOpened = true;
    [ObservableProperty] private bool _onlyMyIssues = true;
    private string _lastSearchMethod = string.Empty;
    private readonly IRedMineApi _api;
    private readonly IRedMineDb _database;
    protected override int PageSize => _api.PageSize;
    protected override string SearchFailureMessage =>
        _api.DescribeLastFailure("Redmine 问题查询失败");

    public RedMineIssueManageViewModel(IRedMineApi api, IRedMineDb database)
    {
        _api = api;
        _database = database;
    }

    [RelayCommand]
    private async Task Search(string method)
    {
        _lastSearchMethod = method;
        CurrentPage = 1;
        await DoSearchInternalAsync();
    }

    protected override Task<(bool ok, IEnumerable<IssueInfo>? results, int total)> ExecuteSearchAsync(int page)
        => Task.Run(() =>
        {
            if (string.IsNullOrEmpty(_lastSearchMethod)) return (false, (IEnumerable<IssueInfo>?)null, 0);
            return _lastSearchMethod == SearchById
                ? _api.SearchIssueByIds(out var ids, out var idTotal, OnlyMyIssues, OnlyOpened, page, SearchTerm)
                    ? (true, ids, idTotal) : (false, ids, idTotal)
                : _api.SearchIssueByKeywords(out var keywords, out var keywordTotal, OnlyMyIssues, OnlyOpened, page, SearchTerm)
                    ? (true, keywords, keywordTotal) : (false, keywords, keywordTotal);
        });

    [RelayCommand]
    private async Task Import(IssueInfo issue)
    {
        var result = await Task.Run(() =>
        {
            if (!_api.GetProject(out var project, issue.Project.Id) || project is null)
                return (Success: false, Error: _api.DescribeLastFailure("无法读取问题所属的 Redmine 项目"));
            _database.AddRedMineProject(project.Id, project.Name, project.Description);
            _database.AddRedMineIssue(issue.Id, issue.Subject, issue.AssignedTo.Name, issue.Project.Id, issue.Status.IsClosed);
            return (Success: true, Error: string.Empty);
        });
        if (!result.Success)
        {
            EventDispatcher.ShowToast(
                result.Error,
                NotificationType.Error,
                NotificationRetention.Session);
            return;
        }
        EventDispatcher.DbChanged(RedMineUiEvents.IssueChanged);
    }
}
