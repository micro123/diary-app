using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Diary.App.Messages;

public class ToastEvent(string text): ValueChangedMessage<string>(text);