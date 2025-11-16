using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Diary.App.Messages;

public class RunCommandEvent(string command) : ValueChangedMessage<string>(command);