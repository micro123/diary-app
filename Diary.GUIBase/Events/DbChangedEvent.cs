using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Diary.GUIBase.Events;

public class DbChangedEvent(uint what) : ValueChangedMessage<uint>(what)
{
    public const uint All = 0xFFFF;
    public const uint WorkTags = 0x4;

    public const uint ShareData = 0x100;
}
