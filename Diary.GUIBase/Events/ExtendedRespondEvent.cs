using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Diary.GUIBase.Events;

public sealed class ExtendedRespondEvent(string respond) : ValueChangedMessage<string>(respond)
{
}
