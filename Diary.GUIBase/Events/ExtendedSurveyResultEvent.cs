using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Diary.GUIBase.Events;

public sealed class ExtendedSurveyResultEvent(string content) : ValueChangedMessage<string>(content)
{
}
