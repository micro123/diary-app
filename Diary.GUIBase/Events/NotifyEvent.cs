using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.Messaging.Messages;
using Ursa.Controls;

namespace Diary.GUIBase.Events;

public class NotifyOptions(
    string title,
    string body,
    bool modal = false,
    bool lightDismiss = true,
    DialogMode mode = DialogMode.None,
    DialogButton button = DialogButton.OK,
    NotificationRetention retention = NotificationRetention.Persistent,
    NotificationAction? action = null,
    NotificationType type = NotificationType.Information)
{
    public string Title { get; } = title;
    public string Body { get; } = body;
    public DialogMode Mode { get; } = mode;
    public DialogButton Button { get; } = button;
    public bool Modal { get; } = modal;
    public bool LightDismiss { get; } = lightDismiss;
    public NotificationRetention Retention { get; } = retention;
    public NotificationAction? Action { get; } = action;
    public NotificationType Type { get; } = type;
}

public class NotifyEvent(NotifyOptions options) : ValueChangedMessage<NotifyOptions>(options);
