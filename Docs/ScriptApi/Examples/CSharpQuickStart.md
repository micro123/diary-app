# C# 5 分钟入门：查询并追加日志项

在脚本创建向导中选择 C# 和 Application 入口，将下面代码放入生成的脚本文件。C# 脚本默认由 Worker 执行，宿主通过 `IScriptApplicationContext` 提供 API。

```csharp
using System.Collections.Immutable;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Diary.ScriptBase;
using Diary.ScriptHost;

public sealed class DailySummaryScript : ApplicationScriptV2
{
    public override string Id => "daily-summary";
    public override string Name => "每日摘要";
    public override string? Description => "查询当天工作项并追加一条摘要记录";
    public override IReadOnlyList<ScriptParameterDefinition> Parameters =>
    [
        new("date", "日期", ScriptParameterType.Date, Required: true),
    ];

    public override async ValueTask<ScriptExecutionResult> ExecuteAsync(
        IScriptApplicationContext context,
        CancellationToken cancellationToken = default)
    {
        var diary = context.GetRequiredApi<IDiaryApi>();
        var date = context.Arguments["date"];

        var query = await diary.QueryAsync(new ScriptWorkItemQuery
        {
            StartDate = date,
            EndDate = date,
            Limit = 100,
        }, cancellationToken);
        if (!query.Succeeded)
            return Failed(query.ApiError);

        await context.ReportProgressAsync(new ScriptProgressUpdate(
            0.5, $"已查询 {query.Items.Length} 条工作项"));

        var append = await diary.CreateLogItemAsync(new ScriptLogItemRequest(
            Date: date,
            Hours: 0.5,
            Title: $"{date} 工作摘要",
            Note: $"当天共有 {query.Items.Length} 条工作项。",
            IdempotencyKey: $"daily-summary:{date}",
            Preview: context.Metadata?.Preview == true), cancellationToken);
        if (!append.Succeeded)
            return Failed(append.ApiError);

        return new(ScriptExecutionStatus.Succeeded, ImmutableArray<ScriptDiagnostic>.Empty, append.Effects);
    }

    private static ScriptExecutionResult Failed(ScriptApiError? error) =>
        new(
            ScriptExecutionStatus.Failed,
            [new ScriptDiagnostic(
                error?.Code ?? "SCRIPT_API_FAILED",
                error?.Message ?? "脚本 API 调用失败。",
                ScriptDiagnosticSeverity.Error,
                ScriptDiagnosticCategory.Host)]);
}
```

使用说明：

- 示例是 V2 脚本，管理页会根据 `Parameters` 显示必填日期控件，并在执行前校验输入。
- `QueryAsync` 只读查询；`CreateLogItemAsync` 只追加新记录，不修改或删除历史记录。
- `IdempotencyKey` 用业务动作和日期组成，重复执行会返回已提交结果而不再次追加。
- 预览执行通过请求的 `Preview` 标志传播到 API，只返回投影记录和副作用摘要。
- API 失败使用稳定的 `ApiError.Code` 处理，不要把所有失败都当成数据库错误。

相关章节：[C# API Reference](../CSharp.md)。
