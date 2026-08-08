# 脚本 Worker 契约设计

## 1. 文档范围

本文定义 DiaryApp 脚本 worker 的进程边界、生命周期、消息协议、宿主 API 转发、
取消和超时语义。本文适用于 C#、Lua 和 Python worker；语言实现不应把自己的对象模型
暴露给 `Diary.App` 或 `Diary.Core`。

本文同时记录目标设计和当前实现。当前已实现协议握手、版本/能力协商、UTF-8 JSON 行编解码、
可注入传输层、本机进程 transport、C#/Lua/Python Worker 执行链路、按 EngineName 隔离的 supervisor、
双向宿主 API 转发、通道终止结构化失败以及执行消息和宿主调用次数限制。
操作系统级强内存限制按平台能力处理，多语言 worker 仍在后续阶段。Worker 协议 stdout 已与脚本标准输出隔离，并限制脚本输出大小。现有脚本 V1 类型和执行结果见
[`ScriptSystemDesign.md`](ScriptSystemDesign.md) 及 `Diary.ScriptBase`。

## 2. 设计结论

- worker 是常驻进程，但单次脚本执行必须有明确开始和结束。
- 同一种语言的普通脚本可以共享 worker；高风险脚本可以使用独立 worker。
- 主程序是 worker 的 supervisor 和宿主 API 服务端，worker 不能直接访问核心数据库、DI、UI 或 Tracker 客户端。
- 主程序和 worker 之间使用带长度限制的 UTF-8 JSON 消息；每条消息一行，协议数据只写入 stdout，日志只写入 stderr。
- 协议支持双向消息。主程序等待脚本结果期间，worker 可以发送宿主 API 请求。
- 超时首先发送取消消息；宽限期结束后由 supervisor 终止 worker 进程。
- worker 崩溃只影响该 worker 中的请求，不应影响主程序；未完成请求统一标记为 worker 崩溃。
- 所有远程写入操作必须使用幂等键和显式确认，第一版只开放只读宿主 API。

## 3. 目标和非目标

### 3.1 目标

- 将脚本运行时故障与主程序进程隔离。
- 为 C#、Lua、Python 提供相同的执行和宿主 API 边界。
- 支持一个常驻 worker 顺序执行多个脚本请求。
- 支持执行取消、超时、worker 重启和协议错误恢复。
- 限制消息大小、并发请求数、宿主调用数和资源使用。
- 使执行诊断可以关联脚本 ID、worker ID、请求 ID 和执行 ID。

### 3.2 非目标

- 第一版不支持脚本之间直接通信。
- 第一版不支持脚本常驻监听器或无限生命周期脚本。
- 第一版不传递 .NET 对象、数据库连接、服务容器或 Avalonia 控件。
- 第一版不把 worker 协议设计成远程执行协议；传输端点只绑定本机。
- 第一版不保证脚本代码本身没有逻辑错误，只保证 worker 故障不会直接终止主程序。

## 4. 术语和边界

| 名称 | 含义 |
| --- | --- |
| supervisor | 主程序中的 worker 生命周期管理器，负责启动、健康检查、终止和重启 |
| worker | 独立语言进程，负责加载脚本并执行，不拥有宿主数据权限 |
| workerId | supervisor 为一次 worker 进程实例分配的随机 ID；重启后变化 |
| requestId | 一次协议请求的 ID，用于匹配请求和响应 |
| executionId | 一次用户可见脚本执行的稳定 ID，贯穿日志、诊断和历史 |
| host call | worker 请求主程序执行宿主 API 的消息 |
| capability | 当前执行被授予的能力集合，不等同于脚本声明的能力 |

进程关系如下：

```text
ScriptManager
      |
      v
WorkerSupervisor
      |
      +-- WorkerProcess(csharp, workerId)
      |       +-- script request A
      |       +-- script request B
      |
      +-- WorkerProcess(lua, workerId)
      +-- WorkerProcess(python, workerId)
      |
      v
HostApiDispatcher -> Diary.Database / Tracker Plugin / App services
```

worker 不应持有 `DbInterfaceBase`、`IServiceProvider`、`App` 或具体 Tracker SDK 的引用。
宿主 API 只返回协议 DTO，并在每次调用时重新检查能力和 `executionId`。

## 5. Worker 生命周期

### 5.1 状态

```text
Stopped -> Starting -> Handshaking -> Ready
                         |              |
                         v              v
                      Failed       Busy <-> Ready
                                        |
                                        v
                                  Draining -> Stopped
```

- `Stopped`：没有子进程。
- `Starting`：已创建进程，等待启动完成。
- `Handshaking`：等待 `hello`，检查协议、语言和能力。
- `Ready`：可以接受执行请求。
- `Busy`：至少有一个执行请求；第一版默认同一 worker 串行执行。
- `Draining`：拒绝新请求，等待当前请求结束或终止进程。
- `Failed`：启动、握手或协议失败，等待 supervisor 重启。

### 5.2 启动

supervisor 启动 worker 时必须：

1. 使用绝对可执行文件路径和受控工作目录。
2. 单独配置 stdin、stdout、stderr，并禁止继承不必要的句柄。
3. 不通过命令行传递 Token、密码或完整配置内容。
4. 使用环境变量白名单；不把主进程全部环境变量复制给 worker。
5. 设置启动超时，默认建议为 10 秒。
6. 读取第一条 `hello` 消息并完成协议协商后，才把状态设置为 `Ready`。

### 5.3 常驻和回收

常驻 worker 只表示进程常驻，脚本执行仍然是请求级生命周期。每个请求完成后，worker
必须释放该请求的上下文、取消注册的回调和临时资源。

supervisor 应支持：

- 空闲回收，例如连续空闲 10 分钟后退出。
- 最大请求数回收，防止解释器或运行库长期积累状态。
- 内存上限和工作集监控。
- 协议无响应、stderr 持续异常或心跳超时时的强制终止。
- worker 退出后的指数退避重启，避免启动失败时忙循环。

第一版同一语言 worker 默认串行执行。后续若需要并发，应启动多个 worker 实例，不能
在同一解释器上下文中默认并发执行多个脚本。

## 6. 传输和消息封装

### 6.1 传输方式

第一版使用本机子进程管道：

```text
主程序 stdin  -> worker stdin
主程序 stdout <- worker stdout
主程序 stderr <- worker stderr（日志，不进入协议）
```

每条消息是一个 UTF-8 JSON 对象加一个 `\n`。消息内容不得包含未转义的换行。读取方
必须使用最大行长度限制，不能无限等待或无限增长缓冲区。

默认限制建议：

| 项目 | 默认值 |
| --- | ---: |
| 单条消息最大大小 | 4 MiB |
| 单次脚本结果最大大小 | 16 MiB |
| 单次宿主调用最大数量 | 100 |
| 同一 worker 最大并发请求 | 1 |
| 启动/握手超时 | 10 秒 |
| 宿主调用响应超时 | 30 秒 |
| 取消宽限期 | 2 秒 |

超过限制时，supervisor 必须拒绝或终止当前请求，并返回结构化诊断。

### 6.2 通用封装

所有消息使用以下封装：

```json
{
  "protocol": "diary.script.worker",
  "version": 1,
  "type": "execute",
  "requestId": "req-01J...",
  "executionId": "exec-01J...",
  "payload": {}
}
```

字段要求：

- `protocol` 必须精确匹配协议名称。
- `version` 是协议主版本；不兼容时握手失败。
- `type` 是固定消息类型，未知类型必须返回错误或终止连接。
- `requestId` 对请求和响应必填；通知消息可省略。
- `executionId` 对脚本执行和宿主调用必填，握手消息除外。
- `payload` 必须是对象；禁止把任意二进制或对象图直接嵌入消息。

ID 应使用不可预测的随机值或 UUID。日志中可以显示 ID，但不能将凭据放入 ID。

## 7. 握手协议

worker 启动后必须先发送 `hello`：

```json
{
  "protocol": "diary.script.worker",
  "version": 1,
  "type": "hello",
  "requestId": "hello-1",
  "payload": {
    "language": "python",
    "workerVersion": "1.0.0",
    "supportedApiVersions": [1],
    "supportedHostApis": ["workItems.query"],
    "pid": 12345
  }
}
```

主程序返回 `hello.accepted`：

```json
{
  "protocol": "diary.script.worker",
  "version": 1,
  "type": "hello.accepted",
  "requestId": "hello-1",
  "payload": {
    "apiVersion": 1,
    "maxMessageBytes": 4194304,
    "heartbeatSeconds": 30
  }
}
```

握手必须验证：

- 协议名称和主版本。
- worker 声明的语言是否与启动配置一致。
- 至少一个双方支持的 Script API 版本。
- worker 版本和入口信息是否满足宿主策略。
- worker 是否在本次 supervisor 创建的进程中。

握手失败时，主程序不得发送脚本源码或宿主数据，应终止 worker 并返回
`WORKER_HANDSHAKE_FAILED`。

## 8. 执行协议

### 8.1 执行请求

```json
{
  "protocol": "diary.script.worker",
  "version": 1,
  "type": "execute",
  "requestId": "req-01J...",
  "executionId": "exec-01J...",
  "payload": {
    "scriptId": "daily-summary",
    "sourcePath": "scripts/application/daily-summary.py",
    "target": null,
    "arguments": {
      "date": "2026-08-06"
    },
    "grantedCapabilities": ["workItems.query", "log.write"],
    "deadline": "2026-08-06T08:00:30Z"
  }
}
```

worker 应先返回 `execute.accepted` 或 `execute.rejected`，再返回最终的 `execute.result`。
这样 supervisor 可以区分“请求未被接收”和“脚本已经开始执行”。

### 8.2 执行结果

```json
{
  "protocol": "diary.script.worker",
  "version": 1,
  "type": "execute.result",
  "requestId": "req-01J...",
  "executionId": "exec-01J...",
  "payload": {
    "status": "Succeeded",
    "diagnostics": [],
    "value": null,
    "durationMilliseconds": 184
  }
}
```

`status` 映射现有 `ScriptExecutionStatus`：`Succeeded`、`Failed`、`Cancelled`、
`Rejected` 和 `TimedOut`。执行结果中的 `value` 必须是 JSON 标量、数组或对象，且不能
包含宿主对象、句柄或延迟执行对象。

## 9. 双向宿主 API

worker 执行脚本时可以发送 `host.call`：

```json
{
  "protocol": "diary.script.worker",
  "version": 1,
  "type": "host.call",
  "requestId": "host-01J...",
  "executionId": "exec-01J...",
  "payload": {
    "method": "workItems.query",
    "params": {
      "startDate": "2026-08-01",
      "endDate": "2026-08-06",
      "tagFilter": "Any",
      "limit": 100
    }
  }
}
```

主程序返回 `host.result`：

```json
{
  "protocol": "diary.script.worker",
  "version": 1,
  "type": "host.result",
  "requestId": "host-01J...",
  "executionId": "exec-01J...",
  "payload": {
    "success": true,
    "result": {
      "items": []
    },
    "error": null
  }
}
```

主程序处理每个 `host.call` 时必须重新检查：

- `executionId` 是否仍然有效。
- 脚本是否仍在运行。
- `method` 是否在协商后的 API 白名单中。
- 当前执行是否拥有所需 capability。
- 参数大小、数量、日期范围和分页上限。
- 结果是否需要脱敏。

第一版支持 `workItems.query`、日志项创建、模板日志项创建、Tracker 实例目录、剪贴板、
用户交互和 `log.write`。工作项更新、删除和 Tracker 远程写入不进入第一版协议。

应用程序扩展的 `target` 必须为 `null`。编辑器扩展的 `target` 是结构化对象，`kind` 为
`Year`、`Quarter`、`Month`、`Day` 或 `WorkItem`。年、季度、月、日目标由宿主解析为包含
边界的日期范围；事项目标携带不可变的 `ScriptWorkItem` 快照。宿主在每次执行和每个
HostCall 前校验目标，不把核心数据库实体传入 Worker。

脚本可以通过语言上下文读取 `dateRange`、`workItem` 和 `items.stream()`。范围流只能用于
有日期范围的目标，事项目标应直接处理 `workItem`。

日志使用 `log.write` HostCall：

```json
{
  "method": "log.write",
  "params": {
    "level": "Info",
    "message": "脚本已读取当前目标"
  }
}
```

宿主为日志补充脚本 ID 和 executionId，并限制单条消息大小；日志不得包含密码、Token 或
其他敏感配置。

宿主 API 错误使用稳定错误码，例如：

```text
PermissionDenied
InvalidInput
DatabaseUnavailable
ProviderFailure
Cancelled
RateLimited
RemoteFailure
```

错误消息不得包含 API Key、Token、密码、数据库连接字符串或完整远程响应中的敏感字段。

## 10. 取消、超时和终止

取消分为三个层次：

1. 主程序发送 `cancel`，worker 将取消令牌传给脚本。
2. worker 在取消宽限期内返回 `Cancelled`。
3. worker 未响应时，supervisor 先关闭 stdin，再终止进程；必要时强制杀进程。

取消消息示例：

```json
{
  "protocol": "diary.script.worker",
  "version": 1,
  "type": "cancel",
  "requestId": "cancel-01J...",
  "executionId": "exec-01J...",
  "payload": {
    "reason": "Timeout",
    "deadline": "2026-08-06T08:00:32Z"
  }
}
```

`TimedOut` 只能由 supervisor 在 deadline 到期或 worker 被终止后产生；脚本不能自行把
普通失败伪装成超时。进程被终止时，所有未完成请求都标记为 `Failed`，错误码为
`WORKER_TERMINATED`，若明确超过 deadline 则使用 `SCRIPT_EXECUTION_TIMED_OUT`。

主程序不能因为 worker 不响应而同步等待。读写管道、宿主调用和进程退出监听都必须是
异步的，并使用独立取消令牌。

## 11. Worker 故障和重启

以下情况视为 worker 故障：

- 非零退出码或未预期退出。
- stdout 出现无法解析的消息。
- 消息缺少必要字段、超过大小限制或 requestId 不匹配。
- 心跳连续两次超时。
- worker 在握手后发送未知协议版本。
- worker 长时间占满资源并超过 supervisor 限制。

故障处理顺序：

1. 标记 worker 为 `Failed`，拒绝新请求。
2. 记录 workerId、进程退出码、stderr 摘要和受影响的 executionId。
3. 将未完成请求转换为结构化失败结果。
4. 关闭并释放管道和进程句柄。
5. 按指数退避重启，建议间隔为 1、2、5、10、30 秒，达到上限后暂停自动重启。
6. 新 worker 必须重新握手，不能复用旧 worker 的上下文或 capability。

worker 重启不应自动重试有副作用的操作。只读查询可以由策略决定是否重试；远程写入
必须要求幂等键或用户重新确认。

## 12. 心跳和健康检查

主程序每 30 秒发送 `ping`，worker 返回 `pong`。消息只表示进程和协议通道存活，不能
表示当前脚本一定可取消或宿主 API 一定可用。

worker 在执行脚本期间仍必须响应协议层的 `cancel` 和宿主响应。若语言运行时阻塞了
整个事件循环，supervisor 应将其视为不可控 worker，并在宽限期后终止进程。

## 13. 脚本状态和共享 Worker

默认语义是“请求之间不保证共享状态”。worker 实现可以复用解释器，但必须做到：

- 不暴露其他脚本的模块、变量或对象。
- 每次执行创建新的脚本上下文。
- 执行结束后清理宿主 API 代理和取消回调。
- 限制脚本注册的全局变量和模块缓存，或在达到请求数上限后重启 worker。
- 脚本执行结果不能携带下一次执行可调用的对象引用。

如果未来需要持久化状态，应增加显式的 `state.load`/`state.save` 宿主 API，并将状态
按脚本 ID 隔离、限制大小、版本化和持久化；不能依赖 worker 进程内全局变量作为持久化机制。

## 14. 能力和数据安全

脚本元数据中的 capability 是请求能力，不是授权结果。真正传给 worker 的
`grantedCapabilities` 必须由主程序根据用户授权、脚本 ID、目标和当前操作计算。

worker 只能看到当前 execution 的授权集合。主程序不应把完整配置传给 worker，而应：

- 通过宿主 API 返回脱敏后的配置 DTO。
- 对 Tracker 使用 `PluginId + InstanceId` 定位实例。
- 不把远程客户端、Token 或密码序列化到消息中。
- 对每次远程副作用要求确认和幂等键。
- 限制日志中的脚本输出、错误和参数大小。

worker 可拥有独立的临时目录，但该目录不等于宿主文件系统权限。文件系统能力开放
前必须定义目录白名单、符号链接处理、大小限制和清理策略。

## 15. 协议版本和兼容性

协议主版本变更表示消息结构或生命周期不兼容。握手时双方协商：

- worker 协议主版本。
- `ScriptApiVersion`。
- 支持的宿主 API 方法。
- 支持的 capability 名称。

新增字段必须可选，未知字段默认忽略。新增消息类型不能要求旧版本理解；如果旧版本
收到无法忽略的消息，应返回 `WORKER_PROTOCOL_UNSUPPORTED` 并断开连接。

每个 worker 构建包应包含语言名称、worker 版本、协议版本和运行时版本。诊断必须记录
这些信息以及脚本路径和 executionId。

## 16. 日志和诊断

协议消息不得混入普通日志。worker 的 stdout 只允许输出协议消息；脚本的 `print`、
Lua 输出和 Python traceback 应重定向到 stderr 或转换成受限的 `log` 消息。

诊断至少包含：

- 稳定错误码。
- severity 和 category。
- workerId、requestId、executionId。
- scriptId、语言和 worker 版本。
- sourcePath、行号和列号（可用时）。
- 脱敏后的 stderr 摘要。

日志和执行历史必须限制单条消息和总量，避免脚本通过无限输出耗尽主程序内存。

## 17. 资源限制

supervisor 应支持以下限制，并在 worker 启动或执行前配置：

- CPU 时间或墙钟时间。
- 工作集/内存上限。
- 单次请求消息大小。
- stdout、stderr 和脚本返回值大小。
- 宿主调用数量和并发数。
- worker 最大存活时间或最大执行次数。

跨平台资源限制能力可能不同。无法强制设置的限制必须在握手诊断中记录为“软限制”，
不能伪装成硬隔离。

## 18. 各语言适配要求

### 18.1 C#

- 运行入口使用 worker 内的 C# 脚本程序适配器。
- 不加载主程序的 App、DI 或数据库程序集实例。
- 保留 Roslyn 编译诊断和脚本契约验证。
- worker 被终止时释放 collectible AssemblyLoadContext。

### 18.2 Lua

- 使用 NuGet `NLua` 1.7.9（依赖 `KeraLua >= 1.4.9`）承载在独立 .NET worker 中，使用标准 Lua 5.4 运行时。
- worker 以 `--language lua` 握手，默认移除 `io`、`os`、`debug`、`package`、`require`、`dofile` 和 `loadfile`。
- 禁止调用 `LoadCLRPackage`，不注册 `LuaUserData`、反射对象或任意 .NET 类型；每次执行创建独立的 Lua script/context。
- `NLua` 托管程序集和 KeraLua native 资产按 RID 随 Lua worker 部署，至少覆盖 `win-x64`、`linux-x64` 和 macOS 对应架构。
- 将 Lua 错误转换为 sourcePath、行号和列号诊断，并将脚本输出限制在 stderr 配额内。
- 通过 `diary.workItems.query` 和 `log.write` HostCall 访问不可变工作项 DTO 与受限日志 API；第一版不开放更新、删除、文件、网络和进程能力。

### 18.3 Python

- 使用独立 Python 3 解释器进程和受控 `worker.py`，不嵌入 CPython；Worker 源码作为 `Diary.Script.Python.dll` 嵌入资源，通过 `python -I -c` 启动。
- `PythonRuntimeResolver` 位于 `Diary.Script.Python`，负责绝对解释器路径、版本探测、worker 路径和环境诊断。
- Windows 正式包使用应用内的 embeddable distribution；Linux 使用系统 `python3`/`python3.X` 包；macOS 使用显式配置或系统/用户 `python3`。
- embeddable distribution 只作为应用发布资源解压使用，不执行 pip，不修改 PATH/注册表；Linux 不调用发行版包管理器。
- 禁止脚本直接把协议 JSON 写入 stdout；`print` 和 traceback 写入受限 stderr。
- worker 通过 `compile(source, sourcePath, "exec")` 做语法检查，执行时创建新的 globals/context；第一版每个 worker 最多执行一个请求。
- worker 退出码、traceback、超时和取消必须转换成统一结果；运行时缺失不能静默跳过 `.py` 脚本。
- 依赖声明必须经过宿主策略检查，第一版不自动执行任意安装命令，也不执行 `pip install`。
- 嵌入资源不构成程序集级信任边界，正式发布仍需要代码签名或等效的程序集完整性校验。

### 18.4 多语言路由

- `ScriptEngineRegistry` 注册 C#、Lua、Python 三个引擎，即使 Python 解释器当前缺失。
- `ScriptBuildRequest` 携带来自 metadata/manifest 的 descriptor hint；Lua/Python 使用 hint 生成 descriptor，目录加载器校验 C# descriptor 与 metadata 一致。
- `ScriptCatalog` 保存稳定的 `EngineName`，`WorkerScriptExecutor` 按 EngineName 选择独立 supervisor，不根据扩展名临时猜测。
- C#、Lua、Python worker 使用独立进程和独立故障状态；一个 worker 终止不得影响其他语言。
- `WorkerHelloPayload` 的语言值固定为 `csharp`、`lua` 或 `python`，运行时版本和 worker 版本作为可选诊断字段传递。
- 统一使用 `grantedCapabilities`、目标校验和 `executionId` 校验 HostCall；只允许当前执行已协商的宿主 API。

## 19. 测试和验收

### 19.1 协议测试

- 合法握手成功，语言和版本不匹配时拒绝。
- 缺少字段、未知主版本、非法 JSON 和超大消息被拒绝。
- 请求和响应按 requestId 正确匹配。
- 执行期间可以并发处理 host.call 和 cancel。
- stdout 污染、stderr 输出和协议消息不会互相混淆。

### 19.2 生命周期测试

- worker 启动失败不会阻止核心应用启动。
- worker 非零退出后未完成请求返回 `WORKER_TERMINATED`。
- worker 可按退避策略重启并重新握手。
- 心跳超时会终止并重建 worker。
- 空闲回收不丢失已完成执行历史。
- 一个 worker 的故障不会终止主程序或其他语言 worker。

### 19.3 执行测试

- 脚本普通异常只影响当前执行。
- 脚本返回错误、取消和超时映射正确。
- 忽略取消的脚本在宽限期后被终止。
- worker 重启不会自动重复远程副作用。
- 共享 worker 的脚本之间没有可见的运行时状态。

### 19.4 宿主 API 测试

- `workItems.query` 的参数、权限、取消和结果与进程内 API 一致。
- 五种编辑器目标的范围解析、事项快照和 `items.stream()` 行为一致。
- `log.write` 的级别映射、消息大小限制和敏感信息过滤正确。
- 未授权的 API 调用被拒绝。
- `PluginId + InstanceId` 可以准确定位 Tracker 实例。
- 敏感配置不会出现在请求、响应、日志或错误中。
- 大结果集、超限分页和宿主调用频率会被限制。

验收标准：主程序可以启动并复用 C#、Lua 或 Python 常驻 worker；脚本异常、协议错误、
worker 崩溃、超时和取消不会使主程序退出；只读工作项查询可以通过统一协议完成；
worker 重启后不会自动重复未确认的副作用操作。

脚本作者可从脚本管理页和内置编辑器的 `API Reference` 入口查看或打开随应用发布的中文语言文档。管理页使用 Avalonia 原生控件渲染标题、正文和代码块，避免引入额外 Markdown 主题和 SVG 扩展依赖；文档按 C#、Lua、Python 分开维护，并以各 Worker 当前实际暴露的上下文、宿主调用和沙箱限制为准。新建脚本流程提供按语言生成的“空白脚本”和“查询工作项”样板，样板所需能力会同步写入 metadata。

脚本管理页采用左侧简要列表、右侧概览与诊断的布局；执行历史仅在内存保留最近 30 条，单条记录可以复制包含 Worker 标识和脱敏诊断的完整日志，应用退出后不恢复历史。

脚本是否可执行只由目录加载和编译结果决定；历史 metadata 中的 `enabled` 字段会被忽略，不再阻止有效脚本加载。删除普通脚本需要二次确认并删除源码及 metadata，删除脚本包则删除整个包目录。

脚本契约不再包含 capability 权限字段，默认获得宿主已实现的 API；Worker 握手仍通过
`supportedHostApis` 声明实际可用方法。C#、Lua、Python Worker 已接入工作项查询、日志项
创建、模板日志项、Tracker 只读实例目录、剪贴板、用户交互和 `log.write` HostCall。

## 20. 分阶段实施

### 第一阶段：协议骨架

- [x] 定义 `WorkerMessage`、握手、执行、结果、错误和取消消息。
- [x] 实现本机 stdin/stdout 管道和消息大小限制。
- [x] 实现 supervisor 单 worker 串行队列。
- [x] 实现 C# worker 最小适配器。

### 第二阶段：宿主 API 和恢复

- [x] 实现双向 `host.call`/`host.result`。
- [x] 接入只读 `workItems.query`。
- [x] 已实现心跳、超时、进程退出事件监听、执行通道终止诊断、退出码诊断、后台空闲回收、指数退避启动和宽限期后强制终止进程树。
- [x] 执行历史已关联 worker ID、Worker request ID 和执行 ID。

### 第三阶段：多语言

- [x] 接入独立 Lua worker，使用 `NLua 1.7.9 + KeraLua` 和受限标准库/CLR 暴露策略。
- [x] 接入独立 Python 3 worker，使用 `PythonRuntimeResolver` 管理解释器发现。
- [x] 按语言维护独立 supervisor 和故障状态，复用同一协议与 HostCall。
- [x] 目录加载和构建结果保留所选 `EngineName`，确保 Lua/Python 执行不会回退到 C# Worker。
- [~] 已实现多语言路由、运行时发现和版本诊断；Windows/Linux/macOS 启动与打包矩阵待持续验证。
- [x] 实现 worker 强制终止和忽略取消脚本的超时回收；stderr 仅保留超限状态，不保留文本摘要。

### 第四阶段：资源和副作用

- [x] 已增加执行消息、执行结果、stderr 和脚本 stdout 输出、调用数、请求数、后台空闲回收和工作集软限制；操作系统级强内存限制按平台能力处理。
- 设计写入 API、预览、确认、幂等键和审计。
- 根据风险级别选择共享 worker 或独立 worker。

### C# Worker 当前宿主 API

C# Worker 通过 HostCall 使用以下已实现能力：`workItems.query`、`trackerInstances.get`、`clipboard.get`、`clipboard.set`、`ui.notify` 和 `ui.confirm`。剪贴板和用户交互由主进程提供实现；工作项查询结果包含备注、标签等只读日记数据。写入日记、Tracker 写入及任意数据库/DI 访问不属于当前协议。
