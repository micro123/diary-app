# CHANGELOG

发布说明（Release Notes）。版本号为 `1.0.0-r{CommitCount}`：`1.0.0` 为数据格式版本（`Diary.Core/DataVersion.cs`），`rN` 为发布时的 Git 提交计数（构建脚本 `Diary.App/Scripts/gen_version.sh` 自动生成）。正式发布通过推送 `v*` 标签触发 CI（`.github/workflows/release-on-tags.yml`），Release body 自动引用本文件对应版本章节——各版本标题需保持 `## 版本号` 格式。

## 1.0.0-r420 (未发布)

日期：2026-08-14（预计）

### 脚本系统（Worker 可靠性、自动化与查询）

- Worker 心跳与超时：心跳 30s 间隔/15s 超时（仅 Ready 状态）；握手超时 `WORKER_HANDSHAKE_TIMED_OUT`、宿主调用超时 `WORKER_HOST_CALL_TIMED_OUT`；应用退出优雅停止 Worker
- 执行进度：管理页运行区进度条 + 执行历史详情进度时间线（会话内存态，重启即失；持久化明确延期）
- 自动化脚本：`Scheduled`（`daily HH:mm`）与 `Startup`（启动补跑）触发、30s 调度器防重、请求级幂等键；管理页 metadata 设置区与创建向导可配置
- 查询脚本：`QueryScript` 基类 / `query_main` 入口、管理页运行入口
- metadata 编辑 UI：管理页「概览」可改名称/描述/调度/启动补跑（JSON 保留未知字段、原子写入）
- 示例：`AutomationDailyCheck`（每日自查补录）与 `QueryMonthlySummary`（本月工时汇总），C#/Lua/Python 三语言 + 说明文档 + C# 示例编译锁定测试
- C# 脚本基础库白名单扩充 10 个程序集（LINQ、正则、System.Text.Json、Span、并发/非泛型集合、Numerics、加密哈希），危险命名空间禁令不变

### 维护与协议收紧

- 移除遗留 `IScriptApi`/`IScriptEngine`/`IScript`/`ScriptUsage`/`ITrackerScriptApi` 接口族与适配层（全部实现已迁移至 V1，删除前确认无实现、无 DI、无测试引用）
- Worker 协议：握手协商结果（`maxMessageBytes`/`maxResultMessageBytes`/`apiVersion`）三语言 Worker 全面生效；新增 `WORKER_INVALID_MESSAGE`、`WORKER_HOST_CALL_TOO_LARGE` 诊断码并区分消息超限/格式错误；执行结果按 16MB 单独读取使 `WORKER_RESULT_TOO_LARGE` 可达；补充 4MB/16MB/1MB 消息大小层级注释
- print 语义统一：C# `Console`、Lua `print`、Python `print` 按行转发到脚本日志 Info 级（管理页「运行日志」Tab 可见），执行结束冲刷残余半行，总量 1MB 兜底；转发为尽力而为（log.write 未配置/失败不导致脚本失败）；C# 白名单新增 `System.Console.dll` 支持控制台输出（输入恒为空流）
- Effects 透传与展示：Lua/Python 入口返回 create 结果表即透传 `effects`；管理页执行历史条目与完成通知显示追加条数/预览/幂等重放/新建 ID
- LuaWorker 引导脚本（沙箱 + API 门面 + 上下文装配）外置为嵌入资源 `lua-bootstrap.lua`，与 Python `worker.py` 同构

### 发布与数据库门禁

- CI 改为 Windows/Ubuntu Release 构建矩阵，移除测试容错并执行解决方案内全部测试项目；正式标签和手动候选发布均在验证通过后才构建产物
- Worker 真实进程测试移除 Linux-only 路径和平台跳过，Windows/Ubuntu CI 固定 Python 3.10，并强制执行 C#、Lua、Python 的握手、执行、取消、超时、日志、Effects 与消息上限用例
- Windows Worker 环境白名单保留系统启动所需的 `SYSTEMROOT`，修复 Python 3.10 因系统随机源初始化失败而在握手前退出
- `win-x64` 与 `linux-x64` 改由对应原生 Runner 发布，按 RID 还原整个解决方案；发布包显式包含 Jira/Redmine UI 插件与脚本 Worker，并在压缩前校验关键文件
- 自动化脚本调度器改用可注入 `TimeProvider`，消除启动补跑测试受本机时间影响的波动
- 核心迁移契约新增成功迁移保留业务数据、失败回滚后保留原数据、当前 provider 无待执行迁移和提升数据版本必须同步登记 SQLite/PostgreSQL 迁移的测试；SQLite 在实际迁移前创建可恢复快照，备份失败会阻止升级
- Linux CI 强制运行 PostgreSQL Testcontainers；测试依赖显式覆盖到已修复安全问题的 `SSH.NET 2026.0.0`

### 发布流程

- 新增本文件（CHANGELOG），README 补充版本策略说明；Release 工作流改为从本文件提取对应版本章节作为 Release body

## 1.0.0-r112 (v1.0.0-r112, 2025-12-22)

首次正式发布标签。功能基线：工作日记（按天记录、复制前一天、快捷工时）、Tracker 插件契约（RedMine 插件）、自定义事项查询、统计页、Survey 协议 v1。详细历史见 [`Docs/CompletedWork.md`](CompletedWork.md)。
