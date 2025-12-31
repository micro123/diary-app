using CommunityToolkit.Mvvm.Messaging.Messages;
using Diary.Utils;

namespace Diary.GUIBase.Events;

public class QuickSurveyEvent(DateTime date, AdjustPart part)
    : ValueChangedMessage<(DateTime, AdjustPart)>((date, part));
