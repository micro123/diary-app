using System;
using CommunityToolkit.Mvvm.Messaging.Messages;
using Diary.Utils;

namespace Diary.App.Messages;

public class QuickStatisticsEvent(DateTime date, AdjustPart part): ValueChangedMessage<(DateTime,AdjustPart)>((date, part));