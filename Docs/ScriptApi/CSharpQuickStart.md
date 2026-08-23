# C# 脚本 5 分钟入门

C# 是 DiaryApp 的主推脚本语言。新脚本优先从“脚本管理 → 新建脚本”创建，宿主会生成正确的基类、descriptor 和入口签名。

如果让外部 AI 协助编写脚本，可先在“脚本管理 → AI 上下文”中生成 Markdown/JSON，或刷新只读 stdio MCP 快照。它会提供当前标签、附加字段、模板等结构信息，但默认不包含事项正文。详见 [AI 脚本上下文使用指南](../AiScriptContextGuide.md)。

## 1. 记住一个入口

业务 API 统一从 `context.Api()` 获取：

~~~csharp
using Diary.ScriptHost;

var api = context.Api();
var diary = api.Diary;
var system = api.System;
var exports = api.Exports;
var log = api.Log;
~~~

底层的 `GetApi<T>()` / `GetRequiredApi<T>()` 仍可用于高级场景，但不作为新脚本的首选写法。

## 2. 查询今天的记录

~~~csharp
var items = (await context.Api().Diary
    .QueryTodayAsync(limit: 100, cancellationToken))
    .EnsureSucceeded();

foreach (var item in items)
    await context.Api().Log.InfoAsync($"{item.Date}: {item.Comment}", cancellationToken);
~~~

自定义日期范围使用 `QueryRangeAsync(startDate, endDate)`；复杂筛选继续使用 `QueryAsync(new ScriptWorkItemQuery { ... })`。

## 3. 创建日志

~~~csharp
var item = (await context.Api().Diary.CreateLogItemAsync(
    date: DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd"),
    hours: 1.5,
    title: "脚本生成记录",
    note: "由 C# 脚本创建",
    idempotencyKey: context.Metadata?.IdempotencyKey,
    cancellationToken: cancellationToken)).EnsureSucceeded();
~~~

`EnsureSucceeded()` 会在失败时抛出 `ScriptApiCallException`，异常消息带稳定错误码。需要自行分支处理时，直接检查原始结果的 `Succeeded`、`Error` / `ApiError`。

## 4. 运行参数、超时与预览

从脚本管理页点击“运行”后，可设置：

- 每行一个 `key=value` 的参数；脚本从 `context.Arguments` 读取。
- 本次执行的幂等键。
- 1–3600 秒超时。
- Preview。

Preview 由宿主强制执行，脚本不需要相信或转发请求中的预览标志：

- 创建日志和按模板创建日志自动转为预览，不写数据库或幂等存储。
- 目录选择返回虚拟令牌，不弹出选择框。
- 导出自动执行完整参数、格式、模板和绑定校验，但不创建文件。
- 写剪贴板、打开导出文件等外部副作用会被拒绝。

`QueryScript` 是只读入口；即使脚本直接调用写 API，宿主也会拒绝。查询脚本在 Preview 下可以执行导出预检，但不会落盘。

## 5. metadata 只放运行配置

C# 的 `Id`、`Name`、`Description`、作用域、入口类型和编辑器目标以源码 descriptor 为唯一来源。同目录 `.cs.json` 只保存运行配置，例如：

~~~json
{
  "Schedule": "daily 09:30",
  "RunOnStartup": false,
  "Triggers": ["WorkItemSaved"],
  "DefaultArguments": {
    "range": "today"
  },
  "TimeoutSeconds": 300
}
~~~

普通应用/编辑器/查询脚本不能配置自动化调度字段；`AutomationScript` 至少需要 schedule、启动触发或事件触发之一。

## 6. 导出骨架

真实运行时先选择目录，再提交导出；Preview 下相同代码会获得虚拟目录并只做校验：

~~~csharp
var api = context.Api();
var directory = await api.System.PickDirectoryAsync(
    new DirectoryPickerOptions { Title = "选择导出目录" },
    cancellationToken);
if (directory is null)
    return ScriptExecutionResult.Cancelled();

var result = (await api.Exports.ExportAsync(new ExportRequest
{
    FormatId = "csv",
    DirectorySelectionId = directory.SelectionId,
    FileName = "diary.csv",
    Content = new ExportTableContent
    {
        Columns = [new ExportColumn("title")],
        Rows = [new object?[] { "Diary" }],
    },
}, cancellationToken)).EnsureSucceeded();

if (!result.ValidatedOnly && result.FileId is not null)
    await api.System.AskToOpenExportedFileAsync(result.FileId, cancellationToken);
~~~

完整 DTO、错误码、编辑器目标和模板导出说明见 [CSharp.md](CSharp.md)。
