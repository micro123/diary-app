using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Diary.App.Messages;

public class DbChangedEvent(): ValueChangedMessage<int>(0);