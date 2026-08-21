# DiaryApp TODO

本文只维护当前未完成、进行中和后续计划。已完成内容已提取到 [`CompletedWork.md`](CompletedWork.md)，避免当前 TODO 被历史完成项淹没。

规划内容必须有明确的前置依赖和验收标准。

## 非 tracker TODO

- [~] 字体不再编译进 DLL；发布包将 Noto Sans Mono CJK SC 及其 SIL Open Font License 1.1 授权文本作为 `Fonts/` 下的应用默认字体和 CJK 后备，以中英文 2:1 等宽字形替代 LXGW WenKai Mono，OpenMoji 已移除。视图设置已提供“默认字体”“跟随系统”“系统字体”“字体文件”四种来源，新配置默认选择随包字体，保存后可运行时切换；无效配置优先回退随包字体，随包字体缺失时再回退平台默认字体。Windows 构建、发布复制、系统字体、外部文件加载及运行时切换已验证，仍需完成 Linux 中文、Emoji 与等宽字体回退验证。
- [x] 已加固 SQLite/PostgreSQL 核心迁移执行、逐步提交、失败回滚、降级和断链校验；业务数据保留与失败恢复由共享 provider 契约测试覆盖
- [~] SQLite 已支持手动创建、校验和下次启动还原完整数据库，并在启动复检失败时恢复还原前安全副本；PostgreSQL 已接入 Client `bin` 目录配置、Windows/Linux 工具探测、custom-format dump/restore、工具版本检查、最小权限预检、独立目标库还原、启动原貌复检和成功后的配置切换；Tracker 级还原复检及更完整的跨版本门禁仍待补齐。应用包更新已完成[清单协议、客户端事务、自举协议与 GitHub 同步服务契约设计](ApplicationUpdateDesign.md)及[服务端专项需求设计](ApplicationUpdateServerDesign.md)，正式 Tag CI 已生成不含 changelog 的机器可读源资产 metadata 并校验运行包大小和 SHA-256；同步服务清单生成、更新检查、独立更新器、回滚以及服务实现仍待开发。Windows/Linux 终止性托管异常已通过独立 DiagnosticsClient 进程生成 Triage Dump，并显示最小崩溃提示窗口

## 阶段 7：代码质量与运行稳定性

目标：收敛应用退出、后台任务和 UI 状态更新的线程边界，避免异常丢失、同步阻塞和对象生命周期失控。

### 7.1 线程安全与异步生命周期

- [x] 梳理 `Diary.Survey` 中接收循环、消息分发和停止流程；接收循环使用取消令牌，消息处理任务可等待且异常可诊断，应用通过异步 `ShutdownAsync`/`StopServerAsync` 完成停止，不在 UI 线程同步阻塞。
- [x] `WorkEditorViewModel.Upload()` 已通过 UI Dispatcher 统一回写 `UploadResults`、锁定状态和绑定属性，并补充从后台线程调用的 Headless 回归测试。
- [~] 已收敛 `Diary.App` 的调查配置重载、问题发送和退出清理，以及 `Diary.Survey` 的接收任务；左上角菜单重启会复用完整退出清理，并在单实例锁释放后启动新进程；脚本目录加载、脚本管理加载/刷新和脚本编辑器关闭等异步入口已增加统一异常观察，其他 UI fire-and-forget 任务仍需继续审计。

验收：后台任务异常可以进入日志或结构化诊断，应用关闭不会遗留接收任务；工作项上传从后台线程调用时不会跨线程修改 UI 绑定对象。

### 7.2 CrashDump 与崩溃提示

- [x] `Program.Main` 最早阶段注册终止性托管异常捕获；同一 `Diary.App` 可执行文件以独立命令行模式生成 Triage Dump、写入结果并显示最小 Avalonia 崩溃提示窗口。
- [x] 崩溃窗口显示异常类型、简要消息、Dump 状态和可复制路径，详情过长时滚动且底部操作保持可见，并提供“打开 Dump 文件夹”；Dump 仅保存在本机、默认保留最近 5 个，不自动上传。
- [~] 已覆盖 Windows/Linux DiagnosticsClient 真实进程 Dump；`FailFast`、StackOverflow 和本机代码严重崩溃仍需 Runtime/操作系统级 Dump 兜底。

设计文档：[`CrashDumpDesign.md`](CrashDumpDesign.md)

验收：终止性托管未处理异常不依赖原进程 UI 弹窗；捕获失败不覆盖原始异常；Windows/Linux CI 能从独立进程对真实 .NET Worker 生成非空 Dump。

### 7.3 Provider 数据升级登记

- [x] 已明确 SQLite/PostgreSQL `DbRecords` 当前无业务数据升级时返回 `null`；`ProviderMigrationRegistrationTests` 锁定上一正式数据版本，提升 `DataVersion` 时必须同步登记两个 provider 的迁移。
- [~] 已将核心数据库兼容性从单一数据版本升级为 provider 能力、结构快照指纹、迁移状态/历史和基础数据完整性检查；provider 指纹按核心契约归一化，迁移每步提交后同步 Running 元数据并在最终复检通过后写入 Stable，结构漂移和更新版本数据库不会直接进入可写状态。共享 SQLite/PostgreSQL 契约测试已覆盖空库/断连、provider 与元数据不匹配、缺表缺字段和错误索引、额外索引归一化、迁移历史与 checksum、第二步失败后的部分提交、失败状态重启检查，以及 SQLite 外键和 PostgreSQL 字段键完整性错误；本地 `Diary.DbTests` 现为 230/230 通过，完整项目构建与 CI PostgreSQL 容器门禁仍需继续保持。

后续增强测试（不阻塞当前核心迁移功能验收）：

- [ ] 增加 `WriteSchemaMetadata()`、`RecordMigrationHistory()`、事务提交和回滚失败的故障注入测试，确认底层 I/O 错误不会被误报为迁移成功。
- [ ] 使用真实上一正式版本的 SQLite 数据库文件和 PostgreSQL 初始化快照，执行至少一条包含真实 DDL 的升级链，而不只使用测试迁移写入版本号。
- [ ] 增加 SQLite 备份目录不可写、临时文件清理、磁盘空间不足等更多文件系统故障注入测试。
- [x] 增加 SQLite 手动备份、备份校验、下次启动还原和还原失败回滚；PostgreSQL 增加 `pg_dump`/`pg_restore` 工具目录配置与跨平台探测测试。
- [~] PostgreSQL 已实现 `pg_dump`/`pg_restore`、工具/服务端主版本检查、当前操作所需的最小权限预检、独立目标数据库、无 `CREATEDB` 时恢复到配置的已有空数据库、启动原貌兼容性复检、RedMine/Jira 已知表组检查、成功后的配置切换，以及工具超时终止和输出脱敏；后续补充 Tracker schema 版本/业务语义复检、用户取消和匹配主版本工具下的真实还原门禁；不覆盖当前数据库。
- [ ] 如果未来明确支持同一数据库的多实例并发，再增加迁移锁和并发启动测试；当前产品没有要求两个进程同时迁移同一个数据库，该项暂不作为发布门禁。

前置依赖：先确认核心数据库与插件数据库的版本职责边界，避免把插件迁移重复登记到核心 provider。

备份还原设计：[`DatabaseBackupRestoreDesign.md`](DatabaseBackupRestoreDesign.md)

验收：当前数据版本无升级时迁移流程保持幂等；共享契约测试验证成功迁移保留业务数据、失败迁移回滚版本写入并保留原数据；CI Linux 门禁强制运行 PostgreSQL 容器测试。

## 阶段 8：常见 Tracker 后端扩展

目标：在不修改 `Diary.Core` 和核心编辑器的前提下，逐步增加常见 Tracker 后端，验证通用插件、实例、UI、
本地绑定、远程上传和脚本 API 的扩展能力。

统一要求：

- [ ] 每个后端独立实现 `ITrackerPlugin`，不得在主程序增加具体 Tracker 分支。
- [ ] 每个后端支持稳定的 `PluginId`，并明确是否支持多实例。
- [ ] 每个后端使用 `(PluginId, InstanceId)` 作为本地绑定、UI 和脚本访问的身份。
- [ ] 远程 API Key、Token 和密码使用现有敏感配置存储和遮罩策略。
- [ ] 网络请求不能进入核心本地保存事务。
- [ ] 远程失败必须保留核心工作项和本地绑定，并支持重试。
- [ ] 每个后端提供配置页、实例启用/禁用、连接测试和错误诊断。
- [ ] 每个后端提供本地数据库扩展、迁移和 SQLite/PostgreSQL 契约测试（如确实需要本地缓存）。
- [ ] 每个后端补充缺失配置、权限不足、网络失败、重复上传和实例隔离测试。
- [ ] 后端专用类型只位于插件边界，不加入 `Diary.Core`。

### 8.1 GitHub Issues（优先级高）

- [ ] 设计 GitHub 配置实例：API 地址、Token、Owner、Repository 和默认筛选条件。
- [ ] 支持 GitHub.com，预留 GitHub Enterprise Server 地址配置。
- [ ] 实现 Issue 列表、详情、Label、Milestone 和状态读取。
- [ ] 实现工作项与 GitHub Issue 的本地绑定。
- [ ] 实现创建/更新 Issue 或评论等远程写入前的确认流程。
- [ ] 实现上传耗时、描述和本地工作项链接的映射策略。
- [ ] 增加 REST/GraphQL API 错误、限流和权限不足处理。
- [ ] 增加仓库多实例和同一仓库不同配置实例的隔离测试。

验收：用户可以配置一个或多个 GitHub 仓库，浏览并绑定 Issue，保存本地工作项，按确认执行远程上传，
远程失败时不丢失本地数据。

### 8.2 Linear（优先级高）

- [ ] 设计 Linear 配置实例：API Token、Team、默认 Project 和默认状态。
- [ ] 实现 GraphQL 客户端和结构化错误处理。
- [ ] 支持 Issue、Project、Cycle、Label、Priority 和状态读取。
- [ ] 实现工作项与 Linear Issue 的本地绑定。
- [ ] 实现耗时、备注和状态的上传映射，并明确副作用确认策略。
- [ ] 对 GraphQL schema 变化、限流和网络中断增加处理。
- [ ] 增加多 Team、多 Project 和多实例隔离测试。

验收：用户可以按 Team/Project/Cycle 筛选 Linear Issue，绑定到本地工作项，并在明确确认后上传工作记录。

### 8.3 GitLab Issues（优先级中）

- [ ] 设计 GitLab 配置实例：Server URL、Private Token、Project ID 和默认筛选条件。
- [ ] 同时支持 GitLab.com 和自托管 GitLab。
- [ ] 实现 Issue、Label、Milestone、Assignee 和状态读取。
- [ ] 实现工作项与 GitLab Issue 的本地绑定和远程上传。
- [ ] 处理不同 GitLab 版本和权限模型的差异。
- [ ] 增加自托管地址、证书、网络错误、限流和 Token 失效测试。
- [ ] 增加多个 GitLab Server 和多个 Project 实例隔离测试。

验收：用户可以配置 GitLab.com 或自托管 GitLab 项目，完成 Issue 浏览、绑定、保存和可重试上传。

### 8.4 Markdown/本地任务（优先级中）

- [ ] 评估 Markdown 任务语法和支持范围，例如 `- [ ]`、标签和任务 ID。
- [ ] 设计本地目录、文件编码、换行和冲突处理策略。
- [ ] 支持扫描任务、按文件和标签筛选、绑定 Diary 工作项。
- [ ] 支持文件变更检测和手动刷新，避免静默覆盖用户编辑。
- [ ] 评估与 Obsidian、Logseq 等常见 Markdown 工作流的兼容性。
- [ ] 增加完全离线测试，不依赖网络服务和外部凭据。

验收：用户可以指定本地 Markdown 目录，浏览和绑定任务，应用变更前能够看到文件差异，冲突不会覆盖原文件。

### 8.5 Jira（优先级高，最小工时闭环已实现）

- [x] 已实现 Jira 插件 manifest、多实例配置、启用/禁用和配置迁移。
- [x] 已实现 Jira REST 项目查询、Issue 查询、连接测试和 Worklog 追加。
- [x] 已实现 SQLite/PostgreSQL 本地项目、Issue、工作项绑定和远程 Worklog ID 持久化。
- [x] 已接入 Jira 工作项编辑器扩展；已提交的本地工作项不可重复上传或删除远程工时。
- [~] 当前使用 Jira Cloud v3 风格 API；Jira Server/Data Center 的授权、版本和字段差异仍需真实环境验证。
- [ ] 增加真实 Jira Cloud、自托管 Jira 和权限矩阵集成测试。

设计文档：[`JiraTrackerDesign.md`](JiraTrackerDesign.md)

验收：用户可以配置 Jira 实例，刷新并选择 Issue，保存本地工作项，追加耗时并保留远程 Worklog ID；
Jira 失败时核心工作记录仍可保存。

### 8.6 PLM（公司要求，等待开放 API）

- [x] 确认 PLM 是必须实现的目标系统，不从产品规划中移除。
- [~] 在没有开放 API 前保留插件边界和最小工时契约，不实现猜测性的远程适配。
- [ ] API 开放后确认认证、项目/任务查询、工时追加、权限和幂等语义。
- [ ] 实现 PLM 插件、多实例配置、本地绑定和追加式工时上传。

验收：PLM API 开放后，DiaryApp 可以在不修改核心工作记录模型的前提下接入项目任务和工时追加。

### 8.7 通用 Tracker 能力补强

- [ ] 统一只读查询、远程写入、确认、失败和重试结果模型。
- [ ] 增加 Tracker 后端能力声明，例如读取 Issue、创建 Issue、上传工时和管理标签。
- [ ] 在 UI 中按能力隐藏不支持的操作，而不是由具体 ViewModel 猜测。
- [ ] 为每个 Tracker 后端生成诊断摘要，禁止导出 Token 和密码。
- [ ] 补充无 Tracker、单实例、多实例和插件缺失时的核心编辑器集成测试。

验收：新增 Tracker 后端只需实现插件契约和自身 UI/数据库扩展，核心主程序、编辑器和查询系统无需增加专用分支。

## 阶段 9：脚本系统落地

设计文档：[`ScriptSystemDesign.md`](ScriptSystemDesign.md)

目标：将当前 `Diary.ScriptBase` 的接口草案落地为可发现、可编译、可执行、可诊断且受权限控制的脚本系统，
同时为日记批处理、编辑器工作流和 Tracker 只读联动提供稳定宿主边界。

设计约束：

- 脚本不能访问核心数据库连接、DI 容器、`App` 实例或 Avalonia 控件。
- 模板由宿主/编辑器负责选择、应用和持久化，脚本不能选择、创建、修改、删除或应用模板。
- 编辑器脚本使用结构化目标表达年、季度、月、日和事项上下文；日期目标由宿主解析为范围，事项目标传递安全快照。
- Tracker 目标必须使用 `PluginId + InstanceId` 复合身份，不能只使用显示名称或单独实例 ID。
- 脚本异常、编译失败、超时和权限拒绝只能影响当前脚本执行，不能阻止核心程序运行。
- 远程 Tracker 写入默认需要明确授权和确认；当前已实现查询、受控日志项/模板日志项创建和系统交互，远程 Tracker 写入仍未实现。
- 工作记录采用追加式模型；脚本自动化不提供删除或直接改写历史记录，错误更正通过可追溯的修正/冲正记录表达。
- 面向脚本作者的 SDK 按应用命令、编辑器目标、自动化触发等功能提供不同程序入口，底层 Worker 协议保留统一执行适配层。

### 9.1 基础契约和运行时

- [~] 目标校验已覆盖五类目标；项目、Tracker Issue 和 Tracker 实例目标待扩展。
- [x] 已移除脚本 metadata、manifest、目录加载结果和管理页模型中遗留的 `Enabled` 字段；脚本可用性只由目录加载与构建结果决定，旧 JSON 多余字段继续兼容忽略。

### 9.2 C# 脚本引擎

- [~] C# 引擎使用受限程序集引用和 Roslyn 语义策略拒绝文件、网络、进程、反射、数据库和 DI API，并封锁动态绑定、`GetType`、线程和脱离生命周期的任务调度；当前统一通过可终止的独立 Worker 执行，Roslyn 策略不单独承担进程隔离。
- [~] 已拒绝对文件系统、网络、进程、数据库连接、服务容器和主程序对象的直接引用；Worker 进程边界负责崩溃、超时和资源失控隔离。
- [~] 已覆盖成功编译、语法错误、入口类型错误、只读宿主引用、直接危险 API、动态绑定、类型反射入口和后台执行逃逸；更全面的引用边界测试待补充。

### 9.3 执行、取消和完整 Host API

- [~] 应用和编辑器入口已在后台执行；通用调用方仍需遵循后台执行约定。 C# 脚本编辑器已补充进程内 Roslyn LSP-like 实时诊断、语义补全和基础悬停服务；悬停提示仍需改为可靠的单词命中和专用浮动 Popup，多语言外部 LSP、重构和定义跳转仍待评估。
- [~] 已实现成功、失败、取消、超时和拒绝状态；脚本默认拥有宿主已注册的完整 Host API，拒绝仅用于 API 未配置、参数无效或运行时故障，不作为用户授权门禁。 工作项相关自动化失败会通过非阻塞错误 Toast 提示“工作项/标签已保存，但自动化脚本执行失败”，后台定时和启动任务仍以日志、执行历史为准。
- [~] 已定义读写日记、交互、剪贴板和 Tracker 能力；网络和文件系统仍由 Worker/语言沙箱边界隔离。
- [x] 普通日志项和模板日志项创建均支持 `Preview`；真实写入使用 provider 事务，失败时回滚，预览不写数据库和幂等存储；提交成功后会通知事件记录页重新读取当前日期，幂等重放和失败路径不刷新。批量创建和未来追加式修正/冲正仍待设计；脚本不提供删除或直接修改历史记录。

### 9.4 脚本 API 和宿主能力

- [x] 已明确脚本查询与追加工作项的事务边界和失败行为；当前无脚本授权体系，默认访问宿主已注册的完整 Host API。普通/模板追加使用 provider 事务，失败回滚；数据库级幂等原子化和批量追加仍待后续评估。
- [~] 已统一 Tracker 实例目录的只读 DTO、能力声明和错误结果；后端 Issue 查询模型随首个新 Tracker 实现补充。
- [x] 已为查询 API 增加 `today`/`yesterday`/`thisWeek`/`thisMonth` 日期范围快捷值，并统一三语言 create API 的 `preview`/`idempotencyKey` 文档与幂等持久化说明。
- [x] C# 脚本 API 已按主推语言收口：新增 `context.Api()` 推荐路径、今日/区间查询、简化日志创建、`EnsureSucceeded()` 与独立快速入门；新建模板不再生成底层 `GetApi<T>()` 样板。
- [x] C# metadata 已收缩为运行配置：descriptor 身份、作用域、入口类型和编辑器目标只由源码声明；管理页保存会清理旧身份字段，目录加载按编译后入口校验自动化配置。
- [x] 脚本管理手动运行已增加参数、幂等键、超时和 Preview 对话框；默认参数和默认超时可写入 metadata。
- [x] Query 只读和 Preview 已由宿主强制：日志写入转预览，导出转 validate-only，预览目录使用虚拟令牌，剪贴板写入/打开文件等副作用拒绝；Worker 与进程内路径语义一致。
- [x] Python/Lua 公共门面新增并推荐 snake_case 命名，保留旧 camelCase 别名；请求和结果 DTO 继续遵循宿主协议字段，避免与 C# 风格强行统一。
- [x] 已支持通过版本化 .diaryscripts 共享包批量共享已成功加载脚本的源码和运行配置：加载失败的脚本不能导出；开发者在脚本管理页导出，普通用户从全局设置菜单导入且无需开启开发者功能；导入先做大小、路径、语言、SHA-256 和 metadata 校验，冲突默认跳过、显式授权后原位覆盖，批量失败会恢复备份，并在完成后重载脚本目录和自动化调度。设计见 [ScriptSharingDesign.md](ScriptSharingDesign.md)。

- [x] 已增加 Week 编辑器目标（用周一的 `yyyy-MM-dd` 标识），日历右键可对本周/上周运行脚本，C#/Lua/Python 的 `dateRange` 同步解析周范围。
- [x] 查询工作项标签 DTO 已增加语义化的 `IsPrimary`/`isPrimary` 字段；`Level` 保留用于兼容，C#、Lua、Python 示例均使用语义化字段判断主标签。
- [x] 标签支持只读字符串键值元数据，编辑器可维护 `projectNumber` 等项目属性，并同步暴露给 C#、Lua、Python 脚本；当前数据库通过初始表结构和本地 SQLite 手工补列保持兼容。
- [x] 标签附加字段已落地：字段按标签定义多个全局唯一 `FieldKey`，类型创建后固定，字段通过禁用保留历史数据；工作项使用独立对话框按标签编辑可选值，日志界面只显示按钮和 Tooltip 预览；SQLite/PostgreSQL 由核心数据库基类契约和 provider 初始化表支持，脚本通过 `FieldKey` 只读访问且不触发字段脚本；迁移导入的只读工作项不显示附加字段入口。
- [x] 标签管理页支持 `.diarytags` 导入导出标签、元数据、附加字段定义和可选 Tracker 规则；禁用标签可导出但导入后默认启用，Tracker 只记录类型和名称，导入时可映射到同类型本地实例或不关联，无效、不存在和无法验证的规则不会写入。
- [~] 脚本交互式通用导出：按 [`ScriptSpreadsheetExportDesign.md`](ScriptSpreadsheetExportDesign.md) 分阶段实现：
  - [x] 已增加仅允许有人值守的 `Editor+Editor`、`Application+Manual` 和 `Query+Manual` 选项选择、目录选择、XLSX 导出和询问打开 HostCall；`RequireChoice` 禁止右上角关闭，并对取消、Worker 终止、通道断开和响应发送失败做一次性清理。
  - [x] 已增加绑定 `ExecutionId`/`WorkerId` 的 `DirectorySelectionId` 和短期 `FileId` 生命周期；非法文件名直接拒绝，不做替换。
  - [x] 已增加通用 `IExportApi`、格式目录和 ClosedXML XLSX 表格处理器，支持中文、基础样式、合并单元格、日期/时间/日期时间、数值、`Duration` 和 `SUM` 合计；`Time` 不参与合计，`Duration` 使用 `[h]:mm:ss`。
  - [x] 已实现独立 CSV 插件，固定 UTF-8 BOM/CRLF/RFC 4180，包含公式注入防护、合计计算和简易标记模板；支持标量替换、按行循环、按列循环和 M×N 矩阵展开，模板插值会重新按字段执行逗号、引号、换行转义和公式前缀防护。
  - [x] 已实现独立 DOCX 插件，支持文档块、标题、段落、表格、合计、合并和简易标记模板；支持标量替换、表格行循环、单元格列循环和 M×N 矩阵展开，模板导入拒绝外部关系、宏/ActiveX/OLE/嵌入对象及可能访问外部资源的字段指令。
  - [x] 已完成 C#、Lua、Python 门面、Worker 代理、snake_case 协议契约以及无数据/取消/打开失败相关基础测试；真实 UI 端到端测试仍待补充。
  - [x] XLSX 工作表名称格式选项已统一为区分大小写的 `sheet_name`，不兼容尚未对外使用的 `sheetName`，并已覆盖正式键生效和旧键拒绝测试。
  - [x] 已补充共享导出契约、格式能力矩阵、结构化错误与生命周期说明，以及 C#、Lua、Python 加班明细导出示例；用户视角问题及修复记录见 [`ScriptExportApiReview.md`](ScriptExportApiReview.md)。
  - [x] 已修复导出 API 审查中的剩余问题：非法值返回结构化非重试错误，重复列名和聚合错误在共享层拒绝，模板诊断保留绑定键，样式/合计标签实际生效，不支持的字段和格式选项明确拒绝。
  - [x] 已将通用 XLSX、CSV、DOCX 处理器抽取为独立导出插件并统一由格式注册表调度；XLSX 保留后台生成，插件不得获得目录选择、FileId、UI 或数据库权限。重复格式 ID 和模板扩展名会拒绝注册，应用输出目录的真实 DLL 扫描已有回归测试。
  - [x] 已补充导出插件启动扫描、逐插件加载成功/失败、格式处理器注册及最终汇总日志；底层程序集加载异常会携带路径回传宿主日志，并有异常回调和格式注册日志回归测试。
  - [x] 脚本管理页面首次进入已延迟 API Reference 解析，并将脚本、诊断、历史和运行日志列表刷新合并为批量 Reset 通知；移除与首次目录诊断重复的启动诊断页签，启动加载过程保留在应用日志中。
  - [x] 增加数据模板注册表和管理页面：模板文件由宿主管理，按扩展名选择唯一模板插件；宿主按 `plugin_id.template_name` 生成并校验 `template_id`，插件负责模板校验、导出数据 schema 和渲染；模板限制为 20 MiB、最多 2048 个压缩包条目和 100 MiB 解压总量，OpenXML 模板统一拒绝外部关系、宏和嵌入对象；支持导入校验、启用/禁用、重新校验、版本查看和归档，脚本只通过 `exports.templates.list` 获取可用模板。
  - [x] 增加简易标记模板协议：三种格式统一支持 `{{变量名}}`、`{{items.字段}}`、`{{items.字段|column}}` 和 `{{items|matrix}}`，模板名称从文件名推断、版本固定为 `1.0.0`，不再要求元数据头或隐藏元数据工作表；矩阵前后的固定模板内容会保留，加载失败的模板不会进入可用目录。
  - [x] 增加独立 Mustache 纯文本导出插件：导入 `.mustache` 模板，支持变量、点号路径、列表/布尔区块、反向区块、当前值、注释和转义控制；`values` 与 `tables` 转换为标准 Mustache 上下文，输出支持 `.txt`、`.md`、`.html` 和 `.csv`，暂不支持局部模板、Lambda 和自定义分隔符。

- [x] 标签编辑器已重排为标签导航和右侧详情页签；Tracker 自动化操作独立归入自动化页签，附加字段定义通过二级对话框编辑并由主页面统一保存。
新建脚本向导已提供周目标脚本模板，生成的 metadata 自动声明 Week 目标。

### 9.6 Lua 和 Python 引擎

Worker 契约设计：[`ScriptWorkerDesign.md`](ScriptWorkerDesign.md)

- [~] Windows/Linux CI 已固定 Python 3.10 并强制执行 C#、Lua、Python 真实 Worker 启动、握手、心跳、执行、取消、超时、日志和 Effects 用例；测试工件、dotnet、Python 与 Worker apphost 定位已平台无关，不再因 Windows 环境静默跳过。native/runtime 发布包 Smoke Test 仍待完成。macOS 不在当前产品支持、发布和验证范围内。
- [x] Python Worker 的安全内置函数白名单已加入 `next`，并补充真实 Worker 执行回归测试与 API 文档说明。

### 9.9 Worker 落地

设计文档：[`ScriptWorkerDesign.md`](ScriptWorkerDesign.md)

目标：通过常驻 worker 隔离脚本崩溃、超时和资源问题，同时复用统一宿主 API。

- [x] 已修正自动化调度按 `DateTimeOffset` 自身偏移计算日期，避免依赖 CI Runner 本地时区；Worker 集成测试根据当前测试输出配置定位 Debug/Release 工件；Windows Worker 环境白名单保留 `SYSTEMROOT`，修复 Python 3.10 在 CI 中初始化随机源失败并于握手前退出。 Windows Headless 脚本编辑器测试清理临时目录时对短暂文件占用进行有限重试，避免已完成断言后因系统释放延迟产生误报。

- [x] 已实现显式 Worker 隔离策略：C#、Lua 使用共享 worker，Python 使用每请求独立 worker；高风险脚本可在运行时注册层选择 `Dedicated`。
- [x] 已评估查询流架构：当前保留分页式 Worker HostCall；reader/chunk 仅在跨 provider 异步契约和性能基准证明分页物化为瓶颈后再引入新协议。
- [x] 已接线 Worker 心跳与超时：App 为三个 supervisor 显式开启心跳（30s 间隔/15s 超时，默认关闭；仅 `Ready` 且抢到执行门时 ping）；启动/握手超时（默认 10s）→`Failed`+`WORKER_HANDSHAKE_TIMED_OUT`；宿主调用响应超时（默认 30s）→`Failed`+停止进程+`WORKER_HOST_CALL_TIMED_OUT`（视为 worker 故障不重试）；应用退出通过 `StopAllAsync` 优雅停 worker，修复孤儿进程。
- [~] `ProcessWorkerTransportTests` 的 15 个核心真实进程用例已在 Windows/Linux 共用实现，CI 通过 `DIARY_REQUIRE_PYTHON_TESTS=1` 禁止 Python 测试静默跳过；工作集/输出超限真实进程集成测试及 Windows/Linux 发布包运行时 Smoke Test 仍待完成。macOS 不纳入本阶段验证。

验收：脚本 worker 崩溃、协议失步、超时或被强制终止时，主程序和其他语言 worker 继续运行；
脚本执行历史可以关联 worker ID、请求 ID 和执行 ID；只读宿主调用跨 C#、Lua、Python 使用一致协议。

## 阶段 10：用户体验优化

用户角度审查和完整建议：[`UserExperienceOptimization.md`](UserExperienceOptimization.md)

目标：在不破坏本地追加式工作记录和 Worker 隔离边界的前提下，优先改善保存状态、远程同步确认、失败恢复、日常录入效率和 Tracker 可发现性。

- [~] 第一阶段：已在工作记录列表和编辑区显示本地保存与上传摘要状态，并明确工时同步状态和失败恢复提示；Jira/Redmine 已持久化逐 Tracker 最近一次状态、错误和尝试时间；批量同步已增加预览、逐条选择、确认、逐条结果和仅重试已确认失败项；远程 ID/尝试时间已在编辑器“最近一次同步结果”区展示；剩余待补充为批量同步预览的 Tracker 实例筛选和结果不确定项查询入口。
- [~] 第一阶段：已允许本地删除并按是否存在已上传 Tracker 绑定给出确认提示，并能识别上传结果不确定状态；待补充状态不确定时的查询/重试和 Tracker 专用后续处理入口。
- [~] 第一阶段：数据库不可用时已提供重试连接、打开设置和导出诊断日志；程序设置已提供“打开当前日志”，按最后写入时间定位滚动日志并调用系统默认程序；Tracker 诊断页已提供实例级重试和诊断导出；远程失败已接入批量仅重试失败项流程。远程错误分类和一键打开具体 Tracker 配置仍待补充。
- [x] 应用控制台和滚动日志统一输出托管线程 ID，便于区分 UI 线程、线程池线程和后台任务日志。
- [x] tag 发布的 Windows x64 自动打包同时产出普通包和附带官方 Python 3.13.15 embedded runtime 的包；内置运行时位于 `python/` 目录，普通包继续使用用户配置或系统 Python；Linux 开发机可通过 `Tools/package-win-x64-with-python.sh` 本地生成同布局的带 Python 验证包；本地和 Tag CI 均按版本及 SHA-256 缓存 Python 压缩包。
- [x] 本地 Windows x64 带 Python 打包脚本支持通过 `--upload-filecodebox` 将生成的 ZIP 上传到局域网 FileCodeBox，并以 3 小时有效期输出取件码；默认不上传，上传失败保留本地产物。
- [x] 本地、Tag 和手动发布保留 `runtimes/<目标 RID>/` 与 RID 无关的 `runtimes/any/`，只移除其他平台目录；最终 Windows/Linux ZIP 均要求目标 RID 和 `any` 运行时存在并拒绝其他 RID，避免误删依赖清单仍引用的原生及托管资产。
- [x] Python 语法检查和正式 Worker 在 `-I` 隔离模式前使用 `-X utf8` 显式启用 UTF-8，并统一使用 UTF-8 无 BOM 标准输入输出，避免隔离模式忽略环境变量后由 Windows 本地代码页破坏中文脚本；tag 发布矩阵使用 Python 3.13 验证，主线 CI 继续保留 Python 3.10 最低版本覆盖。
- [~] 第二阶段：已增加复制昨天、复制最近一条和复制整天（支持选择源日期，执行前显示来源、条数/耗时和目标日期并要求确认），以及 15/30 分钟及 1/2/4 小时快捷工时输入和 `30m`、`1h30m`、`1小时30分钟` 等自然时间表达式；新建事项优先展示当天最近使用标签，最近项目已持久化；事件记录页在日期加载、复制新增和每次保存后按优先级升序、ID 升序重排当日工作项并保持当前选择；查询已增加今天/昨天/本周/本月快捷条件、耗时合计、按日期/主标签紧凑汇总、汇总复制和 CSV/Markdown 导出，查询页顶部已按保存查询、筛选条件和结果操作分层重排，筛选条件支持折叠；统计页已重排日期控制、自定义工时、工时分布和标签明细区域。按项目分组汇总和同步状态快捷筛选仍待补充（同步状态快捷筛选明确不在当前阶段实施）。
- [~] 第三阶段：已增加首次成功启动引导、“以后不再显示”选项、设置页重新打开入口和“显示开发者功能”开关，普通用户默认隐藏脚本管理页；Tracker 动态导航继续按已启用实例构建。调查功能已区分调查者和受访者角色：调查者显示调查页、监听固定端口 9721，并通过 localhost 作为受访者接收自己的调查结果；普通受访者填写调查者 IP 后不显示调查页，但启动时独立初始化协议处理器，可继续响应 DiaryToolpp 的 9721 旧协议调查。脚本执行历史与执行进度已实现（会话内存态，重启即失：历史保留最近 30 条，支持状态/来源筛选、清空和复制日志；进度保留最近 20 次执行、每次最多 50 条时间线，管理页底部运行栏显示进度条与文本，历史条目日志追加“进度：”时间线）；执行历史持久化经用户决策明确延期（保持内存 30 条）。剩余待补充为试运行（DryRun）、副作用预览，以及执行历史的关联 worker/request/execution ID。
- [x] 自动化脚本已实现 Scheduled、Startup、WorkItemCreated、WorkItemSaved 与 TagAdded 触发：metadata/manifest 支持 `Schedule`、`RunOnStartup` 和事件 `Triggers`，事件型自动化可不配置 schedule；调度器按定时/启动或 `scriptId + trigger + eventId` 防重，生成对应幂等键；工作项创建、保存和标签添加入口已接入，草稿标签在首次保存后按顺序补发；三语言 context 提供 `automation`（trigger/eventData/idempotencyKey），新建向导和管理页可配置三种事件触发。
- [x] Query 入口已落地：ScriptBase 提供 `IQueryScriptV1` 与 `QueryScript` 抽象基类（Scope=Application、EntryKind=Query），`ScriptProgramAdapter` 与 C# 引擎类型识别已支持；创建向导提供「查询脚本」模板（Lua/Python 使用 `query_main`、C# 使用 `QueryScript` 子类），管理页可直接运行。
- [x] 右上角设置按钮已改为分组下拉菜单：程序设置独立置顶，标签/模板相关入口归为内容配置，Tracker 设置单独归为外部服务，脚本扩展导入单独归为扩展管理，避免从程序设置页面层层进入二级设置。
- [~] 调查协议与使用体验：已保留 DiaryToolpp 兼容的 v1/9721 日期查询，并增加新版 v2/9722 自定义统计查询（关键词、标签、标签模式和优先级）；调查页显式选择兼容/扩展模式，v1 模式隐藏扩展条件卡片，避免填写字段时隐式切换协议；已增加 v2 能力发现、标签/日期/优先级分组、最多 500 条结果明细、节点数量/查询错误状态、随发布包携带并可从页面打开的用户指南；页面已重排为全宽查询配置、扩展条件和结果区分层的卡片布局，查询配置压缩为三行，节点能力在首页仅保留探测摘要并通过独立对话框查看详情，不再占用或挤压结果区，设置页 Expander 样式已隔离到 `.Settings`。能力发现缓存、分页明细、节点级错误身份和更多分组维度仍待补充。

验收：用户能够明确区分本地已保存、待同步、已同步和同步失败；未上传或误写记录可以直接删除；已上传或状态不确定记录的远程影响对用户透明；批量同步产生的副作用在执行前可预览、执行后可追踪；日常记录和耗时汇总不需要理解插件或 Worker 的内部实现。

## 阶段 11：维护清单与发布流程

目标：清理历史遗留、收紧 Worker 协议、统一三语言输出语义，并建立可持续的发布流程。

- [x] 已删除遗留 `IScriptApi`/`IScriptEngine`/`IScript`/`ScriptUsage`/`ITrackerScriptApi` 接口族与 `LegacyScriptAdapters` 适配层（删除前确认无实现类、无 DI、无测试引用）；`Docs/ScriptSystemDesign.md` 同步改写为仅 V1 接口现状。
- [x] Worker 协议已收紧：握手协商结果（`maxMessageBytes`/`maxResultMessageBytes`/`apiVersion`）三语言 Worker 全面生效（C#/Lua Worker 读循环与写路由、Python 模块级上限均使用协商值；`apiVersion` 传入 C#/Lua 构建请求）；新增 `WORKER_INVALID_MESSAGE`、`WORKER_HOST_CALL_TOO_LARGE` 诊断码并区分消息超限与格式错误（`WorkerMessageTooLargeException`/`WorkerInvalidMessageException`）；执行结果按 16MB 上限单独读取，`WORKER_RESULT_TOO_LARGE` 由不可达变为可达（Worker 侧结果写超限回退为同码失败结果）；补充 4MB/16MB/1MB 消息大小层级注释；协议不匹配诊断附带期望/实际值。
- [x] print 输出语义已统一：C# `Console`、Lua `print`、Python `print` 按行转发到脚本日志 Info 级（管理页「运行日志」Tab 可见），执行结束冲刷残余半行，总量 1MB 兜底，非执行期输出丢弃；每条打印计入宿主调用次数上限，文档（CSharp.md §10 / Lua.md §7 / Python.md §7）已同步。
- [x] Effects 三语言透传与 UI 展示已落地：Lua/Python 入口返回 create 结果表即透传 `effects` 字段；管理页执行历史条目（`EffectsSummary`）与完成通知显示追加条数/预览/幂等重放/新建 ID；`AutomationDailyCheck` 三语言示例改为返回 create 结果并更新说明。
- [x] LuaWorker 引导脚本（沙箱 + API 门面 + 分页流 + 上下文装配）已外置为嵌入资源 `lua-bootstrap.lua`，与 Python `worker.py` 同构；`LuaEngine` 构建期沙箱保持不变。
- [x] 发布流程已建立：新增 `Docs/CHANGELOG.md`（`## 版本号` 章节格式），README 补充版本策略（`1.0.0-r{CommitCount}` 含义）与 CHANGELOG 链接；`release-on-tags.yml` 的 Release body 改为从 CHANGELOG 提取对应版本章节（缺失时回退固定文案），并补充 checkout 步骤；新增 `AgentReleaseTagGuide.md`，固化 Agent 的权限边界、提交计数对齐、Tag 推送、CI 跟踪和失败处理流程。
- [x] Tag 与手动发布已将各 RID 的 PDB 从普通运行包中拆出，按原相对目录生成独立 `-dbg.zip`；CI 同时校验普通包不含 PDB、调试符号包非空且只包含 PDB。
