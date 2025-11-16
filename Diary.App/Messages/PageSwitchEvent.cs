using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Diary.App.Messages;

public class PageSwitchEvent(string value) : ValueChangedMessage<string>(value);