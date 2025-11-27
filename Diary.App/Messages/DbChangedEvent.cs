using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Diary.App.Messages;

public class DbChangedEvent(uint what) : ValueChangedMessage<uint>(what)
{
    public const uint All = 0xFFFF;
    public const uint RedMineIssue = 0x1;
    public const uint RedMineActivity = 0x2;
    // public const uint RedMine
    public const uint WorkTags = 0x4;
}