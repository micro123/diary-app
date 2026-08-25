# DiaryApp TODO

本文只维护当前未完成、进行中和后续计划。已完成内容归档到 [`CompletedWork.md`](CompletedWork.md)。

待办条目只描述剩余工作和验收标准，不重复罗列已经完成的实现历史。

## 非 Tracker TODO

- [ ] 完成 Linux 下中文、Emoji 和中英文 2:1 等宽字体回退验证；Windows 构建、随包字体、系统字体、外部字体文件及运行时切换已经验收。
- [ ] 完成应用更新后续优化：客户端逐 Blob 增量和完整包兜底已接入，继续实现跨事务内容缓存、并发控制、可视化进度和断点续传；服务端补事务恢复、下载租约和完整管理鉴权；Windows standard 的 local 增量、完整包兜底和 MCP 占用回滚已验收，继续完成 Windows Python flavor、Linux、初始化失败及进程中止恢复门禁。
- [ ] 补齐数据库备份还原的 Tracker schema/业务语义复检、真实跨版本升级门禁和文件系统故障注入。
- [ ] 将无外部依赖的 Windows/Linux CDP UI 套件纳入定期门禁，并为系统托盘、文件/目录选择器、剪贴板和真实备份还原补充平台原生自动化或明确的发布前检查单；Windows 已完成统一 UI 后的 9/9 常规套件（8 个结构化套件共 74 步，加 smoke）和 5 步 Redmine 只读视觉回归，core 14/14 已覆盖日期操作按钮左右贴边、固定一周、圆形今天/选中/悬停高亮、滚轮逐周浏览、非选中日期右键选中并打开日/周菜单、今天与当前选中状态分离、月份标题月/季度/年度菜单、完整月历模板不裁切及底边完整、重新展开恢复月视图、相邻月份日期精确选择并自动关闭、编辑器字段对齐及跨月浏览后回到今天，手册截图已使用真实窗口表面修复 overlay 重复缩放并统一为逻辑 1×/96 DPI，物理原图仍保留；Linux 已补生命周期工具并通过 X11 下 core 14/14、smoke 与 extended 11/11（含 AI 上下文、MCP 快照和配置复制），仍需完成 Xvfb headless 全量编排和 CI 稳定性门禁。
  - 2026-08-25 已在 Linux X11 修复并复检月份标题上下文菜单、Redmine 空关键字项目列表和 Issue 启停即时刷新，原始 `ui-core-full` 14/14、真实 `ui-redmine-full` 12/12 通过；剩余工作仍是将无外部依赖套件纳入稳定的 Xvfb/CI 门禁。
  - 同日移除 Redmine 管理页与基本信息页签的重复说明卡，Linux X11 `ui-redmine-style` 5/5 通过；该项已完成，不增加新的发布阻塞。
  - 同日完成统一界面紧凑密度和卡片间距调整，主窗口页面四周约 4px，日记页左右卡片间距为 4px 且不再使用重复分隔线，Survey 条件卡显示/隐藏时均保持统一 8px 间距，AI 上下文预览框生成前后保持稳定尺寸；Linux X11 设置、核心、扩展、Survey、数据库异常、附加字段和 Redmine 只读视觉共 63/63 步通过。该项已归档到 `CompletedWork.md`，剩余 TODO 仍是 Xvfb/CI 门禁和平台原生交互覆盖。
  - 同日修复完整月历相邻月份日期多跳一个月、选中后不关闭和重开保留年份视图的问题，Linux X11 `ui-core-full` 14/14 通过；交互修复已归档到 `CompletedWork.md`，不增加新的发布阻塞。
  - 同日完成标题说明横向底部对齐、长说明 Tooltip 化和事项状态胶囊语义配色，Linux X11 核心 14/14、设置 9/9、扩展 11/11、Survey 8/8 通过；该项已归档到 `CompletedWork.md`，剩余 TODO 不变。
  - 同日日记事项备注编辑框已改为随卡片剩余空间自动拉伸，并补充核心 UI 高度与底边间距断言；Linux X11 `ui-core-full` 14/14 通过，该项已归档到 `CompletedWork.md`，不增加新的待办。
  - 同日日记事项编辑器已在没有启用且可用的 Tracker 编辑扩展时隐藏整个“Tracker 关联”卡片；ViewModel 状态测试和 Linux X11 `ui-core-full` 14/14 通过，该项已归档到 `CompletedWork.md`，不增加新的待办。
  - 同日紧凑周历年月按钮已增加年度周次，并复用日期菜单的周次口径；格式单元测试和 Linux X11 `ui-core-full` 14/14 通过，该项已归档到 `CompletedWork.md`，不增加新的待办。
  - 同日标签编辑器增加按名称即时过滤、字段/元数据数量摘要和附加字段即时排序；ViewModel 定向测试 2/2、Linux X11 `ui-extra-fields-full` 8/8 通过，该项已归档到 `CompletedWork.md`，不增加新的待办。
- [ ] 在出现明确的实时查询需求后，评估带用户显式启停、短期令牌和本地命名管道/Unix socket 的 MCP 实时会话；当前版本已完成显式授权快照、Markdown/JSON 导出、六个快照数据工具和无执行脚本校验，不允许数据库直连或脚本执行。

## 阶段 7：代码质量与运行稳定性

目标：继续收敛异步生命周期、严重崩溃诊断和数据库升级门禁。

### 7.1 线程安全与异步生命周期

- [ ] 继续审计 UI 和服务层剩余的 fire-and-forget 入口，确保异常进入日志或结构化诊断，关闭时可等待或取消后台任务。统计刷新已改为后台快照 + UI 原子应用并阻止旧结果覆盖；退出清理失败后可重试；全局 UI 异常只吞可恢复数据库异常；自动化事件防重缓存已设上限。
- [ ] 在 Windows HDD 上运行稀疏 `date-performance` CDP 专项建立同机基线，并调查 25,920 条数据连续切换后 Debug 工作集增长超过 256 MiB 的原因；通过稳定等待、强制 GC 对照和多轮复测区分 GC/Skia/Avalonia 缓存与真实对象泄漏。
- [ ] 在 Windows 与 Linux 分别对优化后的 PostgreSQL + Redmine 日期切换至少复测三轮，确认 P95 稳定低于 300 ms，并继续拆分每事项编辑扩展创建、选项同步和视觉树构造成本。已消除无绑定事项逐条回查，单轮 Linux Debug 测试由每日期 43 次查询降至 5 次，P50 从 242.70 ms 降至 131.99 ms、P95 从 301.78 ms 降至 217.01 ms。

验收：后台任务异常不丢失；应用退出不遗留接收、调度或 Worker 任务；UI 绑定对象只在 UI 线程修改。

### 7.2 Runtime/操作系统级 Dump

- [ ] 为 `FailFast`、StackOverflow 和本机代码严重崩溃补充 Runtime/操作系统级 Dump 配置、说明和验证；现有 DiagnosticsClient 继续负责终止性托管异常。

验收：Windows/Linux 均有可复现的严重崩溃捕获路径，且不会覆盖现有托管异常诊断结果。

### 7.3 Provider 数据升级与备份还原门禁

- [ ] 增加 `WriteSchemaMetadata()`、`RecordMigrationHistory()`、事务提交和回滚失败的故障注入测试，确认底层 I/O 错误不会被误报为迁移成功。
- [ ] 使用真实上一正式版本的 SQLite 数据库文件和 PostgreSQL 初始化快照，执行至少一条包含真实 DDL 的升级链。
- [ ] 增加 SQLite 备份目录不可写、临时文件清理和磁盘空间不足等文件系统故障注入测试。
- [ ] 为 PostgreSQL 还原补充 Tracker schema 版本、业务语义复检、用户取消和匹配服务端主版本工具下的真实还原门禁。
- [ ] 如果未来明确支持同一数据库的多实例并发，再增加迁移锁和并发启动测试；当前不作为发布门禁。

设计文档：[`DatabaseBackupRestoreDesign.md`](DatabaseBackupRestoreDesign.md)

验收：迁移和还原失败不会破坏原数据库或误写 Stable 状态；CI 持续运行 SQLite/PostgreSQL 共享契约与 PostgreSQL 容器测试。

## 阶段 8：常见 Tracker 后端扩展

目标：新增 Tracker 后端时不修改 `Diary.Core` 和核心编辑器，并保持实例隔离、本地保存优先和远程副作用确认。

### 8.0 统一要求

- [ ] 每个后端独立实现 `ITrackerPlugin`，使用稳定 `PluginId` 和 `(PluginId, InstanceId)` 身份，不在主程序增加具体 Tracker 分支。
- [ ] API Key、Token 和密码使用敏感配置存储及遮罩策略；诊断导出不得包含凭据。
- [ ] 网络请求不得进入核心本地保存事务；远程失败必须保留核心工作项和本地绑定，并支持查询或重试。
- [ ] 每个后端提供配置页、实例启停、连接测试、错误诊断及所需的 SQLite/PostgreSQL 数据库契约测试。
- [ ] 覆盖缺失配置、权限不足、网络失败、重复上传、多实例隔离和插件缺失场景。

### 8.1 GitHub Issues（优先级高）

- [ ] 设计 GitHub.com/GitHub Enterprise 配置实例：API 地址、Token、Owner、Repository 和默认筛选条件。
- [ ] 实现 Issue 列表与详情、Label、Milestone 和状态读取，以及本地工作项绑定。
- [ ] 实现创建/更新 Issue、评论或耗时映射，并在远程写入前明确确认副作用。
- [ ] 处理 REST/GraphQL 错误、限流、权限不足，以及仓库和配置实例隔离。

验收：用户可以配置多个仓库、浏览并绑定 Issue；远程操作失败时本地数据不丢失。

### 8.2 Linear（优先级高）

- [ ] 设计 Linear 配置实例：API Token、Team、默认 Project 和默认状态。
- [ ] 实现 GraphQL 客户端及 Issue、Project、Cycle、Label、Priority 和状态读取。
- [ ] 实现本地绑定、耗时/备注/状态上传和副作用确认。
- [ ] 覆盖 schema 变化、限流、网络中断、多 Team/Project 和多实例隔离。

### 8.3 GitLab Issues（优先级中）

- [ ] 设计 GitLab.com/自托管 GitLab 配置实例：Server URL、Private Token、Project ID 和默认筛选条件。
- [ ] 实现 Issue、Label、Milestone、Assignee 和状态读取，以及本地绑定和远程上传。
- [ ] 处理不同 GitLab 版本、权限模型、证书、网络错误、限流和 Token 失效。
- [ ] 覆盖多个 Server、Project 和配置实例隔离。

### 8.4 Markdown/本地任务（优先级中）

- [ ] 确定 Markdown 任务语法、标签和任务 ID 支持范围。
- [ ] 设计本地目录、编码、换行、文件变更检测和冲突处理策略。
- [ ] 支持扫描、筛选、绑定、手动刷新和变更差异预览，禁止静默覆盖用户文件。
- [ ] 评估 Obsidian、Logseq 等工作流兼容性，并增加完全离线测试。

### 8.5 Jira 后续门禁

- [ ] 验证 Jira Server/Data Center 的授权、版本和字段差异。
- [ ] 增加真实 Jira Cloud、自托管 Jira 和权限矩阵集成测试。

设计文档：[`JiraTrackerDesign.md`](JiraTrackerDesign.md)

### 8.6 PLM（等待开放 API）

- [ ] API 开放后确认认证、项目/任务查询、工时追加、权限和幂等语义；开放前不实现猜测性的远程适配。
- [ ] 实现 PLM 插件、多实例配置、本地绑定和追加式工时上传。

### 8.7 通用 Tracker 能力补强

- [ ] 统一只读查询、远程写入、确认、失败、结果不确定和重试模型。
- [ ] 增加后端能力声明，并在 UI 中按能力隐藏不支持的操作。
- [ ] 为每个后端生成不含敏感信息的诊断摘要。
- [ ] 补充无 Tracker、单实例、多实例和插件缺失时的核心编辑器集成测试。

## 阶段 9：脚本系统后续增强

设计文档：[`ScriptSystemDesign.md`](ScriptSystemDesign.md)

### 9.1 目标与 Tracker 能力

- [ ] 将脚本目标校验扩展到项目、Tracker Issue 和 Tracker 实例。
- [ ] 随首个新 Tracker 后端补充统一的 Issue 查询模型和远程写入确认接口。

### 9.2 安全边界和编辑体验

- [ ] 补充更全面的程序集引用和危险 API 边界测试，继续由独立 Worker 承担崩溃、超时和资源失控隔离。
- [ ] 统一剩余通用调用方的后台执行约定。
- [ ] 将 C# 悬停提示改为可靠的单词命中和专用浮动 Popup；多语言外部 LSP、重构和定义跳转后续再评估。

### 9.3 追加式写入和交互导出

- [ ] 设计批量创建、追加式修正/冲正和数据库级幂等原子化；脚本继续禁止删除或直接修改历史记录。
- [ ] 基于现有 Debug-only CDP smoke 基础设施，为脚本交互式 XLSX/CSV/DOCX/Mustache 导出补充真实 UI 端到端测试。

相关设计：[`ScriptSpreadsheetExportDesign.md`](ScriptSpreadsheetExportDesign.md)、[`ScriptExportApiReview.md`](ScriptExportApiReview.md)

### 9.4 Worker 与发布包验证

- [ ] 增加 Worker 工作集/输出超限真实进程测试。
- [ ] 增加 Windows/Linux native/runtime 发布包 Smoke Test；macOS 不在当前支持范围。

设计文档：[`ScriptWorkerDesign.md`](ScriptWorkerDesign.md)

## 阶段 10：用户体验后续增强

- [ ] 批量同步预览增加 Tracker 实例筛选，并为上传结果不确定项提供查询、重试和 Tracker 专用后续处理入口。
- [ ] 细化远程错误分类，并提供一键打开对应 Tracker 实例配置的入口。
- [ ] 查询与统计增加按项目分组汇总；同步状态快捷筛选暂不纳入当前阶段。
- [ ] 为脚本增加显式 DryRun、副作用预览，以及执行历史关联的 worker/request/execution ID；执行历史和进度继续保持会话内存态。
- [ ] 为 Survey v2 增加能力发现缓存、分页明细、节点级错误身份和更多分组维度，同时保持 v1/9721 兼容。

验收：用户能够识别本地保存、远程同步和结果不确定状态；副作用执行前可预览、执行后可追踪；常用查询和统计无需理解插件或 Worker 内部实现。
