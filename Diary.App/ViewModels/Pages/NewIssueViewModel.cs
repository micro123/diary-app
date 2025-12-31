using CommunityToolkit.Mvvm.ComponentModel;
using Diary.GUIBase.ViewModels;
using Diary.Utils;

namespace Diary.App.ViewModels.Pages;

[DiAutoRegister]
public partial class NewIssueViewModel: ViewModelBase
{
    [ObservableProperty] private string _issueTitle = string.Empty;
    [ObservableProperty] private string _issueDesc = string.Empty;
    [ObservableProperty] private bool _assignSelf = true;
    public bool IsValid => !string.IsNullOrWhiteSpace(IssueTitle);
}