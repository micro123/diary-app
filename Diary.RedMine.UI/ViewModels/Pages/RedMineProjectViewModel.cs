using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.GUIBase;
using Diary.GUIBase.Utils;
using Diary.GUIBase.ViewModels;
using Diary.RedMine.Response;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;
using Ursa.Controls;
using NewIssueView = Diary.RedMine.UI.Views.Pages.NewIssueView;

namespace Diary.RedMine.UI.ViewModels.Pages;

[DiAutoRegister]
public partial class RedMineProjectViewModel : PaginatedSearchViewModel<ProjectInfo>
{
    [ObservableProperty] private string _searchTerm = string.Empty;
    private readonly IRedMineApi _api;
    protected override int PageSize => _api.PageSize;

    public RedMineProjectViewModel(IRedMineApi api) => _api = api;

    [RelayCommand]
    private async Task Search()
    {
        CurrentPage = 1;
        await DoSearchInternalAsync();
    }

    protected override Task<(bool ok, IEnumerable<ProjectInfo>? results, int total)> ExecuteSearchAsync(int page)
    {
        var ok = _api.SearchProject(out var results, out var total, page, SearchTerm);
        return Task.FromResult<(bool, IEnumerable<ProjectInfo>?, int)>((ok, results, total));
    }

    [RelayCommand]
    private async Task CreateIssue(ProjectInfo project)
    {
        var options = new OverlayDialogOptions
        {
            Title = "创建问题",
            Buttons = DialogButton.OKCancel,
            CanDragMove = false,
            CanResize = false,
            CanLightDismiss = false,
            Mode = DialogMode.None,
        };
        var viewModel = BaseApp.Instance.Services.GetRequiredService<NewIssueViewModel>();
        var finished = false;
        do
        {
            var result = await OverlayDialog.ShowModal<NewIssueView, NewIssueViewModel>(vm: viewModel, options: options);
            if (result != DialogResult.OK)
            {
                finished = true;
                continue;
            }
            if (!viewModel.IsValid)
            {
                ToastManager?.Show("参数错误！");
                continue;
            }

            IssueInfo? createdIssue = null;
            var created = await Task.Run(() => _api.CreateIssue(
                out createdIssue, project.Id, viewModel.IssueTitle, viewModel.IssueDesc, viewModel.AssignSelf));
            finished = created;
            if (created)
                EventDispatcher.Notify("问题创建成功", $"新问题ID为: {createdIssue!.Id}");
            else
                ToastManager?.Show("创建问题失败了>_<");
        } while (!finished);
    }

    [RelayCommand]
    private void ShowDesc(ProjectInfo project)
        => EventDispatcher.Notify(project.Name,
            string.IsNullOrEmpty(project.Description) ? "描述是空的哟~~" : project.Description);
}
