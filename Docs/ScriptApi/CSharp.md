# C# 脚本 API Reference

本文对应 `ScriptApiVersion.V1`。C# 脚本统一在独立 Worker 中执行，使用 `Diary.ScriptBase` 契约和 `Diary.ScriptHost` API。宿主只注册已经实现的 API，脚本不需要声明或申请 capability。

## 1. 最小脚本

按功能选择 SDK 基类，入口和 descriptor 会自动带上对应的入口类型：

~~~csharp
using System.Threading;
using System.Threading.Tasks;
using Diary.ScriptBase;
using Diary.ScriptHost;

public sealed class DemoScript : ApplicationScript
{
    public override string Id => "demo";
    public override string Name => "示例";
    public override string? Description => "脚本说明";

    public override ValueTask<ScriptExecutionResult> ExecuteAsync(
        IScriptApplicationContext context,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(ScriptExecutionResult.Succeeded());
    }
}
~~~

编辑器脚本继承 `EditorScript`，自动化脚本继承 `AutomationScript`。底层 `IScriptProgramV1.ExecuteAsync` 由 Worker 适配器使用，不是普通脚本作者必须实现的唯一入口。

脚本不能访问 `IServiceProvider`、数据库对象或 UI 对象；宿主 API 只能通过上下文获取。

`GetApi<T>()` 适合可选 API，必需 API 使用 `GetRequiredApi<T>()`。

C# 也可以使用强类型门面：

```csharp
var api = context.Api();
var items = await api.Diary.QueryAsync(new ScriptWorkItemQuery { Limit = 10 }, cancellationToken);
var templates = api.Diary.Templates.List();
var trackers = api.Tracker.ListInstances();
```

`context.Api()` 只做已注册 API 的强类型聚合，不扩大脚本权限；缺少 API 时仍由 `GetRequiredApi<T>()` 报告不可用。

完整示例：[C# 5 分钟入门：查询并追加日志项](Examples/CSharpQuickStart.md)。

自动化脚本示例：[每日自查补录](Examples/AutomationDailyCheck.md)；查询脚本示例：[本月工时汇总](Examples/QueryMonthlySummary.md)。
右键查询“加班”工作项示例：[OvertimeWorkItems.cs](Examples/OvertimeWorkItems.cs)。

## 2. Descriptor

`ScriptDescriptor` 字段：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `Id` | `string` | 稳定的脚本 ID，用于加载、执行和历史记录。 |
| `Name` | `string` | UI 展示名称。 |
| `ApiVersion` | `ScriptApiVersion` | 当前使用 `V1`。 |
| `Scope` | `ScriptScope` | `Application` 或 `Editor`。 |
| `EntryKind` | `ScriptEntryKind?` | `Application`、`Editor`、`Automation` 或预留的 `Query`；与作用域和目标必须一致。 |
| `Description` | `string?` | 可选描述。 |

脚本 metadata 仍可包含旧版 `capabilities` 字段，但当前会忽略该字段，不再作为 API 门禁。

## 3. 执行请求和上下文

`ScriptExecutionRequest`：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `Target` | `ScriptEditorTarget?` | 应用程序扩展为 `null`；编辑器扩展由上下文菜单提供。 |
| `Arguments` | `ImmutableDictionary<string, string>?` | 用户传入的字符串参数。 |
| `Source` | `ScriptExecutionSource` | `Manual`、`Editor`、`Startup`、`Automation`、`WorkItemCreated`、`WorkItemSaved` 或 `TagAdded`。 |
| `EntryKind` | `ScriptEntryKind?` | 脚本入口类型；由宿主和 descriptor 共同校验。 |
| `IdempotencyKey` | `string?` | 追加式写入的业务幂等键；结果由宿主共享幂等存储持久化，应用重启后仍可识别已提交的重复请求。 |
| `Preview` | `bool` | 只返回待追加记录和副作用摘要，不写入数据库。 |

编辑器脚本的目标有 `Year`、`Quarter`、`Month`、`Week`、`Day` 和 `WorkItem` 六种。目标字段由宿主校验，脚本不需要自行计算季度、月份或周边界。各目标的构造方法与字段：

| `Kind` | 构造方法 | 目标字段 | 校验 |
| --- | --- | --- | --- |
| `Year` | `ScriptEditorTarget.ForYear(year)` | `Year` | 1-9999。 |
| `Quarter` | `ForQuarter(year, quarter)` | `Year` + `Quarter` | 自然季度 1-4。 |
| `Month` | `ForMonth(year, month)` | `Year` + `Month` | 1-12。 |
| `Week` | `ForWeek(weekStartDate)` | `WeekStart` | 周一的 `yyyy-MM-dd`，范围为该周周一至周日。 |
| `Day` | `ForDay(date)` | `Date` | `yyyy-MM-dd`。 |
| `WorkItem` | `ForWorkItem(workItem)` | `WorkItem` | 快照 `Id` 必须大于 0，且不允许多余目标字段。 |

执行状态 `ScriptExecutionStatus`：`Succeeded`、`Failed`、`Cancelled`、`Rejected`（入口、目标或 descriptor 校验不通过）、`TimedOut`。

`IScriptExecutionContext`：

| 成员 | 说明 |
| --- | --- |
| `Metadata` | 执行 ID、开始时间、来源、入口类型、幂等键和预览标志。 |
| `EntryKind` | 当前入口类型。 |
| `Arguments` | 用户传入的字符串参数。 |
| `CancellationToken` / `IsCancellationRequested` | 当前执行的取消信号。 |
| `ReportProgressAsync(...)` | 报告 0 到 1 之间的执行进度，不写入脚本日志。`Fraction` 越界（含 NaN）或 `Message` 为空时抛参数异常，由宿主拒绝。管理页运行脚本时进度实时显示在底部运行区，并写入执行历史条目详情（会话内存态，重启即失）。 |
| `GetApi<TApi>()` | 获取已注册 API。API 未实现或不可用时返回 `null`。 |
| `GetRequiredApi<TApi>()` | 获取必需 API；不可用时抛出宿主可转换为稳定错误码的异常。 |

应用程序扩展只接收没有目标的执行请求。编辑器扩展可以将上下文转换为 `IScriptEditorContext`：

```csharp
var editor = context as IScriptEditorContext;
if (editor is null)
    return new(ScriptExecutionStatus.Rejected, []);

var range = editor.GetDateRange();
if (range is not null)
{
    await foreach (var item in editor.StreamItemsAsync(cancellationToken))
    {
        // 处理当前年、季度、月、周或日范围内的事项。
    }
}
else if (editor.WorkItem is not null)
{
    // 事项目标直接提供不可变的 ScriptWorkItem 快照。
}
```

日期目标的 `GetDateRange()` 返回包含边界的 `ScriptDateRange`；事项目标返回 `null`。`StreamItemsAsync()` 使用当前日期范围按页迭代事项，不能用于事项目标。`Week` 目标用 `ScriptEditorTarget.ForWeek("2026-08-10")` 构造，起始日期必须是周一，范围为该周周一至周日。

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
| `Offset` | `int` | 默认 0，最大 1,000,000。 |
| `Range` | `string?` | 日期范围快捷值：`today`、`yesterday`、`thisWeek`、`thisMonth`；提供时覆盖 `StartDate`/`EndDate`，由宿主解析为实际日期范围。 |

返回的 `ScriptWorkItemQueryResult` 包含 `Succeeded`、`Items`、`NormalizedQuery` 和 `Error`，另有计算属性 `ApiError`（`ScriptApiError?`，由 `Error.ToApiError()` 计算，提供稳定大写错误码）。`NormalizedQuery` 是宿主规范化后的参数回显：`Limit` 补全默认值 100、`Offset` 补全 0、`TagFilter` 补全 `Ignore`，`Range` 快捷值已被解析为 `StartDate`/`EndDate` 不再回显。`ScriptWorkItem` 仅是安全 DTO：`Id`、`Date`、`Comment`、`Hours`、`Priority`、`Note`、`Tags` 和只读 `ExtraFields`。该 API 没有更新或删除方法。

每个 `ExtraFields` 项包含 `FieldId`、全局唯一的 `FieldKey`、标签信息、`Label`、`Type` 和 `Value`。脚本应通过稳定的 `FieldKey` 读取，例如：

```csharp
var participants = item.GetExtraFieldValue("meeting.participants");
var field = item.GetExtraField("meeting.participants");
```

附加字段是只读数据；编辑字段不会触发脚本执行。

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

`workItems.query` 单次最多返回 1000 条；`StreamAsync` 页大小必须在 1 到 500 之间，默认 500。第一版是 Worker 通信层的分页式流，不是数据库 reader 流；查询期间数据变化可能影响 offset 分页边界。任一页查询领域失败时抛 `InvalidOperationException`。`IScriptEditorContext.StreamItemsAsync()` 按当前目标日期范围迭代，事项目标没有日期范围，调用会抛 `InvalidOperationException`；需要自定义范围时使用 `IDiaryApi.StreamAsync(query, pageSize)`。

## 5. 创建日志项

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
| `IdempotencyKey` | `string?` | 可选，业务幂等键；重复提交同一业务动作返回已提交结果，应用重启后仍可识别。 |
| `Preview` | `bool` | 可选，为 `true` 时只返回投影记录与副作用摘要，不写入数据库。 |

失败时 `Error.Code` 可能是 `InvalidInput`、`DatabaseUnavailable`、`ProviderFailure` 或 `Cancelled`。成功时 `Item` 返回新建工作项 DTO。脚本不能从该 API 获得可变 `WorkItem` 对象。

`ScriptLogItemResult` 字段：

| 成员 | 类型 | 说明 |
| --- | --- | --- |
| `Succeeded` | `bool` | 是否成功。 |
| `Item` | `ScriptWorkItem?` | 新建项 DTO；失败时为 `null`。 |
| `Error` | `ScriptLogItemError?` | `Code`（领域枚举）+ `Message`。 |
| `Effects` | `ScriptEffectSummary?` | 副作用摘要。 |
| `Duplicate` | `bool` | `true` 表示结果来自幂等重放。 |
| `ApiError` | `ScriptApiError?` | 稳定大写错误码视图，由 `Error.ToApiError()` 计算。 |

`ScriptEffectSummary` 字段：`AppendedCount`（实际追加条数；预览或幂等重放时为 0）、`Preview`、`IdempotencyKey`、`CreatedWorkItemIds`、`RemoteEffects`（预留）。

- `Preview = true` 时返回 `Id = 0` 的投影项：`Effects.Preview = true`、`AppendedCount = 0`、`CreatedWorkItemIds` 为空集合，数据库未写入。
- `Duplicate = true` 表示同一 `IdempotencyKey` 已提交过：不重复追加，`AppendedCount = 0`，`Item` 与 `CreatedWorkItemIds` 保留首次创建的结果。

## 6. 按模板创建日志项

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

模板只读发现：

```csharp
foreach (var template in diary!.Templates.List())
    Console.WriteLine($"{template.Id}: {template.Name} ({template.DefaultHours}h)");
```

`ScriptTemplateInfo` 字段：`Id`（UUID）、`Name`、`DefaultTitle`、`DefaultHours`、`DefaultWorkTagIds`。`DefaultTitle`、`DefaultHours` 和 `DefaultWorkTagIds` 只描述模板默认值，不授予脚本修改模板的权限；调用 `CreateFromTemplateAsync` 时仍需显式传 `Hours`。

宿主能力发现：

```csharp
foreach (var capability in diary!.Host.List())
    Console.WriteLine(capability);
```

返回的是当前执行上下文实际注册的 Worker HostCall 名称，按序稳定排列，例如 `workItems.query`、`templates.list` 和 `host.capabilities.list`。能力列表只用于发现，不替代宿主的权限、作用域和参数校验。

## 7. Tracker 实例目录

```csharp
var api = context.GetApi<ITrackerApi>();
var result = api?.GetInstance("tracker.memory", "company");
if (result is { Succeeded: true })
    Console.WriteLine(result.Instance!.DisplayName);
```

`GetInstance(pluginId, instanceId)` 返回 `TrackerScriptResult`，字段：`Succeeded`、`Instance`（`ScriptTrackerInstance?`，含 `PluginId`、`InstanceId`、`DisplayName`、`Icon`、`IsConfigured`）、`ErrorCode`（`TrackerScriptErrorCode?`）、`ErrorMessage`、`ApiError`（`INVALID_ARGUMENT` 或 `INSTANCE_UNAVAILABLE`）。`ListInstances()` 返回当前已启用实例的同一 DTO 列表，结果按显示名称稳定排序。该 API 不暴露 Tracker 客户端、配置、数据库或 DI。

## 8. 剪贴板

```csharp
var system = context.GetApi<SysApi>();
var oldText = await system!.GetClipboardTextAsync(cancellationToken);
var succeeded = await system.SetClipboardTextAsync("复制内容", cancellationToken);
```

`GetClipboardTextAsync` 返回文本或 `null`；`SetClipboardTextAsync` 返回是否成功。只支持文本，不支持图片、文件列表等剪贴板格式。

## 9. 用户交互

```csharp
var system = context.GetApi<SysApi>();
await system!.NotifyAsync("脚本完成", "日志项已创建。", cancellationToken);
var confirmed = await system.ConfirmAsync("继续操作", "是否继续？", cancellationToken);
```

`NotifyAsync` 显示通知；`ConfirmAsync` 返回用户是否确认。自动化或后台执行时 UI 可能不可用，应捕获异常并将失败作为脚本诊断处理。

### 9.1 交互式导出（第一阶段）

第一阶段支持有人值守执行的选项选择、目录选择、XLSX 导出和结果文件打开询问。`Automation`、`Startup`、`Scheduled` 及事件触发脚本调用这些 API 会返回宿主作用域错误。

```csharp
var directory = await api.System.PickDirectoryAsync(new DirectoryPickerOptions
{
    Title = "选择导出目录",
}, cancellationToken);
if (directory is null)
    return;

var result = await api.Exports.ExportAsync(new ExportRequest
{
    FormatId = "xlsx",
    DirectorySelectionId = directory.SelectionId,
    FileName = "report.xlsx",
    Content = new ExportTableContent
    {
        Columns = [new ExportColumn("时长", ExportColumnType.Duration)],
        Rows = [["25:30:00"]],
        Aggregates = [new ExportAggregateColumn("时长")],
    },
}, cancellationToken);
if (result.Succeeded)
    await api.System.AskToOpenExportedFileAsync(result.FileId!, cancellationToken);
```

模板导出使用 `ExportTemplateSource`，先调用 `api.Exports.ListTemplatesAsync("xlsx")` 获取已经由插件校验的模板和绑定 schema，再提交 `template_id`、`template_version` 以及 `values`/`tables`/`documents`。省略带 `default_value` 的绑定时由宿主填充；缺少没有默认值的必填绑定会返回 `EXPORT_TEMPLATE_BINDING_INVALID`。

Wire JSON 使用全小写/snake_case（例如 `format_id`、`directory_selection_id`、`file_id`、`duration`）；C# 模型仍遵循 C# 命名约定。`Time` 表示时分秒，不参与 `SUM`；`Duration` 表示可超过 24 小时的时长，XLSX 使用 `[h]:mm:ss`。文件名含路径分隔符、控制字符、`.`/`..` 或路径穿越片段时直接拒绝，不做替换。

## 10. 调试日志

```csharp
var log = context.GetApi<ILogApi>();
if (log is not null)
{
    await log.DebugAsync("开始处理脚本参数", cancellationToken);
    await log.InfoAsync("脚本已读取当前目标");
    await log.WarningAsync("发现一个可忽略的事项");
    await log.ErrorAsync("处理事项失败");
}
```

日志带有脚本 ID 和执行 ID，并写入宿主日志。单条消息由宿主限制大小；不要输出密码、Token 或其他敏感配置。

脚本中的 `Console.WriteLine` / `Console.Write` 输出按行转发到脚本日志（Info 级），与 `log.InfoAsync` 一样显示在管理页「运行日志」Tab 和宿主日志中，方便直接用控制台输出调试查询结果（本文示例即如此使用）。转发按行缓冲，执行结束时冲刷未换行的残余；输出总量超过 1MB 时脚本执行失败。每条打印会占用一次宿主调用，计入宿主调用次数上限，打印密集型脚本请改用 `log` API 或合并输出。

## 11. Worker API 映射和限制

| C# API | Worker HostCall |
| --- | --- |
| `IDiaryApi.QueryAsync` | `workItems.query` |
| `IDiaryApi.CreateLogItemAsync` | `logItems.create` |
| `IDiaryApi.CreateFromTemplateAsync` | `templateLogItems.create` |
| `IDiaryApi.Templates.List` | `templates.list` |
| `IDiaryApi.Host.List` | `host.capabilities.list` |
| `ITrackerApi.GetInstance` | `trackerInstances.get` |
| `ITrackerApi.ListInstances` | `trackerInstances.list` |
| `SysApi.GetClipboardTextAsync` | `clipboard.get` |
| `SysApi.SetClipboardTextAsync` | `clipboard.set` |
| `SysApi.NotifyAsync` | `ui.notify` |
| `SysApi.ConfirmAsync` | `ui.confirm` |
| `ILogApi.*Async` | `log.write` |
| `ReportProgressAsync` | `script.progress` |

Worker 调用由主进程执行并返回结构化结果。普通日志项和模板日志项支持 `IdempotencyKey` 与 `Preview`，结果会带 `ScriptEffectSummary`；真实写入使用 provider 事务，失败时回滚，Preview 在数据库访问前返回投影且不改变幂等存储。已提交的幂等结果由宿主共享存储持久化，应用重启后仍能识别重复请求；脚本不能直接访问文件、网络、进程、反射、数据库、DI 或任意 UI 控件。超时、取消、Worker 退出和宿主失败都会转换为执行诊断。

C# 脚本沙箱（构建期检查，违反时构建失败并产生 `CSHARP_API_FORBIDDEN` 诊断）：

- **可用基础库**（引用白名单，其余程序集不引用）：集合（含并发/非泛型）、`System.Linq`、`System.Memory`（Span）、`System.Text.Json`、`System.Text.RegularExpressions`、`System.Numerics`/`System.Runtime.Numerics`、`System.Security.Cryptography`（哈希等纯计算算法）、`System.Console`（输出按行转发到脚本日志，输入恒为空流，见 §10）
- 禁止命名空间：`System.IO`、`System.Net`、`System.Reflection`、`System.Runtime.InteropServices`、`Diary.Database`、`Microsoft.Extensions.DependencyInjection`。
- 禁止类型：`System.AppDomain`、`System.Environment`、`System.Diagnostics.Process`、`System.Diagnostics.ProcessStartInfo`、`System.Threading.Thread`、`System.Threading.ThreadPool`、`System.Threading.Timer`、`System.Threading.PeriodicTimer`、`System.Threading.Tasks.TaskFactory`、`System.Threading.Tasks.TaskScheduler`、`System.Type`、`System.Activator`、`System.Runtime.CompilerServices.RuntimeHelpers`。
- 禁止成员：`Object.GetType`、`Type.GetType`、`Activator.CreateInstance`、`Delegate.DynamicInvoke`、`Task.Run`、`TaskFactory.StartNew`、`TaskFactory.ContinueWhenAll`、`TaskFactory.ContinueWhenAny`。

脚本程序集加载失败时诊断码为 `CSHARP_LOAD_FAILED`。

## 12. 错误、取消、超时和 Worker 终止

返回结果类 API 失败时，优先读取 `ApiError.Code`，它使用稳定的大写错误码；`Error.Code` 是 C# 领域枚举，适合在领域内分支。

```csharp
var result = await api.QueryAsync(new ScriptWorkItemQuery { Limit = 0 }, cancellationToken);
if (!result.Succeeded)
{
    switch (result.ApiError?.Code)
    {
        case ScriptApiErrorCodes.InvalidArgument:
            // 修正参数后再执行。
            break;
        case ScriptApiErrorCodes.Cancelled:
            // 取消不是普通业务失败，不要重试副作用操作。
            break;
        case ScriptApiErrorCodes.ProviderFailure:
        case ScriptApiErrorCodes.HostNotConfigured:
            // 宿主或数据库不可用；是否重试由业务决定。
            break;
    }
}
```

脚本整体执行结果还要区分执行状态：`Cancelled` 表示调用方取消，`TimedOut` 表示超过执行时限；Worker 进程异常退出或通道断开时，诊断代码为 `WORKER_TERMINATED`。这些状态不能当作普通的 `ProviderFailure`，尤其是带有追加副作用的操作不能因为超时就自动重试。

领域枚举到 `ApiError.Code` 的映射：

| 领域枚举 | `ApiError.Code` | category | retryable |
| --- | --- | --- | --- |
| `InvalidInput` | `INVALID_ARGUMENT` | Validation | 否 |
| `PermissionDenied`（仅查询） | `PERMISSION_DENIED` | Permission | 否 |
| `DatabaseUnavailable` | `SCRIPT_API_HOST_NOT_CONFIGURED` | Host | 是 |
| `InstanceUnavailable`（仅 Tracker） | `INSTANCE_UNAVAILABLE` | Host | 否 |
| `ProviderFailure` | `PROVIDER_FAILURE` | Provider | 是 |
| `Cancelled` | `CANCELLED` | Cancellation | 否 |

## 13. 类型参考

### 13.1 上下文接口

| 接口 | 额外成员 | 说明 |
| --- | --- | --- |
| `IScriptApplicationContext` | 无 | 继承 `IScriptExecutionContext` 的全部成员，不新增成员。 |
| `IScriptEditorContext` | `Target`（`ScriptEditorTarget`）、`WorkItem`（`ScriptWorkItem?`）、`GetDateRange()`（返回 `ScriptDateRange?`）、`StreamItemsAsync(CancellationToken)` | 编辑器脚本专用。 |
| `IScriptAutomationContext` | `Automation`（`ScriptAutomationContext`） | 自动化脚本专用。 |

`IScriptExecutionContext` 公共成员：`Metadata`（`ScriptExecutionMetadata?`）、`EntryKind`、`Arguments`、`CancellationToken`、`IsCancellationRequested`、`ReportProgressAsync(ScriptProgressUpdate)`、`GetApi<TApi>()`、`GetRequiredApi<TApi>()`。

`ScriptAutomationContext`：`Trigger`（`ScriptAutomationTriggerKind`）、`EventData`（`IReadOnlyDictionary<string, string>`）、`IdempotencyKey`（`string?`，默认 null）。

`ScriptAutomationTriggerKind` 枚举：

| 值 | 说明 |
| --- | --- |
| `Unknown` | 未知来源。 |
| `Startup` | 应用启动触发。 |
| `Scheduled` | 定时触发。 |
| `WorkItemCreated` | 工作项创建触发。 |
| `WorkItemSaved` | 工作项保存触发。 |
| `TagAdded` | 标签添加触发。 |

当前实现已接线：执行来源为 `Automation` 时注入 `Scheduled`，来源为 `Startup` 时注入 `Startup`，其余三种事件来源分别注入同名触发器；手动、编辑器等非自动化来源为 `Unknown`。

自动化脚本（`AutomationScript`）放 `application` 目录，metadata 的 `entryKind` 写 `Automation`；`schedule` 字段（`"daily HH:mm"`）配置每日定时，`runOnStartup`（true/false）配置启动补跑，`triggers` 数组配置 `WorkItemCreated`、`WorkItemSaved`、`TagAdded`。事件型自动化可省略 `schedule`；事件执行的 `eventData` 包含工作项字段，`TagAdded` 额外包含标签字段，详见 `IScriptAutomationContext.EventData`。到点、补跑或事件发生时宿主自动执行，可在管理页执行历史中按来源筛选。

### 13.2 ScriptEditorTarget

| 成员 | 类型 | 说明 |
| --- | --- | --- |
| `Kind` | `ScriptEditorTargetKind` | 必填，六种之一。 |
| `Year` | `int?` | `Year`、`Quarter`、`Month` 目标。 |
| `Quarter` | `int?` | `Quarter` 目标，1-4。 |
| `Month` | `int?` | `Month` 目标，1-12。 |
| `Date` | `string?` | `Day` 目标，`yyyy-MM-dd`。 |
| `WeekStart` | `string?` | `Week` 目标，周一的 `yyyy-MM-dd`。 |
| `WorkItem` | `ScriptWorkItem?` | `WorkItem` 目标快照。 |

静态工厂：`ForYear(year)`、`ForQuarter(year, quarter)`、`ForMonth(year, month)`、`ForDay(date)`、`ForWeek(weekStartDate)`、`ForWorkItem(workItem)`。类型没有实例方法，读取目标时直接访问属性；`Date` 只属于 `Day` 目标，`Week` 目标使用 `WeekStart`。

### 13.3 执行结果与诊断

`ScriptExecutionResult`：

| 成员 | 类型 | 说明 |
| --- | --- | --- |
| `Status` | `ScriptExecutionStatus` | 执行状态。 |
| `Diagnostics` | `ImmutableArray<ScriptDiagnostic>` | 诊断列表。 |
| `Effects` | `ScriptEffectSummary?` | 副作用摘要。 |

静态工厂只有 `Succeeded()` 与 `Cancelled()`；失败结果需要直接构造：`new(ScriptExecutionStatus.Failed, diagnostics)`。

`ScriptDiagnostic`：`Code`（string）、`Message`（string）、`Severity`（`ScriptDiagnosticSeverity`）、`Category`（`ScriptDiagnosticCategory`）、`SourcePath`（string?）、`Line`（int?）、`Column`（int?）。

`ScriptExecutionMetadata`：`ExecutionId`（Guid）、`StartedAt`（DateTimeOffset）、`Source`、`ScriptId`（string）、`EntryKind`、`IdempotencyKey`（string?）、`Preview`（bool）。

`ScriptProgressUpdate`：`Fraction`（double，0 到 1）、`Message`（string，非空）。`ScriptDateRange`：`StartDate`、`EndDate`（均为 `yyyy-MM-dd`）。

### 13.4 SDK 基类

`ApplicationScript`、`EditorScript`、`AutomationScript` 定义在 `Diary.ScriptBase`：

| 成员 | 类型 | 说明 |
| --- | --- | --- |
| `Id` | `abstract string` | 稳定的脚本 ID。 |
| `Name` | `abstract string` | UI 展示名称。 |
| `Description` | `virtual string?` | 默认 null。 |
| `SupportedTargets` | `virtual IReadOnlyList<ScriptEditorTargetKind>?`（仅 `EditorScript`） | 返回 null 表示支持全部六种目标。 |
| `ExecuteAsync` | 抽象方法 | 按基类签名：`ExecuteAsync(IScriptApplicationContext context, CancellationToken ct = default)` / `ExecuteAsync(IScriptEditorContext context, ...)` / `ExecuteAsync(IScriptAutomationContext context, ...)`，返回 `ValueTask<ScriptExecutionResult>`。 |

`Descriptor` 由基类自动生成（`ApiVersion = V1`、对应 Scope 与 EntryKind），脚本不需要手写。`QueryScript` 基类同样可用（`ScriptScope.Application` + `EntryKind = Query`，上下文为 `IScriptApplicationContext`），对应 Lua/Python 的 `query_main` 入口。

### 13.5 宿主 API 接口签名

```csharp
public interface IDiaryApi
{
    ITemplateScriptApi Templates { get; }
    IHostCapabilitiesScriptApi Host { get; }
    ValueTask<ScriptWorkItemQueryResult> QueryAsync(ScriptWorkItemQuery query, CancellationToken cancellationToken = default);
    IAsyncEnumerable<ScriptWorkItem> StreamAsync(ScriptWorkItemQuery query, int pageSize = 500, CancellationToken cancellationToken = default);
    ValueTask<ScriptLogItemResult> CreateLogItemAsync(ScriptLogItemRequest request, CancellationToken cancellationToken = default);
    ValueTask<ScriptLogItemResult> CreateFromTemplateAsync(ScriptTemplateLogItemRequest request, CancellationToken cancellationToken = default);
}

public interface ITrackerApi
{
    TrackerScriptResult GetInstance(string pluginId, string instanceId);
    IReadOnlyList<ScriptTrackerInstance> ListInstances();
}

public interface SysApi
{
    ValueTask<string?> GetClipboardTextAsync(CancellationToken cancellationToken = default);
    ValueTask<bool> SetClipboardTextAsync(string text, CancellationToken cancellationToken = default);
    ValueTask NotifyAsync(string title, string body, CancellationToken cancellationToken = default);
    ValueTask<bool> ConfirmAsync(string title, string body, CancellationToken cancellationToken = default);
    ValueTask<OptionDialogResult> SelectOptionAsync(OptionDialogRequest request, CancellationToken cancellationToken = default);
    ValueTask<DirectorySelection?> PickDirectoryAsync(DirectoryPickerOptions options, CancellationToken cancellationToken = default);
    ValueTask<OpenExportedFileResult> AskToOpenExportedFileAsync(string fileId, CancellationToken cancellationToken = default);
}

public interface IExportApi
{
    ValueTask<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ExportFormatDescriptor>> ListFormatsAsync(CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ExportTemplateDescriptor>> ListTemplatesAsync(string? formatId = null, CancellationToken cancellationToken = default);
}

public interface ILogApi
{
    ValueTask DebugAsync(string message, CancellationToken cancellationToken = default);
    ValueTask InfoAsync(string message, CancellationToken cancellationToken = default);
    ValueTask WarningAsync(string message, CancellationToken cancellationToken = default);
    ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default);
}
```

`Templates` 与 `Host` 各只有一个只读方法 `List()`，返回 `IReadOnlyList<ScriptTemplateInfo>` 与 `IReadOnlyList<string>`。

`ScriptApiFacade`（`context.Api()` 返回）提供 `Diary`（`IDiaryApi`）、`Tracker`（`ITrackerApi`）、`System`（`SysApi`）、`Log`（`ILogApi`）四个属性，均通过 `GetRequiredApi<T>()` 获取——门面不扩大权限，缺少 API 时同样报错。

### 13.6 入口返回值与异常映射

| 情况 | 执行状态 | 说明 |
| --- | --- | --- |
| 返回 `ScriptExecutionResult` | 由返回值的 `Status` 决定 | C# 是唯一返回值参与结果构造的语言。 |
| 返回 null 或抛出异常 | `Failed` + `SCRIPT_EXECUTION_EXCEPTION` | 脚本编写错误或未处理异常。 |
| `OperationCanceledException` | `Cancelled` | 调用方取消。 |
| 超过执行时限 | `TimedOut` + `SCRIPT_EXECUTION_TIMED_OUT` | 宿主终止执行。 |
| Worker 进程异常退出 | `WORKER_TERMINATED` | 通道断开。 |

Lua 与 Python 的入口返回值约定不同，见各自语言文档的类型参考章节。

### 13.7 枚举速查

| 枚举 | 值 |
| --- | --- |
| `ScriptApiVersion` | `V1`。 |
| `ScriptScope` | `Application`、`Editor`。 |
| `ScriptDiagnosticSeverity` | `Info`、`Warning`、`Error`。 |
| `ScriptDiagnosticCategory` | `Syntax`、`Validation`、`Security`、`Engine`、`Runtime`、`Host`。 |
| `ScriptErrorCategory` | `Validation`、`Permission`、`Host`、`Provider`、`Cancellation`、`Conflict`、`Runtime`。 |

## 附录 A. `ScriptApiErrorCodes` 总表

`ScriptApiError` 字段：`Code`（string，稳定大写码）、`Message`、`Category`（`Validation`、`Permission`、`Host`、`Provider` 或 `Cancellation`）、`Retryable`（bool）、`Details`（可选字典，当前未使用）。

| `Code` | 是否由当前 API 产生 | 说明 |
| --- | --- | --- |
| `INVALID_ARGUMENT` | 是 | 参数不合法。 |
| `PERMISSION_DENIED` | 是（仅查询） | 无权限执行该查询。 |
| `SCRIPT_API_HOST_NOT_CONFIGURED` | 是 | 数据库/宿主未就绪，可稍后重试。 |
| `INSTANCE_UNAVAILABLE` | 是（仅 Tracker） | Tracker 实例不存在或未启用。 |
| `PROVIDER_FAILURE` | 是 | 底层提供程序失败。 |
| `CANCELLED` | 是 | 调用已取消；不要重试带副作用的操作。 |
| `SCRIPT_API_UNAVAILABLE` | 保留 | 宿主 API 不可用。 |
| `SCRIPT_API_SCOPE_NOT_SUPPORTED` | 保留 | 当前作用域不支持该 API。 |
| `TIMEOUT` | 保留 | 调用超时。 |
| `WORKER_TERMINATED` | 保留 | Worker 进程异常退出。 |
| `DUPLICATE_REQUEST` | 保留 | 重复请求。 |

## 附录 B. 执行状态与常见诊断

执行状态：`Succeeded`、`Failed`、`Cancelled`、`Rejected`（入口、目标或 descriptor 校验不通过）、`TimedOut`。

常见诊断码：`CSHARP_API_FORBIDDEN`（使用被禁止的 API）、`CSHARP_LOAD_FAILED`（程序集加载失败）、`SCRIPT_DESCRIPTOR_INVALID`（descriptor 与入口不一致）、`SCRIPT_ENTRY_KIND_MISMATCH`（入口类型与作用域/目标不匹配）、`SCRIPT_TARGET_INVALID`（编辑器目标校验失败）、`WORKER_TERMINATED`（Worker 进程异常退出或通道断开）、`SCRIPT_EXECUTION_TIMED_OUT`（执行超过时限）、`WORKER_HOST_CALL_LIMIT`（宿主调用次数超限）、`WORKER_MESSAGE_TOO_LARGE`（Worker 消息超过大小限制）。

## 附录 C. DTO 字段总表

- `ScriptWorkItem`：`Id`(int)、`Date`、`Comment`、`Hours`(double)、`Priority`(int，0-9)、`Note`(string?)、`Tags`(ImmutableArray&lt;ScriptWorkTag&gt;)。
- `ScriptWorkTag`：`Id`(int)、`Name`、`Color`(int)、`Level`(int)、`IsPrimary`(bool)、`Disabled`(bool)、`Metadata`(IReadOnlyDictionary&lt;string, string&gt;)。`Metadata` 是标签的只读字符串键值元数据，推荐使用 `projectNumber` 保存项目编号；推荐使用 `IsPrimary` 判断主标签，`Level` 保留用于兼容。
- `ScriptTrackerInstance`：`PluginId`、`InstanceId`、`DisplayName`、`Icon`、`IsConfigured`(bool)。
- `ScriptTemplateInfo`：`Id`、`Name`、`DefaultTitle`、`DefaultHours`(double)、`DefaultWorkTagIds`(IReadOnlyCollection&lt;int&gt;)。
