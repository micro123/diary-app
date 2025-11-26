using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Diary.App.Messages;

public class WindowStateEvent(bool opened) : ValueChangedMessage<bool>(opened);
