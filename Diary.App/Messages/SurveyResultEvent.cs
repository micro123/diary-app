using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Diary.App.Messages;

/// <summary>
/// 需要回应给服务端的调查结果消息
/// </summary>
/// <param name="content">返回给服务器的内容</param>
public class SurveyResultEvent(string content): ValueChangedMessage<string>(content)
{
    
}