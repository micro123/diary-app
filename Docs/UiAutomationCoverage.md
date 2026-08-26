# DiaryApp UI 自动化覆盖矩阵

## 1. 范围与状态

本矩阵以 2026-08-24 的 [`UiFeatureInventory.md`](UiFeatureInventory.md) 为功能基准，记录 Windows UI 自动化、单元/集成验证和必须保留的人工边界。它描述的是可重复验证证据，不表示每个页面的每条异常分支都已通过 UI 自动化穷举。

状态统一为：

| 状态 | 含义 |
| --- | --- |
| `Automated` | 通过 CDP 执行真实交互，并验证可见状态、持久化结果或远程结果 |
| `Automated-ReadOnly` | 自动读取页面结构、字段、状态或明细，不执行该功能的主要写入副作用 |
| `Unit/Integration` | 主要由 ViewModel、服务、数据库契约、真实进程或构建集成测试验证 |
| `Manual-Native` | 依赖 Windows 原生托盘、文件/目录选择器、窗口管理或真实灾备环境 |
| `Blocked-External` | 缺少可纳入当前门禁的外部服务、权限或版本矩阵 |
| `Not-Implemented` | 功能清单明确记录为尚未实现 |

统一 UI 复检中，Windows 已分别通过 `ui-settings-full` 9/9、`ui-smoke`、`ui-core-full` 14/14、`ui-extended-full` 11/11、`ui-script-editor` 4/4、`ui-database-error` 8/8、`ui-survey-full` 8/8、`ui-extra-fields-full` 8/8、`ui-redmine-full` 12/12 和 `ui-redmine-style` 5/5。修复截图 DPI 与 overlay 重复缩放后，常规全量编排仍为 9/9 套件，其中 8 个结构化套件共 74 步，最终报告为 `ui-full-test-2026-08-24T14-44-09-118Z.json`；运行方法和性能数据见 [`UiAutomationTesting.md`](UiAutomationTesting.md)。

2026-08-25 Linux X11 回归再次通过 `ui-core-full` 14/14 和真实 Redmine `ui-redmine-full` 12/12；覆盖月份标题上下文菜单的鼠标/键盘入口、Redmine 空关键字项目枚举和 Issue 启停即时刷新。

自动化保存的验收及手册截图统一归一化为逻辑 1×/96 DPI；Windows 使用真实窗口表面避免 CDP 对 overlay 子内容重复缩放，高 DPI 物理原图独立保存在 `screenshots/raw-physical/`。覆盖证据引用逻辑图，物理图仅用于检查缩放、裁切和像素边界。

## 2. 套件到功能映射

| 套件 | 步骤 | 功能域 |
| --- | ---: | --- |
| `ui-settings-full` | 9 | 引导、程序设置、数据库/迁移入口、日志导出、更新 |
| `ui-smoke` | 断言集 | 统一耗时输入、标签、模板、主题、草稿保存、模板新建，以及编辑器模板应用/更新入口 |
| `ui-core-full` | 14 | 主外壳、日记、查询、统计、快捷键 |
| `ui-extended-full` | 11 | 脚本管理、AI 上下文授权与 MCP 快照、程序设置配置复制、创建、运行、历史、日志和删除 |
| `ui-script-editor` | 4 | 独立脚本编辑器和编译检查 |
| `ui-database-error` | 8 | 数据库异常和恢复入口 |
| `ui-survey-full` | 8 | Survey v1/v2、能力、分组、明细和错误 |
| `ui-extra-fields-full` | 8 | 标签过滤与数量摘要、附加字段定义和即时排序、9 类编辑器、持久化、停用历史值和迁移事项入口隐藏 |
| `ui-redmine-full` | 12 | 多 Tracker、Redmine 管理、标签规则、工时和安全 |
| `ui-redmine-style` | 5 | Redmine 只读视觉回归、工具栏布局、截图和 CheckBox 中心线 |
| `ui-navigation-performance` | 4 | 新进程启动、核心与动态导航清单、首次访问、正反向热切换、CPU/内存/I/O；由独立编排器跨进程汇总 |

## 3. 功能级覆盖

### 3.1 主窗口、导航和全局外壳

| 清单 | 功能 | 状态 | 证据或边界 |
| --- | --- | --- | --- |
| 2.1 | 固定页、动态 Tracker 页、导航折叠、`Alt+1..9` | `Automated` | core 验证页面切换、折叠和数字快捷键；Redmine 验证动态导航 |
| 2.1 | 主导航冷切换和热切换性能 | `Automated-Performance` | `navigation-performance` 场景同时开启日记、查询、统计、调查和脚本页，可选加载 Tracker 动态管理页；单进程套件记录可见/稳定延迟与资源增量，跨进程编排器轮换首次访问顺序并生成 JSON/Markdown 汇总；单元测试覆盖每实例 View 复用、真实宿主持久挂载、页面预加载契约、Tracker 页面动态增加/移除/重建、缓存资格默认关闭、统计按需初始化和 `WorkEditorViewModel` 不缓存；脚本管理日志拆分 View 初始化、目录等待、后台整理和 UI 应用耗时；CDP 树传播祖先有效可见性，不纳入常规全量门禁 |
| 2.2 | 应用菜单、关于、版本、主题 | `Automated` | core 验证菜单和关于；smoke 验证主题截图差异；复制版本明细仅结构检查 |
| 2.2 | 发布版用户手册入口 | `Unit/Integration` | 服务测试覆盖 Debug/Release 判定、HTML 优先和 PDF 回退；窗口测试检查命令/可见性绑定；Debug core 确认菜单隐藏；Tag/手动发布包校验强制要求 HTML/PDF。系统默认浏览器或 PDF 阅读器打开行为保留原生人工检查 |
| 2.2 | 最大化、最小化、重启、退出 | `Manual-Native` | 涉及原生窗口/进程生命周期，未纳入连续套件 |
| 2.3 | 托盘显示、恢复和退出 | `Manual-Native` | Avalonia CDP 不控制系统托盘 |
| 2.4 | 数据库、Tracker、通知中心、日期和按需状态入口 | `Automated-ReadOnly` | core 读取数据库、Tracker、通知铃铛和紧凑日期入口，真实打开通知历史 Flyout，并确认版本菜单提供可用的检查更新入口；状态映射、语义色、未读数和点击命令由 ViewModel/服务测试覆盖 |
| 2.4 | 通知历史保留、去重、已读与持久化 | `Unit/Integration` | 服务测试覆盖三级保留策略、跨重启加载、10分钟合并、重新未读、敏感值脱敏、长度限制和100条淘汰；原生打开文件/目录保留人工验证 |
| 2.4 | 后台任务与更新进度预览 | `Unit/Integration` | 状态服务测试覆盖任务创建、进度更新和结束移除；更新准备接入下载比例、增量/完整包、文件数与大小，原生下载和备份文件选择器仍保留人工验证 |
| 2.5 | 首次引导、关闭、从设置重新打开 | `Automated` | settings 覆盖启动引导和设置内重开 |

### 3.2 日记记录和工作项编辑

| 清单 | 功能 | 状态 | 证据或边界 |
| --- | --- | --- | --- |
| 3.1 | 数据库异常提示、重试、设置和诊断导出 | `Automated` | database-error 8 步覆盖日记、查询、统计和导出 |
| 3.2 | 固定一周、逐周浏览、回到今天、新建、使用今天、草稿跨导航保存 | `Automated` | core 验证日期操作按钮精确贴齐左右边缘、7 个日期、滚轮逐周浏览且不改变选中日期、跨月后恢复当前周；smoke 验证事项日期修正、本地保存和跨导航保留 |
| 3.2 | `新建 -> 修改 -> 新建` | `Automated` | smoke 验证第一条有内容草稿自动持久化 |
| 3.2 | 复制昨天/最近入口、复制整天对话框 | `Automated` | core 验证三入口和复制整天安全取消 |
| 3.2 | 复制内容、标签、附加字段和远程绑定排除语义 | `Unit/Integration` | ViewModel 回归覆盖 Tracker 目标初始化和本地复制排除绑定；2026-08-22 富数据定向 CDP 验证带 Tracker 的源事项可复制两个标签和 9 类附加字段，目标无 Redmine/Jira 绑定 |
| 3.2 | 大量数据下连续日期导航性能和无变化写入 | `Automated-Performance` | `date-performance` 隔离场景预置 540 天 × 48 条稀疏事项数据，专项套件执行 120 次逐次切换和两组 24 次高速连按，输出延迟、CPU、内存和进程 I/O；已完成 SQLite/PostgreSQL × Core-only/真实 Redmine 四组对照，Tracker 组每天约 20% 本地绑定，SQLite 断言主文件/WAL/journal 不变化，PostgreSQL 断言业务摘要及插入/更新/删除计数不变化，并确认 Redmine 无远程工时写入；不纳入常规全量门禁 |
| 3.3 | 日历上下文菜单和日期/周/月/季度/年动作 | `Automated + Unit/Integration` | core 使用真实鼠标右键验证非选中日期会变为当前选中并打开日/周菜单，同时验证今天与选中状态分离；月份标题通过标准上下文菜单键验证月/季度/年度操作和脚本组前分隔符，并验证完整月历外层宽高均不小于内部模板、四周边框不会被裁切；空脚本子菜单的禁用“暂无”、命令路由和脚本目标由 ViewModel/脚本测试验证 |
| 3.4 | 当日汇总、事项列表、优先级排序 | `Automated-ReadOnly` | smoke/core 读取列表和保存状态；排序语义由单元/集成测试承担 |
| 3.5 | 标题、日期、工时、优先级、备注等通用字段 | `Automated` | core 断言日期、标题、耗时输入框绝对左边缘偏差不超过 1px；smoke 覆盖耗时 Enter、Esc、失焦应用、模板和保存；完整格式组合由单元测试承担 |
| 3.6 | 标签创建、选择、模板应用和最近标签 | `Automated + Unit/Integration` | smoke 创建标签、验证模板新建，并断言单个“从模板更新”分段按钮出现在“使用今天”之后；ViewModel 测试覆盖主体补空、菜单覆盖、标签清空重建、已有标签保持及数据库持久化；Redmine 验证标签默认值 |
| 3.7 | 9 类附加字段编辑器、空值、格式和只读 | `Automated` | extra-fields 逐控件验证类型化编辑、三态布尔、Choice/Time 清空与重设、日期时间控件无重叠、切换事项后的值持久化、停用字段历史值及迁移只读事项隐藏入口；转换和数据库契约另有定向测试 |
| 3.8 | Tracker 编辑区和同步状态 | `Automated` | Redmine 覆盖默认填充、同步、锁定和防重复；Jira 另列外部边界 |
| 3.9 | 新建、保存、重复、删除取消和同步快捷键 | `Automated` | core 覆盖 `Ctrl+N/S/D/Shift+D`；Redmine 覆盖按钮和 `Ctrl+U` 防重复 |
| 3.10 | 已同步事项锁定、本地删除警告和取消 | `Automated` | Redmine 验证有效禁用状态和仅影响本地警告 |
| 3.11 | 标签自动化填充 Tracker Issue/活动 | `Automated` | Redmine 创建规则并验证新事项默认值 |

### 3.3 查询和统计

| 清单 | 功能 | 状态 | 证据或边界 |
| --- | --- | --- | --- |
| 4.1 | 查询条件、执行和条件折叠 | `Automated` | core 执行查询并等待结果 |
| 4.2 | 结果列表、打开事项、摘要 | `Automated` | core 从结果打开日记事项；摘要结构可见 |
| 4.2 | CSV/Markdown 导出和系统剪贴板 | `Manual-Native` | 依赖文件选择器/系统剪贴板；数据生成由单元测试承担 |
| 4.3 | 保存、重命名、应用、删除取消 | `Automated` | core 覆盖保存查询完整维护链 |
| 5.1 | 周/月/季度/年/自定义 Tab | `Automated-ReadOnly` | core 验证 Tab 和自定义范围激活 |
| 5.2 | 范围计算和重新统计 | `Automated` | core 触发刷新并等待总工时结果；精确计算由数据库契约测试承担 |
| 5.3 | 柱状图/饼图切换、标签明细和树操作 | `Automated-ReadOnly` | core 验证刷新、默认柱状图、切换到饼图及树操作，单元测试覆盖图表模式和饼图系列；不穷举所有数值组合和图形像素 |

### 3.4 调查工具

| 清单 | 功能 | 状态 | 证据或边界 |
| --- | --- | --- | --- |
| 6.1 | 条件可见、页面结构和本机节点 | `Automated` | survey 场景自动开启调查者/受访节点 |
| 6.2 | v1 兼容查询 | `Automated` | 本机回环查询并等待结果 |
| 6.2 | v2 关键词、标签、标签模式、优先级和明细开关 | `Automated` | 验证控件和明细状态 |
| 6.3 | 能力发现、能力详情 | `Automated` | 验证新版节点能力和详情对话框 |
| 6.3 | 标签/日期/优先级分组 | `Automated` | 三种分组均执行真实本机查询 |
| 6.3 | 无效日期范围和节点错误状态 | `Automated` | 验证无效范围在调查者端禁用按钮、保留上一轮结果且不发送请求；多远程节点部分失败由协议集成测试承担 |

### 3.5 脚本管理和编辑器

| 清单 | 功能 | 状态 | 证据或边界 |
| --- | --- | --- | --- |
| 7.1–7.3 | 工作台、列表、详情、搜索、筛选和重载 | `Automated` | extended 覆盖导航、结构和刷新 |
| 7.4 | 执行历史和运行日志 | `Automated` | extended 验证执行历史、可选择的只读运行日志框、复制全部入口，并确认管理页不再显示 API Reference 页签 |
| 7.4 | AI 上下文默认授权、显式事项范围、预览和 MCP 快照 | `Automated` | extended 验证事项默认关闭、日期控件按授权显示、结构与事项预览、不可信数据标记、快照 schema/范围及无标签 metadata；Linux CDP 同时生成手册截图 |
| 7.5 | C#/Lua/Python 新建脚本 | `Automated` | extended 检查 V1/V2 版本说明并以默认 V2 创建三种脚本；版本生成由 ViewModel 定向测试覆盖 |
| 7.6 | 手动运行和 Preview | `Automated` | extended 运行 C# Preview 并验证结果 |
| 7.7 | 独立编辑器、API 文档入口、代码区和编译检查 | `Automated` | script-editor 验证按脚本语言打开 API Reference 的入口，以及成功状态与诊断区共存 |
| 7.7 | 补全、悬停、重构和外部 LSP | `Unit/Integration` | 语言服务有定向测试；部分能力仍在 TODO |
| 7.8 | `.diaryscripts` 导入/导出 | `Manual-Native` | 依赖文件选择器；包安全和回滚由集成测试验证 |
| 7.8 / TODO 9.3 | XLSX/CSV/DOCX/Mustache 交互式导出 | `Unit/Integration` | 导出器和脚本 API 已测试，真实 UI 端到端仍待补齐 |

### 3.6 程序设置、标签和模板

| 清单 | 功能 | 状态 | 证据或边界 |
| --- | --- | --- | --- |
| 8.1–8.3 | 设置分组、编辑、保存、丢弃和动态导航 | `Automated` | settings 覆盖 5 组设置和开发者导航重建 |
| 8.1–8.3 | AI 与 MCP 快照状态、标准设置行、配置复制和跳转 | `Automated` | extended 在真实隔离快照上验证标准分组内五行内容完整可见、AI 可读 Markdown、通用 JSON 复制，以及保存设置后直接打开 AI 上下文；内容格式另有单元测试 |
| 8.4 | 数据库配置入口 | `Automated` | settings/database-error 验证对话框和无效驱动安全失败 |
| 8.4 | SQLite/PostgreSQL 真实备份与还原 | `Manual-Native` | 依赖原生目录/文件、外部工具和灾备环境；底层由集成测试覆盖 |
| 8.5 | 数据迁移向导打开和安全取消 | `Automated` | settings 覆盖向导边界 |
| 8.6 | 调查设置字段 | `Automated-ReadOnly` | settings 读取字段；Survey 套件验证实际页面能力 |
| 8.7 | 更新配置和手动检查 | `Automated` | settings 验证检查中和无匹配发布结果；真实安装升级由独立更新门禁承担 |
| 8.x | 当前运行日志导出 | `Automated` | settings 验证占用中的日志可导出为非空 ZIP |
| 9.1–9.3 | 标签基础信息和 Tracker 自动化页 | `Automated` | smoke 创建标签；Redmine 配置自动规则 |
| 9.2 / 9.4 | 标签元数据和附加字段定义 | `Automated` | extra-fields 创建并重开 9 类定义，验证非法 FieldKey、FieldKey/类型不可修改、说明更新和停用；元数据低层语义继续由模型测试覆盖 |
| 9.5 | `.diarytags` 导入/导出 | `Unit/Manual-Native` | 导出标签选择、全选/清空和空选择禁用由单元测试覆盖；保存位置依赖文件选择器 |
| 10.1 | 工作项模板创建、字段、标签、应用和草稿保留 | `Automated` | smoke 完整验证 |
| 10.2 | 数据模板空状态和入口 | `Automated-ReadOnly` | core 验证管理页空状态 |
| 10.2 | 数据模板导入、预览和删除文件 | `Manual-Native` | 依赖文件选择器和真实文件 |

### 3.7 Tracker、Redmine 和 Jira

| 清单 | 功能 | 状态 | 证据或边界 |
| --- | --- | --- | --- |
| 11.1 | Tracker 设置、多提供者和临时第二实例增删 | `Automated` | Redmine 套件验证提供者数和实例往返 |
| 11.2 | 插件状态、启停和动态 UI | `Automated` | 验证运行状态和管理页动态导航；低层生命周期有集成测试 |
| 11.3 | 通用批量同步预览和逐项结果 | `Unit/Integration` | 当前 UI 套件覆盖单事项真实同步；通用批量协调由 ViewModel/服务测试承担 |
| 12.1 | Redmine 配置、敏感编辑器和连接状态 | `Automated` | 验证控件类型、配置保存和插件运行状态 |
| 12.2 | Redmine 标签规则 | `Automated` | 创建规则并验证 Issue/活动默认填充 |
| 12.3 | Issue 选择、活动、工时同步、锁定和防重复 | `Automated` | 真实测试服务写入并回读 UI 状态 |
| 12.4 | 用户信息和活动同步 | `Automated` | 管理页真实同步 |
| 12.5 | Issue 关键词/ID 搜索、导入、同步、启停和删除 | `Automated` | 完整维护链通过 |
| 12.6 | 项目搜索、说明和创建 Issue | `Automated` | 真实测试服务创建测试数据 |
| 12.x | 配置加密和日志脱敏 | `Automated` | 验证配置文件加密标记和日志无敏感标记；报告不保存凭据 |
| 13.1–13.2 | Jira 配置、Issue、worklog 和锁定 | `Blocked-External` | 缺少可用于自动门禁的 Jira Cloud/自托管服务与权限矩阵；低层已有单元/集成测试 |

### 3.8 对话框、快捷键和安全边界

| 清单 | 功能 | 状态 | 证据或边界 |
| --- | --- | --- | --- |
| 14 | 关于、标准消息、确认和 Toast | `Automated` | 多套件验证打开、长消息可滚动、确认取消和通知结果 |
| 14 | 文件/目录选择器 | `Manual-Native` | 不属于 Avalonia CDP 视觉树 |
| 14 | Survey 节点能力对话框 | `Automated` | survey 验证详情打开和关闭 |
| 15 | `Alt+数字` 主导航 | `Automated` | core 覆盖主键区和应用代码同时支持数字键盘区 |
| 15 | 日记 `Ctrl+T/N/S/D/Shift+D` | `Automated` | smoke/core 覆盖主要路径；模板快捷键和批量同步由命令测试承担 |
| 15 | Redmine `Enter/Ctrl+Enter` 搜索 | `Automated` | Redmine 覆盖关键词和 ID 搜索 |
| 16 | 数据库异常不表现为空数据 | `Automated` | database-error 覆盖查询结果保留和恢复入口 |
| 16 | 远程写入锁定、防重复和删除警告 | `Automated` | Redmine 覆盖 |
| 16 | 敏感配置加密和日志脱敏 | `Automated` | Redmine 安全步骤覆盖 |
| 16 | Release 不包含 CDP | `Unit/Integration` | 由 Release restore、构建和发布包内容校验承担，不在 Release 运行 CDP |
| 16 | Release 应用 ZIP 携带用户手册 | `Unit/Integration` | Tag/手动发布等待文档构建，复制稳定路径的 HTML/PDF 后再压缩；发布包校验器使用 `--require-user-manual` 拒绝缺失文件的 ZIP |
| 16 | Release 应用 ZIP 携带脚本文档和示例 | `Unit/Integration` | App 发布递归复制 `Docs/ScriptApi/`；发布包校验器使用 `--require-script-api` 检查入口文档及其本地引用，工具单元测试覆盖示例存在和缺失两种结果 |

## 4. 仍需保留的发布前人工检查

即使全量 CDP 套件通过，发布前仍需按风险执行以下检查：

1. Windows 托盘显示、隐藏、恢复和退出。
2. 最大化、最小化、重启和多显示器窗口行为。
3. 文件/目录选择器、剪贴板和系统默认程序打开。
4. SQLite/PostgreSQL 真实备份、还原、工具发现和取消。
5. Release 包内容检查，确认没有 CDP 调试程序集和监听入口，并确认应用菜单可通过系统默认程序打开随包 HTML 用户手册。
6. 有可用环境时执行 Jira Cloud/自托管权限与版本矩阵。

## 5. 日期说明

本轮统一 UI 复检日期和系统日期均为 2026-08-24，隔离 profile 目录名中的 `20260824` 与实际日期一致。测试未修改系统时钟。
