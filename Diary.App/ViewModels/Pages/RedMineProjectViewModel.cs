using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.GUIBase.Utils;
using Diary.GUIBase.ViewModels;
using Diary.RedMine;
using Diary.RedMine.Response;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;
using Ursa.Controls;
using NewIssueView = Diary.App.Views.Pages.NewIssueView;

namespace Diary.App.ViewModels.Pages;

[DiAutoRegister]
public partial class RedMineProjectViewModel : PaginatedSearchViewModel<ProjectInfo>
{
    [ObservableProperty] private string _searchTerm = string.Empty;
    private readonly IRedMineApi _api;
    protected override int PageSize => _api.PageSize;

    public RedMineProjectViewModel(IRedMineApi api)
    {
        _api = api;
    }

    [RelayCommand]
    private async Task Search()
    {
        CurrentPage = 1;
        await DoSearchInternalAsync();
    }

    protected override Task<(bool ok, IEnumerable<ProjectInfo>? results, int total)> ExecuteSearchAsync(int page)
    {
        var ok = _api.SearchProject(out var results, out int total, page, SearchTerm);
        return Task.FromResult<(bool, IEnumerable<ProjectInfo>?, int)>((ok, results, total));
    }

    [RelayCommand]
    private async Task CreateIssue(ProjectInfo project)
    {
        var opt = new OverlayDialogOptions
        {
            Title = "创建问题",
            Buttons = DialogButton.OKCancel,
            CanDragMove = false,
            CanResize = false,
            CanLightDismiss = false,
            Mode = DialogMode.None
        };
        var vm = App.Instance.Services.GetRequiredService<NewIssueViewModel>();
        bool finish = false;
        do
        {
            var result = await OverlayDialog.ShowModal<NewIssueView, NewIssueViewModel>(vm: vm, options: opt);
            if (result == DialogResult.OK)
            {
                if (!vm.IsValid)
                {
                    ToastManager?.Show("参数错误！");
                }
                else
                {
                    IssueInfo? issue;
                    (finish, issue) = await Task.Run(() =>
                    {
                        var ok = _api.CreateIssue(out IssueInfo? info, project.Id,
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
