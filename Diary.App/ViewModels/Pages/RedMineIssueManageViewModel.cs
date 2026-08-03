using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.Database;
using Diary.GUIBase.ViewModels;
using Diary.RedMine;
using Diary.RedMine.Response;
using Diary.Utils;

namespace Diary.App.ViewModels.Pages;

[DiAutoRegister]
public partial class RedMineIssueManageViewModel : PaginatedSearchViewModel<IssueInfo>
{
    public const string SearchById = "SearchById";
    public const string SearchByKeyword = "SearchByKeyword";

    [ObservableProperty] private string _searchTerm = string.Empty;
    [ObservableProperty] private bool _onlyOpened = true;
    [ObservableProperty] private bool _onlyMyIssues = true;
    private string _lastSearchMethod = string.Empty;

    protected override int PageSize => RedMineApis.PageSize;

    private DbInterfaceBase? Db => App.Instance.UseDb;

    [RelayCommand]
    private async Task Search(string method)
    {
        _lastSearchMethod = method;
        CurrentPage = 1;
        await DoSearchInternalAsync();
    }

    protected override async Task<(bool ok, IEnumerable<IssueInfo>? results, int total)> ExecuteSearchAsync(int page)
    {
        if (string.IsNullOrEmpty(_lastSearchMethod))
            return (false, null, 0);

        return await Task.Run(() =>
        {
            bool ok;
            IEnumerable<IssueInfo>? results;
            int total;
            switch (_lastSearchMethod)
            {
                case SearchById:
                    ok = RedMineApis.SearchIssueByIds(out results, out total, OnlyMyIssues, OnlyOpened, page, SearchTerm);
                    break;
                case SearchByKeyword:
                    ok = RedMineApis.SearchIssueByKeywords(out results, out total, OnlyMyIssues, OnlyOpened, page, SearchTerm);
                    break;
                default:
                    ok = false;
                    results = null;
                    total = 0;
                    break;
            }
            return (ok, results, total);
        });
    }

    [RelayCommand]
    private async Task Import(IssueInfo issue)
    {
        if (Db == null)
            return;

        await Task.Run(() =>
        {
            RedMineApis.GetProject(out var project, issue.Project.Id);
            Debug.Assert(project != null);
            Db.AddRedMineProject(project.Id, project.Name, project.Description);

            Db.AddRedMineIssue(issue.Id, issue.Subject, issue.AssignedTo.Name, issue.Project.Id, issue.Status.IsClosed);
        });
    }
}
