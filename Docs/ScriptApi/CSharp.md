# C# 脚本 API 参考

## 脚本入口

C# 脚本必须实现 `Diary.ScriptBase.IScriptProgramV1`。`Descriptor` 声明脚本 ID、名称、作用域和申请的能力，`ExecuteAsync` 是执行入口。

```csharp
using System.Threading;
using System.Threading.Tasks;
using Diary.ScriptBase;
using Diary.ScriptHost;

public sealed class DemoScript : IScriptProgramV1
{
    public ScriptDescriptor Descriptor { get; } = new(
        "demo", "示例", ScriptApiVersion.V1,
        ScriptScope.Application, ScriptCapability.ReadDiary);

    public async ValueTask<ScriptExecutionResult> ExecuteAsync(
        ScriptExecutionRequest request,
        IScriptExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var api = context.GetApi<IWorkItemQueryScriptApi>();
        if (api is null)
            return new(ScriptExecutionStatus.Rejected, []);

        var result = await api.QueryAsync(new ScriptWorkItemQuery
        {
            StartDate = "2026-08-01",
            EndDate = "2026-08-31",
            Limit = 100,
        }, cancellationToken);
        return result.Succeeded
            ? ScriptExecutionResult.Succeeded()
            : new(ScriptExecutionStatus.Failed, []);
    }
}
```

## 执行请求

`ScriptExecutionRequest` 提供以下属性：

| 属性 | 类型 | 含义 |
| --- | --- | --- |
| `Target` | `ScriptTarget` | 应用脚本或编辑器脚本的执行目标。 |
| `Arguments` | `ImmutableDictionary<string, string>?` | 用户传入的执行参数。 |
| `Source` | `ScriptExecutionSource` | `Manual`、`Editor`、`Startup` 或 `Automation`。 |

编辑器脚本可以从 `request.Target.Editor` 读取 `StartDate`、`EndDate` 和 `Granularity`。业务目标位于 `request.Target.Business`。

## 执行上下文

`IScriptExecutionContext` 提供已授权的 `Capabilities`、可选的执行 `Metadata` 和 `GetApi<TApi>()`。当 API 未注册或所需能力未授权时，`GetApi<TApi>()` 返回 `null`。

## 查询工作项

`IWorkItemQueryScriptApi` 需要 `ScriptCapability.ReadDiary` 能力。

`ScriptWorkItemQuery` 字段：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `StartDate`、`EndDate` | `string?` | 包含边界的 ISO 日期范围，格式为 `yyyy-MM-dd`。 |
| `TagIds` | `ImmutableArray<int>` | 配合 `TagFilter` 使用的标签 ID。 |
| `TagFilter` | `ScriptWorkItemTagFilter` | `Ignore`、`Any`、`All`、`None` 或 `Exact`。 |
| `Text` | `string?` | 文本筛选条件。 |
| `Priority` | `int?` | 可选的优先级筛选。 |
| `Limit` | `int?` | 最大返回数量。 |
| `Offset` | `int` | 分页偏移量。 |

结果包含 `Succeeded`、`Items`、`NormalizedQuery` 和 `Error`。每个工作项包含 `Id`、`Date`、`Comment`、`Hours`、`Priority`、`Note` 和安全的标签 DTO。

## 能力与沙箱限制

- 脚本只能取得宿主已注册且 `Descriptor` 已申请的 API。
- `ReadDiary` 当前用于启用工作项只读查询。
- `Tracker` 当前只提供进程内只读实例目录，尚未通过所有 Worker 转发，不应在可移植脚本中使用。
- 文件、进程、网络、反射等被禁止的 API 会在 C# 脚本检查阶段被拒绝。
