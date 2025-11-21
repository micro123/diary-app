using System;
using Diary.App.ViewModels;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.App.Dialogs;

[DiAutoRegister]
public class TemplateEditorViewModel: ViewModelBase, IDialogContext
{
    public void Close()
    {
        RequestClose?.Invoke(this, null);
    }

    public event EventHandler<object?>? RequestClose;
}