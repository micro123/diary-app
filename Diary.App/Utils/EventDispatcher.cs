using CommunityToolkit.Mvvm.Messaging;
using Diary.App.Messages;
using Diary.Utils;

namespace Diary.App.Utils;

public static class EventDispatcher
{
    private static WeakReferenceMessenger Messenger => WeakReferenceMessenger.Default;

    public static void Notify(string title, string body)
    {
        Messenger.Send(new NotifyEvent(title, body));
    }

    public static void RouteToPage(string page)
    {
        Messenger.Send(new PageSwitchEvent(page));
    }

    public static void DbChanged()
    {
        Messenger.Send(new DbChangedEvent());
    }

    public static void ShowToast(string content)
    {
        Messenger.Send(new ToastEvent(content));
    }
}