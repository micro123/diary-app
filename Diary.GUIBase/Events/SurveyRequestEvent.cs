using CommunityToolkit.Mvvm.Messaging.Messages;

namespace Diary.GUIBase.Events;

/// <summary>
/// 服务器发来的调查请求消息
/// </summary>
/// <param name="query">调查参数</param>
public sealed class SurveyRequestEvent(string query): ValueChangedMessage<string>(query)
{
    
}