using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Diary.App.Messages;

/// <summary>
/// 客户端发来的调查结果消息
/// </summary>
/// <param name="respond">内容</param>
public sealed class RespondEvent(string respond): ValueChangedMessage<string>(respond)
{
    
}