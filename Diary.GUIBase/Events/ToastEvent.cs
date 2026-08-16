using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Diary.GUIBase.Events;

public sealed class ToastEvent(string text, NotificationType type = NotificationType.Information)
    : ValueChangedMessage<string>(text)
{
    public NotificationType Type { get; } = type;
}
