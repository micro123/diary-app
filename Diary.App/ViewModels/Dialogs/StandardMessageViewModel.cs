using CommunityToolkit.Mvvm.ComponentModel;
using Diary.GUIBase.ViewModels;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;
using Ursa.Controls;

namespace Diary.App.ViewModels.Dialogs;

[DiAutoRegister]
public partial class StandardMessageViewModel : ViewModelBase, IDialogContext
{
    [ObservableProperty] private string _body = string.Empty;

    public void Close()
    {
        RequestClose?.Invoke(this, DialogResult.Cancel);
    }

    public event EventHandler<object?>? RequestClose;
}