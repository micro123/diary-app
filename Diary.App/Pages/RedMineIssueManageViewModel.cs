using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.ViewModels;
using Diary.Core.Data.RedMine;
using Diary.Database;
using Diary.RedMine;
using Diary.RedMine.Response;
using Diary.Utils;

namespace Diary.App.Pages;

[DiAutoRegister]
public partial class RedMineIssueManageViewModel : ViewModelBase
{
    public const string SearchById = "SearchById";
    public const string SearchByKeyword = "SearchByKeyword";

    // 搜索参数
    [ObservableProperty]
    private string _searchTerm = string.Empty;

    [ObservableProperty] private bool _onlyOpened;
    [ObservableProperty] private bool _onlyMyIssues;
    private string _lastSearchMethod = string.Empty;

    [ObservableProperty] private int _resultCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FirstPageCommand), nameof(LastPageCommand), nameof(PrevPageCommand),
        nameof(NextPageCommand))]
    private int _currentPage = 1;

    [ObservableProperty] private int _totalPage = 1;
    [ObservableProperty] private ObservableCollection<IssueInfo> _searchResults = new();

    private DbInterfaceBase? Db => App.Current.UseDb;

    private void UpdateSearchResults(IEnumerable<IssueInfo> searchResults)
    {
        SearchResults.Clear();
        foreach (var result in searchResults)
        {
            SearchResults.Add(result);
        }
    }

    [RelayCommand]
    private async Task Search(string method)
    {
        _lastSearchMethod = method;
        CurrentPage = 1; // 点击搜索那就是第一页
        await SearchInternal();
    }

    private async Task SearchInternal()
    {
        if (string.IsNullOrEmpty(_lastSearchMethod))
            return;
        await Task.Run(() =>
        {
            switch (_lastSearchMethod)
            {
                case SearchById:
                {
                    bool ok = RedMineApis.SearchIssueByIds(out var results, out int total,
                        OnlyMyIssues, OnlyOpened, CurrentPage - 1, SearchTerm);
                    if (ok)
                    {
                        ResultCount = total;
                        TotalPage = total / RedMineApis.PageSize + 1;
                        Dispatcher.UIThread.InvokeAsync(() => UpdateSearchResults(results!));
                    }
                    else
                    {
                        ResultCount = 0;
                        NotificationManager?.Show("似乎有什么出错了 >_!", NotificationType.Error);
                    }
                }
                    break;
                case SearchByKeyword:
                {
                    bool ok = RedMineApis.SearchIssueByKeywords(out var results, out int total,
                        OnlyMyIssues, OnlyOpened, CurrentPage - 1, SearchTerm);
                    if (ok)
                    {
                        ResultCount = total;
                        TotalPage = total / RedMineApis.PageSize + 1;
                        Dispatcher.UIThread.InvokeAsync(() => UpdateSearchResults(results!));
                    }
                    else
                    {
                        ResultCount = 0;
                        NotificationManager?.Show("似乎有什么出错了 >_!", NotificationType.Error);
                    }
                }
                    break;
            }
        });
    }
    

    [RelayCommand(CanExecute = nameof(CanGoFirstPage))]
    private async Task FirstPage()
    {
        CurrentPage = 1;
        await SearchInternal();
    }

    private bool CanGoFirstPage => CurrentPage != 1;

    [RelayCommand(CanExecute = nameof(CanGoPrevPage))]
    private async Task PrevPage()
    {
        CurrentPage -= 1;
        await SearchInternal();
    }

    private bool CanGoPrevPage => CurrentPage > 1;

    [RelayCommand(CanExecute = nameof(CanGoNextPage))]
    private async Task NextPage()
    {
        CurrentPage += 1;
        await SearchInternal();
    }

    private bool CanGoNextPage => CurrentPage != TotalPage;

    [RelayCommand(CanExecute = nameof(CanGoLastPage))]
    private async Task LastPage()
    {
        CurrentPage = TotalPage;
        await SearchInternal();
    }

    private bool CanGoLastPage => CurrentPage < TotalPage;

    [RelayCommand]
    private async Task Import(IssueInfo issue)
    {
        if (Db == null)
            return;

        await Task.Run(() =>
        {
            // 先导入项目
            RedMineApis.GetProject(out var project, issue.Project.Id);
            Debug.Assert(project != null);
            Db.AddRedMineProject(project.Id, project.Name, project.Description);
        
            // 再导入问题
            Db.AddRedMineIssue(issue.Id, issue.Subject, issue.AssignedTo.Name, issue.Project.Id);
        });
    }
}