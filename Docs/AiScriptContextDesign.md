# AI 脚本上下文与只读 MCP 设计

## 1. 目标

DiaryApp 已提供 C#、Lua、Python 脚本 API 文档，但 AI 生成脚本时还需要知道当前用户的标签、附加字段、模板、Tracker 实例和保存查询。本文定义两种共用同一份数据契约的只读入口：

1. 脚本管理页生成、预览并导出 Markdown/JSON 上下文包；
2. 本地 Agent 通过 stdio MCP 查询用户明确生成的 JSON 快照，并对 AI 生成的脚本执行无运行副作用的编译校验。

程序设置另提供配置辅助入口：它只根据 MCP 可执行文件和快照的绝对路径生成通用 JSON 与 AI 可读 Markdown，不读取或嵌入快照正文，也不直接修改第三方 Agent 的配置文件。

第一版采用授权快照，而不是让 MCP 进程直接连接数据库。快照是用户在应用内主动生成的、范围固定的只读副本；MCP 进程只读取该文件并在内存中筛选，不能扩大披露范围。

## 2. 信任边界

```text
SQLite/PostgreSQL + 模板 + Tracker 注册表
                  |
                  | App 内显式选择、裁剪、脱敏、预算
                  v
        AiContextSnapshot v1（只读 JSON）
             /                    \
       Markdown/JSON 导出      Diary.Mcp stdio <--- AI 提交的源码文本
                                  |                    |
                                  v                    v
                         快照内白名单查询        只编译/解析，不执行
```

以下对象不得进入共享契约或 MCP 依赖图：

- 数据库路径、连接字符串和数据库 provider；
- Tracker URL、Token、API Key、加密配置和标签元数据；
- App 的 `IServiceProvider`、完整 `ScriptApiFacade`、任意 SQL；
- 日志创建、模板创建、导出落盘、剪贴板、UI、脚本执行器和脚本运行上下文。

MCP 允许依赖三种内置语言引擎的校验能力，但不注册脚本目录加载器、执行器、Worker、Host API 或 App 服务。校验输入只接受请求中的源码和语言，不接受本地路径、附加程序集、NuGet 包或模块搜索路径。

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

程序设置末尾的“AI 与 MCP”作为标准 `SettingGroup`，使用“已生成 · 日期时间”或“未生成 · 操作提示”的紧凑单行状态显示快照是否存在及更新时间，并以独立标准设置行提供“打开 AI 上下文”“复制 AI 说明”“复制 MCP JSON”和使用文档入口。状态行和操作行复用统一的 150px 标签、400px 内容区及帮助提示布局；复制按钮在快照不存在时禁用，跳转按钮只保存当前程序设置并打开披露页面，不自动创建快照或改变披露范围。

配置说明固定描述 stdio transport、`command`、`--snapshot` 参数、七个工具名称和最小环境要求。路径通过 JSON 序列化器转义；生成内容不得包含快照正文、环境变量、凭据或数据库连接信息。

## 6. stdio MCP

`Diary.Mcp` 是独立 Console 程序，使用官方 C# MCP SDK 的 stdio transport。stdout 只输出协议消息，日志写 stderr。启动参数必须提供 `--snapshot <path>`；程序启动时加载并校验快照，之后不监视数据库或配置目录。

Windows apphost 显式设置 `CETCompat=false`，与主程序、脚本 Worker 和更新器采用同一内部 Windows 兼容策略；Linux 行为不受该 PE 标记影响。

发布时 MCP 仍保持独立进程和独立 apphost，但不再生成单文件包。`Diary.App` 先按目标 RID 将 MCP 发布为自包含多文件目录，再把该目录安全合并到主应用发布根目录；`Diary.Mcp.dll`、`Diary.Mcp.deps.json`、`Diary.Mcp.runtimeconfig.json` 和对应平台 apphost 都是发布包必需文件。主应用与 MCP 的 .NET Runtime、Roslyn 和脚本引擎依赖若路径、大小及 SHA-256 完全一致则只保留一份；任一同名文件内容不同都必须中止发布，Windows 目标还要拒绝仅大小写不同的路径冲突。MCP 发布关闭调试符号，合并过程拒绝符号链接和 PDB。

该布局减少重复运行时，但意味着发布版 MCP 依赖安装目录中的共享文件。配置第三方 Agent 时可以直接引用安装目录内的 MCP apphost，不得只复制 `Diary.Mcp.exe` 或 `Diary.Mcp` 到其他位置；如需迁移，应保留整个应用发布目录。

工具白名单：

- `diary_list_tags`
- `diary_list_extra_fields`
- `diary_list_templates`
- `diary_list_tracker_instances`
- `diary_summarize_work_items`
- `diary_query_work_items`
- `diary_validate_script`

前四项返回对应快照节；`diary_list_extra_fields` 的字段定义包含配置的 `default_value`，但不包含或推导事项实际字段值。事项查询与汇总只能筛选快照中已经披露的事项。查询参数只支持日期、标签 ID、文本、优先级、limit 和 offset，不支持 SQL 或路径。所有结果都返回 JSON，并保留不可信数据标记。

事项节未披露时，`diary_query_work_items` 和 `diary_summarize_work_items` 不得抛出通用工具异常，也不得返回空数组或零汇总，以免调用方把“未授权”误判为“已授权但无数据”。两者返回正常 MCP 文本结果，正文为 `available=false`、`error=work_items_not_disclosed`、`section=work_items` 和引导用户显式包含事项后刷新快照的消息；事项已披露时继续保持原有数组或汇总对象结构。

`diary_validate_script` 接受 `language` 和 `source`，语言限 `csharp`、`lua`、`python`，源码 UTF-8 最大 256 KiB，单次最多返回 100 条诊断，超时 10 秒且最多并行处理两个请求。服务端使用固定虚拟文件名，不读取调用方提供的路径：C# 只通过 Roslyn 执行策略检查并 Emit 到内存，不加载程序集、不反射类型、不实例化入口且不写缓存；Lua 只调用 `LoadString` 编译代码块；Python 只在隔离解释器中执行 `ast.parse` 和固定安全策略。该工具只能确认源码通过相应编译/解析阶段，不能保证入口元数据完整或运行时成功。

stdio 进程继承环境变量是常见风险。用户配置 Agent 时应使用最小环境；Diary.Mcp 本身不读取凭据环境变量，也不会在日志中打印快照正文。

## 7. 审计与错误

App 日志只记录 schema version、启用节、各节数量、截断数量、目标类型和成功/失败，不记录正文、备注、附加字段值或完整快照。MCP 日志只记录工具名、返回数量和错误类型。

以下情况必须失败并给出明确错误：

- schema/version 不受支持；
- 快照超过 2 MiB或 JSON 无效；
- 日期格式不是 `yyyy-MM-dd`、范围反转或 limit 超限；
- 快照未包含标签、附加字段、模板或 Tracker 等请求的数据节；事项查询与汇总按第 6 节返回结构化不可用结果；
- 快照文件不存在或无读取权限。
- 校验语言不受支持、源码为空或超过 256 KiB；
- 脚本编译/解析失败、运行时不可用或校验超时。

## 8. 测试与验收

- 契约序列化使用 snake_case，枚举稳定为字符串；
- Markdown 和 JSON 来自同一快照，且不出现标签 metadata 或连接信息；
- 事项默认不导出，显式导出时执行数量和文本预算；
- 快照读取拒绝超限、未知 schema 和无效 JSON；
- MCP 只列出七个白名单工具，查询不能越过快照内容，脚本校验不能读取路径或执行源码；
- 三种语言分别验证成功与失败诊断；C# 使用会抛异常的静态构造函数证明校验过程不加载或实例化程序集；
- Linux 使用 JSON-RPC stdio 探针完成 initialize、tools/list、快照查询和 `diary_validate_script` 调用，并验证未披露事项时查询与汇总不会成为工具错误；
- App 构建、相关单元测试和脚本管理页 Headless/CDP 回归通过。
- 配置生成使用绝对路径、合法 JSON 且不包含 `env` 或快照正文；程序设置 CDP 回归验证复制两种格式和跳转到 AI 上下文。

## 9. 后续演进

如果后续确实需要实时数据，可在 App 内增加用户显式启停、短期令牌保护的本地命名管道/Unix socket，并让 stdio 进程作为桥接器。该方案必须继续复用本契约和白名单，且不能退化为数据库直连。V1 不实现实时 IPC、网络 MCP 或写工具。
