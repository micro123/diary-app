# 脚本 API 用户体验优化评审

## 1. 文档目的

本文从脚本作者的角度评估当前脚本 API，记录实际使用流程中的阻力、跨语言差异、文档问题和后续优化方向。

本文是脚本系统设计的补充评审文档，记录用户体验判断、已落地契约和仍待实施的建议。实施状态以 `Docs/TODOS.md` 为准。

关联设计：Docs/ScriptSystemDesign.md、Docs/ScriptWorkerDesign.md。

## 实施状态（2026-08-09）

本轮已按推荐顺序完成入口契约的第一版实现，并统一保持 Worker 执行：

- C# 提供 `ApplicationScript`、`EditorScript` 和 `AutomationScript` SDK 基类；底层 `IScriptProgramV1` 只作为 Worker 适配契约。
- Lua/Python 使用 `application_main`、`editor_main`、`automation_main`、`query_main` 明确入口名；当前创建向导生成应用或编辑器入口。
- `ScriptEntryKind` 已进入 descriptor、metadata、manifest 和执行请求，入口、作用域、编辑器目标不一致时在构建/加载/执行边界拒绝。
- 上下文已提供入口类型、参数、取消状态、`GetRequiredApi` 和进度报告；自动化上下文携带触发器、事件数据和幂等键。
- 普通日志项和模板日志项支持幂等键、预览和副作用摘要。当前幂等结果存于宿主进程内存，应用重启后不会保留，持久化幂等记录仍是后续工作。
- C# 提供 `context.Api()` 强类型门面；C#、Lua、Python 均可只读发现模板、已启用 Tracker 实例和当前 Worker HostCall 能力。模板默认标题、默认工时、默认标签和工作项返回字段已明确映射。
- 结果类 API 使用 `ApiError.Code`/`apiError.code` 返回稳定大写错误码；Python HostCall 异常和 Lua 同步 HostCall 也已提供可识别的错误码格式。
- 查询契约已统一默认 `limit=100`、`offset=0`，单次最多 1,000 条，流式页大小为 1 到 500，偏移最多 1,000,000，标签最多 100 个；非法输入返回稳定的 `InvalidInput` 错误。

仍未实现的建议包括日期范围快捷 API、持久化幂等、追加式修正/冲正、Lua 可轮询取消和完整的跨语言错误契约测试。宿主能力列表只用于发现，不替代权限判断。

## 2. 当前能力概览

当前脚本 API 已覆盖以下场景：

- 应用脚本和编辑器脚本两种作用域。
- 按日期范围、标签、文本和优先级查询工作项。
- 分页查询和按日期范围流式读取工作项。
- 创建普通日志项和按模板创建日志项。
- 查询 Tracker 实例的只读目录信息。
- 读写文本剪贴板。
- 显示通知和请求用户确认。
- 输出 Debug、Info、Warning、Error 四级脚本日志。
- 统一的脚本 ID、执行目标、执行来源、诊断和取消模型。
- C#、Lua、Python 三种语言均通过独立 Worker 执行。

当前 API 的主要组成如下：

| 领域 | C# | Lua | Python |
| --- | --- | --- | --- |
| 工作项查询 | IDiaryApi.QueryAsync | diary.workItems.query | context.diary.workItems.query |
| 工作项流式读取 | IDiaryApi.StreamAsync | diary.workItems.stream | context.diary.workItems.stream |
| 创建日志项 | IDiaryApi.CreateLogItemAsync | diary.logItems.create | context.diary.logItems.create |
| 按模板创建 | IDiaryApi.CreateFromTemplateAsync | diary.templateLogItems.create | context.diary.templateLogItems.create |
| Tracker 实例 | ITrackerApi.GetInstance | diary.trackerInstances.get | context.diary.trackerInstances.get |
| 剪贴板 | SysApi | diary.clipboard | context.diary.clipboard |
| 用户交互 | SysApi | diary.ui | context.diary.ui |
| 日志 | ILogApi | diary.log | context.log |

当前 API 已经有稳定的领域划分，但不同语言的入口组织和错误语义还没有完全统一。

## 3. 用户旅程评估

### 3.1 第一次创建脚本

当前创建向导可以按语言、作用域和模板生成脚本，编辑器也提供 API Reference 入口，这是比较好的基础。

主要问题是三种语言生成的代码模型不同：

- C# 可继承与功能对应的 SDK 基类，入口和 descriptor 由基类生成；高级场景仍可直接实现 `IApplicationScriptV1` 等接口。
- Lua/Python 按 `ScriptEntryKind` 使用明确入口函数，例如 `application_main(context)` 或 `editor_main(context)`。
- C# 仍可通过 `GetApi<T>()` 获取底层 API，并可用 `GetRequiredApi<T>()` 把缺失 API 转成明确异常。

建议增加统一的“5 分钟入门”示例，并在创建向导中明确显示：

1. 当前脚本是什么作用域。
2. 当前脚本从哪里获得目标和参数。
3. 当前语言可调用哪些宿主 API。
4. 脚本默认通过 Worker 执行，不能访问主程序对象。
5. 脚本失败、超时或取消时会发生什么。

### 3.2 按功能区分程序入口

底层仍保留统一的 `IScriptProgramV1.ExecuteAsync(request, context)`，用于 Worker 协议和运行时调度；面向脚本作者的入口已经按 `ScriptEntryKind` 拆分。入口类型由 descriptor、metadata/manifest 和执行请求共同确认，不能只依赖 `ScriptExecutionSource` 推断场景。

这样既保留 Worker 的统一生命周期、超时、取消和诊断模型，又让脚本作者在入口层获得明确的上下文边界：应用脚本没有编辑器目标，编辑器脚本必须收到目标，自动化脚本通过自动化上下文读取触发器和事件数据。

当前入口约定如下：

| 功能 | 入口 | 主要输入 |
| --- | --- | --- |
| 应用命令脚本 | Application | 参数、当前应用环境、手动执行来源 |
| 编辑器脚本 | Editor | 年/月/日/季度/事项目标和日期范围 |
| 自动化脚本 | Automation | 触发器类型、事件数据、幂等键和取消信号 |
| 查询/报表脚本 | Query（契约已预留） | 查询参数，只读结果或可展示报告 |

C# 的公开入口接口分别是 `IApplicationScriptV1`、`IEditorScriptV1` 和 `IAutomationScriptV1`，SDK 基类进一步减少 descriptor 和入口样板代码。Lua/Python 使用同一组入口名：`application_main`、`editor_main`、`automation_main` 和预留的 `query_main`；当前不再依赖通用 `main(context)` 的隐式判断。
### 3.3 查询工作项

当前查询接口能够覆盖常见过滤条件，但用户需要理解日期字符串格式、标签过滤枚举、分页上限和 offset 行为。

建议保留当前高级查询对象，同时增加高频场景的快捷 API：

~~~text
today()
thisWeek()
thisMonth()
thisYear()
queryByDateRange(startDate, endDate)
~~~

跨语言应保持相同语义，而不要求语法完全一致。C# 可以使用类型安全的日期范围对象，Lua/Python 继续使用字符串和表/字典。

### 3.4 创建日志项

当前只支持追加创建，不提供脚本删除历史记录的能力。这不是 API 缺口，而是工作记录程序的业务约束：历史记录应保持可追溯，脚本自动化不应删除或直接改写已经产生的记录。

当前真正需要优化的是追加操作的安全性：

- 重复执行脚本可能创建重复日志项。
- 创建前无法预览或确认将要写入的内容。
- 如果业务需要更正，应通过明确的修正记录或冲正记录表达，而不是删除原始记录。

幂等键和预览能力已提供；当前实现仍需补充持久化幂等和更完整的审计边界：

~~~text
CreateLogItem(..., idempotencyKey)
PreviewCreateLogItem(...)
~~~

更新、删除不属于脚本自动化 API 的目标能力；后续如需处理错误记录，应设计独立的可追溯修正模型。

### 3.5 使用模板和 Tracker

当前模板和 Tracker API 都要求脚本作者提前知道一个外部 ID：

- 模板需要 UUID。
- Tracker 需要 PluginId + InstanceId。

但当前 API 没有提供模板列表或 Tracker 实例列表，用户只能从配置文件、源码或 UI 状态中猜测 ID。

建议增加只读发现 API：

~~~text
templates.list()
templates.findByName(name)
trackerInstances.list()
trackerInstances.find(name)
~~~

同时在 UI 中提供“复制模板 ID”和“复制 Tracker 实例标识”的操作。

## 4. 主要问题

### 4.1 API 入口结构不一致

C# 当前使用多个独立 API：

~~~csharp
context.GetApi<IDiaryApi>();
context.GetApi<ITrackerApi>();
context.GetApi<SysApi>();
context.GetApi<ILogApi>();
~~~

Lua/Python 则使用上下文对象下的领域树：

~~~text
context.diary.workItems
context.diary.logItems
context.diary.trackerInstances
context.diary.ui
context.log
~~~

建议提供统一的概念模型：

~~~text
context
  ├── diary
  │    ├── workItems
  │    ├── logItems
  │    ├── templates
  │    └── trackerInstances
  ├── system
  │    ├── clipboard
  │    └── ui
  ├── log
  ├── target
  ├── arguments
  └── cancellation
~~~

C# 可以提供强类型属性，GetApi<T>() 保留为底层兼容入口。

### 4.2 API 可用性依赖空值检查

IScriptExecutionContext.GetApi<T>() 在 API 未注册时返回 null。这会把配置错误、作用域限制和宿主缺少实现都混在一起。

建议增加：

~~~csharp
var diary = context.GetRequiredApi<IDiaryApi>();
~~~

并返回明确诊断：

~~~text
SCRIPT_API_UNAVAILABLE
SCRIPT_API_SCOPE_NOT_SUPPORTED
SCRIPT_API_HOST_NOT_CONFIGURED
~~~

脚本作者应能在 API Reference 中看到每个 API 的适用作用域和不可用时的行为。

### 4.3 错误处理语义不统一

当前三种语言的行为不同：

- C# 大多返回带 Succeeded 和 Error 的结果对象。
- Python 的 Worker HostCall 失败抛出 HostCallError。
- Lua 的 Worker HostCall 失败主要表现为普通 Lua 错误。

建议统一错误数据结构：

~~~text
code
message
category
retryable
details
~~~

不同语言可以采用符合自身习惯的表达方式，但错误代码、分类和重试建议必须保持一致。

Lua 尤其应提供带 code 属性的错误对象，避免脚本只能解析错误文本。

### 4.4 取消和进度反馈不够统一

C# 上下文直接暴露 `CancellationToken`、`IsCancellationRequested` 和 `ReportProgressAsync`；Python 提供 `context.isCancelled()`、`context.progress.report(...)`，Lua 提供 `context.isCancelled()`、`context.progress.report(...)`。宿主调用由 Worker 负责关联当前执行的取消生命周期。

当前已提供：

- 上下文直接暴露当前取消状态。
- 宿主 API 默认使用当前执行的取消令牌。
- 动态语言提供 `context.isCancelled()` 或等价方法。
- 统一进度 API，不再要求用户用日志模拟进度。

示例：

~~~text
context.progress.report(0.5, "正在处理工作项")
~~~

### 4.5 写入 API 的幂等和预览能力

脚本管理页面允许手动执行脚本，但脚本失败后用户可能重试。当前创建接口已接受幂等键，重复提交同一业务动作会返回重复结果而不再次追加；预览请求只返回投影记录和副作用摘要。当前幂等表是进程内缓存，尚不能跨重启保证幂等。

建议先增加：

- idempotencyKey：同一业务动作重复提交时只产生一个结果。
- preview 或 dryRun：返回将要创建的内容，不立即写入。
- 执行历史中记录副作用摘要。

不建议为脚本自动化开放更新和删除；如果业务需要修正历史记录，应设计追加式的修正或冲正记录，并保留原始记录。

### 4.6 发现型 API 不足

模板和 Tracker 实例已经提供只读列表 API；当前仍缺少可用宿主 API 的统一发现列表和按名称搜索。后续可在 UI 中增加复制稳定 ID 的入口。

### 4.7 C# 样板代码偏多

当前最小 C# 脚本可以继承按功能划分的 SDK 基类，descriptor 和入口适配由基类完成：

~~~csharp
public sealed class DemoScript : ApplicationScript
{
    public override string Id => "demo";
    public override string Name => "示例";

    public override ValueTask<ScriptExecutionResult> ExecuteAsync(
        IScriptApplicationContext context,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ScriptExecutionResult.Succeeded());
}
~~~

底层 V1 契约仍保留为 Worker 适配边界；由于当前版本未发布，不额外承诺旧入口源码兼容。

## 5. 安全和可靠性建议

当前所有语言默认使用 Worker，这是合理的隔离边界。后续 API 优化仍应保持以下原则：

- 脚本不能直接获取数据库连接、DI 容器、App 实例或 UI 控件。
- 写入 API 应默认提供明确的追加结果和副作用摘要。
- Worker 不应自动重试可能产生副作用的请求。
- 超时、取消、Worker 崩溃和宿主错误必须转换为稳定诊断码。
- 查询结果应保持为不可变 DTO。
- API 版本、字段弃用和兼容策略应在 SDK 文档中明确说明。

## 6. 优化优先级

### P0：修正文档和契约表达

- 已修正 Worker/进程内执行描述，当前三种语言统一通过 Worker。
- 修正 Offset 限制、章节编号和 Get/GetInstance 命名。
- 统一 Title/Comment 的语义说明。
- 建立统一错误码和宿主 API 可用性文档。

### P1：改善脚本日常开发体验

- 已增加入口类型、强类型上下文、`GetRequiredApi<T>()` 和统一进度报告。
- 增加日期范围快捷方法。
- 已增加模板和 Tracker 实例发现 API。
- 增加按 Application/Editor/Automation 入口划分的 C# SDK 基类和最小示例。
- 为三种语言各提供一个完整的“查询并创建日志项”示例。

### P2：增强自动化能力

- 已增加幂等键、预览、副作用摘要和统一进度报告；持久化幂等和完整取消示例仍待补齐。
- 设计可追溯的记录修正/冲正能力（仅在业务确有需要时）。
- 增加稳定游标分页。

## 7. 推荐实施顺序

1. 修正文档与入口契约，统一 Worker 执行说明和入口命名。
2. 抽象跨语言共享的宿主 API 语义、错误码、取消和进度模型。
3. 增加追加写入的幂等、预览和副作用摘要；持久化幂等另行实施。
4. 已增加 C# SDK、三种语言模板以及模板/Tracker 发现 API。
5. 再实施持久化幂等、日期范围快捷 API，以及确有业务需要时的追加式修正/冲正。

## 8. 评审结论

当前脚本 API 已经适合作为 V1 内部扩展接口，但如果目标是作为面向用户和第三方开发者的 SDK，优先需要解决“能不能发现 API、能不能知道错误、能不能安全重试、三种语言行为是否一致”这四个问题。

当前不建议直接扩大底层权限或开放数据库对象；应先改善统一上下文、错误模型、发现 API 和副作用控制。
