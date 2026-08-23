# 脚本系统设计

## 1. 文档范围

本文描述 Diary.App 脚本系统的目标设计、运行时边界和分阶段实现计划。

本文同时记录目标设计和当前实现。当前代码已经定义版本化基础契约、最小脚本管理器、
构建与执行边界、受限只读事项查询宿主、脚本目录自动加载和脚本管理页；C#、Lua 和 Python 均通过独立 Worker 执行链路。

## 2. 设计目标

脚本系统用于让用户通过脚本扩展日记处理、编辑器操作和 Tracker 联动，同时保持核心程序稳定。

目标如下：

- 支持应用脚本和编辑器脚本两类使用场景。
- 通过统一的宿主 API 访问日记、任务进度和 Tracker；模板由宿主负责，不交给脚本操作。
- 脚本引擎可插拔，C#、Lua、Python 不直接耦合到核心 UI。
- 编译错误和运行时错误可以定位到脚本、引擎、行号和列号。
- 脚本执行不应阻塞 UI，也不应因为脚本异常导致主程序退出。
- 对文件、网络、进程和写入操作提供明确的权限边界。
- 先实现稳定的运行时和 C# 引擎，再扩展 Lua、Python 等语言。

非目标：

- 不为脚本暴露核心数据库连接或 DI 容器。
- 不保证任意语言脚本可以无修改地互相移植。
- 不在第一阶段实现完整 IDE、断点调试和远程脚本执行。

## 3. 当前实现

当前版本化契约位于 `Diary.ScriptBase`：

- `ScriptApiVersion.V1`、`IScriptProgramV1`、`IScriptEngineV1` 和 `IScriptValidatorV1`：稳定的执行、构建与无执行校验边界。
- `ScriptDescriptor`：稳定 ID、名称、API 版本、应用/编辑器范围、入口类型和描述。
- `ScriptDiagnostic`、`ScriptBuildResult`、`ScriptValidationResult` 和 `ScriptExecutionResult`：结构化构建、校验与运行诊断。
- `ScriptEntryKind`、`ScriptAutomationContext`、`IScriptApplicationContext`、`IScriptEditorContext`、`IScriptAutomationContext` 和 `ScriptExecutionRequest`：按功能入口划分的执行上下文。
- `ILogApi`：跨进程异步调试日志 API，Worker 通过 `log.write` 转发到宿主。
- `ScriptProgramAdapter`：将按功能划分的 C# 入口适配到 Worker 使用的 `IScriptProgramV1`。

`Diary.Script.Runtime` 当前提供：

- `ScriptEngineRegistry`：注册引擎并按匹配优先级选择。
- `ScriptBuildService`：选择引擎、构建并规范化失败诊断。
- `ScriptCatalog`：按稳定脚本 ID 注册和读取构建后的程序。
- `ScriptExecutionContext`：按入口类型暴露目标、参数、日期范围、自动化事件、取消和进度 API；`Diary.ScriptHost` 提供 C# `context.Api()` 强类型门面。
- `ScriptExecutor`：目标校验、独立执行 ID、取消、超时和异常隔离。
- `ScriptManager`：组合构建、注册和执行的最小入口。
- `ScriptDirectoryLoader`：扫描 application/editor 目录和脚本包，读取入口类型元数据，校验 descriptor/manifest 与作用域、目标的一致性，并隔离单个脚本失败。

`Diary.ScriptHost` 当前提供 `IDiaryApi`，只返回不可变事项、备注和标签 DTO。
标签 DTO 同时保留兼容用的数值 `Level`，提供语义化的 `IsPrimary`/`isPrimary` 字段供三种脚本判断主标签，
并提供只读的字符串键值 `Metadata`/`metadata` 元数据；推荐使用 `projectNumber` 保存项目编号。
复用核心 `WorkItemQuery` 的校验和查询语义，并返回权限、输入、数据库和取消错误。

当前引擎项目为：

- `Diary.Script.CSharp`：已实现基于 Roslyn 的 V1 构建、入口发现和行列诊断，并拒绝动态绑定、
  类型反射入口、线程、脱离生命周期的任务调度及文件、网络、进程、原生调用等危险 API；
  运行时统一由 C# Worker 承载；校验模式只 Emit 到内存，不加载程序集或实例化入口。Roslyn 引用集在普通部署中优先使用 TPA 文件，单文件发布中从已加载程序集的原始 metadata 建立引用。
- `Diary.Script.Lua`：使用 `NLua 1.7.9 + KeraLua` 做语法构建和 descriptor 校验；校验模式只调用 `LoadString`，运行时由独立 .NET worker 承载。
- `Diary.Script.Python`：通过 `PythonRuntimeResolver` 发现本机 Python 3 解释器；校验模式只在隔离解释器中执行 `ast.parse` 和固定安全策略，正式运行使用受控 `worker.py` 独立进程。

Worker 进程边界、消息封装、生命周期和重启语义见
[`ScriptWorkerDesign.md`](ScriptWorkerDesign.md)。

脚本作者视角的 API 使用评审、已发现问题和优化优先级见
[`ScriptApiOptimization.md`](ScriptApiOptimization.md)。

当前尚未完成：

- 更细粒度的 Tracker、网络和文件系统权限。
- 执行状态历史持久化和更丰富的快捷入口。
- 更细粒度的运行时资源限制和跨平台强制终止策略。

`Diary.ScriptTests` 当前覆盖契约、引擎选择、构建隔离、目录项注册、目标校验、异常、
取消、超时、非法宿主调用、只读查询结果一致性和敏感信息边界。

`Diary.App` 当前提供基于 Semi.Avalonia.AvaloniaEdit + AvaloniaEdit.TextMate 的 C# 内置编辑器，支持保存、同目录另存为、
metadata 随源码移动、外部修改冲突保护、编译诊断和按行列跳转，窗口内容避让系统标题栏；对应窗口交互由 Avalonia Headless 测试覆盖。
脚本管理列表中的 C#、Python 和 Lua 脚本使用官方 SVG 图标（[C#](https://github.com/dotnet/brand/blob/main/logo/language-icons/csharp-72.svg)、
[Python](https://s3.dualstack.us-east-2.amazonaws.com/pythondotorg-assets/media/files/python-logo-only.svg)、[Lua](https://upload.wikimedia.org/wikipedia/commons/c/cf/Lua-Logo.svg)），未知语言继续使用 Material 图标回退。
脚本管理页的新建向导支持 C#、Lua 和 Python，并为源码写入对应扩展名、引擎元数据和入口类型；Lua/Python 模板按入口类型生成 `application_main(context)` 或 `editor_main(context)`。

## 4. 分层架构

```text
脚本 UI / 命令
       |
       v
ScriptManager
       |
       +-- ScriptCatalog       脚本发现、元数据和加载状态
       +-- ScriptBuildService  选择引擎、编译、缓存和诊断
       +-- ScriptExecutor      执行、取消、超时和异常隔离
       +-- Execution Context   目标、参数、日期范围和事项快照
       |
       v
IScriptEngineV1
       |
       +-- C# Engine
       +-- Lua Engine
       +-- Python Engine
       |
       v
Worker 协议（HostCall 分发）
       |
       +-- Diary API
       +-- Tracker API
       +-- UI API
       +-- Progress API

AI / MCP
       |
       v
IScriptValidatorV1
       |
       +-- C# 内存 Emit
       +-- Lua LoadString
       +-- Python ast.parse
       |
       x  不进入 ScriptExecutor / Worker / HostCall
```

核心程序只依赖 `Diary.ScriptBase` 和运行时抽象，不依赖某一种脚本语言的实现。

上下文执行、模板边界和 Tracker 复合身份见 [脚本上下文图](diagrams/script-context.svg)。
图表源文件为 [`script-context.puml`](diagrams/script-context.puml)。

进度报告已接线：`Diary.App/Models/ScriptProgressTracker`（内存，最近 20 次执行、每次最多 50 条时间线，`Changed` 事件）同时接入两条执行路径（Worker 路径 dispatcher 的 progressReporter 与进程内路径 `ScriptExecutionContext` 的 progressReporter）；`ScriptManagementViewModel` 提供 ProgressFraction/ProgressMessage/HasProgress，管理页底部运行栏显示进度条与文本，执行历史条目日志追加「进度：」时间线。执行历史与进度均为会话内存态，重启即失，持久化明确延期（用户决策）。

## 5. 脚本类型、执行上下文和生命周期

脚本按功能入口分为 Application、Editor、Automation 和 Query。底层 Worker 仍使用统一的 `IScriptProgramV1` 适配协议。

### 5.1 应用脚本

应用脚本使用 `IApplicationScriptV1` 或 `ApplicationScript`，对应 `ScriptEntryKind.Application`。它接收没有编辑器目标的 `IScriptApplicationContext`，适合批量整理、导出统计和创建追加式工作记录。

### 5.2 编辑器脚本

编辑器脚本使用 `IEditorScriptV1` 或 `EditorScript`，对应 `ScriptEntryKind.Editor`。它必须收到 `Year`、`Quarter`、`Month`、`Week`、`Day` 或 `WorkItem` 目标，并可通过 `GetDateRange()`、`StreamItemsAsync()` 或不可变的 `ScriptWorkItem` 快照读取上下文。

编辑器脚本可以在 metadata 中声明 `supportedEditorTargets`；未声明时视为支持全部六类目标。`ScriptExecutionRequest.Target` 对应用程序扩展必须为 `null`，对编辑器扩展必须是上述六类目标之一。查询和 Tracker/模板发现均为只读 API。

### 5.3 自动化脚本

自动化脚本使用 `IAutomationScriptV1` 或 `AutomationScript`，对应 `ScriptEntryKind.Automation`。`IScriptAutomationContext.Automation` 携带触发器类型、事件数据和幂等键；触发器类型包括 Startup、Scheduled、WorkItemCreated、WorkItemSaved 和 TagAdded。自动化脚本只允许通过追加式 API 产生工作记录，不提供删除或直接改写历史记录。

五类自动化触发均已接线。metadata/manifest 支持 `Schedule`（格式 `"daily HH:mm"`）、`RunOnStartup`（bool，默认 false）和 `Triggers`（`WorkItemCreated`、`WorkItemSaved`、`TagAdded` 数组）；其中 `Triggers` 仅允许 Automation 入口，事件型自动化可不配置 `Schedule`，但必须至少声明一个事件触发、定时或启动补跑方式。目录加载时校验，非法配置→ `SCRIPT_SCHEDULE_INVALID` 构建失败且不注册。`Diary.App/Services/ScriptAutomationScheduler` 以 30 秒 DispatcherTimer tick + `SemaphoreSlim` 串行执行，定时/启动使用内存 last-run 防重，事件使用 `scriptId + trigger + eventId` 防重；事件请求幂等键为 `event:{trigger}:{eventId}`。工作项保存入口在创建成功后触发 `WorkItemCreated`，更新成功后触发 `WorkItemSaved`；持久化工作项添加标签立即触发 `TagAdded`，新建草稿中的标签在首次保存成功后按添加顺序补发，重复标签、加载和删除不触发。事件数据包含 `workItemId`、`date`、`comment`、`time`、`priority`；`TagAdded` 额外包含 `tagId`、`tagName`、`tagLevel`、`tagSource`、`sequence`。三语言 context 均提供 `automation = { trigger, eventData, idempotencyKey }`，应用 PreShutdown 时停止调度器。

### 5.4 查询入口和模板边界

Query 入口已落地：ScriptBase 提供 `IQueryScriptV1` 接口与 `QueryScript` 抽象基类（Scope=Application、EntryKind=Query、上下文 `IScriptApplicationContext`），`ScriptProgramAdapter` 与 C# 引擎类型识别均已支持；创建向导提供「查询脚本」模板（C# 使用 `QueryScript` 子类，Lua/Python 使用 `query_main` 入口），管理页可直接运行（CanRun 已放行 Application scope）。模板和已启用 Tracker 实例提供只读发现 API；模板的选择、读取、应用和持久化仍由编辑器或宿主完成，脚本不能创建、修改、删除或直接应用模板。

脚本生命周期为：

```text
发现 -> 读取元数据/manifest -> 校验入口和目标 -> 选择引擎 -> 构建/加载缓存
     -> 注册脚本 -> 用户执行 -> 创建执行上下文 -> Worker 执行
     -> 返回结果/诊断/副作用摘要 -> 释放上下文
```

`ScriptAutomationScheduler` 挂在脚本目录加载完成后：`LoadScriptsAsync` 成功后应用加载结果并启动调度器，再在后台执行启动补跑（`RunStartupCatchUpAsync`）；调度器按内存 last-run 表防止同一调度窗口重复执行。工作项创建、保存或标签事件触发的自动化脚本若返回失败或抛出异常，主界面显示非阻塞错误 Toast，明确工作项/标签已经保存，失败的是后续自动化；Startup 和 Scheduled 后台任务仍只记录日志与执行历史。

追加式日志项的幂等结果由宿主共享的 `ScriptIdempotencyStore` 保存。普通日志项和模板日志项使用不同的 API 作用域，即使幂等键字符串相同也不会互相覆盖；已提交结果会在应用重启后恢复，Worker 重启不会自动重放带副作用的请求。普通日志项和模板日志项的真实写入均在 provider 事务中完成，失败时回滚；`Preview=true` 只返回投影记录和副作用摘要，不开启写入事务，也不改变数据库或幂等存储。事务提交成功后，创建 API 通过应用层注入的回调发送共享数据变更通知，使事件记录页重新读取当前日期；Preview、幂等重放和写入失败不发送通知，通知失败也不会把已提交写入改判为失败。脚本自动化不提供删除或直接改写历史记录，Tracker 远程写入也不在当前 Worker HostCall 范围内。

C# 脚本编辑器已接入进程内的 LSP-like 语言服务：复用 `CSharpEngine` 的 Roslyn 引用集，按 250ms 防抖执行实时语义诊断，并提供基于真实符号类型的成员/作用域补全和悬停信息服务；编辑器仍保留关键字补全作为降级路径。实时诊断只改善编辑体验，不替代保存前的正式构建检查，也不改变 Worker 沙箱和脚本安全策略。

构建失败的脚本可以显示在诊断列表中，但不得阻止其他脚本和核心程序启动。

## 6. 引擎契约

当前使用 `IScriptEngineV1` 完整构建可执行程序，并使用 `IScriptValidatorV1` 提供不产生 `IScriptProgramV1` 的独立校验。构建请求携带源码和 descriptor hint，构建结果携带程序及结构化诊断；校验请求只携带虚拟路径、源码和 API 版本，结果只包含成功状态与诊断。不同语言的完整程序最终都由 Worker 适配层执行，校验路径不会进入 Worker。

当前契约模型：

```csharp
public sealed record ScriptBuildRequest(
    string SourcePath,
    string Source,
    ScriptApiVersion ApiVersion = ScriptApiVersion.V1,
    ScriptDescriptorHint? DescriptorHint = null);

public sealed record ScriptDiagnostic(
    string Code,
    string Message,
    ScriptDiagnosticSeverity Severity,
    ScriptDiagnosticCategory Category,
    string? SourcePath = null,
    int? Line = null,
    int? Column = null);

public sealed record ScriptBuildResult(
    bool Succeeded,
    IScriptProgramV1? Program,
    ImmutableArray<ScriptDiagnostic> Diagnostics);

public sealed record ScriptValidationRequest(
    string SourcePath,
    string Source,
    ScriptApiVersion ApiVersion = ScriptApiVersion.V1);

public sealed record ScriptValidationResult(
    bool Succeeded,
    ImmutableArray<ScriptDiagnostic> Diagnostics);
```

引擎应提供以下能力：

- `Name`：稳定的引擎标识，例如 `csharp`、`lua`、`python`。
- `Version`：用于缓存失效和诊断。
- `Match`：根据扩展名或脚本包声明判断是否支持。
- `Build`：编译或加载脚本并返回结构化诊断。
- `Validate`：通过独立的 `IScriptValidatorV1` 只执行编译/解析和安全策略，不能加载编译产物、实例化脚本或执行入口。
- `Cacheable`：说明是否支持编译结果缓存。（注：早期遗留接口 `IScriptEngine`（含 `Cacheable` 成员）已移除，当前契约只有 `IScriptEngineV1`；V1 引擎的缓存行为由各引擎实现内部决定，例如 C# 引擎按引擎名/版本、API 版本、安全策略版本和源码哈希做编译缓存。）

引擎不负责扫描目录、显示 UI 或保存用户权限。上述职责由宿主运行时承担。

## 7. 脚本管理器

当前 `IScriptManager` 统一提供构建注册和执行入口；`IScriptDirectoryLoader` 负责启动时目录发现和注册：

```csharp
public interface IScriptManager
{
    ValueTask<ScriptBuildResult> BuildAndRegisterAsync(
        ScriptBuildRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<ScriptExecutionOutcome> ExecuteAsync(
        string scriptId,
        ScriptExecutionRequest request,
        IScriptExecutionContext context,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    ValueTask<ScriptExecutionOutcome> ExecuteAsync(
        string scriptId,
        ScriptExecutionRequest request,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}
```

注：实际接口（`Diary.Script.Runtime/ScriptManager.cs`）除带 `context` 的 `ExecuteAsync` 外，还有第三个无 `context` 参数的 `ExecuteAsync` 重载，由管理器内部创建执行上下文。
```

当前管理器已经负责构建、注册、按 ID 查找和统一执行；目录加载器负责发现、重载和按加载结果管理可执行状态。目标职责还包括：

- [已完成] 扫描 `scripts/application` 和 `scripts/editor` 目录。
- [已完成] 根据扩展名选择引擎，读取相邻 JSON 元数据并按加载结果处理可执行状态；metadata 和 manifest 不提供启用/禁用字段，旧 JSON 中的多余字段按未知属性忽略。
- 扫描脚本包。
- 维护脚本 ID、显示名称、类型、状态和错误信息。
- [已完成] 使用源码哈希、引擎版本、契约版本和安全策略版本管理 C# 编译缓存。
- [已完成] 创建每次执行独立的上下文。
- 编辑器菜单按脚本声明的 `SupportedEditorTargets` 过滤目标；未声明时当前契约默认支持全部目标。
- 统一处理取消、超时、异常和执行结果。

目录加载由应用层的共享加载状态协调。应用初始化阶段立即在后台启动一次异步预加载，目录枚举、元数据读取和脚本构建不会占用 UI 线程；脚本管理页显示时复用进行中的任务或缓存结果，手动重新加载和脚本变更后的检查才会强制启动新一轮扫描。

脚本管理器当前校验上下文：

- 应用程序扩展的 `Target` 必须为 `null`。
- 编辑器扩展的 `Target` 必须是 `Year`、`Quarter`、`Month`、`Week`、`Day` 或 `WorkItem`。
- 日期目标的日期格式、月份长度、季度边界和日期范围由 `ScriptEditorTargetResolver` 校验。
- 事项目标必须包含有效 ID，并只能携带 `ScriptWorkItem` 安全快照。
- `ScriptExecutionContext.Metadata` 提供执行 ID、来源、脚本 ID 和开始时间，供宿主日志关联。

模板相关校验和应用不属于脚本管理器职责。

ViewModel 不应直接调用 `IScriptEngineV1` 或具体脚本类型。外部 AI 的 MCP 入口只能调用 `IScriptValidatorV1`，不得复用会加载程序的 `BuildAsync` 或进入 `ScriptExecutor`。

## 8. 脚本元数据和目录

当前支持源码文件旁的元数据文件和包含 `manifest.json` 的脚本包。编辑器脚本可通过
`supportedEditorTargets` 声明适用目标；应用脚本忽略该字段：

```text
配置目录/scripts/
  application/
  editor/
  cache/
```

建议元数据模型：

```csharp
public sealed record ScriptDescriptor(
    string Id,
    string Name,
    ScriptApiVersion ApiVersion,
    ScriptScope Scope,
    string? Description = null,
    IReadOnlyList<ScriptEditorTargetKind>? SupportedEditorTargets = null);
```

脚本 ID 必须稳定且唯一。不能直接使用显示名称作为 ID。

脚本包可以包含：

```text
manifest.json
main.cs / main.lua / main.py
README.md
assets/
```

`manifest.json` 的 `entry` 必须指向包目录内的源码文件，不能使用 `../` 越界路径。

脚本包应校验路径，禁止通过 `../` 访问脚本目录外的文件。

## 9. 执行模型

脚本默认在后台线程执行，禁止直接阻塞 UI 线程。Windows x64 发布包可选择附带 Python 3.13.15 embedded runtime，运行时位于应用目录的 `python/` 子目录；Python 脚本启动时会优先使用该目录中的 `python.exe`，未附带时再按用户配置和系统环境查找。Python 语法检查和正式 Worker 在 `-I` 隔离模式前通过 `-X utf8` 显式启用 UTF-8，并使用 UTF-8 无 BOM 标准输入输出，避免隔离模式忽略环境变量后由 Windows 本地代码页影响中文脚本和协议数据。

通过 `IScriptExecutionContextFactory` 为每次执行创建独立的 `ScriptExecutionContext`，包含：

- 脚本 ID和执行 ID。
- 取消令牌。
- 超时时间。
- 执行目标、参数、日期范围和事项快照。
- 日志上下文。
- 当前用户操作来源。

当前执行结果由 `ScriptExecutionOutcome` 携带执行时间和耗时，结果本身携带状态与诊断：

```csharp
public sealed record ScriptExecutionOutcome(
    Guid ExecutionId,
    ScriptExecutionResult Result,
    DateTimeOffset? StartedAt,
    TimeSpan Duration,
    ScriptExecutionSource Source);
```

宿主必须捕获脚本异常。脚本异常只能结束当前执行，不得让异常传播到应用主循环。

对于无法强制取消的引擎，超时后至少应停止等待、标记执行失败，并记录引擎不支持强制终止。
Python 等外部进程引擎应使用独立进程，以便在超时或崩溃时终止进程。

## 10. 宿主边界和日志

V1 不把脚本 capability 当作用户授权门禁。宿主只注册已经实现的 API，未注册的 API
返回结构化的不支持错误；独立 Worker 通过握手声明实际支持的 HostCall，宿主仍会校验方法、参数、
目标、执行 ID 和消息大小。

已实现的边界 API 包括 `IDiaryApi`、`ITrackerApi`、`SysApi` 和 `ILogApi`。网络、文件系统、
数据库连接、DI 容器和 Avalonia 对象不会注入脚本。`ILogApi` 的日志通过 `log.write`
HostCall 转发到宿主，并限制单条消息大小。宿主会把格式化后的脚本日志同时写入主程序
logger 和脚本管理页的共享运行日志窗口；共享窗口只接收脚本日志，不显示其他程序日志，
当前会话最多保留 2000 条。诊断和日志不得包含密码、API Key 或授权令牌。

C# 脚本尤其需要限制引用和宿主对象。不能将 `App`、数据库连接、服务容器或任意程序集实例直接注入脚本。
当前所有脚本都在独立 Worker 中执行。C# 的 Roslyn 限制策略用于减少危险引用和常见逃逸，Worker 进程边界负责隔离崩溃、超时和资源失控；它仍不是面向不受信任代码的完整安全沙箱。

## 11. 脚本 API

V1 使用按领域拆分的宿主 API：

```text
IScriptExecutionContext
  +-- IDiaryApi
  +-- ITrackerApi
  +-- SysApi
  +-- ILogApi
```

这样可以：

- 降低单个接口的增长速度。
  - 便于为脚本提供测试替身。
  - 让脚本文档可以按领域和实际 HostCall 生成。

通用选项对话框、目录选择令牌、XLSX 导出、结果文件询问打开及对应的 HostCall/跨语言协议已完成第一阶段实现；CSV/DOCX 仍属于后续扩展，详见 [`ScriptSpreadsheetExportDesign.md`](ScriptSpreadsheetExportDesign.md)。交互 API 只允许有人值守的 `Editor+Editor`、`Application+Manual` 和 `Query+Manual` 执行使用；无人值守自动化入口禁止调用，能力发现不能替代宿主每次 HostCall 的上下文校验。RequireChoice 对话框禁止关闭，但会在取消令牌、Worker 终止、通道断开或响应无法发送时执行一次性清理并结束调用。

### 11.1 Tracker API

当前 `GetTracker(string pluginId)` 只适合简单的单实例键值读取。由于 Tracker 已支持多实例，建议扩展为：

```csharp
ITrackerScriptApi? GetTracker(string pluginId, string instanceId);
```

（注：上述建议已以不同形状落地。早期遗留的 `IScriptApi.GetTracker(string pluginId)`（`Diary.ScriptBase/IScriptApi.cs`）连同整个遗留接口族已移除，当前 V1 脚本 API 使用多实例的 `ITrackerApi.GetInstance(pluginId, instanceId)`（`Diary.ScriptHost/ScriptApis.cs`），配合 `trackerInstances.get`/`trackerInstances.list` HostCall 定位实例；上面的扩展签名仅作为历史设计记录保留。）

Tracker 脚本 API 至少应包含：

```csharp
public interface ITrackerScriptApi
{
    string PluginId { get; }
    string InstanceId { get; }

    object? Get(string key);
    object? Query(string operation, IReadOnlyDictionary<string, object?>? parameters = null);
    object? Execute(string operation, IReadOnlyDictionary<string, object?>? parameters = null);
}
```

宿主只依赖通用契约，不应把 `RedmineApi`、GitHub SDK 或其他具体后端类型暴露到 `Diary.Core`。

## 12. 缓存

编译缓存键至少应包含：

```text
引擎名称
引擎版本
源码哈希
ScriptBase API 版本
安全策略版本
```

缓存失效条件包括：

- 源码发生变化。
- 引擎版本发生变化。
- 脚本契约版本发生变化。
- 安全策略版本发生变化。
- 脚本元数据发生变化。（注：C# 编译缓存键（`CSharpEngine.GetCachePath`，`Diary.Script.CSharp/CSharpEngine.cs`）实际不含 metadata，只含引擎名、引擎版本、API 版本、安全策略版本和源码哈希；metadata 变化由目录加载阶段的 `ScriptDirectoryLoader.MatchesMetadata` 拦截，导致脚本被判定为不匹配，而不是直接触发缓存失效。）

缓存文件应通过临时文件写入后原子替换，避免程序异常留下损坏缓存。

脚本缓存不是信任边界。加载缓存前仍必须校验脚本 ID、引擎版本和安全策略版本。

## 13. 错误和诊断

错误至少分为：

- 脚本发现错误。
- 元数据错误。
- 引擎匹配错误。
- 编译错误。
- 加载错误。
- 执行错误。
- 权限拒绝。
- 超时或取消。
- API 调用错误。

每条诊断建议包含：

- 脚本 ID。
- 脚本路径。
- 引擎名称和版本。
- 错误类别。
- 消息。
- 行号和列号。
- 执行 ID。
- 内部异常摘要。

UI 可以提供脚本列表、重新加载、编译检查、最近执行状态和错误详情。脚本加载失败时自动标记为不可执行，
修复源码或元数据后重新加载即可重试，不提供手动启用/禁用操作。

## 14. 引擎实施顺序

### 14.1 C#

C# 是第一个实现目标。建议使用 Roslyn，限制脚本引用和可访问宿主 API。

优点：

- 与 .NET 宿主契约一致。
- 编译诊断可以包含准确行号和列号。
- 便于实现源码缓存。

### 14.2 Lua

Lua 适合轻量自动化。应选用可以限制全局对象和库访问的实现，默认不开放文件系统、网络和进程能力。

### 14.3 Python

Python 不建议第一阶段嵌入主进程。优先考虑独立 Python 进程，通过受限协议与宿主通信：

```text
Diary.App <-> JSON/RPC stdin/stdout <-> python worker
```

独立进程便于处理解释器崩溃、依赖隔离和强制终止，但需要额外处理 Python 版本、环境发现、启动开销和跨平台打包。

### 14.4 Lua/Python 多语言接入决策

Lua 和 Python 均使用独立 worker，不在 `Diary.App` 进程内执行。两者共享
`ScriptWorkerDesign.md` 定义的 JSON 行协议、执行生命周期和只读宿主 API，但使用不同的运行时绑定：

```text
ScriptDirectoryLoader
        |
        v
ScriptBuildService -> ScriptEngineRegistry
        |                    |
        |                    +-- csharp -> C# WorkerSupervisor
        |                    +-- lua    -> Lua WorkerSupervisor
        |                    +-- python -> Python WorkerSupervisor
        v
ScriptCatalog（保存 EngineName 和 Descriptor）
```

#### 14.4.1 共同范围

当前 Lua/Python 均支持 `ScriptApiVersion.V1`、应用脚本和编辑器脚本。脚本 metadata 中的
capability 字段已移除并兼容忽略；Worker 通过 `supportedHostApis` 声明实际支持的方法。
当前开放工作项查询、受控日志项/模板日志项创建、Tracker 只读实例目录、剪贴板、用户交互、
`log.write` 和 `host.capabilities.list`；脚本可通过能力列表发现当前 Worker 实际注册的 HostCall。能力列表不替代权限、作用域和参数校验。
不提供工作项更新、Tracker 远程写入、网络、文件系统或进程创建，也不自动安装第三方依赖。

脚本相邻 metadata 或脚本包中的 `manifest.json` 是 ID、名称、Engine、Scope 和目标类型的权威来源。
引擎构建请求需要携带已解析的 descriptor hint，构建结果中的 Descriptor 必须与 metadata 一致，
不能由脚本源码静默改变脚本身份或执行范围；历史 capability 字段不参与权限判断。

`ScriptCatalog` 和执行路由必须保存稳定的 `EngineName`，不能在执行时再次根据文件扩展名猜测
worker。Host API 返回结果携带 `ApiError` 时使用稳定的大写错误码；Python HostCall 异常使用 `HostCallError.code`，Lua 同步 HostCall 使用 `[ERROR_CODE] message` 格式。
C#、Lua、Python 使用相互独立的 supervisor；某一种语言 worker 故障、重启或运行时缺失
不得影响其他语言。

#### 14.4.2 Lua

- 使用 NuGet `NLua` 1.7.9（依赖 `KeraLua >= 1.4.9`）承载在独立 .NET worker 中，使用标准 Lua 5.4 运行时。
- Lua worker 使用 `--language lua` 启动并通过统一协议握手，不直接引用主程序的 DI、数据库或 UI。
- 每次执行创建新的 Lua script/context；worker 可复用进程，但不能复用脚本全局变量、模块状态或宿主 API 代理。
- 创建 Lua 状态后只开放白名单标准库，默认关闭 `io`、`os`、`debug`、`package`、`require`、`dofile`、`loadfile` 和动态加载入口。
- 禁止调用 NLua 的 `LoadCLRPackage`，不注册 `LuaUserData`、反射对象或任意 .NET 类型；Lua 只能看到基础值、受控表和宿主 API 回调。
- Lua 脚本通过按入口类型约定的入口函数（`ScriptEntryKind` 依次对应 `application_main`、`editor_main`、`automation_main`、`query_main`，见 `Diary.Script.Worker/LuaWorker.cs` 的 `GetEntryFunctionName`）接收只读上下文和 `diary.workItems.query` 代理，宿主只传递不可变 DTO。
- 语法错误、运行时错误和入口错误必须转换为 `ScriptDiagnostic`，尽可能保留 sourcePath、行号和列号。
- Lua 的缓存第一版只缓存语法检查结果或源码 hash，不缓存可跨进程恢复的运行时对象。（当前未实现：`LuaEngine.BuildAsync`（`Diary.Script.Lua/LuaEngine.cs`）每次构建都新建受控 Lua 状态做语法检查，没有任何缓存；上述缓存策略仍为设计意图。）

#### 14.4.3 Python

- 使用独立的 Python 3 解释器进程和项目内受控 worker 脚本，不嵌入 CPython，也不自动创建或修改虚拟环境。
- `worker.py` 作为 `Diary.Script.Python.dll` 的嵌入资源，通过 `python -I -c` 启动，不以可替换的松散文件形式进入应用输出目录。
- Windows tag 发布同时提供轻量包和附带官方 Python 3.13.15 embeddable distribution 的包；Linux 开发机也可通过 `Tools/package-win-x64-with-python.sh` 生成同布局的本地验证包。运行时固定解压到应用目录的 `python/` 子目录，不写入系统 Python 注册表或 PATH；`PythonRuntimeResolver` 会优先检查应用目录及其 `python` 子目录，再检查 PATH 中的 `python3.exe`/`python.exe`/`py.exe` 候选。
- Linux 正式发行包不携带 Python，优先使用发行版提供的 `python3`/`python3.X` 系统包；应用不负责调用 apt、dnf、apk 或其他包管理器安装运行时。
- macOS 暂不携带官方 Python runtime，沿用显式配置路径或系统/用户提供的 `python3`，后续再单独评估发行策略。
- `PythonRuntimeResolver` 位于 `Diary.Script.Python`，负责按平台选择 runtime、探测 `--version`、确认 worker 路径和生成环境诊断。
- 运行时缺失或版本不支持时仍注册 Python 引擎；`.py` 脚本保留在目录列表中并显示结构化诊断，不静默当作未知扩展名。
- Python worker 的 stdout 只输出协议 JSON 行；脚本 `print` 和 traceback 写入受限 stderr，不能污染协议。
- 执行前使用 `ast.parse(source, filename=sourcePath)` 做语法检查（`Diary.Script.Python/PythonEngine.cs` 的语法探测脚本和 `worker.py` 的 `source_diagnostics`），`compile` 只在执行阶段调用；执行时使用受控入口和新的 globals/context；第一版默认每个 worker 最多处理一个请求后回收，避免模块和全局状态泄露。
- Python worker 只通过 `diary.workItems.query` 发起 HostCall，不直接读取宿主文件、数据库、环境中的凭据或网络服务。
- Windows embeddable distribution 不包含 pip；第一版只使用 Python 标准库，后续第三方依赖必须由应用发布包固定携带并经过兼容性验证，禁止运行时自动安装。
- 运行时缺失、版本不支持、语法错误、worker 启动失败、握手失败、非零退出、超时和取消必须映射为稳定诊断码。
- 嵌入资源只能降低单独替换 Worker 文件的风险，不能替代发布包签名或程序集完整性校验。

#### 14.4.4 运行时发现、路由和降级

Python 运行时发现只属于 `Diary.Script.Python`，不下沉到通用 transport 或 supervisor。解析结果当前为
`PythonRuntimeResolution(Succeeded, ExecutablePath, Version, Diagnostics)`（`Diary.Script.Python/PythonRuntimeResolver.cs`），
只携带解析是否成功、解释器绝对路径、Python 版本和诊断。以下字段为设计预期，当前未实现（全仓库尚无
`RuntimeKind` 枚举）：

- `RuntimeKind`：`WindowsEmbedded`、`SystemPackage` 或 `Explicit`。
- runtime 根目录和 worker 资源路径。
- 是否使用隔离模式、是否存在可用标准库。

平台策略如下：

| 平台 | 默认来源 | 备用来源 | 禁止行为 |
| --- | --- | --- | --- |
| Windows | 应用发布包内按 RID 匹配的 embeddable distribution | 用户显式配置的绝对路径；开发环境可选系统 Python | 不写 PATH/注册表，不自动下载或安装 Python |
| Linux | 系统 `python3` 或 `python3.X` 包 | 用户显式配置的绝对路径 | 不调用系统包管理器，不捆绑另一套 glibc/musl Python |
| macOS | 用户显式配置或系统/用户 `python3` | 后续评估应用附带 runtime | 不假设官方 Windows embeddable ZIP 可复用 |

用户显式配置用于测试、便携部署和管理员指定版本；正式 Windows 包中应优先使用随包 runtime，
避免因为 PATH 上的 Python 版本变化导致脚本行为改变。每个候选必须通过绝对路径启动探测命令，
记录解释器路径和版本；不通过 shell 拼接命令，不执行 `pip install`。Lua worker 的 `NLua` 托管程序集
和 `KeraLua` native 资产随对应 RID 部署，不依赖系统 Lua 命令。设计上要求至少验证以下 native 资产
（当前未实现：代码中没有 native 资产存在性验证，Lua 5.4 运行时由 `NLua 1.7.9`/`KeraLua 1.4.9`
NuGet 包按 RID 提供）：

- Windows：`win-x64` 的 `lua54.dll`。
- Linux：`linux-x64` 的 `liblua54.so`。
- macOS：对应架构的 `liblua54.dylib`。

引擎、目录加载器和脚本页应保留以下诊断码（当前实际实现与下方设计清单有差异，以实际码为准）：

- Lua：`LUA_RUNTIME_UNAVAILABLE`、`LUA_SYNTAX_ERROR`；worker 启动/终止失败落到通用 `WORKER_EXECUTION_FAILED`。
- Python：`PYTHON_RUNTIME_NOT_FOUND`、`PYTHON_VERSION_UNSUPPORTED`、`PYTHON_SYNTAX_ERROR`；worker 启动/终止失败落到通用 `WORKER_EXECUTION_FAILED`。
- 设计稿中的 `LUA_RUNTIME_NOT_FOUND`、`LUA_WORKER_START_FAILED`、`LUA_WORKER_TERMINATED`、`PYTHON_WORKER_START_FAILED`、`PYTHON_WORKER_TERMINATED` 当前未实现，保留为未来细化的诊断方向。

超时流程为：发送 `cancel` -> 等待有限宽限期 -> 终止对应 worker 进程树 -> 将当前执行标记为
`TimedOut` 或 `WORKER_TERMINATED`。不能把只停止等待当作已终止脚本。worker 终止后不自动重放
可能产生副作用的请求。

## 15. 测试计划

脚本系统至少需要以下测试：

- 引擎按扩展名匹配。
- 不支持的脚本不会被加载。
- 编译成功返回正确脚本类型。
- 编译失败返回结构化诊断。
- 脚本管理器发现脚本，加载失败的脚本自动禁用。
- 源码变化后缓存失效。
- 引擎版本变化后缓存失效。
- 应用脚本和编辑器脚本分发到正确入口。
- 脚本异常不会传播到宿主。
- 取消和超时返回正确状态。
- 权限不足时 API 调用被拒绝。
- 日记读写 API 的成功和失败路径。
- 新建脚本向导为 C#、Lua 和 Python 生成正确扩展名、入口和 metadata。
- 新建编辑器脚本向导按日、周、月、季度、年和当前事项提供目标样板，并将目标兼容性写入 metadata。
- Tracker 使用 `PluginId + InstanceId` 定位正确实例。
- 日志和诊断不会泄露敏感配置。
- Python worker 崩溃和退出码错误可以被宿主识别。
- Lua 和 Python 的运行时缺失、worker 启动失败、握手失败、非零退出、超时和取消均返回稳定诊断码。
- Lua/Python worker 故障不会影响 C# worker、其他脚本列表项或核心日记启动。
- Python worker 的解释器发现不依赖真实用户环境；测试使用假的解释器和内存文件/transport 替身。

第一阶段测试不应依赖真实 Redmine、真实 Python 环境或 UI 桌面会话；外部引擎和宿主 API 使用内存替身。

## 16. 分阶段路线

### 第一阶段：最小可用运行时

- 实现脚本目录扫描。
- [已完成] 实现 `ScriptDescriptor`、诊断和执行结果。
- [已完成] 实现最小 `ScriptManager`、构建服务和执行服务。
- [已完成] 实现 C# Roslyn 引擎。
- [已完成] 增加构建失败、执行异常、取消和超时测试。

### 第二阶段：应用集成

- 增加脚本设置页和脚本列表。
- 支持手动重新加载，并在脚本加载失败时自动禁用。
- 支持应用脚本和编辑器脚本命令。
- 增加后台任务、取消和超时。

### 第三阶段：安全边界

- 增加权限能力模型。
- 增加用户授权和权限拒绝诊断。
- 限制 C# 引用和宿主对象。
- 审查所有 V1 脚本 API（`Diary.ScriptHost` 各 `IScriptApiV1` 实现）的写入和 UI 操作。

### 第四阶段：多语言

- [设计已确定] Lua 使用受限 .NET 运行时和独立 Lua worker，默认关闭文件、网络、进程和动态加载。
- [设计已确定] Python 使用独立 Python 3 worker，由 `PythonRuntimeResolver` 负责解释器发现和版本诊断。
- [设计已确定] ScriptCatalog 保存 EngineName，C#、Lua、Python 使用独立 supervisor 路由。
- 实现 Lua 引擎、worker 适配器、构建诊断和执行入口。
- 实现 Python 引擎、runtime resolver、Python worker 和 HostCall 代理。
- 增加各引擎的运行时缺失、语法错误、崩溃、超时、取消和跨语言隔离测试。

### 第五阶段：Tracker 自动化

- 扩展多实例 Tracker 脚本 API。
- 为 Redmine 提供脚本操作。
- 为后续 GitHub、Linear、GitLab 等 Tracker 保留通用能力边界。
- 增加操作确认、失败重试和远程调用审计。

## 17. 维护约定

- 新增脚本引擎不得把语言实现类型加入 `Diary.Core`。
- 新增宿主能力必须说明权限要求和失败行为。
- 脚本 API 变更应增加契约版本或兼容适配。
- 任何远程 Tracker 操作都必须明确是否会产生写入副作用。
- 脚本诊断和日志不得输出敏感配置。
- 运行时实现与目标设计不一致时，应同步更新本文档的“当前实现”章节。


## 18. 脚本共享包

脚本管理页支持通过单个 `.diaryscripts` 包批量导出已成功加载的脚本源码及运行配置；加载失败的脚本不能导出。导入入口位于主窗口全局设置菜单，普通用户无需开启开发者功能即可安装高级用户提供的脚本扩展。共享包采用版本化 manifest、包内安全路径和 SHA-256 完整性校验；导入先预览，冲突默认跳过并要求用户显式授权覆盖。宿主不整包解压，限制脚本数和解压大小，并在批量写入失败时恢复备份。导入动作不立即执行脚本，但导入后会重新加载脚本目录和自动化调度，自动化脚本随后按其运行配置参与正常调度。完整格式和安全边界见 [ScriptSharingDesign.md](ScriptSharingDesign.md)。
