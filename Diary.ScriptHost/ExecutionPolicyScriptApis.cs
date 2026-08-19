using Diary.Script.Runtime;
using Diary.ScriptBase;

namespace Diary.ScriptHost;

public sealed class ExecutionPolicyLogItemScriptApi(
    ILogItemScriptApi inner,
    ScriptHostCallContext context) : ILogItemScriptApi
{
    public ValueTask<ScriptLogItemResult> CreateAsync(
        ScriptLogItemRequest request,
        CancellationToken cancellationToken = default)
    {
        if (context.EntryKind == ScriptEntryKind.Query)
            return ValueTask.FromResult(ScriptLogItemResult.Failure(
                ScriptLogItemErrorCode.PermissionDenied,
                "查询脚本为只读入口，不允许创建日志记录。"));

        return inner.CreateAsync(
            context.Preview ? request with { Preview = true } : request,
            cancellationToken);
    }
}

public sealed class ExecutionPolicyTemplateLogItemScriptApi(
    ITemplateLogItemScriptApi inner,
    ScriptHostCallContext context) : ITemplateLogItemScriptApi
{
    public ValueTask<ScriptLogItemResult> CreateAsync(
        ScriptTemplateLogItemRequest request,
        CancellationToken cancellationToken = default)
    {
        if (context.EntryKind == ScriptEntryKind.Query)
            return ValueTask.FromResult(ScriptLogItemResult.Failure(
                ScriptLogItemErrorCode.PermissionDenied,
                "查询脚本为只读入口，不允许按模板创建日志记录。"));

        return inner.CreateAsync(
            context.Preview ? request with { Preview = true } : request,
            cancellationToken);
    }
}

public sealed class ExecutionPolicyClipboardScriptApi(
    IClipboardScriptApi inner,
    ScriptHostCallContext context) : IClipboardScriptApi
{
    public ValueTask<string?> GetTextAsync(CancellationToken cancellationToken = default) =>
        inner.GetTextAsync(cancellationToken);

    public ValueTask<bool> SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (context.EntryKind == ScriptEntryKind.Query || context.Preview)
            return ValueTask.FromException<bool>(new InvalidOperationException(
                context.Preview
                    ? "预览执行不允许写入剪贴板。"
                    : "查询脚本为只读入口，不允许写入剪贴板。"));

        return inner.SetTextAsync(text, cancellationToken);
    }
}
