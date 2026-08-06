# 脚本系统设计

## 1. 文档范围

本文描述 Diary.App 脚本系统的目标设计、运行时边界和分阶段实现计划。

本文同时记录目标设计和当前实现。当前代码已经定义版本化基础契约、最小脚本管理器、
构建与执行边界以及受限只读事项查询宿主；脚本目录扫描、语言引擎和 UI 集成仍未完成。

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

- `ScriptApiVersion.V1`、`IScriptProgramV1` 和 `IScriptEngineV1`：稳定的执行与引擎边界。
- `ScriptDescriptor`：稳定 ID、名称、API 版本、应用/编辑器范围和能力声明。
- `ScriptDiagnostic`、`ScriptBuildResult` 和 `ScriptExecutionResult`：结构化构建与运行诊断。
- `ScriptTarget`、`EditorScriptContext` 和 `ScriptExecutionRequest`：最小上下文式执行入口。
- `LegacyScriptAdapters`：保留现有应用脚本和日期/范围编辑器脚本的兼容适配。

`Diary.Script.Runtime` 当前提供：

- `ScriptEngineRegistry`：注册引擎并按匹配优先级选择。
- `ScriptBuildService`：选择引擎、构建并规范化失败诊断。
- `ScriptCatalog`：按稳定脚本 ID 注册和读取构建后的程序。
- `ScriptExecutionContext`：按能力暴露宿主 API。
- `ScriptExecutor`：目标校验、独立执行 ID、取消、超时和异常隔离。
- `ScriptManager`：组合构建、注册和执行的最小入口。

`Diary.ScriptHost` 当前提供 `IWorkItemQueryScriptApi`，只返回不可变事项、备注和标签 DTO，
复用核心 `WorkItemQuery` 的校验和查询语义，并返回权限、输入、数据库和取消错误。

当前引擎项目为：

- `Diary.Script.CSharp`：已定义 C# 引擎类型，但 `Build` 尚未实现。
- `Diary.Script.Lua`：当前为占位项目。
- `Diary.Script.Python`：当前为占位项目。

当前尚未完成：

- 脚本目录和脚本包格式。
- 文件系统扫描、元数据读取和启用状态持久化。
- 具体语言引擎发现、构建和脚本加载。
- 编译缓存。
- 后台任务调度和执行日志上下文。
- 更细粒度的 Tracker、网络和文件系统权限。
- 脚本 UI 和快捷入口。
- C#、Lua 和 Python 实际引擎。

`Diary.ScriptTests` 当前覆盖契约、引擎选择、构建隔离、目录项注册、目标校验、异常、
取消、超时、能力拒绝、只读查询结果一致性和敏感信息边界。

## 4. 分层架构

```text
脚本 UI / 命令
       |
       v
ScriptManager
       |
       +-- ScriptCatalog       脚本发现、元数据和启用状态
       +-- ScriptBuildService  选择引擎、编译、缓存和诊断
       +-- ScriptExecutor      执行、取消、超时和异常隔离
       +-- ScriptPermission    权限检查和用户授权
       |
       v
IScriptEngine
       |
       +-- C# Engine
       +-- Lua Engine
       +-- Python Engine
       |
       v
IScriptApi
       |
       +-- Diary API
       +-- Tracker API
       +-- UI API
       +-- Progress API
```

核心程序只依赖 `Diary.ScriptBase` 和运行时抽象，不依赖某一种脚本语言的实现。

上下文执行、模板边界和 Tracker 复合身份见 [脚本上下文图](diagrams/script-context.svg)。
图表源文件为 [`script-context.puml`](diagrams/script-context.puml)。

## 5. 脚本类型、执行上下文和生命周期

脚本继续分为两类：

### 5.1 应用脚本

应用脚本通过 `IApplicationScript.Execute` 执行，适合：

- 批量整理日记。
- 导出或统计数据。
- 创建或整理工作项数据。
- 调用 Tracker 的批量操作。

### 5.2 编辑器脚本

编辑器脚本不应继续增加 `ExecuteWeek`、`ExecuteMonth`、`ExecuteYear` 等固定方法。
建议统一接收一个执行上下文，由上下文描述时间范围、日历粒度和业务目标。

时间粒度和业务目标必须分开建模：年/月/日是时间粒度，项目、Tracker Issue 和事项目标是业务目标。

建议模型：

```csharp
public enum ScriptTimeGranularity
{
    WorkItem,
    Day,
    Week,
    Month,
    Quarter,
    Year,
    CustomRange,
}

public enum ScriptTargetKind
{
    CurrentEditor,
    CurrentWorkItem,
    SelectedWorkItems,
    CalendarPeriod,
    Project,
    TrackerIssue,
    TrackerInstance,
}

public sealed record ScriptScope(
    ScriptTimeGranularity Granularity,
    string StartDate,
    string EndDate);

public sealed record ScriptTarget(
    ScriptTargetKind Kind,
    string? PluginId = null,
    string? InstanceId = null,
    string? TargetId = null);

public sealed record EditorScriptContext(
    ScriptScope Scope,
    ScriptTarget Target,
    IReadOnlyDictionary<string, object?> Parameters);
```

模板不是脚本上下文的一部分。模板的选择、读取、应用和持久化均由编辑器或宿主完成；脚本不能：

- 选择模板或切换当前模板。
- 创建、修改或删除模板。
- 将模板 ID 作为脚本参数来批量应用。
- 直接修改模板中的 Tracker 扩展 payload。

如果编辑器已经根据用户选择模板创建了工作项草稿，脚本可以处理宿主传入的草稿内容，
但这不表示脚本拥有模板操作权限。脚本 API 默认也不提供模板写入接口。

建议上下文式入口：

```csharp
public interface IContextScript : IScript
{
    ScriptExecutionResult Execute(
        EditorScriptContext context,
        IScriptApi api);
}
```

现有 `IEditorScript.ExecuteDay` 和 `ExecuteRange` 可以作为兼容适配层；新脚本优先实现上下文式入口。

编辑器脚本适合：

- 处理当前日期的工作项。
- 对日期范围执行格式化或汇总。
- 在编辑器中生成或修改内容。

### 5.3 工作流脚本

当脚本面向时间周期、项目或 Tracker 批处理，而不是当前编辑器 UI 时，建议使用 `Workflow` 概念。
它可以先作为 `IContextScript` 的一种使用方式，不必立即新增独立接口。

建议后续将 `ScriptUsage` 扩展为 `Application`、`Editor` 和 `Workflow`，但不应为了年/月/日分别增加脚本类型。

脚本生命周期建议为：

```text
发现 -> 读取元数据 -> 检查权限 -> 选择引擎 -> 构建/加载缓存
     -> 注册脚本 -> 用户执行 -> 创建执行上下文
     -> 执行 -> 返回结果/诊断 -> 释放上下文
```

构建失败的脚本可以显示在诊断列表中，但不得阻止其他脚本和核心程序启动。

## 6. 引擎契约

现有 `IScriptEngine` 需要逐步扩展为结构化的构建请求和构建结果。

建议模型：

```csharp
public sealed record ScriptBuildRequest(
    string SourcePath,
    string Source,
    ScriptExecutionPolicy Policy);

public sealed record ScriptDiagnostic(
    string Severity,
    string Message,
    string? FilePath = null,
    int? Line = null,
    int? Column = null);

public sealed record ScriptBuildResult(
    bool Success,
    IScript? Script,
    IReadOnlyList<ScriptDiagnostic> Diagnostics);
```

引擎应提供以下能力：

- `Name`：稳定的引擎标识，例如 `csharp`、`lua`、`python`。
- `Version`：用于缓存失效和诊断。
- `Match`：根据扩展名或脚本包声明判断是否支持。
- `Build`：编译或加载脚本并返回结构化诊断。
- `Cacheable`：说明是否支持编译结果缓存。

引擎不负责扫描目录、显示 UI 或保存用户权限。上述职责由宿主运行时承担。

## 7. 脚本管理器

当前 `IScriptManager` 统一提供构建注册和执行入口；目录发现与重新加载仍属于下一阶段：

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
}
```

当前管理器已经负责构建、注册、按 ID 查找和统一执行。目标职责还包括：

- 扫描脚本目录和脚本包。
- 根据路径或包声明选择引擎。
- 维护脚本 ID、显示名称、类型、状态和错误信息。
- 使用源码哈希和引擎版本管理缓存。
- 创建每次执行独立的上下文。
- 统一处理取消、超时、异常和执行结果。

脚本管理器还应校验上下文：

- 时间范围是否合法，且 `StartDate` 不晚于 `EndDate`。
- `Granularity` 与范围是否一致。
- 目标类型是否需要 `PluginId`、`InstanceId` 或 `TargetId`。
- 当前脚本是否允许访问目标 Tracker。

模板相关校验和应用不属于脚本管理器职责。

ViewModel 不应直接调用 `IScriptEngine` 或具体脚本类型。

## 8. 脚本元数据和目录

第一阶段可以使用源码文件旁的元数据文件，后续再支持脚本包：

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
    string DisplayName,
    string SourcePath,
    string Engine,
    ScriptUsage Usage,
    bool Enabled,
    ScriptCapability Capabilities);
```

脚本 ID 必须稳定且唯一。不能直接使用显示名称作为 ID。

后续脚本包可以包含：

```text
manifest.json
main.cs / main.lua / main.py
README.md
assets/
```

脚本包应校验路径，禁止通过 `../` 访问脚本目录外的文件。

## 9. 执行模型

脚本默认在后台线程执行，禁止直接阻塞 UI 线程。

每次执行应创建独立的 `ScriptExecutionContext`，包含：

- 脚本 ID和执行 ID。
- 取消令牌。
- 超时时间。
- 授权能力。
- 日志上下文。
- 当前用户操作来源。

建议结果模型：

```csharp
public enum ScriptExecutionStatus
{
    Succeeded,
    Failed,
    Cancelled,
    TimedOut,
    PermissionDenied,
}

public sealed record ScriptExecutionResult(
    ScriptExecutionStatus Status,
    TimeSpan Duration,
    IReadOnlyList<ScriptDiagnostic> Diagnostics);
```

宿主必须捕获脚本异常。脚本异常只能结束当前执行，不得让异常传播到应用主循环。

对于无法强制取消的引擎，超时后至少应停止等待、标记执行失败，并记录引擎不支持强制终止。
Python 等外部进程引擎应使用独立进程，以便在超时或崩溃时终止进程。

## 10. 权限模型

脚本是用户可执行代码，不能仅依赖 UI 隐藏操作实现安全控制。建议定义能力枚举：

```csharp
[Flags]
public enum ScriptCapability
{
    None = 0,
    ReadDiary = 1,
    WriteDiary = 2,
    ReadTracker = 4,
    WriteTracker = 8,
    Clipboard = 16,
    UiInteraction = 32,
    Network = 64,
    FileSystem = 128,
}
```

建议默认策略：

- 只读脚本默认允许 `ReadDiary`。
- 写入日记、写入 Tracker、剪贴板和 UI 交互需要用户授权。
- 网络和文件系统默认关闭。
- 权限拒绝必须返回结构化结果，不应静默失败。
- 日志、错误导出和诊断信息不得包含密码、API Key 或完整授权令牌。

C# 脚本尤其需要限制引用和宿主对象。不能将 `App`、数据库连接、服务容器或任意程序集实例直接注入脚本。

## 11. 脚本 API

现有 `IScriptApi` 可以作为第一阶段兼容入口，但长期建议按能力拆分：

```text
IScriptApi
  +-- IApplicationInfo
  +-- IDiaryQuery
  +-- IDiaryWriter
  +-- ITrackerAccess
  +-- IUiAccess
  +-- ITaskProgress
```

这样可以：

- 让权限检查与 API 区域对应。
- 降低单个接口的增长速度。
- 便于为脚本提供测试替身。
- 让脚本文档可以按能力生成。

### 11.1 Tracker API

当前 `GetTracker(string pluginId)` 只适合简单的单实例键值读取。由于 Tracker 已支持多实例，建议扩展为：

```csharp
ITrackerScriptApi? GetTracker(string pluginId, string instanceId);
```

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
权限策略
```

缓存失效条件包括：

- 源码发生变化。
- 引擎版本发生变化。
- 脚本契约版本发生变化。
- 权限策略发生变化。
- 脚本元数据发生变化。

缓存文件应通过临时文件写入后原子替换，避免程序异常留下损坏缓存。

脚本缓存不是信任边界。加载缓存前仍必须校验脚本 ID、引擎版本和权限策略。

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

UI 可以提供脚本列表、启用/禁用、重新加载、编译检查、最近执行状态和错误详情。

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

## 15. 测试计划

脚本系统至少需要以下测试：

- 引擎按扩展名匹配。
- 不支持的脚本不会被加载。
- 编译成功返回正确脚本类型。
- 编译失败返回结构化诊断。
- 脚本管理器发现、启用和禁用脚本。
- 源码变化后缓存失效。
- 引擎版本变化后缓存失效。
- 应用脚本和编辑器脚本分发到正确入口。
- 脚本异常不会传播到宿主。
- 取消和超时返回正确状态。
- 权限不足时 API 调用被拒绝。
- 日记读写 API 的成功和失败路径。
- Tracker 使用 `PluginId + InstanceId` 定位正确实例。
- 日志和诊断不会泄露敏感配置。
- Python worker 崩溃和退出码错误可以被宿主识别。

第一阶段测试不应依赖真实 Redmine、真实 Python 环境或 UI 桌面会话；外部引擎和宿主 API 使用内存替身。

## 16. 分阶段路线

### 第一阶段：最小可用运行时

- 实现脚本目录扫描。
- [已完成] 实现 `ScriptDescriptor`、诊断和执行结果。
- [已完成] 实现最小 `ScriptManager`、构建服务和执行服务。
- 实现 C# Roslyn 引擎。
- [已完成] 增加构建失败、执行异常、取消和超时测试。

### 第二阶段：应用集成

- 增加脚本设置页和脚本列表。
- 支持启用/禁用和手动重新加载。
- 支持应用脚本和编辑器脚本命令。
- 增加后台任务、取消和超时。

### 第三阶段：安全边界

- 增加权限能力模型。
- 增加用户授权和权限拒绝诊断。
- 限制 C# 引用和宿主对象。
- 审查所有 `IScriptApi` 写入和 UI 操作。

### 第四阶段：多语言

- 实现 Lua 引擎。
- 评估 Lua 缓存和调试支持。
- 实现独立 Python worker。
- 增加各引擎的集成测试。

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
