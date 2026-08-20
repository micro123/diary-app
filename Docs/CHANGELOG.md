# CHANGELOG

发布说明（Release Notes）。版本号为 `1.0.0-r{CommitCount}`：`1.0.0` 为数据格式版本（`Diary.Core/DataVersion.cs`），`rN` 为发布时的 Git 提交计数（构建脚本 `Diary.App/Scripts/gen_version.sh` 自动生成）。正式发布通过推送 `v*` 标签触发 CI（`.github/workflows/release-on-tags.yml`），Release body 自动引用本文件对应版本章节——各版本标题需保持 `## 版本号` 格式；内部验证标签可不带 `rN`（如 `v1.0.0-alpha1`），对应本文件独立小节。

## 未发布

日期：2026-08-17

- 新增面向普通用户和测试人员的调查功能使用指南，覆盖角色配置、网络拓扑、页面字段、查询场景、新旧版本兼容、排障和隐私边界；README 与协议设计增加分层入口。
- 调查页增加本地使用指南入口，发布包携带对应 Markdown；查询协议改为显式选择兼容 v1 或扩展 v2，扩展条件区只在 v2 模式显示。
- “自定义总时间”改称“占比计算基准”；调查状态直接显示已收到的节点数量和扩展查询错误，能力列表统一在 UI 线程更新。
- 调查页重新设计为全宽查询配置、扩展条件和结果区分层的卡片布局，查询配置压缩为标题、模式与日期、计算与执行三行；新版节点能力在首页只保留探测摘要和详情入口，完整信息移入独立对话框，不再挤压主页面结果空间；设置页专用 Expander 样式已限制到 `.Settings`，不再影响调查页和其他页面。
- 事件记录页的当日工作项统一按优先级升序、ID 升序排列；每次保存、日期加载和复制新增后都会重新整理列表，同时保持当前选中项。
- 脚本通过普通或模板 API 成功新增记录后会通知事件记录页重新读取当前日期；预览、幂等重放和写入失败不会触发刷新。
- 导出模板改为三种格式统一的简易标记协议：支持 `{{变量名}}` 标量替换、`{{items.字段}}` 行循环、`{{items.字段|column}}` 列循环和 `{{items|matrix}}` M×N 矩阵展开；矩阵前后的固定模板内容会保留，模板名称从文件名推断，版本固定为 `1.0.0`，移除元数据头和隐藏元数据工作表要求。
- 新增独立 Mustache 纯文本导出器：支持 `.mustache` 模板、变量、点号路径、列表/布尔区块、反向区块、当前值、注释和转义控制，可输出 TXT、Markdown、HTML 或 CSV；模板上下文复用脚本 `values` 和 `tables` 数据。
- 将面向用户的“导出模板”统一更名为“数据模板”，调整设置入口、模板管理页面、导入校验提示及相关文档表述。
- 应用控制台和滚动日志统一增加托管线程 ID，便于定位后台任务与 UI 线程之间的执行关系。
- tag 发布的 Windows x64 包增加带 Python 3.13.15 embedded runtime 的可选 ZIP，同时保留不附带 Python 的轻量 ZIP；带 Python 的包会将运行时放在 `python/` 目录并由脚本运行时自动优先使用。
- 新增 Linux 本地打包脚本，可交叉发布 `win-x64` 自包含应用、下载并校验 Python 3.13.15 embeddable runtime，并生成与 tag 发布包布局一致的带 Python ZIP。
- 修复本地、Tag 和手动发布包误删整个 `runtimes/` 导致运行失败的问题；现在保留目标 RID 和 `runtimes/any/`，只移除其他平台目录，并在压缩后校验保留目录和非法 RID 条目。
- Python 语法检查和正式 Worker 通过 `-X utf8` 在隔离模式前显式启用 UTF-8，并统一使用 UTF-8 无 BOM 标准输入输出，修复 Windows 环境下含中文脚本被本地代码页错误解码的问题；异常退出诊断会保留受限长度的 stderr 和退出码，tag 发布验证改用 Python 3.13。
- 标签管理页新增版本化 `.diarytags` 导入导出：迁移标签、元数据和附加字段定义，禁用标签导入后默认启用；Tracker 仅记录类型和名称，导入时可映射同类型本地实例并逐条拒绝非法、不存在或无法验证的规则。
- 本地 Windows x64 带 Python 打包脚本新增 `--upload-filecodebox` 选项，可将生成的 ZIP 上传到局域网 FileCodeBox，以 3 小时有效期输出取件码；默认不上传，上传失败时保留本地产物。
- 本地和 Tag CI 打包按 Python 版本及 SHA-256 复用 embeddable runtime 缓存，缓存缺失或校验失败时才重新下载。

## 1.0.0-alpha6 (内部验证版)

日期：2026-08-20

本轮内部验证包含标签迁移能力和发布包运行时资产清理：

- 标签管理页支持 `.diarytags` 标签包导入导出，包含标签基本信息、元数据和附加字段定义；禁用标签可以导出，但导入后默认启用。
- Tracker 规则只记录 Tracker 类型和名称，导入时可选择不关联本地 Tracker；存在多个同类型实例时支持选择目标实例，并校验值不存在、值非法或无法验证的规则。
- Windows/Linux 发布包校验目标 RID 运行时文件后移除冗余 `runtimes/` 目录，避免携带其他平台和重复资产。

## 1.0.0-alpha5 (内部验证版)

日期：2026-08-19

本轮内部验证集中完善数据库维护和脚本系统的可用性、安全边界与共享能力：

- 新增 SQLite 在线备份、完整性检查、启动前还原与失败回滚；PostgreSQL 接入 pg_dump/pg_restore，使用独立目标数据库完成非覆盖式还原，并补齐配置切换失败后的清理与恢复。
- 脚本导出能力迁移到插件注册表，支持 XLSX、CSV、DOCX、模板绑定、交互式选项与目录令牌；三语言公共门面统一推荐 snake_case，并增加包大小、外部关系、宏和嵌入对象等模板安全校验。
- 脚本运行增加参数、幂等键、超时和 Preview 对话框；Query、Preview 和导出 validate-only 由宿主强制限制副作用，C# 继续作为主推脚本语言。
- 优化脚本管理页首次加载、集合刷新和目录诊断展示，API Reference 改为按需解析，减少首次切换页面时的同步卡顿。
- 新增版本化 `.diaryscripts` 共享包，可批量导入、导出 C#、Lua、Python 源码及运行配置；普通用户可从全局设置菜单导入脚本扩展，无需开启开发者功能，开发者仍在脚本管理页完成导出和诊断。导入提供预览、冲突显式覆盖、路径和大小限制、SHA-256 完整性校验以及批量失败回滚。共享包当前不包含数字签名或发布者信任链，只应导入可信来源。

已知限制：脚本执行历史与进度仍为会话内存态；Python 脚本需要本机安装 Python 3.10+；macOS 不在支持范围；PostgreSQL 备份还原依赖兼容的客户端工具；脚本管理页默认隐藏，需在设置中开启“显示开发者功能”。

## 1.0.0-alpha2 (内部验证版)

日期：2026-08-17

在 alpha1 基础上修复调查功能与诊断体验：调查结果区支持滚动；普通受访者启动时独立初始化 v1/v2 请求处理器，并记录连接、收包和成功回包日志；程序设置可直接打开当前滚动日志文件。同时补充面向 Agent 的新 Tag 发布指南。

已知限制：执行历史与进度仍为会话内存态（重启即失）；Python 脚本需要本机安装 Python 3.10+；macOS 不在支持范围；当前 `release-on-tags.yml` 会把所有包含连字符的 Tag 标记为 prerelease。

## 1.0.0-alpha1 (内部验证版)

日期：2026-08-17

首个内部验证版（alpha）：面向内部评测与反馈。完整变更清单见下方 `1.0.0-r435` 条目。已知限制：执行历史与进度为会话内存态（重启即失）；Python 脚本需要本机安装 Python 3.10+；macOS 不在支持范围；脚本管理页默认隐藏，需在设置中开启「显示开发者功能」。

## 1.0.0-r435 (未发布)

日期：2026-08-17（预计）

### 脚本系统（Worker 可靠性、自动化与查询）

- C# 脚本编辑器：新增复用正式编译引用的 Roslyn LSP-like 语言服务，支持 250ms 防抖实时诊断、语义成员/作用域补全和悬停信息；保留关键字补全降级。
- 脚本日志项创建：普通创建补充 provider 事务，失败时回滚；普通和模板创建的 `Preview` 均在数据库访问前返回投影结果，不写入数据库或幂等存储
- 自动化失败提示：工作项创建、保存和标签事件触发的脚本失败时显示非阻塞错误 Toast，明确区分“工作项已保存”和“后续自动化失败”；启动/定时脚本继续通过日志与执行历史追踪

- Worker 心跳与超时：心跳 30s 间隔/15s 超时（仅 Ready 状态）；握手超时 `WORKER_HANDSHAKE_TIMED_OUT`、宿主调用超时 `WORKER_HOST_CALL_TIMED_OUT`；应用退出优雅停止 Worker
- 执行进度：管理页运行区进度条 + 执行历史详情进度时间线（会话内存态，重启即失；持久化明确延期）
- 自动化脚本：`Scheduled`（`daily HH:mm`）与 `Startup`（启动补跑）触发、30s 调度器防重、请求级幂等键；管理页 metadata 设置区与创建向导可配置
- 查询脚本：`QueryScript` 基类 / `query_main` 入口、管理页运行入口
- metadata 编辑 UI：管理页「概览」可改名称/描述/调度/启动补跑（JSON 保留未知字段、原子写入）
- 示例：`AutomationDailyCheck`（每日自查补录）与 `QueryMonthlySummary`（本月工时汇总），C#/Lua/Python 三语言 + 说明文档 + C# 示例编译锁定测试
- C# 脚本基础库白名单扩充 10 个程序集（LINQ、正则、System.Text.Json、Span、并发/非泛型集合、Numerics、加密哈希），危险命名空间禁令不变

### 维护与协议收紧

- 发布维护：新增面向 Agent 的新 Tag 发布指南，明确 `rN` 提交计数对齐、CHANGELOG 提取、权限确认、单 Tag 推送、CI/Release 验收和失败恢复边界。
- Survey 调查链路：普通受访者即使不显示调查页，也会在启动时初始化请求处理器；补充受访者连接、收包和成功回包日志，便于区分连接与协议问题；调查结果区增加独立滚动浏览；程序设置增加“打开当前日志”入口。
- CrashDump：Windows/Linux 终止性托管异常由独立 DiagnosticsClient 进程生成 Triage Dump；隔离的最小 Avalonia 窗口显示简要异常和 Dump 状态，并提供打开 Dump 文件夹；Dump 仅保存在本机且默认保留最近 5 个
- 移除遗留 `IScriptApi`/`IScriptEngine`/`IScript`/`ScriptUsage`/`ITrackerScriptApi` 接口族与适配层（全部实现已迁移至 V1，删除前确认无实现、无 DI、无测试引用）
- Worker 协议：握手协商结果（`maxMessageBytes`/`maxResultMessageBytes`/`apiVersion`）三语言 Worker 全面生效；新增 `WORKER_INVALID_MESSAGE`、`WORKER_HOST_CALL_TOO_LARGE` 诊断码并区分消息超限/格式错误；执行结果按 16MB 单独读取使 `WORKER_RESULT_TOO_LARGE` 可达；补充 4MB/16MB/1MB 消息大小层级注释
- print 语义统一：C# `Console`、Lua `print`、Python `print` 按行转发到脚本日志 Info 级（管理页「运行日志」Tab 可见），执行结束冲刷残余半行，总量 1MB 兜底；转发为尽力而为（log.write 未配置/失败不导致脚本失败）；C# 白名单新增 `System.Console.dll` 支持控制台输出（输入恒为空流）
- Effects 透传与展示：Lua/Python 入口返回 create 结果表即透传 `effects`；管理页执行历史条目与完成通知显示追加条数/预览/幂等重放/新建 ID
- LuaWorker 引导脚本（沙箱 + API 门面 + 上下文装配）外置为嵌入资源 `lua-bootstrap.lua`，与 Python `worker.py` 同构

### 发布与数据库门禁

- CI 改为 Windows/Ubuntu Release 构建矩阵，移除测试容错并执行解决方案内全部测试项目；正式标签和手动候选发布均在验证通过后才构建产物
- Worker 真实进程测试移除 Linux-only 路径和平台跳过，Windows/Ubuntu CI 固定 Python 3.10，并强制执行 C#、Lua、Python 的握手、执行、取消、超时、日志、Effects 与消息上限用例
- Windows Worker 环境白名单保留系统启动所需的 `SYSTEMROOT`，修复 Python 3.10 因系统随机源初始化失败而在握手前退出
- Windows Headless 脚本编辑器测试清理临时目录时增加有限重试，避免文件句柄释放延迟导致 CI 假失败
- `win-x64` 与 `linux-x64` 改由对应原生 Runner 发布，按 RID 还原整个解决方案；发布包显式包含 Jira/Redmine UI 插件与脚本 Worker，并在压缩前校验关键文件
- 自动化脚本调度器改用可注入 `TimeProvider`，消除启动补跑测试受本机时间影响的波动
- 核心迁移契约新增成功迁移保留业务数据、失败回滚后保留原数据、当前 provider 无待执行迁移和提升数据版本必须同步登记 SQLite/PostgreSQL 迁移的测试；SQLite 在实际迁移前创建可恢复快照，备份失败会阻止升级
- Linux CI 强制运行 PostgreSQL Testcontainers；测试依赖显式覆盖到已修复安全问题的 `SSH.NET 2026.0.0`

### 发布流程

- 新增本文件（CHANGELOG），README 补充版本策略说明；Release 工作流改为从本文件提取对应版本章节作为 Release body

## 1.0.0-r112 (v1.0.0-r112, 2025-12-22)

首次正式发布标签。功能基线：工作日记（按天记录、复制前一天、快捷工时）、Tracker 插件契约（RedMine 插件）、自定义事项查询、统计页、Survey 协议 v1。详细历史见 [`Docs/CompletedWork.md`](CompletedWork.md)。
