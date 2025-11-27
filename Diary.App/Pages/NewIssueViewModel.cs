using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Diary.App.ViewModels;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.App.Pages;

[DiAutoRegister]
public partial class NewIssueViewModel: ViewModelBase
{
    [ObservableProperty] private string _issueTitle = string.Empty;
    [ObservableProperty] private string _issueDesc = string.Empty;
    [ObservableProperty] private bool _assignSelf = true;
    public bool IsValid => !string.IsNullOrWhiteSpace(IssueTitle);
}