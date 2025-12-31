using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Diary.GUIBase.Events;

public class ToastEvent(string text): ValueChangedMessage<string>(text);