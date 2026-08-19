using System.Collections.Immutable;
using Diary.ScriptBase;

namespace Diary.ScriptHost;

/// <summary>
/// 面向 C# 脚本常见任务的简写 API。底层结果对象仍保留，复杂场景可继续直接调用原始接口。
/// </summary>
public static class ScriptApiConvenienceExtensions
{
    /// <summary>查询今天的工作项。</summary>
    public static ValueTask<ScriptWorkItemQueryResult> QueryTodayAsync(
        this IDiaryApi api,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        return api.QueryAsync(new ScriptWorkItemQuery { Range = "today", Limit = limit }, cancellationToken);
    }

    /// <summary>按 yyyy-MM-dd 闭区间查询工作项。</summary>
    public static ValueTask<ScriptWorkItemQueryResult> QueryRangeAsync(
        this IDiaryApi api,
        string startDate,
        string endDate,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        return api.QueryAsync(new ScriptWorkItemQuery
        {
            StartDate = startDate,
            EndDate = endDate,
            Limit = limit,
        }, cancellationToken);
    }

    /// <summary>使用常用字段创建一条日志记录。</summary>
    public static ValueTask<ScriptLogItemResult> CreateLogItemAsync(
        this IDiaryApi api,
        string date,
        double hours,
        string title,
        string? note = null,
        string? idempotencyKey = null,
        bool preview = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        return api.CreateLogItemAsync(
            new ScriptLogItemRequest(date, hours, title, note, idempotencyKey, preview),
            cancellationToken);
    }

    /// <summary>返回成功查询的项目；失败时抛出包含稳定错误码的异常。</summary>
    public static ImmutableArray<ScriptWorkItem> EnsureSucceeded(this ScriptWorkItemQueryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Succeeded)
            return result.Items;
        throw CreateException(result.ApiError, "工作项查询失败。");
    }

    /// <summary>返回成功创建的项目；失败时抛出包含稳定错误码的异常。</summary>
    public static ScriptWorkItem EnsureSucceeded(this ScriptLogItemResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Succeeded && result.Item is not null)
            return result.Item;
        throw CreateException(result.ApiError, "日志记录创建失败。");
    }

    /// <summary>返回成功的导出结果；失败时抛出包含稳定错误码的异常。</summary>
    public static ExportResult EnsureSucceeded(this ExportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Succeeded)
            return result;
        throw CreateException(result.Error, "导出失败。");
    }

    private static ScriptApiCallException CreateException(ScriptApiError? error, string fallbackMessage) =>
        new(error?.Code ?? "SCRIPT_API_CALL_FAILED", error?.Message ?? fallbackMessage, error);
}

/// <summary>脚本宿主 API 返回失败结果时由便利方法抛出的异常。</summary>
public sealed class ScriptApiCallException(
    string code,
    string message,
    ScriptApiError? error = null) : InvalidOperationException($"[{code}] {message}")
{
    public string Code { get; } = code;
    public ScriptApiError? Error { get; } = error;
}
