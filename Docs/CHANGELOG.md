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
