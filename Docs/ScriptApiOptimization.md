# 脚本 API 用户体验优化评审

## 1. 文档目的

本文从脚本作者的角度评估当前脚本 API，记录实际使用流程中的阻力、跨语言差异、文档问题和后续优化方向。

本文是脚本系统设计的补充评审文档，不作为当前工作项清单；具体实施时应根据版本目标拆分为独立任务，不直接写入 Docs/TODOS.md。

关联设计：Docs/ScriptSystemDesign.md、Docs/ScriptWorkerDesign.md。

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

- C# 需要实现 IScriptProgramV1、填写 ScriptDescriptor 并实现 ExecuteAsync。
- Lua 和 Python 只需要实现 main(context) 或 execute(context)。
- C# 用户需要手动通过 context.GetApi<T>() 查找宿主 API，样板代码较多。

建议增加统一的“5 分钟入门”示例，并在创建向导中明确显示：

1. 当前脚本是什么作用域。
2. 当前脚本从哪里获得目标和参数。
3. 当前语言可调用哪些宿主 API。
4. 脚本默认通过 Worker 执行，不能访问主程序对象。
5. 脚本失败、超时或取消时会发生什么。

### 3.2 查询工作项

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

### 3.3 创建日志项

当前只支持创建，不支持修改和删除。这有利于控制副作用，但用户会遇到两个实际问题：

- 重复执行脚本可能创建重复日志项。
- 创建前无法预览或确认将要写入的内容。

建议增加幂等键和预览能力，而不是直接开放无约束的更新/删除：

~~~text
CreateLogItem(..., idempotencyKey)
PreviewCreateLogItem(...)
~~~

更新和删除能力应在后续通过字段白名单、用户确认和审计记录逐步开放。

### 3.4 使用模板和 Tracker

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
GetRequiredApi<T>()
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

C# 要求脚本作者手动将 CancellationToken 传给每一个异步 API；Python 依赖 Worker 的执行跟踪；Lua 的宿主桥接目前没有完整传递取消令牌。

建议：

- 上下文直接暴露当前取消状态。
- 宿主 API 默认使用当前执行的取消令牌。
- 动态语言提供 context.isCancelled() 或等价方法。
- 增加统一进度 API，而不是让用户用日志模拟进度。

示例：

~~~text
context.progress.report(0.5, "正在处理工作项")
~~~

### 4.5 写入 API 缺少幂等和预览

脚本管理页面允许手动执行脚本，但脚本失败后用户可能重试。当前创建接口没有幂等键，重复执行可能产生重复日志项。

建议先增加：

- idempotencyKey：同一业务动作重复提交时只产生一个结果。
- preview 或 dryRun：返回将要创建的内容，不立即写入。
- 执行历史中记录副作用摘要。

更新和删除应在这些机制完善后再开放。

### 4.6 发现型 API 不足

模板、Tracker 实例和可用宿主 API 的 ID 目前都需要用户从其他界面或文档获得。

建议提供只读列表和搜索接口，并在 UI 中支持复制稳定 ID。

### 4.7 C# 样板代码偏多

当前最小 C# 脚本需要实现完整的 IScriptProgramV1 和 descriptor。建议增加 SDK 层基类或辅助工厂：

~~~csharp
public abstract class DiaryScript
{
    public abstract string Id { get; }
    public abstract string Name { get; }

    public abstract ValueTask RunAsync(
        ScriptContext context,
        CancellationToken cancellationToken);
}
~~~

底层 V1 契约继续保留，用于高级扩展和兼容性控制。

## 5. 安全和可靠性建议

当前所有语言默认使用 Worker，这是合理的隔离边界。后续 API 优化仍应保持以下原则：

- 脚本不能直接获取数据库连接、DI 容器、App 实例或 UI 控件。
- 写入 API 应默认提供明确的副作用结果。
- Worker 不应自动重试可能产生副作用的请求。
- 超时、取消、Worker 崩溃和宿主错误必须转换为稳定诊断码。
- 查询结果应保持为不可变 DTO。
- API 版本、字段弃用和兼容策略应在 SDK 文档中明确说明。

## 6. 优化优先级

### P0：修正文档和契约表达

- 修正所有文档中的 Worker/进程内执行描述。
- 修正 Offset 限制、章节编号和 Get/GetInstance 命名。
- 统一 Title/Comment 的语义说明。
- 建立统一错误码和宿主 API 可用性文档。

### P1：改善脚本日常开发体验

- 增加强类型 ScriptContext 外观。
- 增加 GetRequiredApi<T>()。
- 增加日期范围快捷方法。
- 增加模板和 Tracker 实例发现 API。
- 增加 C# SDK 基类和最小示例。
- 为三种语言各提供一个完整的“查询并创建日志项”示例。

### P2：增强自动化能力

- 增加幂等键和 DryRun/预览。
- 增加统一进度报告。
- 改善取消传播。
- 设计安全的工作项更新能力。
- 增加稳定游标分页。

## 7. 推荐实施顺序

1. 先修正文档与现有契约说明，避免用户按照错误限制编写脚本。
2. 抽象跨语言共享的宿主 API 语义和错误码。
3. 增加模板、Tracker 实例和日期范围的发现/快捷 API。
4. 增加 C# SDK 辅助层，减少 descriptor 和入口样板代码。
5. 最后增加幂等、预览、进度和更安全的写入能力。

## 8. 评审结论

当前脚本 API 已经适合作为 V1 内部扩展接口，但如果目标是作为面向用户和第三方开发者的 SDK，优先需要解决“能不能发现 API、能不能知道错误、能不能安全重试、三种语言行为是否一致”这四个问题。

当前不建议直接扩大底层权限或开放数据库对象；应先改善统一上下文、错误模型、发现 API 和副作用控制。
