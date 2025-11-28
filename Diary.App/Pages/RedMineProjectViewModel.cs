using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.Utils;
using Diary.App.ViewModels;
using Diary.RedMine;
using Diary.RedMine.Response;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;
using Ursa.Controls;

namespace Diary.App.Pages;

[DiAutoRegister]
public partial class RedMineProjectViewModel : ViewModelBase
{
    // 搜索参数
    [ObservableProperty] private string _searchTerm = string.Empty;

    // 搜索状态
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FirstPageCommand), nameof(LastPageCommand), nameof(PrevPageCommand),
        nameof(NextPageCommand))]
    private int _currentPage = 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FirstPageCommand), nameof(LastPageCommand), nameof(PrevPageCommand),
        nameof(NextPageCommand))]
    private int _totalPage = 1;

    [ObservableProperty] private ObservableCollection<ProjectInfo> _searchResults = new();
    [ObservableProperty] private int _resultCount;

    [RelayCommand]
    private async Task Search()
    {
        CurrentPage = 1;
        await SearchInternal();
    }

    private async Task SearchInternal()
    {
        var ok = RedMineApis.SearchProject(out var results, out int total,
            CurrentPage - 1, SearchTerm);
        if (!ok)
        {
            NotificationManager?.Show("似乎有什么出错了 >_!", NotificationType.Error);
        }
        await Dispatcher.UIThread.InvokeAsync(() => UpdateSearchResults(results, total));
    }

    private void UpdateSearchResults(IEnumerable<ProjectInfo>? projects, int total)
    {
        ResultCount = total;
        TotalPage = total / RedMineApis.PageSize + 1;
        SearchResults.Clear();
        if (projects == null) return;
        foreach (var project in projects)
        {
            SearchResults.Add(project);
        }
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
    private async Task CreateIssue(ProjectInfo project)
    {
        // NotificationManager?.Show("还没实现~~", NotificationType.Information);
        var opt = new OverlayDialogOptions
        {
            Title = "创建问题",
            Buttons = DialogButton.OKCancel,
            CanDragMove = false,
            CanResize = false,
            CanLightDismiss = false,
            Mode = DialogMode.None
        };
        var vm = App.Current.Services.GetRequiredService<NewIssueViewModel>();
        bool finish = false;
        do
        {
            var result = await OverlayDialog.ShowModal<NewIssueView, NewIssueViewModel>(vm: vm, options: opt);
            if (result == DialogResult.OK)
            {
                // check parameters
                if (!vm.IsValid)
                {
                    ToastManager?.Show("参数错误！");
                }
                else
                {
                    IssueInfo? issue;
                    (finish, issue) = await Task.Run(() =>
                    {
                        var ok = RedMineApis.CreateIssue(out IssueInfo? info, project.Id,
                            vm.IssueTitle, vm.IssueDesc,
                            vm.AssignSelf);
                        return (ok, info);
                    });
                    if (finish)
                    {
                        EventDispatcher.Notify("问题创建成功", $"新问题ID为: {issue!.Id}");
                    }
                    else
                    {
                        ToastManager?.Show("创建问题失败了>_<");
                    }
                }
            }
            else
            {
                finish = true;
            }
        } while (!finish);
    }

    [RelayCommand]
    private void ShowDesc(ProjectInfo project)
    {
        EventDispatcher.Notify(project.Name, string.IsNullOrEmpty(project.Description) ? "描述是空的哟~~" : project.Description);
    }
}