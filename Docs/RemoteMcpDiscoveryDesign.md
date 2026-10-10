# 远程 MCP 服务与 Survey v2 自动发现设计

## 1. 目标与边界

本功能让 DiaryApp 受访者按需开放一个只读 MCP 服务，调查者通过 Survey v2 自动发现这些服务，并把它们临时接入内置 Agent。模块 ID 为 `diary.mcp.remote`，随应用发布但默认关闭。

本期不修改工作项、标签或 Tracker 的核心数据结构，不新增数据库表，不提供写入、删除、脚本执行、Tracker 上传或 UI 操作工具。工作项查询继续复用 `IWorkItemQueryScriptApi`，宿主注册时固定 `includeLocalNotes: false`，因此远程 MCP 不会返回本地备注。

## 2. 组件划分

- `Diary.Survey`：定义 `mcp_services` v2 消息、服务广告注册表、发现目录、TTL 和 Peer 访问策略。
- `Diary.App`：`McpServiceDiscoveryCoordinator` 管理调查者的定时发现生命周期，并在 Survey v2 收发入口分流 MCP 发现消息；统计调查仍由 `SurveyViewModel` 处理。
- `Diary.Mcp.Remote`：默认关闭的应用模块；启用后在系统分配的随机 TCP 端口启动轻量 Streamable HTTP MCP 服务，并向本机广告注册表登记。
- `Diary.Agent.UI`：订阅发现目录，把在线服务转换为仅运行时存在的 MCP Client Profile；不写入 AI 设置文件，也不改动用户手工维护的 MCP Server。
- `Diary.Agent`：`McpClientManager` 合并已保存与自动发现的 Profile，服务下线时断开连接并移除对应工具。

解决方案文件夹按职责调整为：

- `AI`：`Diary.Agent`、`Diary.Agent.UI`、`Diary.AiContext`、`Diary.Mcp`、`Diary.Mcp.Remote`；
- `Modules`：`Diary.ModuleBase`、`Diary.ModuleUI`、`Diary.ModuleTestFixture`；
- `Tests`：继续集中 Agent 和 Module 测试项目。

此调整只修改 `.sln` 的逻辑分组，不移动磁盘目录或改变程序集引用。

## 3. Survey v2 协议

请求：

```json
{
  "version": 2,
  "request_id": "每轮唯一 ID",
  "kind": "mcp_services"
}
```

响应 `data`：

```json
{
  "kind": "mcp_services",
  "hostname": "PC-01",
  "username": "user",
  "instance_id": "本次进程实例 ID",
  "services": [
    {
      "service_id": "diary.readonly",
      "display_name": "DiaryApp 只读数据",
      "transport": "streamable_http",
      "endpoints": ["http://192.168.10.20:随机端口/mcp"],
      "capabilities": ["diary_query_work_items"],
      "started_at": "2026-10-10T08:00:00+00:00",
      "expires_in_seconds": 90
    }
  ]
}
```

每次启动生成新的 `instance_id`。服务以 `instance_id + service_id` 去重；相同服务的刷新只延长 TTL，不触发 Agent 反复重连。只接受当前或最近 90 秒自动发现轮次的 `request_id`，忽略版本、类型、传输或 Endpoint 无效的响应。调查者还会按主机名（忽略大小写）丢弃本机响应，因此同一台机器上的当前实例和其他 DiaryApp 实例都不会进入自动发现目录；本机 MCP 仍可供用户手工配置的客户端直连。

## 4. 定时发现生命周期

调查配置满足“启用且作为调查者”时：

1. v2 surveyor 成功启动后立即发起一次 `mcp_services` 调查；
2. 默认每 30 秒再发起一次；
3. 每轮同时清理超时请求和发现目录；
4. 服务广告默认 TTL 为 90 秒，连续三轮未刷新即从目录移除；
5. 收到响应后先过滤本机主机名，只把其他机器的服务写入发现目录；
6. 调查配置变更、调查功能关闭或应用退出时先取消循环，再停止 v2 surveyor；
7. 手工统计调查和自动发现共用 v2 surveyor 的串行发送门禁，避免并发破坏 NNG survey 状态。

受访者不依赖调查页面 ViewModel。应用级协调器直接识别 `mcp_services` 请求并从广告注册表生成响应；其他 v2 请求继续进入原有页面处理链。

## 5. MCP 服务

服务监听 `0.0.0.0:0`，由操作系统选择端口。每次生成发现响应时都会重新计算 Endpoint：优先返回“通往当前调查者 IP 的本地网卡地址”，其次返回其他活动 IPv4 地址，最后返回回环地址。这样调查者配置变化无需重启模块，多网卡机器也优先选择调查者实际可达的 Endpoint。

桌面应用不额外依赖 ASP.NET Core Runtime。模块使用基于 `TcpListener` 的轻量 HTTP/1.1 实现，支持当前 Agent 所需的 MCP `2025-11-25` 子集：

- `initialize`；
- `notifications/initialized`；
- `tools/list`；
- `tools/call`；
- Streamable HTTP 的 JSON 响应。

服务不创建 MCP Session，也不开放独立 GET 通知流，因为本期工具列表在模块生命周期内固定。请求头上限 32 KiB，请求体上限 1 MiB，每个连接处理一个请求并主动关闭，避免桌面进程维护无界长连接。

固定只读工具：

- `diary_list_tags`；
- `diary_list_extra_fields`；
- `diary_get_current_context`；
- `diary_query_work_items`；
- `diary_summarize_work_items`。

所有工具在 MCP 注解中声明 `readOnlyHint=true`、`destructiveHint=false`。Agent 对自动发现工具再次建立本地只读策略；名称具有删除语义的工具仍由现有门禁拒绝。

## 6. Peer 限制与 RPM

HTTP 请求进入 JSON-RPC 解析前先检查来源地址。允许范围为：

- IPv4/IPv6 回环地址；
- 本机活动网卡地址，即同一台机器通过本机 LAN IP 访问；
- Survey 设置中当前配置的调查者 IP。

其他来源返回 `403 Forbidden`。允许名单每次请求动态读取调查配置，调查者 IP 修改后无需重启模块。

通过 Peer 检查后按规范化来源 IP 执行 60 秒滑动窗口 RPM 限制。默认每个 IP 每分钟 60 个 HTTP 请求，超过后返回 `429 Too Many Requests` 和 `Retry-After`。设置文件位于：

```text
<应用配置目录>/remote-mcp/settings.json
```

默认内容：

```json
{
  "requestsPerMinute": 60
}
```

允许范围为 1 到 6000。损坏或越界配置会让模块启动失败并写入模块启动诊断，不影响 DiaryApp 核心功能。

## 7. Agent 动态接入

发现服务转换为 `StreamableHttp` Profile，使用直连模式，不继承模型代理或系统代理。Profile ID 由 `instance_id + service_id` 的 SHA-256 摘要生成，避免主机名和用户名进入工具模型名。

只把广告 `capabilities` 中列出的工具设置为只读并启用；远端额外返回但未广告的工具不会暴露给模型。自动发现 Profile 只存在内存中：

- 不写入 `ai-agent/settings.json`；
- 不出现在用户手工 MCP JSON 中；
- TTL 到期后自动断开并从 `AgentToolRegistry` 移除；
- 手工 Profile 与发现 Profile ID 冲突时，手工配置优先。

## 8. 失败与降级

- 模块未启用或启动失败：广告注册表为空，Survey v2 返回空服务列表，核心调查功能继续可用；
- 自动发现轮次失败：保留尚未过期的目录项，下一轮重试；
- 某个 MCP Endpoint 无法连接：只记录该 Server 诊断，不影响模型、内置工具和其他 MCP Server；
- Agent 模块未启用：调查者仍可维护发现目录，但不会建立 MCP 连接；
- 调查功能未启用：远程 MCP 模块即使已启用也不会被网络发现，但本机服务仍受 Peer 与 RPM 门禁保护。

## 9. 验证

- Survey 协议、注册替换、TTL 刷新/淘汰和 Peer 白名单单元测试；
- RPM 按来源 IP 限流测试；
- 轻量 HTTP MCP 的只读工具清单测试；
- 使用现有 `McpClientConnection` 完成真实 `initialize → notifications/initialized → tools/list` 回归；
- 主解决方案编译和模块发布目录检查。

后续可增加发现服务状态 UI、RPM 图形化配置和多 Endpoint 连接回退；这些增强不改变当前协议或核心数据结构。
