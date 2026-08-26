using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.Models;
using Diary.GUIBase.ViewModels;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.App.ViewModels.Dialogs;

public sealed partial class TagExtraFieldEditorViewModel : ViewModelBase, IDialogContext
{
    public EditableTagExtraField Field { get; }
    public string Title => Field.IsNew ? "新增附加字段" : "编辑附加字段";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationMessage))]
    private string _validationMessage = string.Empty;

    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

    public TagExtraFieldEditorViewModel(EditableTagExtraField field)
    {
        Field = field;
    }

    public event EventHandler<object?>? RequestClose;

    public void Close() => Cancel();

    [RelayCommand]
    private void Save()
    {
        if (!Field.Validate(out var error))
        {
            ValidationMessage = error ?? "字段配置无效。";
            return;
        }

        RequestClose?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(this, false);
}

