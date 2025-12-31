using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Diary.GUIBase.Events;

public class PageSwitchEvent(string value) : ValueChangedMessage<string>(value);