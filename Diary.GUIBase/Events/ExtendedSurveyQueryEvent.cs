using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Diary.GUIBase.Events;

public sealed class ExtendedSurveyQueryEvent(string query) : ValueChangedMessage<string>(query)
{
}
