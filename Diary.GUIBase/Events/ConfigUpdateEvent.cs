using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Diary.GUIBase.Events;

public class ConfigUpdateEvent() : ValueChangedMessage<int>(0);