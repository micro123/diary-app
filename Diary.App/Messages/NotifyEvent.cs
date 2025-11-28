using CommunityToolkit.Mvvm.Messaging.Messages;
using Ursa.Controls;

namespace Diary.App.Messages;

public class NotifyOptions(
    string title,
    string body,
    bool modal = false,
    bool lightDismiss = true,
    DialogMode mode = DialogMode.None,
    DialogButton button = DialogButton.OK)
{
    public string Title { get; } = title;
    public string Body { get; } = body;
    public DialogMode Mode { get; } = mode;
    public DialogButton Button { get; } = button;
    public bool Modal { get; } = modal;
    public bool LightDismiss { get; } = lightDismiss;
}

public class NotifyEvent(NotifyOptions options) : ValueChangedMessage<NotifyOptions>(options);