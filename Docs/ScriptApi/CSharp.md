# C# 脚本 API Reference

本文对应 `ScriptApiVersion.V1`。C# 脚本在独立 Worker 或进程内执行时使用相同的 `Diary.ScriptBase` 契约和 `Diary.ScriptHost` API。宿主只注册已经实现的 API，脚本不需要声明或申请 capability。

## 1. 最小脚本

```csharp
using System.Threading;
using System.Threading.Tasks;
using Diary.ScriptBase;

public sealed class DemoScript : IScriptProgramV1
{
    public ScriptDescriptor Descriptor { get; } = new(
        "demo", "示例", ScriptApiVersion.V1, ScriptScope.Application,
        Description: "脚本说明");

    public ValueTask<ScriptExecutionResult> ExecuteAsync(
        ScriptExecutionRequest request,
        IScriptExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(ScriptExecutionResult.Succeeded());
    }
}
```

入口必须是同步或异步的 `ExecuteAsync`。长时间运行的 API 调用应传递 `cancellationToken`。脚本只能通过 `GetApi<T>()` 获取宿主 API，不能访问 `IServiceProvider`、数据库对象或 UI 对象。

## 2. Descriptor

`ScriptDescriptor` 字段：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `Id` | `string` | 稳定的脚本 ID，用于加载、执行和历史记录。 |
| `Name` | `string` | UI 展示名称。 |
| `ApiVersion` | `ScriptApiVersion` | 当前使用 `V1`。 |
| `Scope` | `ScriptScope` | `Application` 或 `Editor`。 |
| `Description` | `string?` | 可选描述。 |

脚本 metadata 仍可包含旧版 `capabilities` 字段，但当前会忽略该字段，不再作为 API 门禁。

## 3. 执行请求和上下文

`ScriptExecutionRequest`：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `Target` | `ScriptTarget` | 必填。作用域必须与 Descriptor 一致。 |
| `Arguments` | `ImmutableDictionary<string, string>?` | 用户传入的字符串参数。 |
| `Source` | `ScriptExecutionSource` | `Manual`、`Editor`、`Startup` 或 `Automation`。 |

编辑器脚本从 `request.Target.Editor` 读取 `StartDate`、`EndDate`、`Granularity`；业务目标从 `request.Target.Business` 读取。

`IScriptExecutionContext`：

| 成员 | 说明 |
| --- | --- |
| `Metadata` | 执行 ID、开始时间、来源和脚本 ID。 |
| `GetApi<TApi>()` | 获取已注册 API。API 未实现或不可用时返回 `null`。 |

## 4. 查询工作项

```csharp
using Diary.ScriptHost;

var api = context.GetApi<IDiaryApi>();
if (api is null)
    return new(ScriptExecutionStatus.Failed, []);

var result = await api.QueryAsync(new ScriptWorkItemQuery
{
    StartDate = "2026-08-01",
    EndDate = "2026-08-31",
    TagIds = [1, 2],
    TagFilter = ScriptWorkItemTagFilter.Any,
    Text = "Worker",
    Priority = 0,
    Limit = 100,
    Offset = 0,
}, cancellationToken);

if (!result.Succeeded)
    return new(ScriptExecutionStatus.Failed, []);

foreach (var item in result.Items)
    Console.WriteLine($"{item.Date}: {item.Comment} ({item.Hours}h)");
```

查询字段：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `StartDate` / `EndDate` | `string?` | 包含边界，格式 `yyyy-MM-dd`。 |
| `TagIds` | `ImmutableArray<int>` | 标签 ID。最多 100 个。 |
| `TagFilter` | `ScriptWorkItemTagFilter` | `Ignore`、`Any`、`All`、`None`、`Exact`。 |
| `Text` | `string?` | 匹配工作项标题、备注等文本。 |
| `Priority` | `int?` | 优先级，当前范围为 0 到 9。 |
| `Limit` | `int?` | 默认 100，最大 1000。 |
| `Offset` | `int` | 默认 0，最大 10000。 |

返回的 `ScriptWorkItemQueryResult` 包含 `Succeeded`、`Items`、`NormalizedQuery` 和 `Error`。`ScriptWorkItem` 仅是安全 DTO：`Id`、`Date`、`Comment`、`Hours`、`Priority`、`Note`、`Tags`。该 API 没有更新或删除方法。

## 5. 创建日志项

### 流式查询大量明细

全年等大范围明细应使用 `StreamAsync`。当前实现按页调用宿主查询，每页最多 500 条，不会将全年结果放入单条 Worker 消息：

```csharp
await foreach (var item in api.StreamAsync(new ScriptWorkItemQuery
{
    StartDate = "2026-01-01",
    EndDate = "2026-12-31",
}, pageSize: 500, cancellationToken))
{
    // 逐项处理；不要自行保存全部结果，除非业务确实需要。
}
```

`workItems.query` 单次最多返回 1000 条；`StreamAsync` 页大小必须在 1 到 500 之间。第一版是 Worker 通信层的分页式流，不是数据库 reader 流；查询期间数据变化可能影响 offset 分页边界。

创建日志项只会新建工作项，不会查找、修改或删除已有工作项。

```csharp
var api = context.GetApi<IDiaryApi>();
if (api is null)
    return new(ScriptExecutionStatus.Failed, []);

var result = await api.CreateLogItemAsync(new ScriptLogItemRequest(
    Date: "2026-08-08",
    Hours: 2.5,
    Title: "完善脚本 Worker",
    Note: "补充跨语言宿主 API"), cancellationToken);

if (!result.Succeeded)
    return new(ScriptExecutionStatus.Failed, []);

var created = result.Item!;
```

| 字段 | 类型 | 约束 |
| --- | --- | --- |
| `Date` | `string` | 必须是 `yyyy-MM-dd`。 |
| `Hours` | `double` | 大于 0 且不超过 24。 |
| `Title` | `string` | 非空，最多 500 字符。 |
| `Note` | `string?` | 可选，最多 10000 字符。 |

失败时 `Error.Code` 可能是 `InvalidInput`、`DatabaseUnavailable`、`ProviderFailure` 或 `Cancelled`。成功时 `Item` 返回新建工作项 DTO。脚本不能从该 API 获得可变 `WorkItem` 对象。

## 6. 按模板创建日志项

## 7. Tracker 实例目录

```csharp
var diary = context.GetApi<IDiaryApi>();
var result = await diary!.CreateFromTemplateAsync(new ScriptTemplateLogItemRequest(
    Date: "2026-08-08",
    TemplateId: "00000000-0000-0000-0000-000000000001",
    Hours: 2.5,
    Title: null,
    Note: "按模板记录"), cancellationToken);
```

标题为空时使用模板默认标题；工时使用调用参数；模板默认标签会应用到新建工作项。`Date` 必须是 `yyyy-MM-dd`，`TemplateId` 必须是 UUID。该 API 只创建新工作项，不修改或删除已有项。

```csharp
var api = context.GetApi<ITrackerApi>();
var result = api?.GetInstance("tracker.memory", "company");
if (result is { Succeeded: true })
    Console.WriteLine(result.Instance!.DisplayName);
```

`Get(pluginId, instanceId)` 返回 `ScriptTrackerInstance`：`PluginId`、`InstanceId`、`DisplayName`、`Icon`、`IsConfigured`。错误代码为 `InvalidInput` 或 `InstanceUnavailable`。该 API 不暴露 Tracker 客户端、配置、数据库或 DI。

## 8. 剪贴板

```csharp
var system = context.GetApi<ISystemInteractionApi>();
var oldText = await system!.GetClipboardTextAsync(cancellationToken);
var succeeded = await system.SetClipboardTextAsync("复制内容", cancellationToken);
```

`GetTextAsync` 返回文本或 `null`；`SetTextAsync` 返回是否成功。只支持文本，不支持图片、文件列表等剪贴板格式。

## 9. 用户交互

```csharp
var system = context.GetApi<ISystemInteractionApi>();
await system!.NotifyAsync("脚本完成", "日志项已创建。", cancellationToken);
var confirmed = await system.ConfirmAsync("继续操作", "是否继续？", cancellationToken);
```

`NotifyAsync` 显示通知；`ConfirmAsync` 返回用户是否确认。自动化或后台执行时 UI 可能不可用，应捕获异常并将失败作为脚本诊断处理。

## 10. Worker API 映射和限制

| C# API | Worker HostCall |
| --- | --- |
| `IDiaryApi.QueryAsync` | `workItems.query` |
| `IDiaryApi.CreateLogItemAsync` | `logItems.create` |
| `IDiaryApi.CreateFromTemplateAsync` | `templateLogItems.create` |
| `ITrackerApi.GetInstance` | `trackerInstances.get` |
| `ISystemInteractionApi.GetClipboardTextAsync` | `clipboard.get` |
| `ISystemInteractionApi.SetClipboardTextAsync` | `clipboard.set` |
| `ISystemInteractionApi.NotifyAsync` | `ui.notify` |
| `ISystemInteractionApi.ConfirmAsync` | `ui.confirm` |

Worker 调用由主进程执行并返回结构化结果。脚本不能直接访问文件、网络、进程、反射、数据库、DI 或任意 UI 控件。超时、取消、Worker 退出和宿主失败都会转换为执行诊断。
