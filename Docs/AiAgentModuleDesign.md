# 可选 AI Agent 模块详细设计

## 1. 文档状态

- 状态：详细设计已实现并通过自动化、UI 与发布门禁
- 日期：2026-10-08
- 需求基线：[`AiAgentModuleRequirements.md`](AiAgentModuleRequirements.md)
- 评审记录：[`AiAgentModuleDesignReview.md`](AiAgentModuleDesignReview.md)
- 实施状态：阶段 A、P0、P1 和首版 MCP Client 已完成；受控浏览器与独立在线更新保留为后续边界

本文定义 DiaryApp 可选内置 AI Agent 的组件边界、生命周期、数据流、工具策略、配置、网络、UI、发布和测试方案。当前已经实现的 AI 脚本上下文和只读 MCP 继续以
[`AiScriptContextDesign.md`](AiScriptContextDesign.md) 为准；本设计不得把现有 `Diary.Mcp` 扩展为写入入口。

截至 2026-10-08，通用模块、三协议 Agent、连接/代理/凭据、只读 Diary 工具、受控网页、草稿与确认事项写入、会话/审计、stdio/Streamable HTTP MCP Client、AI 页面与设置页均已落地。模块状态损坏时保留原文件并全量禁用；`--core-only` 直接跳过模块发现。内部 DeepSeek/GLM 的真实连接探测仍需要部署方提供实际地址、模型名和凭据，不属于代码实现缺口。

## 2. 设计目标与约束

### 2.1 目标

1. AI 模块未安装或禁用时，核心 DiaryApp 不加载 AI 程序集、不创建网络客户端、不显示 AI 页面；
2. 通过协议适配层支持 OpenAI Chat Completions、OpenAI Responses 和 Anthropic Messages，并接入内部 DeepSeek、GLM 等兼容服务；
3. 使用统一工具注册表承载 DiaryApp 只读工具、外部网页工具和后续 MCP 工具；
4. 工作数据可用于查询和汇总，但工作项本地备注不进入 Agent 数据接口；
5. 支持连接级模型代理、网页代理、认证和加密凭据存储；
6. 保持现有脚本、Tracker、MCP 和更新链路可独立工作。

### 2.2 当前代码约束

当前架构对本设计有以下直接影响：

- `App.ConfigureServices()` 在应用构造阶段一次性构建 `ServiceProvider`，因此模块发现和 `ConfigureServices` 必须发生在 `BuildServiceProvider()` 前；
- `ITrackerPlugin` 带有 Tracker 实例、数据库迁移和绑定语义，不适合作为通用 AI 模块契约；
- `LoadPluginUiAssemblies()` 会扫描应用根目录的 `Diary.*.UI.dll`，AI UI 不得继续散落到根目录并依赖该扫描；
- `MainWindowViewModel.BuildFixedPages()` 目前只支持固定页面和 Tracker 动态页，需要增加通用导航贡献；
- `SettingsViewModel` 目前直接拼装 AI/MCP 设置，需要增加通用设置贡献；
- `ViewLocator` 按 ViewModel 所在程序集查找同名 View，因此 AI View 与 ViewModel 必须位于同一 UI 程序集，或由导航贡献显式提供 View；首版选择前者；
- `DiAutoRegister` 是程序集全量扫描式注册，外部模块必须显式注册服务，避免无意暴露内部类型；
- 当前 `EasySaveLoad` 已支持认证加密，可作为 P0 凭据持久化基础；系统密钥环集成作为后续增强；
- 当前发布会无条件构建并合并 `Diary.Mcp`，AI 完全可选化后需要调整发布装配；
- 当前 `ILogItemScriptApi` 支持预览和幂等，但只创建基础事项，不能完整保存标签、附加字段和 Tracker 编辑扩展，不能直接作为 P1 完整事项创建实现。

## 3. 总体架构

```text
Diary.App
  |- ModuleCatalog / ModuleHost
  |- NavigationContributionRegistry
  |- SettingsContributionRegistry
  |- Script Host APIs
  `- Core UI / Database / Tracker
           |
           | 仅在模块已安装且启用时加载
           v
Modules/diary.ai-agent/
  |- module.json
  |- Diary.Agent.dll
  |- Diary.Agent.UI.dll
  |- Diary.AiContext.dll
  `- private dependencies

Diary.Agent
  |- AgentSessionService
  |- AiModelClient
  |- OpenAiChatCompletionsAdapter
  |- OpenAiResponsesAdapter
  |- AnthropicMessagesAdapter
  |- AgentToolRegistry
  |- AgentToolPolicyService
  |- AiConnectionManager
  |- CredentialStore facade
  |- WebSearch/WebFetch
  `- Audit/Event stream

Diary.Agent.UI
  |- AiAgentModule entry
  |- AgentChatView/ViewModel
  |- AiSettingsView/ViewModel
  `- Navigation/Settings contributions
```

依赖方向：

```text
Diary.ModuleBase <- Diary.ModuleUI <- Diary.App
        ^                  ^
        |                  |
        +----- Diary.Agent.UI
                     |
                     v
               Diary.Agent
                     |
          +----------+----------+
          v                     v
   Diary.ScriptHost       Diary.AiContext
```

`Diary.App` 不引用 `Diary.Agent` 或模型协议实现；AI 模块可以引用稳定宿主契约，但不得引用 `Diary.App` 可执行项目。

## 4. 通用可选模块机制

### 4.1 项目拆分

新增稳定契约项目：

| 项目 | 职责 | 依赖边界 |
| --- | --- | --- |
| `Diary.ModuleBase` | manifest、生命周期、兼容性、诊断 | 不依赖 Avalonia、数据库或具体模块 |
| `Diary.ModuleUI` | 导航和设置贡献契约 | 依赖 `Diary.GUIBase` 和 `Diary.ModuleBase` |
| `Diary.Agent` | Agent Runtime、模型协议、工具、网络和配置 | 不依赖 Avalonia，不引用 `Diary.App` |
| `Diary.Agent.UI` | 模块入口、页面和设置 UI | View 与 ViewModel 同程序集 |

通用模块机制后续可供其他非 Tracker 功能使用，但首个消费者是 AI 模块。

### 4.2 模块目录与 manifest

模块位于安装目录：

```text
Modules/
  diary.ai-agent/
    module.json
    Diary.Agent.UI.dll
    Diary.Agent.dll
    Diary.AiContext.dll
    ...
```

`module.json` 示例：

```json
{
  "id": "diary.ai-agent",
  "displayName": "AI 助手",
  "version": "1.0.0",
  "apiVersion": 1,
  "entryAssembly": "Diary.Agent.UI.dll",
  "entryType": "Diary.Agent.UI.AiAgentModule",
  "enabledByDefault": false,
  "minAppVersion": "1.0.0",
  "maxAppVersion": null,
  "requiredCapabilities": [
    "module.navigation",
    "module.settings",
    "script.work_items.query"
  ]
}
```

Manifest 必须先以 JSON 读取和校验；未启用模块不得加载入口程序集。模块 ID、入口路径和所有文件路径必须拒绝绝对路径、`..`、符号链接逃逸和大小写冲突。

### 4.3 模块契约

```csharp
public interface IAppModule
{
    void ConfigureServices(
        IServiceCollection services,
        AppModuleRegistrationContext context);

    ValueTask StartAsync(
        AppModuleRuntimeContext context,
        CancellationToken cancellationToken = default);

    ValueTask StopAsync(
        CancellationToken cancellationToken = default);
}
```

约束：

- 入口类型必须是公开、非抽象、无参构造；
- `ConfigureServices` 发生在主容器构建前；
- `StartAsync` 在数据库、共享数据和主窗口基础服务就绪后执行；
- `StopAsync` 在应用退出、配置和日志服务仍可用时执行；
- 首版不支持进程内启停和热卸载，状态变更在重启后生效；
- 模块不得获得原始 `IServiceCollection` 之外的内部 App 对象，宿主能力通过稳定服务接口提供。

### 4.4 程序集加载

每个模块使用独立、不可回收的 `AssemblyLoadContext` 和 `AssemblyDependencyResolver`：

1. `Diary.ModuleBase`、`Diary.ModuleUI`、`Diary.Core`、`Diary.GUIBase`、`Diary.ScriptBase`、`Diary.ScriptHost`、Avalonia 和 Microsoft DI 等共享契约从默认加载上下文解析；
2. 模块私有依赖从模块目录解析；
3. 禁止从当前工作目录、PATH 或任意相邻目录探测程序集；
4. 同一模块只能有一个入口实例；
5. 模块加载异常记录为模块诊断，不阻止核心 App 启动。

首版不使用可回收 ALC，避免 UI、DI 单例和事件订阅导致无法卸载却给用户造成热卸载错觉。

### 4.5 启用状态

核心 App 保存非敏感的 `module-states.json`：

```json
{
  "schemaVersion": 1,
  "modules": {
    "diary.ai-agent": {
      "enabled": true
    }
  }
}
```

状态文件损坏时不覆盖原文件，所有非核心模块按禁用处理并展示诊断。Manifest 的 `enabledByDefault` 只在状态缺失时生效，AI 固定为 `false`。

宿主提供独立“模块设置”对话框。“模块管理”列出所有通过 manifest 校验的可选模块，允许修改启用状态并显示当前加载状态与启动诊断；保存采用同目录临时文件和原子替换，损坏文件保持只读。“模块配置”承载当前已加载模块提供的 `ISettingsContribution` 页面。模块启停不进行热加载或热卸载，统一在下次启动时生效。

`--core-only` 启动参数跳过全部可选应用模块和 Tracker/UI 扫描，用于验证核心功能不依赖 AI 或其他扩展；它不修改模块启用状态。

### 4.6 UI 贡献

`Diary.ModuleUI` 提供：

```csharp
public interface INavigationContribution
{
    string Id { get; }
    string Title { get; }
    string Icon { get; }
    int Order { get; }
    ViewModelBase CreateViewModel(IServiceProvider services);
}

public interface ISettingsContribution
{
    string Id { get; }
    string Title { get; }
    int Order { get; }
    ViewModelBase CreateViewModel(IServiceProvider services);
}
```

`MainWindowViewModel` 构建页面时合并固定页、通用模块页和 Tracker 页，再按最终位置分配快捷键。标题栏设置菜单分别提供“程序设置”和“模块设置”：前者只包含核心应用配置，后者管理可选模块启停、诊断和模块贡献设置页；核心 `SettingsViewModel` 不引用 AI 类型或模块设置贡献。

AI 的 View 与 ViewModel 均放在 `Diary.Agent.UI`，继续兼容现有 `ViewLocator` 的同程序集命名约定。

## 5. AI 模块内部组件

### 5.1 组件职责

| 组件 | 职责 |
| --- | --- |
| `AgentSessionService` | 会话、运行互斥、循环、取消和事件流 |
| `AiModelClient` | 选择协议适配器、发送请求、连接生命周期和错误归一化 |
| `IAiProtocolAdapter` | 统一请求、响应、流式事件和工具调用映射契约 |
| `OpenAiChatCompletionsAdapter` | Chat Completions messages、choices/delta 和 tool calls |
| `OpenAiResponsesAdapter` | Responses input/output Items、typed SSE 和 function call output |
| `AnthropicMessagesAdapter` | Messages content blocks、SSE events 和 tool use/result |
| `AiConnectionStore` | 连接配置、schema 迁移、原子保存 |
| `AiConnectionManager` | 客户端生命周期、连接指纹、能力缓存 |
| `IAiCredentialStore` | API Key、代理密码和认证 Header |
| `AgentToolRegistry` | 工具注册、命名、启用过滤和运行快照 |
| `AgentToolPolicyService` | 工具启用和写操作确认策略 |
| `AgentToolExecutor` | 参数绑定、校验、确认、超时和结果预算 |
| `WebFetchService` | 安全 URL 获取、正文提取和来源记录 |
| `IWebSearchProvider` | 可替换搜索实现；无 Provider 时不注册搜索工具 |
| `AgentAuditStore` | 脱敏运行摘要和副作用审计 |

### 5.2 生命周期

模块启动只加载配置和注册 UI，不自动探测模型、不启动 MCP 子进程、不访问网页。用户打开 AI 页面时创建会话服务；第一次选择连接或点击“测试”时才创建对应网络客户端。

模块停止顺序：

1. 取消所有 Agent run；
2. 停止接受新工具调用；
3. 等待有限时间完成审计持久化；
4. 释放模型、网页和 MCP 客户端；
5. 清除内存中的凭据和会话缓存。

## 6. 配置与凭据

### 6.1 非敏感配置

AI 配置位于 `ai-agent/settings.json`，使用版本化 envelope 和原子写入：

```json
{
  "schemaVersion": 1,
  "enabledTools": {
    "diary": true,
    "webSearch": false,
    "webFetch": false,
    "mcp": false
  },
  "defaultProfileId": "internal-glm",
  "profiles": []
}
```

配置读取必须区分 Missing、Loaded、Unreadable；Unreadable 时禁止覆盖，并允许导出脱敏诊断。迁移只允许逐版本、可测试的纯 JSON 转换。

### 6.2 连接配置

```csharp
public sealed record AiConnectionProfile
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required AiProtocol Protocol { get; init; }
    public required Uri BaseUri { get; init; }
    public string? RequestPathOverride { get; init; }
    public required string Model { get; init; }
    public AiAuthenticationConfiguration Authentication { get; init; } = new();
    public AiProxyConfiguration Proxy { get; init; } = new();
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public AiCompatibilityOptions Compatibility { get; init; } = new();
}
```

```csharp
public enum AiProtocol
{
    OpenAiChatCompletions,
    OpenAiResponses,
    AnthropicMessages
}
```

默认路径由协议适配器提供：Chat Completions 为 `chat/completions`，Responses 为 `responses`，Anthropic Messages 为 `messages`。内部网关路径不一致时使用 `RequestPathOverride`，但覆盖值只能改变路径，不能改变主机。

AI 模块启用并选中连接后，标题、日期、时长、标签、附加字段和 Tracker 摘要等普通工作数据可以按工具调用发送到该连接。本地备注始终由工具适配层排除，不通过连接配置改变。

校验规则：

- `BaseUri` 必须是绝对 HTTP/HTTPS URI，禁止 userinfo 和 fragment；
- 路径使用 URI 组合，不允许替换主机；
- Header 名和值校验 CR/LF，禁止用户覆盖 `Host`、`Content-Length` 和连接管理 Header；
- 配置 ID 使用稳定小写标识，只允许 ASCII 字母、数字、点、下划线和短横线；
- 模型名和显示名称有长度上限；
- 超时有最小和最大值。

### 6.3 凭据存储抽象

```csharp
public interface IAiCredentialStore
{
    ValueTask<bool> ExistsAsync(string reference, CancellationToken cancellationToken = default);
    ValueTask<CredentialValue?> GetAsync(string reference, CancellationToken cancellationToken = default);
    ValueTask SetAsync(string reference, ReadOnlyMemory<char> value, CancellationToken cancellationToken = default);
    ValueTask DeleteAsync(string reference, CancellationToken cancellationToken = default);
}
```

P0 提供三种后端：

1. 复用 DiaryApp 现有认证加密文件，作为默认持久化方式；
2. 环境变量，只读引用；
3. 内存，仅当前会话。

Credential Reference 使用 `diary.ai/{profileId}/{purpose}`。日志只记录连接 ID 和凭据是否存在，不记录正文。Windows 系统凭据库和 Linux Secret Service 可在出现明确部署需求后增加，不阻塞 P0。

## 7. 网络与代理

### 7.1 隔离原则

模型、网页搜索/获取和远程 MCP 分别创建客户端池，不能复用应用更新的全局 `HttpClient`。每个客户端池按以下指纹缓存：

```text
目标类别 + BaseUri + 代理模式 + 代理地址 + 绕过规则
+ 认证引用版本 + 连接超时 + TLS 策略
```

配置切换采用“创建新客户端 -> 原子切换 -> 等待旧租约归零 -> 释放旧客户端”，不在请求进行中修改 Handler。

### 7.2 代理模式

- `Inherit`：继承对应类别的模块默认值；
- `System`：使用系统代理；
- `Direct`：`UseProxy=false`；
- `Custom`：独立 `WebProxy`，用户名和密码分离保存。

不修改 `HttpClient.DefaultProxy`。代理 URL 禁止 userinfo。首版支持 HTTP 代理和 CONNECT，不支持 PAC、自定义 TLS 跳过和多跳代理。

### 7.3 HTTP handler

模型客户端使用长期复用的 `SocketsHttpHandler`，配置连接池生命周期、连接超时、自动解压、无 Cookie、有限重定向或禁用自动重定向。模型 API 默认禁用自动重定向，避免认证 Header 被转发到意外目标。

网页获取单独使用安全 Handler，见第 11 节。

## 8. 模型协议适配层

### 8.1 统一契约

协议客户端自行实现最小 HTTP/JSON/SSE 子集，不依赖厂商 SDK。Agent 循环只使用统一对象：

```csharp
public sealed record AgentModelRequest(
    string Model,
    string SystemInstruction,
    IReadOnlyList<AgentMessage> Messages,
    IReadOnlyList<AgentToolDefinition> Tools,
    bool Stream,
    int? MaxOutputTokens,
    AgentProtocolState? ProtocolState);

public interface IAiProtocolAdapter
{
    AiProtocol Protocol { get; }
    string DefaultRequestPath { get; }

    HttpRequestMessage CreateRequest(
        AgentModelRequest request,
        AiConnectionProfile connection,
        CredentialValue? credential);

    ValueTask<AgentModelResponse> ParseResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken);

    IAsyncEnumerable<AgentStreamEvent> ParseStreamAsync(
        Stream stream,
        CancellationToken cancellationToken);
}
```

`AgentProtocolState` 只保存完成当前会话工具闭环所需的协议状态，例如 Responses output Items。它是协议适配器拥有的不透明对象，Agent 工具层不得依赖其内部结构。

系统指令由 DiaryApp 生成，不允许连接配置覆盖。工具 schema 来自 run 开始时的不可变工具快照。兼容选项只能删除可选字段、调整已知 Header 或关闭能力，不能注入任意 JSON 请求片段。

### 8.2 OpenAI Chat Completions

`OpenAiChatCompletionsAdapter` 支持：

- system、user、assistant、tool messages；
- assistant `tool_calls` 和 tool `tool_call_id`；
- `tools`、`tool_choice=auto` 和串行工具执行；
- `stream=true/false`；
- `choices[].delta.content` 和工具 arguments delta；
- finish reason、可选 usage 和未知字段忽略。

适配器按 choice 和 tool-call index 合并增量，只有 finish reason 或完整流结束后才解析 arguments JSON。

### 8.3 OpenAI Responses

`OpenAiResponsesAdapter` 支持 P0 最小子集：

- `instructions`；
- `input` message Items；
- `output` 中的 message、function call 和必要的不透明中间 Items；
- Function Tool 和 `function_call_output`；
- 通过 `call_id` 关联调用和结果；
- `response.output_text.delta`；
- `response.function_call_arguments.delta/done`；
- `response.completed`、error 和 usage。

P0 固定 `store=false`，不使用 `previous_response_id`、Conversations、Background Mode 或 WebSocket。适配器在当前会话内保存模型返回的必要 output Items；工具执行后，将原 output Items 和新的 `function_call_output` 一并转换为下一次请求输入。连接关闭或新建会话时丢弃这些状态。

P0 不向 Responses 注册服务端托管的网页搜索、文件搜索、代码执行、Computer Use 或远程 MCP，只发送 DiaryApp `AgentToolRegistry` 中的自定义 Function Tool。

### 8.4 Anthropic Messages

`AnthropicMessagesAdapter` 支持：

- 顶层 system 指令和 user/assistant messages；
- text、tool use 和 tool result content blocks；
- `tools[].input_schema` 和 `tool_choice=auto`；
- `stream=true/false`；
- message/content-block SSE 事件；
- `input_json_delta` 工具参数拼接；
- stop reason、usage 和协议版本 Header。

认证 Header 和协议版本允许连接级已知字段覆盖，以适配内部兼容网关，但不开放任意请求体模板。

### 8.5 统一流式事件

三个适配器都输出统一事件：

```text
ResponseStarted
TextDelta
ToolCallStarted
ToolArgumentsDelta
ToolCallCompleted
UsageUpdated
ResponseCompleted
ProtocolError
```

所有 SSE 解析器必须：

1. 增量读取事件，不假设单次读取包含完整行或 JSON；
2. 按协议调用 ID 和输出索引合并文本、名称和 arguments；
3. 仅在工具参数完成后解析 JSON；
4. 对单事件、单工具参数和整次响应设置字节上限；
5. 流中断时不执行尚未完成的工具调用；
6. 将实时显示缓冲与协议完整结束后的持久化消息分离。

### 8.6 错误归一化

统一错误类别：配置、DNS、代理、TLS、认证、模型不存在、限流、服务端、超时、取消、协议格式、流中断、上下文超限。错误保留 HTTP 状态和脱敏 request ID，不保存响应正文；开发诊断只允许保存受长度限制且脱敏的错误摘要。

### 8.7 能力探测

用户显式测试连接时使用所选协议适配器依次执行：

1. 最小非流式对话；
2. 最小流式对话；
3. 无副作用单工具调用；
4. 对应协议的工具结果回传并获取最终回答；
5. 可选的流式工具调用；
6. 可选并行工具调用探测。

探测使用临时客户端，不影响当前会话。结果分别记录普通对话、流式文本、普通工具闭环、流式工具闭环、并行工具调用、强制工具选择和测试时间，不记录提示词或凭据。Agent 模式最低要求是普通对话和完整工具调用闭环；流式或扩展工具能力失败可以按已探测能力降级。不得在探测失败后自动改用另一协议重试。

## 9. Agent 会话与运行循环

### 9.1 会话状态

```text
Idle -> Running -> WaitingForApproval -> Running -> Completed
                 \-> Cancelling -> Cancelled
                 \-> Failed
```

同一会话同时只允许一个 run。新建 run 时捕获：连接 profile 版本、工具启用快照、预算和当前页面上下文。运行中修改配置只影响下一次 run。

### 9.2 循环

1. 构建最小消息上下文和允许的工具列表；
2. 调用模型并流式发布 UI 事件；
3. 无工具调用则完成；
4. 有工具调用则检查轮次、数量和工具 ID；
5. 参数绑定、校验和工具策略决策；
6. 只读工具执行，或产生等待确认的 pending action；
7. 将 assistant tool call 和对应 tool result 成对加入历史；
8. 进入下一轮；
9. 达到预算、取消或不可恢复错误时终止。

首版按模型返回顺序串行执行工具，即使服务声明并行工具调用也不并行执行，避免数据库、UI 和外部数据工具的竞态。后续只允许显式标记为并发安全的只读工具并行。

### 9.3 默认预算

| 项目 | 默认 | 硬上限 |
| --- | ---: | ---: |
| Agent 轮次 | 8 | 16 |
| 工具调用 | 12 | 32 |
| 单次工具输入 | 64 KiB | 256 KiB |
| 单次工具结果 | 128 KiB | 512 KiB |
| 单次事项数 | 50 | 100 |
| 模型请求时间 | 120 秒 | 600 秒 |
| 网页重定向 | 3 | 5 |

预算命中产生结构化终止结果，不自动重试。

### 9.4 会话持久化

P0 会话仅内存保存。P1 采用版本化 JSON，保存用户消息、最终回答、工具名、参数摘要、状态、连接 ID、模型名、耗时和 usage。隐藏推理、凭据、认证 Header 和服务端私有会话 ID 不落盘。

## 10. 统一工具系统

### 10.1 工具描述

```csharp
public sealed record AgentToolDescriptor(
    string Id,
    string ModelName,
    string DisplayName,
    string Description,
    JsonElement InputSchema,
    AgentToolOrigin Origin,
    AgentToolRisk Risk,
    string OwnerId);
```

`Id` 是内部稳定 ID；`ModelName` 只允许字母、数字和下划线，并由注册表唯一生成，例如：

```text
diary_query_work_items
web_fetch
mcp_company_knowledge_search
```

冲突、重复 Owner 或不合法 schema 使该工具注册失败，不影响其他工具。

### 10.2 工具接口

```csharp
public interface IAgentTool
{
    AgentToolDescriptor Descriptor { get; }

    ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default);
}
```

工具结果包含成功状态、结构化错误、文本或 JSON 内容、来源、是否来自外部内容、是否截断和效果摘要。模型只看到预算裁剪后的内容。凭据不属于工具结果类型，不能由工具返回给模型。

### 10.3 注册和快照

注册来源：

- `BuiltIn`：AI 模块内置的 DiaryApp 适配器；
- `Module`：显式依赖 AI 契约的可信模块；
- `ExternalWeb`：网页搜索/获取；
- `Mcp`：后续外部 MCP 工具。

每次 run 从注册表和工具策略服务生成不可变快照。运行中工具列表变化不影响当前模型历史。

### 10.4 参数校验

内置工具使用强类型 DTO 绑定和显式字段校验，schema 从同一元数据生成，避免 schema 与执行器漂移。外部 MCP schema 在 P2 引入标准 JSON Schema 验证器前不得自动执行；未知或不支持的 schema 关键字默认降级为每次确认或禁用。

### 10.5 策略

```text
Disabled
AlwaysConfirm
ConfirmWrites
AllowAutomatically
```

服务器或模块声明的只读、破坏性、幂等等信息只是输入；最终策略由本地 `AgentToolPolicyService` 决定。内置只读工具默认允许，外部工具由用户在设置中启用，写工具默认确认。

## 11. DiaryApp 工具适配

### 11.1 P0 只读工具

| 工具 | 宿主能力 | 默认状态 |
| --- | --- | --- |
| `diary_list_tags` | `DbShareData` 的只读适配器 | 启用 |
| `diary_list_templates` | `ITemplateScriptApi` | 启用 |
| `diary_list_tracker_instances` | `ITrackerInstanceScriptApi` | 启用 |
| `diary_query_work_items` | `IWorkItemQueryScriptApi` | 启用 |
| `diary_summarize_work_items` | 查询结果的本地汇总 | 启用 |
| `diary_get_current_context` | UI 当前日期/选择的只读快照 | 启用 |
| `diary_validate_script` | 现有校验服务适配器 | 启用 |

模块不得持有 `DbInterfaceBase`。宿主在 DI 中注册稳定 Script Host API 或只读 facade。Agent 使用 `WorkItemQueryScriptApi` 的显式无备注模式，该模式不会调用 `WorkGetNote` 或 `GetWorkNotesByWorkItemIds`；工具 DTO 同样不定义本地备注字段。普通脚本 API 保持原有可读取备注的行为，其他普通工作字段可以按查询结果返回。

### 11.2 P1 草稿和写入

草稿在 AI 模块内生成结构化 DTO，不访问数据库。真正创建事项不得直接调用现有简化 `ILogItemScriptApi` 后宣称支持标签、附加字段或 Tracker 扩展。

P1 新增稳定 `IWorkItemCommandApi`，由 App 实现并复用 `IWorkItemPersistenceCoordinator`：

```csharp
public interface IWorkItemCommandApi
{
    ValueTask<WorkItemCommandPreview> PreviewCreateAsync(
        WorkItemCreateCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<WorkItemCommandResult> CreateAsync(
        WorkItemCreateCommand command,
        CancellationToken cancellationToken = default);
}
```

首个版本支持日期、标题、工时、优先级、标签、备注和附加字段；Tracker 编辑扩展写入继续禁用，除非各扩展提供明确可预览、可校验的命令契约。命令包含幂等键和预览版本，确认执行时校验数据库状态、标签/字段版本和预览摘要，避免过期确认。

在同一“确认后写入工具”能力组下，内置 Agent 还注册以下程序工具：

| 工具 | 宿主能力 | 确认与执行约束 |
| --- | --- | --- |
| `diary_create_from_template` | `ITemplateLogItemScriptApi`、`ITemplateScriptApi` | 确认前展示模板名称、默认标签和预览，确认后重新预览，再使用幂等键创建 |
| `diary_set_clipboard_text` | `IClipboardScriptApi` | 确认卡展示完整参数，确认后写入，限制为 20000 字符 |
| `diary_notify` | `IUserInteractionScriptApi` | 确认卡展示标题和正文，确认后发送会话级应用通知 |

这些工具复用写工具串行确认协调器：同一时刻只允许一个待确认操作，拒绝、取消或模块停止都不会执行宿主副作用。内置程序写工具与 MCP 写工具共用通用确认卡，但来源和工具名必须明确展示。工具仍受每次 run 的不可变快照、调用预算、参数校验和审计效果摘要约束。该扩展只增加 Agent 适配器和宿主 API 注册，不修改工作项核心数据结构。

## 12. 外部网页数据

### 12.1 `web_search`

只有注册了 `IWebSearchProvider` 且用户在设置中启用时才公开工具。Provider 接收搜索词和结果上限，返回标题、URL、摘要和提供方。P1 不内置特定公网搜索供应商；优先接公司搜索服务或自建网关。搜索词可以来自当前问题或普通工作数据，但不得包含工作项本地备注，因为该字段不会进入 Agent 上下文。

### 12.2 `web_fetch`

首版只支持 GET/HEAD、静态 HTML、纯文本和受限 JSON：

1. URI 必须是绝对 HTTP/HTTPS，无 userinfo；
2. 检查 scheme、host、port 和站点策略；
3. 解析全部 A/AAAA 地址并检查 IP 范围；
4. 直连时在 `ConnectCallback` 内再次解析、校验并连接已批准地址，避免检查后 DNS 重绑定；
5. 禁用自动重定向，每一跳重新执行完整校验；
6. 跨主机重定向删除 Authorization、Cookie 和敏感 Header；
7. `ResponseHeadersRead` 后按压缩前后字节预算读取；
8. HTML 解析移除脚本、样式、表单、隐藏内容并提取正文；
9. 返回 requested URL、final URL、title、content type、时间、截断和外部内容标记。

公网策略拒绝 loopback、unspecified、link-local、private、multicast、保留地址和已知云元数据目标。内部模式只允许显式 host/域后缀/端口白名单，不默认允许整个私网。

### 12.3 代理下的地址校验限制

自定义或系统代理可能由代理端解析目标域名，客户端无法像直连一样固定目标 IP。P1 保留以下基础约束：

- 只允许 HTTP/HTTPS；
- 公网模式拒绝明确解析到 localhost、链路本地、私网和元数据地址的目标；
- 每次重定向重新校验 URL，并移除跨主机认证 Header；
- 内部地址只能在用户显式配置内部站点后访问。

不要求 DiaryApp 验证公司代理的完整出口策略，也不把代理侧 DNS 行为作为 P1 发布阻塞项。

### 12.4 HTML 与内容预算

首版正文提取使用成熟 HTML parser，不使用正则解析 HTML。默认预算：响应头 64 KiB、压缩体 2 MiB、解压体 8 MiB、提取文本 100,000 字符、3 次重定向、30 秒获取超时。Content-Type 不支持或预算超限时返回结构化错误，不下载到磁盘。

网页结果标记为外部内容。系统提示明确禁止把页面中的工具调用、权限修改或系统指令直接当作 Agent 指令执行。

## 13. MCP Client 扩展

MCP Client 属于 P2，不是 P0/P1 网页访问的前置条件。实现时支持：

- 本地 stdio；
- 远程 Streamable HTTP；
- `tools/list`、`tools/call` 和工具列表变化通知；
- 服务器级代理、认证、超时和 Secret Reference；
- 工具名命名空间和本地策略；
- 文本和有预算的结构化结果。

首版 MCP Client 不开放 Resources、Prompts、Sampling、Elicitation、Roots、Tasks 或 MCP Apps。外部工具注解不作为自动授权依据。内置 Agent 不通过 `Diary.Mcp` 查询 DiaryApp，自身数据访问继续走进程内稳定宿主 API。

## 14. UI 设计

### 14.1 导航

AI 模块贡献“AI 助手”导航页。模块未加载时没有导航项。页面顶部显示当前连接、Agent/普通对话模式、外部工具状态和新建会话。

### 14.2 会话页面

布局包含：

- 消息列表和流式文本；
- 输入框、发送、停止；
- 工具调用卡片：来源、参数摘要、状态、耗时和结果摘要；
- 写入确认卡片：实际字段、预览版本、确认/编辑/拒绝；
- 错误恢复：重试从安全边界重新发请求，不重复执行已完成副作用。

### 14.3 设置

AI 设置页分为：

1. 模块和工具启用；
2. 模型连接；
3. 网络和代理；
4. 外部网页；
5. 凭据状态；
6. 诊断与连接测试。

编辑连接使用 working copy。测试连接不提交配置；保存前校验，保存成功后替换运行时客户端。密钥字段只显示“已保存/未保存/来自环境变量”，不回显正文。

### 14.4 模块管理

核心模块诊断页显示已发现、启用、兼容、阻塞和加载失败状态。启停操作提示重启生效。首版不在 App 内下载任意第三方模块。

## 15. 日志与操作记录

### 15.1 运行记录

每个 run 分配 `RunId`，工具调用分配 `InvocationId`。审计记录：

- 会话、run、连接和模型标识；
- 开始/结束时间、耗时、轮次、工具数量；
- 工具 ID、来源、决策、结果类型和错误码；
- 写操作的预览摘要、幂等键和效果摘要；
- usage（服务提供时）。

### 15.2 日志边界

- API Key、代理密码和认证 Header；
- 工作项本地备注；
- 模型隐藏推理；
- 数据库连接字符串和 Tracker Token。

普通运行日志默认记录请求标识、连接、模型、工具名、耗时、大小和错误，不记录完整请求/响应。开发诊断如需保存模型报文，必须由用户显式开启，并继续排除凭据和本地备注。诊断导出只包含配置结构、连接状态、能力标记、错误类别和操作摘要。

## 16. 发布、安装和更新

### 16.1 P0 随主程序发布

P0 构建可以生成独立模块目录，但由完整 DiaryApp 安装包一起发布：

```text
Modules/diary.ai-agent/
  module.json
  Diary.Agent.dll
  Diary.Agent.UI.dll
  dependencies...
```

AI 模块随程序安装但默认禁用。用户启用后重启生效；未启用时不加载入口程序集。构建属性允许生成不包含 AI 模块的核心包，用于验证模块确实可选。`Diary.Mcp` 是否随核心包发布与 AI 模块分离处理。

### 16.2 后续独立模块包

如果后续提供独立下载安装，模块包应包含 manifest、文件清单、SHA-256、RID、模块版本和兼容 App 版本。安装过程：

1. 解压到同文件系统临时目录；
2. 校验路径、大小、哈希、RID 和兼容性；
3. 拒绝符号链接和重复/大小写冲突路径；
4. 原子替换目标模块目录；
5. 保留上一版本供回滚；
6. 标记重启后启用。

包内文件哈希只能验证包内一致性，不能证明发布者身份。独立安装还必须满足以下任一可信来源条件：

- 模块 manifest/文件清单带有由应用内置公钥验证的数字签名；
- 模块摘要由已认证、受信任的更新清单提供，并在下载后进行匹配。

用户手工选择的未知来源包即使内部哈希正确也不得静默安装。签名格式、密钥轮换和撤销策略需要独立 ADR。P0 不提供独立安装入口或第三方模块市场，因此这些要求不阻塞 P0。

### 16.3 更新协调

当前更新器面向整个安装树，P0 由完整 App 更新统一替换官方 AI 模块。以后增加独立模块更新时，需要扩展事务计划并明确：

- 完整 App 更新是否保留、升级或回滚模块；
- 模块文件被占用时的退出提示和回滚；
- App 降级时模块兼容性处理；
- 模块缺失不阻断核心更新；
- 更新 manifest 对模块目录的所有权。

这些问题是独立模块发布的门禁，不是随主程序发布的 P0 门禁。

## 17. 测试设计

### 17.1 单元测试

- manifest、路径、版本和依赖校验；
- 未启用模块不加载程序集；
- 工具命名、冲突、启用状态和不可变快照；
- 三种协议的 DTO、SSE 事件、工具参数拼接和错误归一化；
- Responses output Items、`call_id`、`function_call_output` 和 `store=false` 会话状态；
- Anthropic content blocks、tool use/result 和 `input_json_delta`；
- Agent 轮次、取消、等待确认、预算和历史配对；
- 配置迁移、损坏保护和 Credential Reference；
- 代理指纹和客户端替换；
- URL/IP/重定向/跨域认证校验；
- HTML 正文和大小预算；
- 凭据遮罩和本地备注排除。

### 17.2 协议模拟测试

使用本地测试服务器覆盖：

- Chat Completions、Responses、Anthropic Messages 的模拟服务；
- DeepSeek/GLM 网关的兼容差异；
- 非流式、流式、拆分 arguments、缺失 usage、额外字段；
- 401、404、407、429、5xx、超时和中断；
- 工具调用后拒绝 tool result；
- 重复 tool call ID、未知工具、畸形 JSON；
- 代理缓冲 SSE 和断开长连接。

### 17.3 边界测试

- localhost、私网目标、非 HTTP(S) 协议和越权重定向；
- 跨域认证 Header 泄漏；
- 超大 HTML、错误 Content-Type 和获取预算；
- 网页内容不能直接提升工具权限；
- 本地备注不进入 DTO、模型请求、会话历史或日志；
- 配置、日志、崩溃报告和诊断包凭据扫描；
- 模块路径逃逸和损坏包。

### 17.4 集成与 UI 测试

- 无模块、禁用、损坏模块和不兼容模块仍可启动核心 App；
- 两个模型连接分别使用直连和代理；
- 普通对话降级和 Agent 工具闭环；
- 工具调用和写入确认的 UI 状态；
- App 退出时取消 run 并释放客户端；
- Windows/Linux 发布包和模块启停。

## 18. 实施阶段

### 阶段 A：通用模块基础

- `Diary.ModuleBase`/`Diary.ModuleUI`；
- manifest-first 发现、兼容性、启用状态和诊断；
- 导航/设置贡献；
- 模块目录发布门禁。

### 阶段 B：P0 只读 Agent

- AI 配置和凭据存储；
- 三种协议适配器、统一模型客户端和能力探测；
- Agent 循环、工具注册表和只读 DiaryApp 工具；
- AI 会话和连接设置 UI；
- 无模块/禁用/降级/代理门禁。

### 阶段 C：P1 外部数据

- `web_fetch` 安全获取；
- `IWebSearchProvider`；
- 内部站点配置、代理和认证；
- 网页地址与内容边界测试。

### 阶段 D：P1 草稿与受控写入

- 结构化草稿；
- `IWorkItemCommandApi`；
- 预览版本、确认和幂等；
- 写操作审计。

### 阶段 E：P2 扩展

- MCP Client：已完成 stdio、Streamable HTTP、Session ID、SSE 通知、工具列表原子替换和写工具确认；
- 受控浏览器和复杂内容；
- 独立模块在线更新；
- 更多协议和多模态。

## 19. 关键设计决策摘要

| 决策 | 选择 |
| --- | --- |
| 模块类型 | 新增通用模块契约，不复用 Tracker 插件 |
| 启停 | 首版重启生效，不热卸载 |
| 模型协议 | Chat Completions、Responses、Anthropic Messages 最小子集 |
| SDK | 自有最小 HTTP/JSON/SSE 客户端 |
| Agent 工具 | 统一注册表和运行快照 |
| P0 数据访问 | 进程内 Script Host 只读 API，不通过 `Diary.Mcp` |
| 外部网页 | 受控 `web_search`/`web_fetch`，不是任意 HTTP |
| MCP | P2 外部工具扩展，现有 Server 保持只读 |
| 凭据 | 复用现有认证加密，支持环境变量和会话内存 |
| 模型数据边界 | 普通工作数据可用；工作项本地备注在工具适配层排除 |
| 写入 | P1 新增稳定命令网关，不直接使用不完整简化 API |
| 发布 | P0 随主程序安装但默认禁用；独立下载和更新后续实现 |

## 20. 实施与验证结果

- `Diary.AgentTests` 58/58，通过三协议、完整能力探测、模型/代理、网页安全、事项确认、程序写工具确认、MCP、会话与审计测试；另由 `Diary.ScriptTests` 验证 Agent 无备注查询模式不调用备注读取器；
- `Diary.ModuleTests` 12/12，通过 Debug/Release 模块目录、私有 `AssemblyLoadContext`、禁用和故障隔离测试；
- `Diary.AppTests` 318/318；`Diary.DbTests` 150 通过，111 项 PostgreSQL/Docker 或 Linux 专属用例按当前环境跳过；
- AI CDP 套件 7/7，通过导航、Agent 状态、真实本地假模型工具闭环、拒绝写入、键盘发送、设置贡献和递归 seed；
- `win-x64`、`linux-x64` 自包含发布成功，模块目录只包含 `module.json`、`Diary.Agent.UI.dll`、`Diary.Agent.UI.deps.json`、`Diary.Agent.dll`、`AngleSharp.dll`，宿主根目录无 AI 私有程序集；
- Windows 发布包在默认禁用、`--core-only` 和模块目录缺失三种形态下均可持续启动且不加载 AI 私有能力；
- 真实内部 DeepSeek/GLM 探测必须在取得实际连接参数后执行，当前协议兼容性由本地模拟服务覆盖。
