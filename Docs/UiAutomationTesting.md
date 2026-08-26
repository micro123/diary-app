# DiaryApp UI 自动化测试

## 1. 目标与边界

DiaryApp 在 Windows 和 Linux Debug 构建中提供基于 Chrome DevTools Protocol（CDP）的本地 UI 自动化入口，用于读取 Avalonia 视觉树、聚焦和触发控件、输入文本、发送快捷键、切换页面、截图并采样交互响应时间。

该入口只用于本地开发和验证：

- `Chrome.DevTools.Avalonia.v11` 只在 Debug 配置引用，Release 包不应包含 CDP 程序集。
- 只有显式设置 `DIARY_CDP_PORT` 时才启动监听，默认构建和正常启动不会开放端口。
- `DIARY_UI_TEST_ROOT` 将配置、数据库、日志和临时文件映射到独立测试 profile。
- 测试 profile 使用独立单实例 ID，可以与正常 DiaryApp 并行运行。
- `DIARY_UI_TEST_SCENARIO` 只在隔离 profile 中预置测试场景，不修改用户配置。
- CDP 具备输入模拟、截图和运行时检查能力，不得在正式包或不可信网络中开放。
- Release restore 必须显式传入 `-p:Configuration=Release`；发布包校验会拒绝 `Avalonia.Diagnostics`、`CDP.Integration.*`、`Chrome.DevTools.*` 和 `Xaml.Compiler` 调试组件。

当前使用兼容 Avalonia 11 和 SkiaSharp 2.88.9 的 `Chrome.DevTools.Avalonia.v11 0.1.0-preview.30`。升级 SkiaSharp 3.x 后应重新评估更新版本。

完整功能级状态见 [`UiAutomationCoverage.md`](UiAutomationCoverage.md)，UI 功能入口见 [`UiFeatureInventory.md`](UiFeatureInventory.md)。

## 2. 生命周期和测试场景

Windows 使用 `Tools/ui-test.ps1` 管理单个隔离 App 生命周期：

```powershell
.\Tools\ui-test.ps1 start
.\Tools\ui-test.ps1 status
.\Tools\ui-test.ps1 smoke
.\Tools\ui-test.ps1 run -Suite ui-core-full
.\Tools\ui-test.ps1 stop
```

Linux 使用 `Tools/ui-test.sh`；需要 Bash、Python 3、Node.js 22.5+ 和可用的 X11 显示：

```bash
./Tools/ui-test.sh start --port 9333
./Tools/ui-test.sh status
./Tools/ui-test.sh smoke
./Tools/ui-test.sh run ui-core-full
./Tools/ui-test.sh stop
```

如果已经设置 `DISPLAY`，Linux 工具直接复用当前 X11 会话；未设置时会自动查找并启动 `Xvfb`。可以使用 `--display :0` 指定显示，或使用 `--xvfb` 强制创建 1920×1080、24 位色深的独立虚拟显示。工具把 App 和 Xvfb PID 一并写入状态文件，`stop` 会校验可执行文件身份后分别清理。

如果 Debug App 已构建，可跳过重复构建：

```powershell
.\Tools\ui-test.ps1 start -NoBuild
```

```bash
./Tools/ui-test.sh start --no-build --display :0
```

Linux 默认构建流程先按 Debug 配置执行 restore，再使用 `--no-restore` 构建，避免条件引用的 CDP 包因沿用 Release 资产文件而缺失。

脚本支持以下场景：

| 场景 | Windows / Linux 启动参数 | 用途 |
| --- | --- | --- |
| `default` | 默认 | 核心、设置、标签和模板 |
| `extended` | `-Scenario extended` / `--scenario extended` | 开启开发者功能，显示脚本管理 |
| `survey` | `-Scenario survey` / `--scenario survey` | 开启调查者和本机受访节点 |
| `database-error` | `-Scenario database-error` / `--scenario database-error` | 注入不存在的数据库驱动，验证恢复 UI |
| `extra-fields` | `-Scenario extra-fields` / `--scenario extra-fields` | 预置迁移只读事项，验证标签附加字段定义、类型化编辑和迁移事项入口隐藏 |
| `date-performance` | `-Scenario date-performance` / `--scenario date-performance` | 预置 540 天、每日 48 条富工作数据，验证大量日期切换性能和只读导航不写库 |
| `navigation-performance` | `-Scenario navigation-performance` / `--scenario navigation-performance` | 同时开启调查和开发者功能，测量所有核心导航页及可用 Tracker 管理页的首次访问和重复切换性能 |
| `plugins` | `-Scenario plugins -WithPlugins` / `--scenario plugins --with-plugins` | 加载 Tracker 插件和动态管理页 |

两种工具的 `start` 都会创建 `.build-tmp/ui-test/profiles/<runId>`，等待 CDP ready，并将 PID、端口、profile、场景和冷启动时间写入 `.build-tmp/ui-test/current.json`。Linux 额外记录 `platform`、`display`、Xvfb PID 和应用日志路径。`stop` 会校验 PID 对应的可执行文件后再终止进程，避免误杀其他 DiaryApp 或 Xvfb 实例。

需要复用外部 Tracker 测试配置时，使用已有加密 profile 作为 seed：

```powershell
.\Tools\ui-test.ps1 start -NoBuild -Scenario plugins -WithPlugins -SeedProfile '<encrypted-profile>'
```

```bash
./Tools/ui-test.sh start --no-build --scenario plugins --with-plugins \
  --seed-profile '<encrypted-profile>'
```

seed 只复制加密配置文件，不应提交到 Git，也不得写入报告和文档。未提供 Redmine seed 时，全量编排将该套件标记为 `blocked-external`，而不是伪造通过。

## 3. 全量编排

一次运行全部可重复套件：

```powershell
.\Tools\ui-full-test.ps1 -NoBuild -RedmineSeedProfile '<encrypted-profile>'
```

不需要外部服务时可以省略 `-RedmineSeedProfile`；其余套件仍执行，Redmine 结果记录为 `blocked-external`。编排器按场景创建隔离 profile，并在套件组结束后停止 App。

当前全量多场景编排器仍为 PowerShell。Linux 可以通过 `ui-test.sh start` 和 `ui-test.sh run <suite>` 运行任意单套件；无桌面的 Xvfb 全量编排和定期 CI 门禁仍是后续工作，不能仅凭已有桌面会话结果视为 headless 门禁完成。

当前 11 个套件如下；`ui-date-performance` 是耗时和机器差异较大的专项，不加入常规全量编排：

| 套件 | 结构化步骤 | 主要覆盖 |
| --- | ---: | --- |
| `ui-settings-full` | 9 | 首次引导、设置分组、保存/丢弃、导航动态重建、数据库/迁移对话框、运行日志导出、更新检查、设置性能 |
| `ui-smoke` | 单独断言集 | 标签、模板、主题、新建草稿、`新建 -> 修改 -> 新建`、模板替换前草稿保留、视觉树和截图性能 |
| `ui-core-full` | 14 | 主外壳、状态栏、版本菜单、应用菜单、`Alt+数字`、日期操作按钮左右对齐、固定一周日历、滚轮逐周浏览、非选中日期真实右键选中并打开日/周菜单、今天/选中状态分离、月份标题月/季度/年度菜单、完整月历自然尺寸与边框防裁切、重新展开恢复月视图、相邻月份日期精确选择并自动关闭、跨月回到今天、编辑器字段对齐、标题说明横向底部对齐、未保存/中性状态胶囊类、复制、快捷键、查询、统计和核心性能 |
| `ui-extended-full` | 11 | AI 上下文默认/显式授权、预览、MCP 快照和手册截图，程序设置标准分组布局、配置生成/复制和跳转，以及 C#/Lua/Python 脚本创建、筛选、重新加载、预览运行、执行历史、日志、删除、性能 |
| `ui-script-editor` | 4 | 独立脚本编辑器、按语言打开 API 文档的入口、命令区、编译检查和安全关闭 |
| `ui-database-error` | 8 | 日记/查询/统计数据库异常状态、重试、设置入口、诊断导出和异常状态性能 |
| `ui-survey-full` | 8 | v1 查询、v2 能力发现、详情、筛选、三种分组、明细开关、校验错误和性能 |
| `ui-extra-fields-full` | 8 | 标签过滤、字段/元数据数量摘要、字段即时排序、9 类字段定义、类型化编辑、清空、持久化、停用历史值和迁移只读事项；加载 Tracker 时同时验证扩展内容区只读 |
| `ui-date-performance` | 6 | 25,920 条事项、120 次逐次日期切换、两组高速连按、CPU/内存/进程 I/O；SQLite 检查主文件/WAL，PostgreSQL 检查数据摘要与写入计数 |
| `ui-redmine-full` | 12 | 多 Tracker 设置、Redmine 管理、项目/Issue、标签规则、工时同步、防重复、删除边界、安全和性能 |
| `ui-redmine-style` | 5 | Redmine 配置、插件状态、基本信息、问题/项目工具栏截图和 CheckBox 中心线，只读且不触发远程写入 |

常规全量编排包含 9 个套件，其中 8 个结构化套件合计 74 个步骤；`ui-smoke` 另含标签、模板、主题、草稿、本地持久化和性能断言。日期性能专项另有 6 步，按目标机器和目标磁盘单独运行。

2026-08-24 的统一 UI Windows 复检分别通过设置 9/9、smoke、核心 14/14、扩展 11/11、脚本编辑器 4/4、数据库异常 8/8、Survey 8/8、附加字段 8/8、Redmine 全功能 12/12 和 Redmine 只读视觉 5/5；截图 DPI 与 overlay 重复缩放修复后的常规全量报告 `ui-full-test-2026-08-24T14-44-09-118Z.json` 仍为 9/9 套件通过。复检覆盖日记、查询、统计、程序设置、标签、模板、数据模板、Tracker 配置/状态、Jira/Redmine 实例配置和 Redmine 管理子页面。`ui-redmine-full` 同步兼容统一耗时 `TextBox` 的直接输入，并在保存配置后要求文件使用当前 `DiaryGCM` 整体加密格式；旧 `Salted__` seed 只作为读取兼容输入。

2026-08-25 在 Linux X11 使用原始套件完成回归：`ui-core-full` 14/14 通过，确认月份标题的鼠标右键、`Shift+F10` 和系统上下文请求可打开月/季度/年度菜单；`ui-redmine-full` 12/12 通过，确认空关键字项目列表可直接读取 `/projects.json`，Issue 启停无需重新抓取即可即时更新。报告分别为 `ui-core-full-2026-08-25T02-01-04-386Z.json` 和 `ui-redmine-full-2026-08-25T02-01-27-676Z.json`。

同日精简 Redmine 管理页重复标题后，Linux X11 只读 `ui-redmine-style` 仍为 5/5 通过；基本信息截图确认页面直接从页签和用户/活动/已导入问题内容开始，报告为 `ui-redmine-style-2026-08-25T02-13-43-585Z.json`。

同日统一收紧页面、卡片、表单、工具栏和对话框密度，并将同级主卡片/小型列表卡片间距分别收敛到 6px/4px 后，Linux X11 Debug 构建为 0 警告、0 错误；`ui-settings-full` 9/9、`ui-core-full` 14/14、`ui-extended-full` 11/11、`ui-survey-full` 8/8、`ui-database-error` 8/8、`ui-extra-fields-full` 8/8、`ui-redmine-style` 5/5，共 63/63 步通过。最终报告依次为 `ui-settings-full-2026-08-25T02-31-53-524Z.json`、`ui-core-full-2026-08-25T02-32-19-511Z.json`、`ui-extended-full-2026-08-25T02-32-47-991Z.json`、`ui-survey-full-2026-08-25T02-33-24-091Z.json`、`ui-database-error-2026-08-25T02-34-03-376Z.json`、`ui-extra-fields-full-2026-08-25T02-34-38-712Z.json` 和 `ui-redmine-style-2026-08-25T02-35-03-707Z.json`；`compact-diary.png`、`compact-query.png`、`compact-statistics.png`、`compact-settings-final.png`、`compact-survey.png` 及 Redmine 只读截图确认 1280×800 下无裁切或卡片粘连。

同日扩展标签附加字段套件，新增标签名称过滤、字段/元数据数量摘要以及修改 `SortOrder` 后立即重排的断言；Linux X11 隔离场景 `ui-extra-fields-full` 8/8 通过，报告为 `ui-extra-fields-full-2026-08-25T12-34-35-275Z.json`。

继续消除主窗口 `SplitView`、内容宿主和页面根容器的叠加留白后，主页面四周最终约为 4px；日记页左右两张主卡片改为直接保留 4px 间距，并移除已经由卡片边框替代的独立分隔线。最终 `ui-core-full` 仍为 14/14 通过，报告为 `ui-core-full-2026-08-25T02-46-09-454Z.json`，截图 `compact-diary-no-divider.png`、`compact-query-4px.png` 和 `compact-statistics-4px.png` 确认边缘、完整月历、工具栏和表格均未裁切。

Survey 由 Grid 统一行间距改为各条件卡显式 8px 外边距，修复扩展条件卡隐藏后查询配置与调查结果之间叠加为双倍间距的问题。兼容模式与扩展模式截图 `survey-spacing-compatible.png`、`survey-spacing-extended.png` 的相邻卡片间距一致，`ui-survey-full` 8/8 通过，报告为 `ui-survey-full-2026-08-25T02-49-15-629Z.json`。

脚本工作台 AI 上下文预览框显式设置水平/垂直拉伸和顶部内容对齐；`ui-extended-full` 新增预览框初始高度至少 200px、生成前后高度偏差不超过 1px 的断言，11/11 通过，报告为 `ui-extended-full-2026-08-25T02-57-21-653Z.json`。截图 `manual-ai-context-default.png` 和 `manual-ai-context-work-items.png` 确认预览区域不再随内容高度居中缩放。

新建脚本向导的 CDP 扩展回归会展开 API 版本下拉框，确认 V1/V2 选项内包含对应说明，再在创建 C# 脚本前采集 `manual-script-creation-api-version.png`，并记录 `ScriptCreationView` 的逻辑边界。手册使用该 96 DPI 截图裁切出的 `script-creation-api-version.png`，用于展示默认 V2、可选 V1 和展开项中的版本差异说明。

完整月历选择链路改为读取 Calendar 已提交的选中日期，并解除显示月份与紧凑周历锚点之间的持续绑定，避免选中相邻月份日期时业务层与 Calendar 内部翻月互相推进；选中日期仍保持双向同步以保留键盘操作。Flyout 每次打开显式恢复月视图和当前选中月份，日期点击后立即关闭。Linux X11 `ui-core-full` 14/14 通过，真实验证从 2026 年 8 月点击相邻月“3”后选中 2026 年 9 月 3 日，并覆盖年份视图关闭后重新展开恢复月视图，报告为 `ui-core-full-2026-08-25T03-24-11-830Z.json`。

页面头和区块标题说明统一采用优先横向、底部对齐的紧凑结构；可见说明缩短，长说明通过信息图标或 Tooltip 保留。事项状态胶囊增加语义状态类，核心套件断言新事项为 `StatusWarning`、保存且未配置 Tracker 后恢复中性，同时验证“一般信息”标题与说明左右排列且底边偏差不超过 2px。Linux X11 `ui-core-full` 14/14、`ui-settings-full` 9/9、`ui-extended-full` 11/11、`ui-survey-full` 8/8 通过，报告分别为 `ui-core-full-2026-08-25T03-39-16-779Z.json`、`ui-settings-full-2026-08-25T03-41-00-341Z.json`、`ui-extended-full-2026-08-25T03-40-04-161Z.json` 和 `ui-survey-full-2026-08-25T03-40-20-909Z.json`。

事项备注编辑框必须显式水平、垂直拉伸并保持内容顶部对齐，在 1280×800 核心场景中高度至少为 180px，编辑框底边与备注卡片底边的距离不超过 18px；`ui-core-full` 通过控件实际边界验证，避免主题默认垂直对齐使编辑框退回最小高度并居中显示。Linux X11 实测高度 250px、底边间距 11px，核心套件 14/14 通过，报告为 `ui-core-full-2026-08-25T03-51-37-114Z.json`。

默认无 Tracker 的核心场景必须确认 `TrackerAssociationCard` 不可见，避免编辑器留下只有标题和分隔线的空区域；启用 Tracker 的专项场景继续通过对应编辑器区域和页签验证关联功能可见。Linux X11 `ui-core-full` 14/14 通过，卡片隐藏后备注编辑框高度为 315px、底边间距为 11px，报告为 `ui-core-full-2026-08-25T03-57-31-393Z.json`。

紧凑周历标题必须使用 `yyyy年M月 第X周` 格式，周次与日期右键菜单统一按周一首日、年度第一天所在周为第 1 周计算；核心套件验证今天标题、滚轮后一周标题、相邻月份日期标题和跨月后回到今天均同步更新。Linux X11 实测今天为 `2026年8月 第35周`、滚轮上一周为 `2026年8月 第34周`、跨月浏览为 `2026年7月 第31周`，`ui-core-full` 14/14 通过，报告为 `ui-core-full-2026-08-25T04-10-29-756Z.json`。

## 4. 单套件调试

Redmine 视觉回归必须使用已经加密的隔离 seed profile，并加载插件：

```bash
./Tools/ui-test.sh start --with-plugins --scenario plugins --seed-profile <seed-profile>
./Tools/ui-test.sh run ui-redmine-style
./Tools/ui-test.sh stop
```

`ui-redmine-style` 只切换页面、展开只读信息和截图；不会保存 Tracker 配置，不会同步服务器定义、执行搜索、导入 Issue、创建 Issue 或提交工时。截图输出到 `.build-tmp/ui-test/screenshots/`，API Key 保持密码遮罩。

启动匹配场景后可直接运行 Node 脚本：

```powershell
.\Tools\ui-test.ps1 start -NoBuild -Scenario default
.\Tools\ui-test.ps1 run -Suite ui-core-full
.\Tools\ui-test.ps1 stop
```

脚本管理和编辑器共享同一个 `extended` profile：

```powershell
.\Tools\ui-test.ps1 start -NoBuild -Scenario extended
node .\Tools\ui-extended-full.mjs
node .\Tools\ui-script-editor.mjs
.\Tools\ui-test.ps1 stop
```

日期切换性能专项必须使用新建的隔离 profile，不要传入真实用户 profile。它会一次性生成 25,920 条事项，并按日常使用比例生成 6,480 条备注、33,696 条标签关系和 8,640 条附加字段值，而不是让每条事项都携带全部扩展数据：

```powershell
.\Tools\ui-test.ps1 start -Scenario date-performance -ProfileBase 'D:\DiaryUiPerf'
.\Tools\ui-test.ps1 run -Suite ui-date-performance
.\Tools\ui-test.ps1 stop
```

Linux 等价命令：

```bash
./Tools/ui-test.sh start --scenario date-performance --profile-base /mnt/hdd/diary-ui-perf --display :0
./Tools/ui-test.sh run ui-date-performance
./Tools/ui-test.sh stop
```

`-ProfileBase` / `--profile-base` 决定数据库、WAL、配置和日志实际所在磁盘；验证 HDD 时必须指向目标机械盘，不能只把报告输出到 HDD。专项报告记录逐次切换 P50/P95/P99/最大值、最慢日期、高速连按吞吐、进程 CPU 时间、平均占用核数、工作集与读写字节。测试在预热后记录 SQLite 主文件、WAL 和 journal 的大小与修改时间；120 次逐次切换和两组高速连按结束后这些文件必须保持不变，`-shm` 因锁状态变化不作为写入失败依据。进程总写入超过 1 MiB 或工作集增长超过 256 MiB 会产生 warning，但不直接失败；这些值用于同一 Windows HDD 和同一构建方式下的趋势比较。

远程 PostgreSQL 使用同一个 `date-performance` 场景，但必须连接专用空数据库，不能指向用户数据库。Debug 进程通过环境变量临时覆盖数据库配置，不保存密码；运行 Node 套件的终端也必须保留这些变量，并安装可执行的 `psql`，用于读取数据摘要及 `pg_stat_database` 插入、更新、删除计数：

```bash
export DIARY_UI_TEST_PG_HOST='<postgres-host>'
export DIARY_UI_TEST_PG_PORT='5432'
export DIARY_UI_TEST_PG_DATABASE='diary_cdp_test'
export DIARY_UI_TEST_PG_USER='<test-user>'
export DIARY_UI_TEST_PG_PASSWORD='<test-password>'
./Tools/ui-test.sh start --scenario date-performance --port 9333 --display :0
./Tools/ui-test.sh run ui-date-performance
./Tools/ui-test.sh stop
```

PostgreSQL 模式会使用 `generate_series` 在单一事务中生成相同规模的数据；预热后比较事项数量、ID/标题长度/工时摘要、备注/标签/附加字段数量和数据库写入计数。纯日期浏览导致业务摘要变化，或 `tup_inserted`、`tup_updated`、`tup_deleted` 增加时直接失败。默认启动仍带 `--core-only`，用于获得未加载 Tracker 的核心基线；传入 `--with-plugins` / `-WithPlugins` 后，Debug 场景默认临时启用一个不主动联网的 Jira 实例。设置 `DIARY_UI_TEST_REDMINE_URL`、`DIARY_UI_TEST_REDMINE_API_KEY`、`DIARY_UI_TEST_REDMINE_ACTIVITY_IDS` 和 `DIARY_UI_TEST_REDMINE_ISSUE_IDS` 时则改用真实 Redmine 配置和真实活动/Issue ID，但仍只创建本地绑定，不搜索、创建或上传远程数据。Tracker 模式为 20% 的事项生成 5,184 条绑定，按日期内稳定顺序取样，540 天每天都有 9～10 条关联；已有的按整天聚集旧分布会自动重建。临时配置和凭据不持久化：

```bash
./Tools/ui-test.sh start --scenario date-performance --with-plugins --port 9333 --display :0
./Tools/ui-test.sh run ui-date-performance
./Tools/ui-test.sh stop
```

真实 Redmine 对照测试必须在 App 启动和 Node 套件两个进程中传入相同环境变量。API Key 只应存在于当前 shell 或受保护的本地凭据文件中，不写入脚本、报告或文档。测试前后可通过 Redmine 只读 API 比较当前用户工时总数，确认专项没有远程副作用。

附加字段套件使用独立场景：

```powershell
.\Tools\ui-test.ps1 start -NoBuild -Scenario extra-fields
node .\Tools\ui-extra-fields-full.mjs
.\Tools\ui-test.ps1 stop
```

Linux 的 `run` 会自动传入当前状态文件，不需要手动拼接 `--state`：

```bash
./Tools/ui-test.sh start --no-build --scenario extended --display :0
./Tools/ui-test.sh run ui-extended-full
./Tools/ui-test.sh run ui-script-editor
./Tools/ui-test.sh stop
```

测试脚本使用 `Tools/ui-cdp.mjs` 的原始 WebSocket 客户端和稳定 `Name`/控件类型/可见文字定位。关键操作根据控件行为使用鼠标或 `DOM.focus` 配合键盘触发；菜单、列表选择和异步命令会等待可观察状态，并在输入偶发丢失时执行有限重试。导航完成条件是目标 View 已可见，不以点击命令返回作为完成信号。

### 4.1 主导航冷热切换性能

`ui-navigation-performance` 在单个新进程中先访问所有未打开页面，再按正序和倒序重复切换。测试会自动要求日记记录、事项查询、统计工具、调查工具和脚本管理五个核心页面；使用 `--with-plugins` 加载已有 Tracker 配置后，还会把可见的 Tracker 管理页加入清单。每次操作分别记录输入派发、目标页面可见和视觉树连续稳定三个时间，并采集 CPU、工作集及进程 I/O 增量。

单进程调试：

```powershell
.\Tools\ui-test.ps1 start -NoBuild -Scenario navigation-performance
.\Tools\ui-test.ps1 run -Suite ui-navigation-performance
.\Tools\ui-test.ps1 stop
```

```bash
./Tools/ui-test.sh start --no-build --scenario navigation-performance --display :0
./Tools/ui-test.sh run ui-navigation-performance
./Tools/ui-test.sh stop
```

正式比较应使用跨进程编排器。它每轮创建新的隔离 profile 和 DiaryApp 进程，轮换首次访问顺序，并生成 JSON 与 Markdown 汇总报告：

```powershell
node .\Tools\ui-navigation-performance-run.mjs --runs 5 --hot-rounds 5 --mode core
```

```bash
node Tools/ui-navigation-performance-run.mjs --runs 5 --hot-rounds 5 --mode core --display :0
```

`core` 模式不加载 Tracker 插件，适合作为稳定基线。`full` 模式加载插件；需要通过现有加密 profile 提供已启用且带管理页的 Tracker 配置：

```bash
node Tools/ui-navigation-performance-run.mjs --runs 5 --hot-rounds 5 --mode full \
  --seed-profile '<encrypted-profile>' --require-dynamic-page
```

常用参数：

- `--runs <n>`：新进程次数，默认 5；正式计算冷切换 P95 建议至少 20 次。
- `--hot-rounds <n>`：每个进程内热切换轮数，默认 5；每轮让每个页面恰好成为一次目标页。
- `--preload-wait-ms <n>`：日记页稳定后等待空闲导航预热的时间，默认 1800 ms；比较缓存优化时应显式使用相同值。
- `--build`：编排开始前执行一次 Debug 构建；默认要求调用者已构建并以 `--no-build` 启动各轮。
- `--require-dynamic-page`：要求至少出现一个 Tracker 动态管理页，否则测试失败。
- `--seed-profile <path>`：只复制已有加密配置文件，不读取正常用户 profile，也不把配置或凭据写入报告。

这里的“冷切换”指新进程内首次把目标 View 挂载到主视觉树，包括首次 XAML/样式加载、相关 JIT、真实父级布局、`OnShow()` 和同步可观察的数据加载；空闲预热可能已经在离屏状态创建并测量该 View。“热切换”复用同一 ViewModel 实例对应的同一 View，不再重复创建控件树。日记页是启动默认页，因此单独报告启动到日记页稳定时间，其后返回日记页的样本不计入其他页面的冷切换汇总。测试不清理操作系统文件缓存，结果属于“新进程冷启动”，不是磁盘完全冷启动。

## 5. 报告和判定

输出目录：

- `.build-tmp/ui-test/reports/*.json`
- `.build-tmp/ui-test/screenshots/*.png`
- `.build-tmp/ui-test/screenshots/raw-physical/*.png`
- `.build-tmp/ui-test/profiles/*`
- `.build-tmp/ui-test/logs/*.log`

单套件报告包含场景、profile、冷启动时间、步骤耗时、断言结果、性能样本和 finding。全量报告汇总每个套件的 `passed`、`failed` 或 `blocked-external` 状态。

保存到 `screenshots/` 根目录的图片是供验收和用户手册使用的逻辑 1× 截图。公共截图工具先读取 `Page.getLayoutMetrics` 的当前窗口逻辑视口；Windows 使用 `PrintWindow` 捕获真实窗口合成表面，Linux 使用 `Page.captureScreenshot`，再按物理像素与逻辑视口的实际比例归一化并写入 96 DPI 元数据。例如 Windows 150% 缩放下的 1942×1256 原图会输出为约 1295×837 的手册图。缩放倍率由当前窗口自动推导，不读取或硬编码系统 DPI，因此支持多显示器上的 100%、125%、150%、175% 和 200% 等不同倍率。物理像素与逻辑尺寸不一致时，未经缩放的原图同时保存在 `screenshots/raw-physical/`，只用于高 DPI 裁切、像素边界和渲染清晰度复核。

截图报告保留兼容字段 `path`、`bytes` 和 `sha256`，并增加 `width`、`height`、`dpi`、`captureSource`、`normalized`、`renderScale`、`physicalWidth`、`physicalHeight` 和可选 `physicalPath`。编写手册时应从 `screenshots/` 根目录取图后再做脱敏或裁剪，不应直接使用 `raw-physical/`。PNG 归一化实现不依赖第三方 Node 图像包，可单独验证：

```powershell
node --test Tools/ui-screenshot.test.mjs
```

判定规则：

- 功能断言失败、对话框未关闭、目标页面未出现或状态未持久化均为 `failed`。
- 外部服务配置未提供时为 `blocked-external`，不计作功能通过。
- 性能值用于同一机器、同一构建方式下的趋势比较，不作为跨机器固定发布阈值。日期性能专项的默认 warning 线为逐次切换 P95 300ms、最大值 1.5s、24 次高速切换 8s、进程写入 1 MiB 和工作集增长 256 MiB。
- 主导航性能专项暂以核心页面热切换可见 P95 300ms、Tracker 页面 800ms、核心页面首次可见 2s、Tracker 页面 3s 和热切换工作集增长 128 MiB 作为 warning，不作为跨机器硬门禁。
- 首次页面创建包含视图构造和数据加载，应与预热后的视觉树/动作耗时分开观察。
- 原生文件选择器、目录选择器、系统托盘和真实备份/还原不由 Avalonia CDP 控制，保留 Windows 原生人工或专用驱动验证。

## 6. 2026-08-22 本机基线

最终全量报告：`.build-tmp/ui-test/reports/ui-full-test-2026-08-22T06-09-21-453Z.json`。

| 项目 | 结果 |
| --- | ---: |
| 全量套件 | 9 / 9 passed |
| 结构化步骤 | 72 / 72 passed |
| smoke | passed，含 1 个默认日期与当天不同的 warning |
| 全量耗时 | 91,288 ms |
| Debug 冷启动到 CDP ready | 1,516–2,510 ms |
| core 视觉树 P50 / P95 | 4.82 / 8.40 ms |
| core 全窗口截图 P50 / P95 | 120.05 / 138.42 ms |
| smoke `DOM.getDocument` P50 / P95 | 7.40 / 20.67 ms |
| smoke `DOM.querySelector` P50 / P95 | 0.58 / 0.76 ms |
| smoke 截图 P50 / P95 | 122.92 / 135.29 ms |
| 设置对话框三次打开 P95 | 138.59 ms |
| 脚本页刷新最大值 | 49.28 ms |
| Survey 视觉树 P95 / 动作 P95 | 7.86 / 21.05 ms |
| 附加字段完整套件 | 15,769 ms |
| Redmine 工时同步 | 235.94 ms |
| Redmine 管理页导航 P95 | 107.69 ms |

稳定性收口还使用两个全新 profile 连续执行 `ui-core-full`，两次均为 14/14 passed，用于确认删除取消对话框和保存查询删除取消不会遮挡后续页面。

本轮运行日期和系统日期均为 2026-08-22，隔离 profile 目录名中的 `20260822` 与实际日期一致。smoke 的日期 warning 表示新建事项继承的当前编辑日期为 2026-08-06；执行“使用今天”后正确切换为 2026-08-22，不影响草稿保留和本地保存断言。

Redmine 套件使用隔离测试服务并产生可清理测试数据；报告只记录耗时、数量和远程 ID 摘要，不包含服务器地址、账号或凭据。

该 Windows 历史全量报告生成后，`ui-extended-full` 新增 AI 上下文和程序设置 MCP 配置两个步骤，随后又增加 5 步 `ui-redmine-style`。当前常规全量中的 8 个结构化套件合计 74 步；独立只读 `ui-redmine-style` 另有 5 步。历史报告中的 72/72 数字保持不变，避免把未执行的新步骤写入旧报告。

富数据日期切换专项使用 10 个日期、每日 14 条事项、250 个 Redmine Issue，并为每条事项设置 Tracker 绑定、2 个标签和 9 类附加字段。相同 CDP 脚本连续切换 12 次日期：优化前中位数约 149 ms、P95/最大约 218 ms；批量预取附加字段并共享 Tracker 选项后，首轮中位数约 127 ms、P95 约 194 ms，热身后的连续切换中位数约 80 ms、P95 约 135 ms。该结果用于本机趋势比较，不作为跨机器固定阈值。

同一富数据 profile 还验证了“复制最近”：源事项包含 Redmine 绑定、2 个标签和 9 类附加字段。修复前复制事务回滚；修复后目标事项成功落在 2026-08-22，两个标签和 9 个字段值与源事项一致，Redmine/Jira 绑定数为 0，符合确认框“不复制远程 Tracker 绑定”的语义。测试新增记录随后已删除。

### 6.1 2026-08-23 Linux X11 验证

Linux 原生 Debug App 在 `DISPLAY=:0`、1280×800 桌面会话中完成验证：生命周期工具可在命令退出后保持独立 App 会话，并由后续 `status/run/stop` 命令接管。最终视觉复检结果为 `ui-settings-full` 9/9、`ui-smoke` passed、`ui-core-full` 14/14、`ui-extended-full` 11/11、`ui-script-editor` 4/4、`ui-survey-full` 8/8、`ui-database-error` 8/8、`ui-extra-fields-full` 8/8、`ui-redmine-style` 5/5；共 67 个本轮实际执行的结构化步骤和 smoke 通过。会产生远程写入的 `ui-redmine-full` 未纳入本轮只读视觉复检。

本轮 Debug 冷启动到 CDP ready 为 1,318–1,839 ms，core 截图 P50/P95 为 54.37/71.02 ms，smoke `DOM.getDocument` P50/P95 为 6.89/13.13 ms；最新扩展套件耗时 9,152 ms。Redmine 两个筛选 CheckBox 与搜索框中心线偏差均为 0px。

`ui-core-full` 还会打开版本号菜单，确认“检查更新”快捷入口可见且可用；随后打开应用图标菜单并确认 Debug 构建不显示发布版“用户手册”入口。Release 不编译 CDP，因此 Release 侧由单元测试验证菜单绑定和文件解析，并由 Tag/手动工作流的 ZIP 契约检查 HTML/PDF；系统默认浏览器或 PDF 阅读器的实际启动仍属于原生人工检查。

### 6.2 2026-08-24 SQLite 初始密集数据基线

`ui-date-performance` 在 Linux X11 Debug 构建完成 6/6 步。测试 profile 位于 NVMe/Btrfs，不代表 Windows HDD 性能，但用于验证场景、键盘事件、指标采集和数据库无写入断言已经完整串通。该轮使用每条事项 2 个标签和 1 个附加字段的初始密集分布；场景后来改为稀疏日常分布，因此该报告保留为历史实现基线，不与后续稀疏结果直接比较。

| 项目 | 结果 |
| --- | ---: |
| 富数据规模 | 540 天 × 48 条，共 25,920 条事项 |
| 逐次切换 | 120 次 |
| P50 / P95 / P99 | 76.73 / 152.14 / 166.97 ms |
| 最大值 / 平均值 | 189.68 / 88.11 ms |
| 高速连按 | 前进 17.34 次/秒，后退 15.64 次/秒 |
| SQLite 主文件/WAL/journal 变化 | 0 |
| 测量阶段进程写入 | 36 KiB，数据库文件无变化 |
| 平均 CPU | 1.67 核 |
| 工作集增长 | 394.23 MiB，产生 warning |

报告：`.build-tmp/ui-test/reports/ui-date-performance-2026-08-24T05-52-51-388Z.json`。工作集增长可能包含 Debug/JIT、Skia/Avalonia 缓存和 GC 尚未归还的内存，不能仅凭一次样本判定泄漏；后续需要在 Windows HDD 上重复运行并增加稳定等待或强制 GC 对照。

### 6.3 2026-08-24 四组稀疏日常数据对照基线

同一 Linux X11 Debug 构建按相同口径依次运行 SQLite Core-only、SQLite + 真实 Redmine、远程 PostgreSQL Core-only、远程 PostgreSQL + 真实 Redmine，四组均完成 6/6 步。每组使用 25,920 条事项、6,480 条备注、33,696 条标签关系和 8,640 条附加字段值；Tracker 组另有 5,184 条本地 Redmine 绑定。修正后的取样在 540 天每天生成 9～10 条关联，2026-08-24 为 48 条事项中的 10 条。测试只读取 Redmine 当前用户、活动和 Issue，并在测试前后确认远程工时总数保持 6 条。

| 模式 | P50 | P95 | P99 | 平均值 | 高速前进/后退 | 平均 CPU | 工作集增长 | 进程写入 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| SQLite Core-only | 70.74 ms | 105.63 ms | 122.15 ms | 73.64 ms | 18.79 / 18.35 次/秒 | 1.68 核 | 392.94 MiB | 36 KiB |
| SQLite + Redmine | 85.67 ms | 161.17 ms | 180.75 ms | 93.31 ms | 14.19 / 14.35 次/秒 | 1.58 核 | 442.38 MiB | 28 KiB |
| PostgreSQL Core-only | 113.27 ms | 163.88 ms | 183.62 ms | 118.89 ms | 7.38 / 6.85 次/秒 | 1.05 核 | 401.83 MiB | 44 KiB |
| PostgreSQL + Redmine | 242.70 ms | 301.78 ms | 325.47 ms | 243.83 ms | 3.90 / 3.92 次/秒 | 0.79 核 | 455.57 MiB | 40 KiB |

SQLite 加载 Redmine 后 P50 增加约 21%、P95 增加约 53%，平均值增加约 27%，高速连按吞吐下降约 22%～24%。PostgreSQL 加载 Redmine 后 P50 增加约 114%、P95 增加约 84%、P99 增加约 77%，平均值增加约 105%，高速连按吞吐下降约 43%～47%；P95 略高于 300 ms warning 线。四组的 SQLite 主文件/WAL/journal 或 PostgreSQL 业务摘要及插入、更新、删除计数均未变化，因此差异主要反映 Tracker 绑定查询、编辑扩展创建、选项同步和视觉树构造，而不是浏览导致的落盘写入。

报告：

- SQLite Core-only：`.build-tmp/ui-test/reports/ui-date-performance-2026-08-24T07-41-26-920Z.json`
- SQLite + Redmine：`.build-tmp/ui-test/reports/ui-date-performance-2026-08-24T07-42-25-161Z.json`
- PostgreSQL Core-only：`.build-tmp/ui-test/reports/ui-date-performance-2026-08-24T07-43-46-283Z.json`
- PostgreSQL + Redmine：`.build-tmp/ui-test/reports/ui-date-performance-2026-08-24T07-44-57-230Z.json`

上述数据作为优化前基线保留。SQL Trace 确认，Redmine 日期加载虽然已经批量读取绑定，但无绑定事项收到的 `null` 又被编辑扩展解释为“尚未查询”，当天 48 条事项、10 条绑定时会额外执行 38 次单事项查询，总查询数由理论 5 次扩大为 43 次；SQLite 与 PostgreSQL 使用相同逻辑，因此都存在该问题。优化后由批量加载接口明确区分“未预取”和“已确认无绑定”，并复用当天事项 ID 批量读取备注、标签、附加字段和 Tracker 绑定，Redmine/Jira SQLite 与 PostgreSQL provider 均按最多 500 个 ID 一批查询，日期加载固定为 5 次数据库查询。

同一构建和数据集完成优化后单轮复测，四组均通过 6/6 步：

| 模式 | P50 | P95 | P99 | 平均值 | 高速前进/后退 | 平均 CPU | 工作集增长 | 进程写入 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| SQLite Core-only | 68.63 ms | 131.43 ms | 147.23 ms | 76.05 ms | 19.00 / 18.23 次/秒 | 1.65 核 | 388.87 MiB | 36 KiB |
| SQLite + Redmine | 82.83 ms | 164.67 ms | 172.19 ms | 90.11 ms | 14.11 / 14.77 次/秒 | 1.61 核 | 439.95 MiB | 28 KiB |
| PostgreSQL Core-only | 118.41 ms | 167.87 ms | 184.00 ms | 126.35 ms | 7.72 / 7.03 次/秒 | 1.08 核 | 395.05 MiB | 44 KiB |
| PostgreSQL + Redmine | 131.99 ms | 217.01 ms | 287.92 ms | 145.23 ms | 6.03 / 5.90 次/秒 | 1.07 核 | 457.27 MiB | 36 KiB |

PostgreSQL + Redmine 相对优化前 P50 降低约 46%、P95 降低约 28%、平均值降低约 40%，高速前进/后退吞吐提高约 55%/51%，P95 已低于 300 ms warning 线。SQLite + Redmine 的 P50 和平均值分别降低约 3% 和 3%，P95 单轮增加约 2%，属于当前 Debug 单轮波动范围；查询数同样从 43 次降为 5 次，但本地往返成本低，因此端到端收益不如远程 PostgreSQL 明显。Core-only 两组也有单轮尾延迟波动，需以同机多轮结果判断趋势，不能据此认定回归。

优化后报告：

- SQLite Core-only：`.build-tmp/ui-test/reports/ui-date-performance-2026-08-24T09-14-12-859Z.json`
- SQLite + Redmine：`.build-tmp/ui-test/reports/ui-date-performance-2026-08-24T09-14-48-838Z.json`
- PostgreSQL Core-only：`.build-tmp/ui-test/reports/ui-date-performance-2026-08-24T09-15-38-211Z.json`
- PostgreSQL + Redmine：`.build-tmp/ui-test/reports/ui-date-performance-2026-08-24T09-17-52-091Z.json`

优化后四组的 SQLite 文件状态或 PostgreSQL 业务摘要与写入计数仍未变化；本地 Redmine 绑定保持 5,184 条、每天 9～10 条，远端工时保持 6 条。四组工作集仍增长超过 256 MiB 并产生 warning；该值可能包含 Debug/JIT、Skia/Avalonia 缓存和 GC 尚未归还内存，需要在 Windows HDD 上多轮复测后判断。剩余日期切换成本主要位于每事项编辑扩展创建、选项同步和视觉树构造。

扩展套件使用隔离 profile 预置一个标签、一项附加字段定义和一条示例事项，验证事项披露默认关闭、日期控件仅在显式授权后显示、预览包含版本化 schema 和不可信数据标记，以及刷新后的 MCP 快照只包含授权范围。随后打开程序设置，验证“AI 与 MCP”使用标准设置分组、五个设置行完整可见、复制内容中的可执行文件/快照路径、AI 可读说明和通用 JSON，并通过“打开 AI 上下文”返回正确页签。截图前会将最后一个操作行滚动到可视区域；用户手册版本已裁掉状态栏和 MCP 命令中的本机路径。

smoke 在较矮窗口中只渲染前三个列表项，第 4 个模板事项会被 ListBox 虚拟化。最终持久化断言因此读取隔离 SQLite profile，确认标题、1.5 小时和标签关联已提交；UI 侧仍验证模板应用、保存状态和页面导航。smoke 还会在通知层消退后生成程序设置、标签和模板手册截图；Redmine 只读套件生成插件状态和管理页截图。这些保存到截图根目录的手册图统一为逻辑 1×/96 DPI，150% 等高 DPI 原图保存在 `raw-physical/`。当前机器未安装 Xvfb，因此本轮没有宣称 headless CI 已通过。

2026-08-26 用户手册复核使用当前 `1.0.1-r564` Linux X11 Debug 界面重新采集程序设置、标签、附加字段、模板操作和 V2 参数表单。写入手册的新增或替换图片必须从逻辑 1× 原图继续裁到目标控件、卡片或对话框主体，保留识别入口所需的最少上下文，不直接放入完整主窗口；未裁切原图仍只保存在 `.build-tmp/ui-test/screenshots/` 供复核。本轮 smoke 完整通过，`ui-extra-fields-full` 8/8 通过；精简 V2 参数辅助说明后，`ui-extended-full` 11/11 通过并自动生成参数表单原图，手册版本进一步裁切为仅包含运行对话框的关键区域。

同日将标签编辑器页头和当前标签说明从小尺寸信息图标 Tooltip 改为直接显示，并移除备注区与“仅本地”状态重复的说明图标。smoke 重新生成标签基础信息原图并通过；`ui-extra-fields-full` 增加附加字段手册原图输出，菜单入口改为点击实际 `MenuItem` 容器后 8/8 通过；`ui-core-full` 14/14 通过并验证备注区直接显示“仅本地”、不再依赖旧 Tooltip。两张标签图片写入手册前均继续裁到标签对话框关键区域。

### 6.4 2026-08-26 主导航 core 冷热切换基线

Linux X11 Debug 构建使用 `navigation-performance` 场景连续启动 5 个新进程，每个进程执行 3 轮热切换。日记、查询、统计、调查和脚本五个核心页面全部出现，20 次首次访问和 75 次热切换均成功，没有产生性能 warning。该轮只用于验证新工具和建立本机 core 基线，不包含 Tracker 动态管理页。

| 页面 | 冷切换 P50 | 热切换 P50 | 热切换 P95 | 首次访问惩罚 |
| --- | ---: | ---: | ---: | ---: |
| 事项查询 | 285.32 ms | 193.98 ms | 250.26 ms | 1.47× |
| 统计工具 | 489.18 ms | 149.33 ms | 216.80 ms | 3.28× |
| 调查工具 | 247.90 ms | 165.85 ms | 231.58 ms | 1.49× |
| 脚本管理 | 304.40 ms | 120.30 ms | 223.74 ms | 2.53× |
| 日记记录 | 启动默认页 | 134.84 ms | 177.36 ms | — |

CDP Ready P50/P95 为 2,727/2,803 ms，进程启动至日记页视觉树稳定 P50/P95 为 4,158/4,949 ms。每轮 3 次热切换后工作集平均增长约 63.93 MiB，进程数据读取和写入字节均为 0；这包含 Debug、Avalonia/Skia 视觉资源和 View 重建缓存，后续应在 Windows 测试机上使用更多轮数观察增长是否趋稳。

汇总报告：`.build-tmp/ui-test/reports/ui-navigation-performance-aggregate-2026-08-26T03-19-09-497Z.json`。

### 6.5 2026-08-26 每实例 View 缓存优化复测

主窗口导航启用每 ViewModel 实例一个 View 的弱引用缓存，并在窗口打开后空闲预热可缓存主页面；缓存资格默认关闭，`WorkEditorViewModel`、对话框和 Tracker 编辑区域继承默认行为，只有主导航页面显式加入缓存。Linux X11 Debug 构建再次启动 5 个新进程，每个进程等待预热 2200 ms 后执行 3 轮热切换，20 次首次访问和 75 次热切换全部成功。

| 页面 | 优化前冷 P50 | 优化后冷 P50 | 优化前热 P50 | 优化后热 P50 | 热 P50 变化 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 事项查询 | 285.32 ms | 258.20 ms | 193.98 ms | 94.24 ms | -51.4% |
| 统计工具 | 489.18 ms | 501.09 ms | 149.33 ms | 144.37 ms | -3.3% |
| 调查工具 | 247.90 ms | 186.52 ms | 165.85 ms | 77.27 ms | -53.4% |
| 脚本管理 | 304.40 ms | 231.79 ms | 120.30 ms | 85.59 ms | -28.9% |
| 日记记录 | 启动默认页 | 启动默认页 | 134.84 ms | 89.08 ms | -33.9% |

优化后 CDP Ready P50/P95 为 2,517/2,631 ms，进程启动至日记页稳定 P50/P95 为 4,146/4,317 ms。查询、调查、脚本和返回日记的热切换明显下降；统计页冷切换和热切换变化有限，说明其主要成本仍位于首次接入真实视觉树后的样式、模板、布局或页面刷新。当前方案保持较低生命周期风险，不把全部页面常驻挂载；如果继续优化统计页，应先对首次挂载阶段做细分采样。

优化后汇总报告：`.build-tmp/ui-test/reports/ui-navigation-performance-aggregate-2026-08-26T04-29-17-570Z.json`。缓存资格、实例所有权和预热边界见 [`UiNavigationViewCachingDesign.md`](UiNavigationViewCachingDesign.md)。

### 6.6 2026-08-26 持久挂载与统计按需初始化复测

主导航改为专用 `NavigationViewHost`：可缓存页面在空闲预热时进入真实视觉树并保持挂载，切换只改变显示和命中状态；统计页构造阶段不再查询所有页签，也不提前创建 LiveCharts 和 `TreeDataGrid`。CDP 树构建同步向子节点传播祖先不可见/禁用状态，避免隐藏缓存页面参与定位。

Linux X11 Debug 构建使用相同 2200 ms 预热等待运行 5 个新进程，20 次首次访问和 75 次热切换全部成功：

| 页面 | 上一轮冷 P50 | 本轮冷 P50 | 上一轮热 P50 | 本轮热 P50 |
| --- | ---: | ---: | ---: | ---: |
| 事项查询 | 240 ms | 108 ms | 103 ms | 37 ms |
| 统计工具 | 512 ms | 387 ms | 183 ms | 34 ms |
| 调查工具 | 196 ms | 71 ms | 89 ms | 33 ms |
| 脚本管理 | 207 ms | 86 ms | 94 ms | 35 ms |
| 日记记录 | 启动默认页 | 启动默认页 | 100 ms | 32 ms |

随后把统计初始化调度到后台优先级，3 个新进程补充复测中统计冷 P50 进一步降至 339 ms，其余页面冷 P50 为 64–73 ms、热 P50 为 31–41 ms。`ui-core-full` 14/14 通过；查询结果定位回归说明 CDP 的局部 `IsVisible` 不能代表祖先有效可见性，公共树构建器已统一归一化。

5 进程汇总报告：`.build-tmp/ui-test/reports/ui-navigation-performance-aggregate-2026-08-26T06-02-05-541Z.json`；3 进程补充报告：`.build-tmp/ui-test/reports/ui-navigation-performance-aggregate-2026-08-26T06-11-36-248Z.json`。

## 7. 当前覆盖边界

- Jira 真实服务、权限矩阵和自托管版本差异为 `Blocked-External`。
- 原生文件/目录选择器、托盘、真实备份与还原为 `Manual-Native`。
- 查询/统计计算、附加字段值转换和 Tracker 数据契约等低层语义继续由单元和集成测试承担；附加字段的真实控件交互和持久化已由 CDP 套件覆盖。
- 后台任务进度预览 UI 尚未实现，状态为 `Not-Implemented`。
- Release 不运行 CDP；“Release 包不含 CDP”和“应用 ZIP 携带稳定路径用户手册”由构建和发布包集成校验负责。
- 脚本交互式 XLSX/CSV/DOCX/Mustache 导出仍缺少真实 UI 端到端门禁。

## 8. CDP 兼容性

已在 Windows 和 Linux 验证 `DOM.getDocument`、`DOM.querySelector`、`DOM.getBoxModel`、`DOM.focus`、`Input.dispatchMouseEvent`、`Input.dispatchKeyEvent`、`Input.insertText`、`Page.getLayoutMetrics` 和 `Page.captureScreenshot`。当前预览版 CDP 的 `Page.captureScreenshot` 会返回 Avalonia 后备缓冲区的物理像素、忽略 `clip.scale`，并在 Windows 高 DPI 下对 `OverlayDialog` 子内容重复应用窗口缩放；DOM Bounds 正确，但截图中的 overlay 会错位放大。Windows 保存截图因此改用 `Tools/ui-window-screenshot.ps1` 的 `PrintWindow` 真实窗口捕获，CDP 仍负责逻辑尺寸和交互。Playwright 的 `connectOverCDP()` 可以建立连接，但高层截图调用可能等待超时，因此项目脚本继续使用原始 CDP 命令。

Linux 下部分 Avalonia 控件收到第一组坐标点击时只获取焦点，公共点击辅助函数会先执行 `DOM.focus` 再发送鼠标事件；带 Ctrl/Alt/Meta 的快捷键不再额外发送 `char` 事件，避免 `Ctrl+S` 保存后字符 `s` 进入文本框。状态栏日期断言按年月日数值匹配，不依赖 `yyyy/M/d` 或 `MM/dd/yyyy` 等区域格式。
