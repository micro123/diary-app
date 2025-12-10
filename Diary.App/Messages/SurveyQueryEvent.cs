using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Diary.App.Messages;

/// <summary>
/// 调查消息，作为服务端准备向客户端发送调查
/// </summary>
/// <param name="query">调查的参数</param>
public class SurveyQueryEvent(string query): ValueChangedMessage<string>(query)
{
    
}