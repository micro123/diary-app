using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Diary.App.Messages;

public class NotifyOptions(string title, string body)
{
    public string Title { get; } = title;
    public string Body { get; } = body;
}

public class NotifyEvent(string title, string body): ValueChangedMessage<NotifyOptions>(new NotifyOptions(title, body));