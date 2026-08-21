# DiaryApp 已完成工作记录

本文记录已经完成的项目，作为 Docs/TODOS.md 的历史归档。当前未完成、进行中和后续计划仍以 [`TODOS.md`](TODOS.md) 为准。

本文件只记录完成结果，不作为当前待办列表。

## 2026-08-21 TODO 清理归档

- [x] 日志编辑可靠性与 Redmine 真实联调：修复 `新建 -> 修改 -> 新建`、复制最近/整日复制前的自动持久化，修复未启用 Tracker 阻断配置保存、Redmine Issue 导入后不刷新和响应正文泄露凭据；在隔离 profile 中完成 admin 用户、项目/Issue/活动管理、创建测试 Issue、0.25 小时同步、远程回读和防重复提交验证。

- [x] Windows Debug UI 自动化基础：集成显式启用的 Avalonia CDP、独立配置/数据库/profile 和单实例身份，提供 `start/smoke/status/stop` 工具，真实覆盖首次引导、主导航、程序设置、主题、新建事项、输入、本地保存、截图和响应时间采样；Release 保持不含 CDP。

- [x] 工作项附加字段编辑体验：9 种字段类型分别使用单行/多行文本、数值、三态布尔、日期、时间、日期时间和选项控件；保留空值、规范字符串格式、日期时间偏移和只读边界，并补充类型映射与转换测试。

- [x] Windows standard local 真实升级门禁：新增 PowerShell 原生服务/打包/发布入口和无 GitHub 轮询的 `serve-local` 模式；修复 Windows `Diary.Updater.exe` 被错误要求 Unix executable 标记的事务校验矛盾；使用两个独立 local sequence 完成 `UpdateAvailable -> ReadyToApply -> Restarted -> Confirmed`，并校验 installed manifest 与目标 `Diary.App.dll` SHA-256。
- [x] 应用完整包更新闭环：主程序完成检查、用户确认、流式下载、长度与 SHA-256 校验、安全解压、逐文件复检、事务计划、外部 Updater 应用、正常退出、新版本重启、稳定性确认、用户确认式程序文件回滚和事务清理；Python 局域网服务、发布资产契约及双 RID Updater 发布验证同步完成。
- [x] 核心数据库迁移加固：SQLite/PostgreSQL 支持逐步提交、失败回滚、降级和断链校验；provider 能力、结构指纹、迁移状态/历史、基础数据完整性和迁移登记测试已经建立。
- [x] 数据库维护基础：SQLite 支持手动备份、校验、下次启动还原及失败回滚；PostgreSQL 接入 `pg_dump`/`pg_restore`、跨平台工具探测、版本与权限预检、独立目标库还原、启动复检和配置切换。
- [x] 异步生命周期：Survey 接收与停止流程、应用退出清理、菜单重启、脚本目录和编辑器异步入口已收敛；`WorkEditorViewModel.Upload()` 的 UI 回写和 Headless 回归测试完成。
- [x] CrashDump 基础：终止性托管异常由独立 DiagnosticsClient 进程生成 Triage Dump，并由最小 Avalonia 窗口显示摘要、路径和打开目录操作；本地保留最近 5 个。
- [x] Jira 最小工时闭环：插件 manifest、多实例配置、项目/Issue 查询、连接测试、Worklog 追加、SQLite/PostgreSQL 本地绑定和编辑器扩展已完成。
- [x] PLM 被确认为必须保留的目标后端，并已预留插件边界和最小工时契约。
- [x] 脚本配置模型已移除遗留 `Enabled` 字段；可用性统一由目录加载和构建结果决定。
- [x] 脚本日志项创建支持普通/模板 Preview、provider 事务、失败回滚、幂等重放和成功后编辑器刷新；Query 与 Preview 的副作用由宿主强制限制。
- [x] 三语言脚本 API 已统一日期快捷值、C# `context.Api()` 推荐入口、Python/Lua snake_case 门面、运行参数、幂等键、超时和 Preview 对话框。
- [x] `.diaryscripts` 共享包支持脚本源码和运行配置的安全导入导出、冲突处理、批量失败恢复和目录重载。
- [x] Week 编辑器目标、主标签语义字段、标签元数据、标签附加字段及 `.diarytags` 导入导出已经完成。
- [x] 脚本导出体系已完成 XLSX、CSV、DOCX、Mustache 插件、格式注册表、目录/FileId 生命周期、数据模板管理、简易标记协议、安全校验、三语言门面和基础测试。
- [x] Worker 心跳、握手/宿主调用超时、取消、日志、Effects、进度跟踪、自动化 Scheduled/Startup/WorkItemCreated/WorkItemSaved/TagAdded 触发及 Query 入口已经落地。
- [x] Windows/Linux 真实 Worker 测试已统一工件、dotnet 和 Python 定位；CI 固定 Python 最低版本并禁止必需测试静默跳过。
- [x] 用户体验基础已完成：同步摘要和批量预览/重试、数据库与 Tracker 诊断入口、复制记录、快捷工时、自然时间表达式、最近标签/项目、查询汇总导出、首次引导和开发者功能开关。
- [x] Survey 已保留 v1/9721 兼容并增加 v2/9722 扩展查询、能力发现、分组、结果明细、角色区分、页面重排和用户指南。
- [x] 发布与打包已完成 Python 3.13 Windows 可选包、本地交叉打包、FileCodeBox 上传、Python 缓存、目标 RID 与 `runtimes/any` 保留、Node.js 24 Actions、PDB 独立调试包和 Release CHANGELOG 提取。
- [x] 维护清单已完成遗留脚本接口删除、Worker 协议大小协商和诊断收紧、三语言 print/Effects 统一、Lua bootstrap 外置及持续发布流程建立。

## 当前基线

- [x] `Diary.PluginBase` 插件契约、manifest、兼容性检查
- [x] 插件程序集发现和 `PluginHost` 注册
- [x] 插件实例注册表和 `(PluginId, InstanceId)` 身份校验
- [x] `Diary.PluginUI` 配置、管理页、编辑器扩展契约
- [x] SQLite/PostgreSQL Redmine 数据库扩展
- [x] 插件数据库版本表和 schema 迁移（数据库 schema 0 -> 1，配置 schema 0 -> 1 -> 2）
- [x] Redmine 数据表使用 `instance_id` 隔离
- [x] Redmine 配置实例列表和启用状态
- [x] 当前架构文档与组件、生命周期、数据库扩展图

## 本轮已完成

- [x] Windows/Linux CrashDump：终止性托管异常启动独立 DiagnosticsClient 捕获进程生成 Triage Dump，再由隔离的最小 Avalonia 窗口显示简要信息并提供打开 Dump 文件夹操作；默认本地保留最近 5 个，补充真实进程测试和设计文档
- [x] 脚本日志项写入：普通日志项创建补充 provider 事务并在失败时回滚；普通和模板创建的 Preview 在数据库访问前返回投影结果，不修改数据库或幂等存储；新增真实 SQLite、回滚和 Preview 回归测试
- [x] 维护清单：删除遗留 `IScriptApi`/`IScriptEngine`/`IScript`/`ScriptUsage`/`ITrackerScriptApi` 接口族与 `LegacyScriptAdapters` 适配层，`Docs/ScriptSystemDesign.md` 同步改写为仅 V1 接口现状
- [x] 维护清单：Worker 协议收紧——三语言 Worker 遵守握手协商（消息上限/结果上限/ApiVersion）；新增 `WORKER_INVALID_MESSAGE`、`WORKER_HOST_CALL_TOO_LARGE` 诊断码与 `WorkerMessageTooLargeException`/`WorkerInvalidMessageException` 异常类型；`WORKER_RESULT_TOO_LARGE` 可达；4MB/16MB/1MB 大小层级注释；协议不匹配诊断附带期望/实际值
- [x] 维护清单：print 语义统一——C# `Console`/Lua `print`/Python `print` 按行转发到脚本日志 Info 级（运行日志 Tab 可见），1MB 总量兜底，文档同步
- [x] 维护清单：Effects 三语言透传与 UI 展示——Lua/Python 入口返回 create 结果表即透传 `effects`；管理页执行历史与完成通知显示追加条数/预览/幂等重放/新建 ID；`AutomationDailyCheck` 示例改为返回 create 结果
- [x] 维护清单：LuaWorker 引导脚本外置为嵌入资源 `lua-bootstrap.lua`（沙箱 + API 门面 + 分页流 + 上下文装配），与 Python `worker.py` 同构
- [x] 维护清单：发布流程——新增 `Docs/CHANGELOG.md`（`## 版本号` 章节格式，含未发布 1.0.0-r420 与历史 r112 条目）；README 补充版本策略与 CHANGELOG 链接；`release-on-tags.yml` Release body 改为从 CHANGELOG 提取对应版本章节
- [x] 脚本管理页 metadata 设置区（名称/描述/调度/启动补跑）与创建向导调度配置
- [x] 自动化/查询脚本示例（AutomationDailyCheck、QueryMonthlySummary 三语言 + 说明文档）与 C# 示例编译锁定测试
- [x] C# 脚本基础库白名单扩充（LINQ、正则、System.Text.Json 等 10 个纯计算/数据处理程序集）

- [x] 审阅 `DiaryToolpp` SQLite/PostgreSQL 5.0.0 数据结构；迁移仅导入统计所需核心数据，不创建 Tracker 信息，并将导入工作项持久化为只读，同时补充事务、字段、颜色和只读约束回归测试

- [x] 旧 SQLite schema 缺少 `instance_id` 但版本号为 2 的恢复测试（该测试已在 c0c933d（2026-08-05 重写初始数据格式）中删除；当前生产恢复分支 SQLiteRedMineDb.cs:46-53 将旧库直接视为版本 1 处理）
- [x] Redmine 初始化幂等测试
- [x] Redmine 插件 ID 和默认实例 ID 常量化
- [x] 实例注册协调器和成功/失败日志
- [x] 内存 tracker 实例注册测试
- [x] `TrackerKey` 统一扩展身份、批量绑定和模板匹配
- [x] 按 `TrackerKey` 执行扩展克隆，避免依赖集合顺序
- [x] 在编辑器保留按实例的上传结果状态
- [x] UI 贡献工厂和实例贡献注册表
- [x] Redmine API、数据库扩展和编辑器扩展绑定具体实例
- [x] Redmine 管理页及子页面使用当前实例的 API、缓存和数据库扩展
- [x] 模板 contributor 工厂和按实例注册（该机制已按计划撤销，2853480 删除 TrackerTemplateContributorRegistry.cs）
- [x] 宿主遍历插件生成实例配置，移除 Redmine 实例注册硬编码
- [x] 插件 UI 程序集改为通用扫描，缺失 UI 不阻断核心启动
- [x] 编辑器扩展集合和多 tracker 状态聚合基础
- [x] 工作项本地保存事务和远程上传协调
- [x] 通用插件配置加载器和宿主上下文传递测试
- [x] 无 tracker 时核心编辑器和模板路径测试
- [x] 无 tracker 时插件实例、UI 和模板生命周期测试
- [x] 提供 `--core-only` 启动模式跳过 tracker 插件加载
- [x] 插件 UI 缺失时安全跳过测试
- [x] 定义 `TrackerInstanceState` 实例状态模型与失败条目存储
- [x] DB 扩展初始化/迁移失败显式抛 `PluginExtensionInitException`，不再静默返回 null
- [x] coordinator 按 `Enabled`/非 `Enabled` 路由，迁移失败只禁用当前实例
- [x] 迁移失败重试管线（`Registry.Clear` + `DbInterfaceBase.InvalidateExtensions` + `Coordinator.Retry`）与迁移错误细节透传
- [x] 必选依赖存在性校验（`PluginCompatibilityContext.AvailablePluginIds` + validator）与 App 两阶段注册
- [x] 依赖版本范围匹配与必选依赖环检测，阻止不兼容或循环依赖插件注册
- [x] 通用实例配置存储接口与插件实例生命周期协调器

## 阶段 1：通用实例生命周期

目标：主程序不再硬编码只创建 Redmine 实例。

- [x] 定义通用实例配置存储接口，返回所有已配置插件实例并由宿主筛选启用项
- [x] 将 `App.RegisterTrackerInstances()` 改为遍历插件和配置实例
- [x] 将实例创建、数据库初始化、迁移和 UI/模板注册纳入统一生命周期
- [x] 创建实例时按 `InstanceId` 获取对应数据库扩展，禁止所有实例共享默认扩展
- [x] 让数据库扩展工厂接收插件迁移链并统一使用 `PluginMigrationRunner`
- [x] 移除 Redmine provider 的无参数迁移兼容入口
- [x] 明确实例状态：未配置、已启用、已禁用、迁移失败、连接失败
- [x] 迁移失败时只禁用当前插件/实例，不影响核心日记
- [x] 将 `SupportsMultipleInstances` 接入实际配置、导航和编辑器流程

验收：新增一个测试 tracker 后，主程序无需增加 tracker 专用分支即可创建和显示其实例。

## 阶段 2：核心编辑器多 tracker

目标：一个工作项可以同时拥有多个 tracker 扩展。

设计文档：[`MultiTrackerEditorDesign.md`](MultiTrackerEditorDesign.md)

- [x] 将编辑器中的单一 tracker 状态改为扩展集合
- [x] 聚合所有扩展的加载、保存、克隆、锁定和删除权限
- [x] 为每个实例显示独立的本地保存和远程上传状态
- [x] 工作项编辑器使用按实例 Tab 展示多个 Tracker 扩展
- [x] Tracker 设置重注册后刷新已有日记编辑器的 Tab 标题
- [x] 本地工作项与所有 tracker 绑定使用同一个本地事务
- [x] 远程上传移出本地事务，支持单实例失败和重试
- [x] 删除所有 `FirstOrDefault()` 单 tracker 选择逻辑

验收：Redmine 公司实例和测试 tracker 可以同时编辑、保存、克隆和上传。

## 阶段 3：模板字段与 Tracker 规则

- [x] 模板只保存核心字段：UUID、名称、标题、工时和默认标签
- [x] 移除模板承载 Tracker 扩展数据的能力
- [x] Tracker 配置和插件状态整合到独立模态对话框，不占用常规设置页面
- [x] 设置页面和 Tracker 配置均从右上角独立模态按钮打开，配置刷新仅重建固定导航页之后的 Tracker 动态页
- [x] 脚本源码编辑窗口设置主窗口为父窗口并以模态方式打开
- [x] 维护 GitHub Actions，统一 .NET SDK、增加格式检查和核心测试，并更新发布 Action
- [x] Tracker 活动、问题等默认值统一由标签规则推导

验收：模板编辑页面不出现 Tracker 专属字段，模板应用只添加核心字段和默认标签。

## 阶段 4：移除 Redmine 核心耦合

- [x] 将 `IRedMineDb` 和 Redmine 数据模型收敛到 Redmine 插件边界
- [x] 移除 `Diary.App` 对 `RedMineConfigurationStore` 等具体类型的直接依赖
- [x] 移除启动时对默认 `IRedMineUiData` 的预初始化，统一由实例生命周期创建
- [x] 将数据库扩展扫描从 `Diary.RedMine.*.dll` 改为通用插件能力发现
- [x] 核心 UI 不引用 Redmine ViewModel、配置或远程模型
- [x] 插件缺失时核心数据库、编辑器和模板可运行，并覆盖 core-only Headless 主窗口启动验收

验收：移除 Redmine 程序集后，核心日记可以完整启动和使用。

## 阶段 5：配置、诊断和卸载
 
- [x] 主程序统一创建、加载并向插件实例注册传入配置
- [x] 通用插件配置 schema 迁移（配置包、迁移链、原文件保护和 Redmine 单实例升级）
- [x] API Key 等敏感字段的存储、遮罩和更新策略（配置文件加密、UI 密码遮罩和显式编辑）
- [x] 插件管理/诊断页面（实例状态、错误详情、迁移重试和启用/禁用已接入）
- [x] 迁移失败重试、日志详情和导出（日志导出为 ZIP，保留原始日志文件）
- [x] 禁用插件时保留配置和数据
- [x] 只有用户明确确认时才删除插件数据（卸载默认禁用并保留配置/数据）
- [x] tracker 实例名称和左侧导航图标配置入口，非法图标键回退默认图标

验收：用户可以查看插件状态、重试失败迁移，并在不删除核心数据的情况下禁用或移除插件。

## 阶段 6：测试与质量门槛

- [x] 插件缺失、版本不兼容、依赖缺失/版本不符、依赖环和能力缺失测试
- [x] SQLite/PostgreSQL 插件迁移幂等测试
- [x] 错误 schema 版本号但缺少列的恢复测试
- [x] 多实例数据隔离测试
- [x] 多实例数据库扩展身份与实例注册身份一致性测试
- [x] 多 tracker 本地事务和远程失败测试
- [x] 模板只保存核心字段和默认标签，不再保存 Tracker payload
- [x] 外部 Redmine API 测试与本地契约测试分离（外部测试需显式设置 `DIARY_RUN_REDMINE_EXTERNAL_TESTS=1`）

## 阶段 7：自定义工作项查询

设计文档：[`WorkItemQueryDesign.md`](WorkItemQueryDesign.md)

目标：提供统一的工作项查询能力，支持按时间范围、标签和其他核心字段筛选，
并为统计页面、脚本只读 API 和后续保存查询功能提供基础。

设计原则：

- 不继续扩展 `GetWorkItemsByTagAndDate(dateBegin, dateEnd, l1, l2)` 的固定参数。
- 使用结构化 `WorkItemQuery` 表达查询条件。
- 标签匹配语义必须明确区分“忽略标签”“任意标签”“全部标签”“无标签”和“精确匹配”。
- 查询只读取核心工作项和标签数据，不允许通过查询接口修改工作项、标签或模板。
- 查询结果必须使用稳定排序，并保持 SQLite/PostgreSQL 行为一致。

### 7.1 查询模型和数据库接口

- [x] 定义 `WorkItemQuery`，包含开始日期、结束日期、标签 ID 集合、标签匹配方式、关键字、优先级、分页参数。
- [x] 定义 `WorkItemTagFilter`，支持 `Ignore`、`Any`、`All`、`None`、`Exact`。
- [x] 为 `DbInterfaceBase` 增加统一的 `QueryWorkItems(WorkItemQuery query)` 抽象接口。
- [x] 在 SQLite provider 实现日期、标签、关键字、优先级和分页查询。
- [x] 在 PostgreSQL provider 实现与 SQLite 等价的查询语义和参数绑定。
- [x] 统计调用已迁移到新接口，旧接口暂时保留作为兼容入口。
- [x] 查询结果使用日期和工作项 ID 的稳定排序，避免多标签 JOIN 造成重复工作项。
- [x] 为空条件定义明确语义：无标签条件不能与忽略标签条件混淆。

### 7.2 数据库契约测试

- [x] 覆盖日期、关键字、优先级、标签匹配和分页组合条件，并验证结果不重复、空结果稳定。
- [x] SQLite 和 PostgreSQL 对同一查询模型保持一致语义，用户输入全部使用 provider 参数绑定。

### 7.3 查询 UI 和保存查询

- [x] 提供自定义查询页面、日期快捷选择、标签/关键字/优先级筛选、结果定位和可理解的失败提示。
- [x] 支持保存、编辑、重命名和删除查询条件；保存内容不包含执行结果或敏感 Tracker 数据。

### 7.4 脚本和统计复用

- [x] 统计页面迁移到统一查询接口，避免继续维护独立标签查询 SQL。
- [x] 为脚本 API 提供只读 `QueryWorkItems` 能力。
- [x] 脚本查询 API 只能读取宿主允许的工作项数据，不能修改模板。
- [x] 脚本查询结果遵循相同的日期、标签匹配和排序语义。
- [x] 增加查询 API 的权限、异常和敏感字段测试。

验收：用户可以查询指定时间段内具有任意或全部指定标签的工作项，结果不重复且跨 SQLite/PostgreSQL 一致；
统计页面和脚本只读接口可以复用同一查询模型，查询过程不会修改工作项、标签或模板。
## 阶段 7.1：Survey 异步生命周期（已完成条目）

- [x] `Diary.Survey` 接收循环使用取消令牌，消息处理任务可等待并观察异常；`AppSurveyor.StopServerAsync()` 和 `AppRespondent.ShutdownAsync()` 完成异步资源释放。
- [x] 应用调查配置重载、调查问题发送和退出清理均等待 Survey 生命周期任务，不在 UI 线程同步阻塞。
- [x] Survey 快速启动/停止、重复关闭和抛出异常的消息订阅者回归测试通过。

## 阶段 8：常见 Tracker 后端扩展（已完成条目）

### 8.6 通用 Tracker 能力补强
- [x] 为 Tracker 脚本 API 增加 `PluginId + InstanceId` 只读实例目录入口。

## 阶段 9：脚本系统落地（已完成条目）

### 9.1 基础契约和运行时
- [x] 定义版本化 V1 脚本契约，并为旧 `IScript`/`IApplicationScript`/`IEditorScript` 保留兼容适配。
- [x] 定义结构化 `ScriptDiagnostic`、`ScriptBuildResult` 和 `ScriptExecutionResult`。
- [x] 已定义稳定 ID、名称、API 版本、范围和描述，并支持源码旁 metadata/manifest。
- [x] 已定义应用和编辑器范围，以及年、季度、月、周、日和事项六类编辑器目标。
- [x] 编辑器脚本 metadata 支持声明适用的目标类型，旧脚本未声明时兼容为全部目标。
- [x] 已定义 `ScriptDateRange`、`ScriptWorkItem` 快照、日期范围快捷读取和范围事项迭代 API。
- [x] 保留 `ExecuteDay`/`ExecuteRange` 兼容适配，新脚本使用上下文式执行入口。
- [x] 定义并实现最小 `IScriptManager`、`ScriptCatalog`、`ScriptBuildService` 和 `ScriptExecutor` 职责边界。
- [x] 实现脚本目录扫描、扩展名匹配、元数据读取和按加载结果管理可执行状态。
- [x] 确保单个脚本发现或构建失败不会阻断其他脚本和核心启动。

### 9.2 C# 脚本引擎
- [x] 使用 Roslyn 实现 `Diary.Script.CSharp.CSharpEngine.BuildAsync()`。
- [x] 支持应用脚本、编辑器脚本和上下文式脚本入口的识别与实例化。
- [x] 将 Roslyn 编译诊断转换为统一诊断，保留文件名、行号、列号和严重级别。
- [x] 使用 collectible `AssemblyLoadContext` 管理脚本程序集，替换和删除时释放旧程序。

### 9.3 执行、取消和权限
- [x] 每次执行已创建独立执行 ID、取消令牌、超时策略、独立上下文、来源和执行耗时。
- [x] 捕获脚本异常并转换为诊断，不让异常传播到应用主循环。
- [x] 实现用户取消和超时处理，并停止等待无法强制终止的脚本任务。
- [x] 移除脚本 capability 权限门禁；Worker 通过握手声明并由宿主 dispatcher 校验实际 HostCall，当前开放查询、受控日志项/模板日志项创建、Tracker 只读实例目录、剪贴板、用户交互和日志。
- [x] 权限拒绝返回结构化结果，不得静默跳过危险操作。
- [x] 执行历史和错误详情已接入 UI，仅在内存保留最近 30 条，支持复制单条脱敏日志，对常见 Token/Password/Secret 字段脱敏。

### 9.4 脚本 API 和宿主能力
- [x] 已将日记、Tracker、系统交互和日志能力分别整合为 `IDiaryApi`、`ITrackerApi`、`SysApi` 和 `ILogApi`；旧细分接口仅保留为宿主内部适配层。
- [x] 已提供年、季度、月、日目标的日期范围校验和按范围迭代 API；统一时区/周期计算宿主 API 待补。
- [x] 已提供跨 C#、Lua、Python Worker 的异步 `LogApi`，日志通过 `log.write` 转发并限制消息大小。
- [x] 提供只读工作项查询 API，复用 `WorkItemQuery` 和统一标签筛选语义。
- [x] Tracker 实例目录 API 使用 `PluginId + InstanceId`，不允许只按插件类型取得隐含默认实例。
- [x] 脚本只能按模板创建新日志项，不修改模板 Tracker 数据，也不提供已有工作项更新/删除 API。
- [x] 为宿主 API 创建内存替身，方便测试脚本逻辑而不启动完整 UI 或真实服务。

### 9.5 缓存、目录和用户体验
- [x] 约定 application、editor 和 cache 脚本目录，并支持源码旁 metadata 与 `manifest.json` 脚本包。
- [x] 编译缓存使用源码、引擎、契约和安全策略版本构成稳定键，支持失效、原子写入和损坏恢复。
- [x] 脚本管理页支持扫描、重载、编译诊断、运行历史、脱敏日志、源码/目录入口和删除确认。
- [x] 按脚本作用域和目标能力提供应用脚本、编辑器脚本的不同入口；加载或构建失败不会进入可执行菜单。
- [x] Worker 握手声明实际 HostCall，宿主统一提供查询、受控日志项创建、Tracker 只读实例目录、剪贴板和用户交互。

### 9.6 Lua 和 Python 引擎
- [x] Lua 和 Python 均通过独立 Worker 执行，不嵌入主进程、不自动安装依赖，并按引擎路由到独立 supervisor。
- [x] Lua 默认关闭文件、网络、进程、动态加载和 CLR 对象暴露；Python 提供解释器发现和运行时缺失诊断。
- [x] 两种语言复用受限 UTF-8 JSON 行协议，覆盖构建、HostCall、取消、超时、协议异常和非零退出诊断。

### 9.7 脚本测试和验收
- [x] 覆盖脚本发现、元数据校验、编译诊断、入口分发、目标校验、缓存和宿主边界。
- [x] 覆盖异常、取消、超时、权限拒绝、运行时缺失、stdout 污染、Worker 故障和跨语言路由。
- [x] 覆盖多实例 Tracker 定位、查询语义一致性和敏感信息不泄漏；测试不依赖真实 Tracker 服务。

### 9.8 脚本 UI/UX 和上下文执行
- [x] 本地脚本默认按用户已接受风险处理，不增加“受信任脚本”状态和首次启用确认。
- [x] C# 危险 API 暂不开放，继续作为宿主边界保护；不将该限制包装成用户授权流程。
- [x] 日历右键菜单提供日、周、月、季度和年目标（周目标含上一周），不提供自定义日期范围。
- [x] 自定义日期范围不再作为编辑器扩展入口；编辑器脚本使用宿主自动注入的目标范围。
- [x] 工作项列表右键菜单面向当前工作项执行脚本。
- [x] 脚本管理页提供列表、概览、诊断、执行历史、运行日志、重载、源码入口和删除确认。
- [x] 只展示已加载且构建成功的可执行脚本，并明确显示加载、构建、执行、取消、超时和 Worker 故障状态。
- [x] 编辑器脚本由日历的日、周、月、季度、年上下文和工作项菜单触发，目标由宿主自动注入。
- [x] 编辑器脚本按日期或工作项上下文运行，使用宿主注入的结构化目标和安全快照；未保存工作项不开放脚本操作，已锁定工作项只允许只读执行。
- [x] 提供 C#、Lua、Python 脚本创建向导、源码模板、内置编辑器和 API Reference；脚本创建校验稳定 ID、文件名和目标目录，并通过原子写入避免产生不完整脚本包。

### 9.9 Worker 落地
- [x] 完成语言无关的 Worker 生命周期、握手、版本/HostCall 协商、UTF-8 JSON 行协议和进程传输设计。
- [x] 支持执行、取消、超时、心跳、空闲回收、进程树终止和资源/输出限制；Worker 终止转换为结构化失败，不自动重试。
- [x] C#、Lua、Python 按引擎路由到独立 supervisor，单个 Worker 故障不会影响其他语言或主程序。
- [x] 只读查询、Tracker 实例目录、日志、剪贴板和用户交互通过统一 HostCall 转发，执行结果可关联 Worker、请求和执行 ID。
- [x] 覆盖跨语言执行、协议异常、取消/超时、运行时缺失、输出污染和进程终止等生命周期验收。

## 阶段 10：标签自动化规则（已完成条目）
- [x] 定义标签实际新增事件，区分用户添加、模板添加、批量添加和重复事项添加来源。
- [x] 将手动添加标签和应用模板添加标签统一接入同一个标签添加服务。
- [x] 加载已有标签、删除标签和重新加载工作项不得触发自动规则。
- [x] 定义 `TagAutomationContext`、`ITagAutomationCoordinator` 和按实例结构化结果。
- [x] 将规则存储在 Tracker 实例配置中，支持一个标签关联多个实例。
- [x] 支持同一 Tracker 实例配置多条规则、启用/禁用和配置顺序。
- [x] 默认使用 `OnlyIfUnset`，用户后续手动修改字段不被规则覆盖。
- [x] 删除标签不反向清除或恢复 Tracker 字段。
- [x] Redmine 实现实例级标签规则，支持标签到 Activity/Issue 默认值映射。
- [x] 规则字段和动作由 Tracker 插件解释，核心未引入 Redmine 专用字段。
- [x] 支持规则按标签添加顺序逐条应用，并基于前一条规则的最新编辑器状态。
- [x] 已实现同字段冲突、无效目标和稳定裁决；禁用实例跳过自动化，状态和原因由插件管理/诊断页展示。
- [x] 在 Tracker 实例设置页提供规则新增、编辑、删除和启用/禁用；没有可用工作标签时明确提示新增条件。
- [x] 工作项和模板中的标签添加入口在没有可用工作标签时自动禁用；标签编辑器仍保留新建标签入口。
- [x] 在核心标签编辑器提供 Tracker 规则扩展入口，按标签查看关联实例规则。
- [x] 两个规则编辑入口共享编辑 ViewModel，避免配置互相覆盖。
- [x] 规则配置支持 schema 迁移、未知字段保留和敏感信息保护。
- [x] 规则应用只修改当前 Tracker 编辑器草稿，不在标签添加时调用远程 API。
- [x] 规则修改后的最终 Tracker 字段随现有工作项本地事务保存。
- [x] 已增加手动添加、模板添加、顺序、多规则、用户覆盖、实例故障隔离和多实例独立应用测试。
- [x] 重复当前事项时重新通过标签添加服务应用 Tracker 默认元数据，并覆盖回归测试。
- [x] C# Worker 接入剪贴板读写、用户通知/确认 HostCall；只读日记继续统一使用 `workItems.query`。
- [x] 移除脚本 capability 枚举、metadata 字段、执行上下文和 Worker 协议中的 capability 参数；旧 metadata 字段自动忽略。
- [x] 增加 C#、Lua、Python 分页式工作项流 API，避免大结果集进入单条 Worker 消息。
- [x] 通过大结果集、多页查询和长字段数据回归测试验证 Worker 查询边界。
- [x] 模板增加稳定 UUID，并在模板管理页面展示只读 ID。
- [x] 增加按模板创建日志项 API，支持日期、模板 ID、工时、可选标题和备注；Tracker 数据由标签规则处理。
- [x] 移除模板中的旧 Tracker 核心字段，Tracker 专属数据统一存储于透明 `Extensions`（`Template.Extensions` 已在 2853480（2026-08-08）移除，Tracker 默认值统一由标签规则处理）。

## 阶段 9.10：脚本 API 用户体验和功能入口优化

完成日期：2026-08-09。设计评审：[`ScriptApiOptimization.md`](ScriptApiOptimization.md)。

目标：在保持所有脚本默认通过 Worker、工作记录追加式的前提下，按功能提供清晰的 Application、Editor、Automation 入口，统一 C#、Lua、Python 的宿主 API 语义，并降低脚本作者的学习和维护成本。

- [x] 定义 `ScriptEntryKind`，完成 Application、Editor、Automation 入口和预留只读 Query 入口；C# SDK 提供对应基类，Lua/Python 使用 `application_main`、`editor_main`、`automation_main`、`query_main`。
- [x] 统一入口上下文、参数、目标快照、取消、进度、预览、幂等和领域 API 外观；C# 提供 `context.Api()` 与 `GetRequiredApi<T>()`，Lua/Python 提供 `context.diary` 领域树。
- [x] 统一日期、标签、优先级、分页、流式查询、模板发现、Tracker 实例发现和 Worker HostCall 能力发现契约。
- [x] 统一 `ScriptApiError`、稳定错误码和三语言错误处理示例，补充成功、失败、取消、超时和 Worker 终止的跨语言对照测试。
- [x] 普通日志项和模板日志项支持 Preview、副作用摘要和宿主共享持久化幂等；幂等结果按 API 作用域隔离并可跨应用重启恢复。
- [x] 明确脚本自动化不提供删除或直接改写历史记录；Tracker 远程写入、历史修正/冲正暂不纳入当前脚本 API。
- [x] 提供 C#、Lua、Python 的“5 分钟入门”和“查询并追加日志项”完整示例，并同步更新三种语言 Reference、系统设计文档和 Worker 设计文档。
- [x] 通过脚本构建、Worker 入口、模板、跨语言错误/取消/超时和幂等存储回归测试。

验收结果：运行时契约、语言文档、创建模板、示例和测试对同一入口/API 语义保持一致；重复执行、预览、取消、超时和 Worker 异常不会产生未声明的脚本副作用。UI 稳定 ID 复制入口属于后续非阻塞 UI 体验增强，不影响 9.10 API 契约完成。

## 阶段 10：用户体验优化（已完成条目）

- [x] Tracker 配置对话框按配置提供者分 Tab 展示，Jira、RedMine 等提供者各自拥有独立配置页；提供者内部继续管理自身多实例配置。
- [x] 日记页提供“复制昨天”“复制最近”和“复制整天”：整天复制支持选择源日期，执行前显示来源、条数/耗时和目标日期并要求确认；复制只带入本地字段和标签，不复用远程 Tracker 绑定。
- [x] 工时编辑提供 15/30 分钟、1/2/4 小时和清零快捷项，并支持 `30m`、`1h30m`、`1小时30分钟` 等自然时间表达式；新建事项标签列表优先展示当天已有记录中最近使用的标签，最近项目已持久化到应用配置。
- [x] 查询页结果摘要显示记录数和耗时合计，并提供按日期、主标签的紧凑汇总；结果可复制汇总文本，也可导出 CSV 或 Markdown，导出字段包含主标签。
- [x] 调查协议在保留 DiaryToolpp 兼容的 v1/9721 日期查询基础上增加 v2/9722 自定义统计查询（关键词、标签、标签模式和优先级），扩展查询只发送到新版节点；已支持 v2 能力发现、标签/日期/优先级分组和最多 500 条结果明细展示。

## 阶段 10 续：脚本 Worker 可靠性、进度、自动化与 Query 入口

- [x] Worker 心跳与启动/握手、宿主调用响应超时已生产接线：App 为三个 supervisor 显式开启心跳（30s 间隔/15s 超时，默认关闭；仅在 `Ready` 且抢到执行门时 ping，杜绝 Pong 被 Busy 执行接收循环截走）；握手超时（默认 10s）→`Failed`+`WORKER_HANDSHAKE_TIMED_OUT`+停 transport；宿主调用超时（默认 30s）→`Failed`+停进程+`WORKER_HOST_CALL_TIMED_OUT`（视为 worker 故障不重试；超时前可能已产生的追加副作用不可回滚，靠幂等键防线）；`CheckHealthAsync` 新增 timeout 参数（默认 5s）；应用退出 `PreShutdownAsync` 调用 `IWorkerScriptExecutor.StopAllAsync()` 优雅停 worker，修复孤儿进程。
- [x] Worker 真实进程测试已统一 Windows/Linux 工件定位：移除核心用例的 Linux-only 跳过，按平台解析 dotnet、App Worker apphost 和 Python 解释器；CI 固定 Python 3.10，并通过 `DIARY_REQUIRE_PYTHON_TESTS=1` 将运行时缺失从跳过提升为失败。
- [x] 执行进度上报接入管理页：新增 `ScriptProgressTracker`（内存，最近 20 次执行、每次最多 50 条时间线），worker 路径 dispatcher 的 progressReporter 与进程内路径 `ScriptExecutionContext` 的 progressReporter 均已接线；管理页底部运行栏显示进度条与文本，执行历史条目日志追加「进度：」时间线；`IWorkerScriptExecutor.ExecuteAsync` 新增 `Guid? executionId` 参数并经 ScriptManager 透传 metadata.ExecutionId，使 worker 模式 outcome.ExecutionId 与进度回调 executionId 一致。
- [x] 自动化脚本 Scheduled+Startup 已实现：`ScriptFileMetadata`/`ScriptPackageManifest` 新增 `Schedule`（"daily HH:mm"，仅 Automation 入口合法）与 `RunOnStartup`，`ScriptDirectoryEntry` 新增 `Metadata`，加载时校验，非法（或非 Automation 入口携带）→`SCRIPT_SCHEDULE_INVALID` 构建失败不注册；新增 `ScriptAutomationSchedule`（TryParse+GetNextDue，lastRun 为空且当天已过→立即到期）；`ScriptAutomationContextFactory.FromRequest` 按 Source 生成 Trigger（Automation→Scheduled、Startup→Startup），替换 worker 内联三元式，Lua/Python worker 的 context 新增 `automation`（trigger/eventData/idempotencyKey）；`ScriptAutomationScheduler` 以 30 秒 tick + `SemaphoreSlim` 串行 + 内存 last-run 表防重调度，启动补跑一轮 RunOnStartup 与今日到期脚本，并生成请求级幂等键（Scheduled=`auto:{scriptId}:{yyyy-MM-dd HH:mm}`、Startup=`startup:{scriptId}:{yyyy-MM-dd}`）；新建向导提供「自动化脚本」模板（EntryKind=Automation、Schedule="daily 09:00"）。metadata/manifest 已支持 `Triggers`（WorkItemCreated、WorkItemSaved、TagAdded），事件型自动化可不配置 schedule；调度器按 `scriptId + trigger + eventId` 防重并生成事件幂等键，工作项创建/保存和标签添加入口已接入，草稿标签在首次保存后按顺序补发；新建向导和管理页均可配置三种事件触发。
- [x] Query 入口已落地：ScriptBase 新增 `IQueryScriptV1` 接口与 `QueryScript` 抽象基类（Scope=Application、EntryKind=Query、上下文 `IScriptApplicationContext`），`ScriptProgramAdapter` 三处增加 Query 分支，C# 引擎类型识别支持 `IQueryScriptV1`；创建向导提供「查询脚本」模板（Lua/Python 使用 `query_main`、C# 使用 `QueryScript` 子类），管理页可直接运行（CanRun 已放行 Application scope）。
- [x] 决策记录：执行历史与执行进度保持会话内存态（历史 30 条、进度最近 20 次），持久化经用户决策明确延期。
