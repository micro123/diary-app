using System;
using CommunityToolkit.Mvvm.Input;
using Diary.App.ViewModels;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.App.Dialogs;

[DiAutoRegister]
public partial class DatabaseConfigViewModel: ViewModelBase, IDialogContext
{
    public void Close()
    {
        RequestClose?.Invoke(this, null);
    }

    public event EventHandler<object?>? RequestClose;

    [RelayCommand]
    void Save()
    {
        RequestClose?.Invoke(this, true);
    }
}