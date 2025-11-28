using CommunityToolkit.Mvvm.Messaging;
using Diary.App.Messages;
using Diary.Utils;

namespace Diary.App.Utils;

public static class EventDispatcher
{
    private static WeakReferenceMessenger Messenger => WeakReferenceMessenger.Default;

    public static void Notify(string title, string body)
    {
        var opt = new NotifyOptions(title, body);
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

    public static void ShowToast(string content)
    {
        Messenger.Send(new ToastEvent(content));
    }

    public static void RunCommand(string command)
    {
        Messenger.Send(new RunCommandEvent(command));
    }
}