using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.GUIBase.ViewModels;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;
using Ursa.Controls;

namespace Diary.App.ViewModels.Dialogs;

[DiAutoRegister]
public partial class StandardMessageViewModel : ViewModelBase, IDialogContext
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    [NotifyPropertyChangedFor(nameof(ShowMessageKind))]
    private string _title = "通知";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayBody))]
    [NotifyPropertyChangedFor(nameof(HasBody))]
    [NotifyPropertyChangedFor(nameof(GuidanceText))]
    [NotifyCanExecuteChangedFor(nameof(CopyBodyCommand))]
    private string _body = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInformation))]
    [NotifyPropertyChangedFor(nameof(IsSuccess))]
    [NotifyPropertyChangedFor(nameof(IsWarning))]
    [NotifyPropertyChangedFor(nameof(IsError))]
    [NotifyPropertyChangedFor(nameof(MessageKind))]
    [NotifyPropertyChangedFor(nameof(GuidanceText))]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    [NotifyPropertyChangedFor(nameof(ShowMessageKind))]
    private NotificationType _messageType = NotificationType.Information;

    public string DisplayBody => HasBody ? Body : "没有更多详细信息。";
    public string DisplayTitle => IsGenericTitle(Title) ? MessageKind : Title;
    public bool HasBody => !string.IsNullOrWhiteSpace(Body);
    public bool ShowMessageKind => !string.Equals(DisplayTitle, MessageKind, StringComparison.Ordinal);
    public bool IsInformation => MessageType == NotificationType.Information;
    public bool IsSuccess => MessageType == NotificationType.Success;
    public bool IsWarning => MessageType == NotificationType.Warning;
    public bool IsError => MessageType == NotificationType.Error;

    public string MessageKind => MessageType switch
    {
        NotificationType.Success => "操作成功",
        NotificationType.Warning => "需要注意",
        NotificationType.Error => "操作失败",
        _ => "通知",
    };

    public string GuidanceText => HasBody
        ? MessageType switch
        {
            NotificationType.Success => "操作已完成，可查看详细结果。",
            NotificationType.Warning => "请确认消息内容和建议的后续操作。",
            NotificationType.Error => "操作未完成，请根据详细信息处理。",
            _ => "请查看以下消息内容。",
        }
        : MessageType switch
        {
            NotificationType.Success => "操作已完成。",
            NotificationType.Warning => "请确认当前状态后再继续。",
            NotificationType.Error => "操作未完成，但没有提供更多错误信息。",
            _ => "没有提供更多消息内容。",
        };

    public void Initialize(string title, string body, NotificationType type)
    {
        Title = string.IsNullOrWhiteSpace(title) ? "通知" : title;
        Body = body ?? string.Empty;
        MessageType = type;
    }

    private static bool IsGenericTitle(string title) => title.Trim() switch
    {
        "通知" or "提示" or "成功" or "警告" or "错误" or "失败" or "异常" => true,
        _ => false,
    };

    [RelayCommand(CanExecute = nameof(HasBody))]
    private async Task CopyBody()
    {
        if (await CopyStringToClipboardAsync(Body))
            ToastManager?.Show("消息内容已复制");
    }

    [RelayCommand]
    private void Confirm() => RequestClose?.Invoke(this, DialogResult.OK);

    public void Close() => Confirm();

    public event EventHandler<object?>? RequestClose;
}
