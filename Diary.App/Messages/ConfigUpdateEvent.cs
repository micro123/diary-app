using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Diary.App.Messages;

public class ConfigUpdateEvent() : ValueChangedMessage<int>(0);