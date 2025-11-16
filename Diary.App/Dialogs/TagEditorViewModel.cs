using System;
using Diary.App.ViewModels;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.App.Dialogs;

public class TagEditorViewModel: ViewModelBase, IDialogContext
{
    public void Close()
    {
        RequestClose?.Invoke(this, null);
    }

    public event EventHandler<object?>? RequestClose;
}