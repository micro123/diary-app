using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Diary.GUIBase.Events;

public class WindowStateEvent(bool opened) : ValueChangedMessage<bool>(opened);
