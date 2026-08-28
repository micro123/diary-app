using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Diary.ScriptHost;
using Diary.GUIBase.ViewModels;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.App.ViewModels.Dialogs;

public sealed partial class ScriptOptionDialogViewModel : ViewModelBase, IDialogContext
{
    public sealed record OptionItem(DialogOption Option, bool IsDefault)
    {
        public bool HasDescription => !string.IsNullOrWhiteSpace(Option.Description);
        public bool IsDestructive => Option.IsDestructive;
    }

    public string DialogTitle { get; }
    public string? Message { get; }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool RequireChoice { get; }
    public bool CanCancel => !RequireChoice;
    public string GuidanceText => RequireChoice
        ? "脚本需要选择一项后才能继续。"
        : "请选择后续操作，也可以取消本次请求。";
    public ObservableCollection<OptionItem> Options { get; } = [];

    public ScriptOptionDialogViewModel(OptionDialogRequest request)
    {
        DialogTitle = request.Title;
        Message = request.Message;
        RequireChoice = request.DismissPolicy == DialogDismissPolicy.RequireChoice;
        foreach (var option in request.Options)
            Options.Add(new OptionItem(option, string.Equals(option.Id, request.DefaultOptionId, StringComparison.Ordinal)));
    }

    [RelayCommand]
    private void Select(OptionItem? item)
    {
        if (item is not null)
            RequestClose?.Invoke(this, new OptionDialogResult(OptionDialogStatus.Selected, item.Option.Id));
    }

    public void Abort() => RequestClose?.Invoke(this, new OptionDialogResult(OptionDialogStatus.Cancelled));

    [RelayCommand]
    private void Cancel() => Abort();

    public void Close()
    {
        if (!RequireChoice)
            Abort();
    }

    public event EventHandler<object?>? RequestClose;
}
