using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.Messaging;
using Diary.GUIBase.Events;

namespace Diary.GUIBase.Utils;

public static class EventDispatcher
{
    private static WeakReferenceMessenger Messenger => WeakReferenceMessenger.Default;

    public static void Notify(
        string title,
        string body,
        NotificationRetention retention = NotificationRetention.Persistent,
        NotificationAction? action = null,
        NotificationType type = NotificationType.Information)
    {
        var opt = new NotifyOptions(title, body, retention: retention, action: action, type: type);
        Messenger.Send(new NotifyEvent(opt));
    }

    public static void RouteToPage(string page)
    {
        Messenger.Send(new PageSwitchEvent(page));
    }

    public static void DbChanged(uint what = DbChangedEvent.All)
    {
        Messenger.Send(new DbChangedEvent(what));
    }

    public static void ShowToast(
        string content,
        NotificationType type = NotificationType.Information,
        NotificationRetention retention = NotificationRetention.Transient,
        string? title = null,
        NotificationAction? action = null)
    {
        Messenger.Send(new ToastEvent(content, type, retention, title, action));
    }

    public static void RunCommand(string command)
    {
        Messenger.Send(new RunCommandEvent(command));
    }

    public static void Msg<T>(T msg) where T : class
    {
        Messenger.Send(msg);
    }

    public static async Task<bool> Confirm(string title, string body)
    {
        var msg = new ConfirmRequest<ConfirmMessage, bool>(new ConfirmMessage { Title = title, Message = body });
        Messenger.Send(msg);
        try
        {
            return await msg.Task;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
