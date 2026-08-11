using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Diary.GUIBase.Events;

public sealed class ExtendedSurveyRequestEvent(string query) : ValueChangedMessage<string>(query)
{
}
