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
         ScriptScope.Application);

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

`IScriptExecutionContext` 提供执行 `Metadata` 和 `GetApi<TApi>()`。当 API 未注册时，`GetApi<TApi>()` 返回 `null`。

## 查询工作项

`IWorkItemQueryScriptApi` 默认可用，只提供受宿主约束的只读查询。

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

## 宿主 API

脚本默认可以访问宿主已经实现的 API，不再需要在 metadata 中申请权限。当前 C# Worker 提供：

- `IWorkItemQueryScriptApi`：调用 `QueryAsync` 查询工作项。
- `ITrackerInstanceScriptApi`：使用 `Get(pluginId, instanceId)` 查询指定 Tracker 实例的安全 DTO。
- `IClipboardScriptApi`：使用 `GetTextAsync` 和 `SetTextAsync` 读写系统文本剪贴板。
- `IUserInteractionScriptApi`：使用 `NotifyAsync` 显示通知，使用 `ConfirmAsync` 请求用户确认。

Worker 会将调用转发到主进程；进程内执行使用同一份 API 契约。API 不可用时返回结构化失败结果，不会暴露数据库或 DI 对象。

## 能力与沙箱限制

- 宿主注册的 API 默认可用；脚本契约不再包含 capability 字段。
- C# Worker 当前支持 `workItems.query`、`trackerInstances.get`、`clipboard.get`、`clipboard.set`、`ui.notify` 和 `ui.confirm`。
- `workItems.query` 已包含工作项的备注和标签，因此当前只读日记查询使用该 API，不额外暴露数据库接口。
- `WriteDiary` 和 Tracker 写入尚未实现；脚本不能通过默认全权限直接修改日记或远程 Tracker 数据。
- 文件、进程、网络、反射等被禁止的 API 会在 C# 脚本检查阶段被拒绝。
