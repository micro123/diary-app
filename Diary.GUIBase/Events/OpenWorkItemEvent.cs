using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Diary.GUIBase.Events;

public sealed class OpenWorkItemEvent(string date, int workItemId)
    : ValueChangedMessage<(string Date, int WorkItemId)>((date, workItemId));
