# AI 脚本上下文与只读 MCP 设计

## 1. 目标

DiaryApp 已提供 C#、Lua、Python 脚本 API 文档，但 AI 生成脚本时还需要知道当前用户的标签、附加字段、模板、Tracker 实例和保存查询。本文定义两种共用同一份数据契约的只读入口：

1. 脚本管理页生成、预览并导出 Markdown/JSON 上下文包；
2. 本地 Agent 通过 stdio MCP 查询用户明确生成的 JSON 快照。

第一版采用授权快照，而不是让 MCP 进程直接连接数据库。快照是用户在应用内主动生成的、范围固定的只读副本；MCP 进程只读取该文件并在内存中筛选，不能扩大披露范围。

## 2. 信任边界

```text
SQLite/PostgreSQL + 模板 + Tracker 注册表
                  |
                  | App 内显式选择、裁剪、脱敏、预算
                  v
        AiContextSnapshot v1（只读 JSON）
             /                    \
       Markdown/JSON 导出      Diary.Mcp stdio
                                  |
                                  v
                         快照内白名单查询
```

以下对象不得进入共享契约或 MCP 依赖图：

- 数据库路径、连接字符串和数据库 provider；
- Tracker URL、Token、API Key、加密配置和标签元数据；
- `IServiceProvider`、完整 `ScriptApiFacade`、任意 SQL；
- 日志创建、模板创建、导出落盘、剪贴板、UI 和脚本执行能力。

事项标题、备注和附加字段值属于不可信用户数据。JSON 使用结构化字段并带有 `untrusted_user_content` 标记；Markdown 将其放在明确的数据区块中，提示 AI 不得把内容解释为指令。

## 3. 版本化契约

根对象固定包含：

- `schema_id = diary.ai_context`
- `schema_version = 1`
- UTC 生成时间；
- 本次披露范围和预算；
- 标签、附加字段定义、模板、Tracker 安全摘要、保存查询、只读 Host API 能力；
- 可选的事项数据；
- 仅记录范围和数量的审计摘要。

字段只允许追加兼容演进。删除字段、改变语义或放宽默认披露范围必须提升 schema version。读取器必须拒绝未知 schema 和超过大小上限的文件。

## 4. 披露策略与预算

默认包含结构信息，不包含事项正文：

| 数据 | 默认 | 说明 |
| --- | --- | --- |
| 标签目录 | 开启 | 不包含标签 metadata |
| 附加字段定义 | 开启 | 包含类型、说明和 Choice 选项，不包含值 |
| 模板 | 开启 | 包含默认标题、工时和标签 ID |
| Tracker 实例 | 开启 | 仅 plugin/instance/display name/icon/isConfigured |
| 保存查询 | 开启 | 作为脚本查询示例，不执行查询 |
| 只读 Host 能力 | 开启 | 固定白名单 |
| 事项正文、备注、附加字段值 | 关闭 | 必须显式选择日期范围和数量 |

V1 预算：事项最多 100 条；标题最多 2,000 字符；备注最多 4,000 字符；单个附加字段值最多 2,000 字符；JSON 快照最多 2 MiB；MCP 单次返回最多 100 条。超长文本会被截断并在审计摘要中计数。

默认 MCP 快照保存到应用配置目录的 `ai-context/mcp-snapshot.json`。写入采用同目录临时文件加原子替换；Unix 平台尽力设置为仅当前用户可读写。用户导出的 Markdown/JSON 文件由用户自行选择位置，其生命周期不由应用管理。

## 5. 应用服务

`Diary.AiContext` 只包含 DTO、序列化、Markdown 渲染、快照校验和内存查询，不引用数据库或 GUI。

`Diary.App` 中的适配服务负责从当前已连接数据库和应用内存状态收集数据：

1. 复制标签但丢弃 `WorkTag.Metadata`；
2. 读取启用的附加字段定义；
3. 通过现有只读脚本 API 形状复制模板和 Tracker 摘要；
4. 读取保存查询文件；
5. 只有在用户选择时，按日期和数量调用 `QueryWorkItems`，再批量读取标签、备注和附加字段值；
6. 构建纯 DTO 后立即脱离数据库对象。

UI 的“生成预览”只更新内存预览；“刷新 MCP 快照”才写默认快照；Markdown/JSON 导出使用相同的内存对象和序列化器，保证三种输出范围一致。

## 6. stdio MCP

`Diary.Mcp` 是独立 Console 程序，使用官方 C# MCP SDK 的 stdio transport。stdout 只输出协议消息，日志写 stderr。启动参数必须提供 `--snapshot <path>`；程序启动时加载并校验快照，之后不监视数据库或配置目录。

Windows apphost 显式设置 `CETCompat=false`，与主程序、脚本 Worker 和更新器采用同一内部 Windows 兼容策略；Linux 行为不受该 PE 标记影响。

工具白名单：

- `diary_list_tags`
- `diary_list_extra_fields`
- `diary_list_templates`
- `diary_list_tracker_instances`
- `diary_summarize_work_items`
- `diary_query_work_items`

前四项返回对应快照节；后两项只能筛选快照中已经披露的事项。查询参数只支持日期、标签 ID、文本、优先级、limit 和 offset，不支持 SQL、路径或脚本。所有结果都返回 JSON，并保留不可信数据标记。

stdio 进程继承环境变量是常见风险。用户配置 Agent 时应使用最小环境；Diary.Mcp 本身不读取凭据环境变量，也不会在日志中打印快照正文。

## 7. 审计与错误

App 日志只记录 schema version、启用节、各节数量、截断数量、目标类型和成功/失败，不记录正文、备注、附加字段值或完整快照。MCP 日志只记录工具名、返回数量和错误类型。

以下情况必须失败并给出明确错误：

- schema/version 不受支持；
- 快照超过 2 MiB或 JSON 无效；
- 日期格式不是 `yyyy-MM-dd`、范围反转或 limit 超限；
- 快照未包含请求的数据节；
- 快照文件不存在或无读取权限。

## 8. 测试与验收

- 契约序列化使用 snake_case，枚举稳定为字符串；
- Markdown 和 JSON 来自同一快照，且不出现标签 metadata 或连接信息；
- 事项默认不导出，显式导出时执行数量和文本预算；
- 快照读取拒绝超限、未知 schema 和无效 JSON；
- MCP 只列出六个白名单工具，查询不能越过快照内容；
- Linux 使用 JSON-RPC stdio 探针完成 initialize、tools/list 和 tools/call；
- App 构建、相关单元测试和脚本管理页 Headless/CDP 回归通过。

## 9. 后续演进

如果后续确实需要实时数据，可在 App 内增加用户显式启停、短期令牌保护的本地命名管道/Unix socket，并让 stdio 进程作为桥接器。该方案必须继续复用本契约和白名单，且不能退化为数据库直连。V1 不实现实时 IPC、网络 MCP 或写工具。
