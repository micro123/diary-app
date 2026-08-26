using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Diary.GUIBase.Events;

public enum NotificationRetention
{
    Transient,
    Session,
    Persistent,
}

public sealed record NotificationAction(string Label, string Command, string? Argument = null);

public sealed class ToastEvent(
    string text,
    NotificationType type = NotificationType.Information,
    NotificationRetention retention = NotificationRetention.Transient,
    string? title = null,
    NotificationAction? action = null)
    : ValueChangedMessage<string>(text)
{
    public NotificationType Type { get; } = type;
    public NotificationRetention Retention { get; } = retention;
    public string? Title { get; } = title;
    public NotificationAction? Action { get; } = action;
}
