using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Diary.GUIBase.Events;

public class RunCommandEvent(string command) : ValueChangedMessage<string>(command);