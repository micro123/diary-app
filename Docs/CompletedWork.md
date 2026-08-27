# DiaryApp 已完成工作记录

本文记录已经完成的项目，作为 Docs/TODOS.md 的历史归档。当前未完成、进行中和后续计划仍以 [`TODOS.md`](TODOS.md) 为准。

本文件只记录完成结果，不作为当前待办列表。

## 2026-08-27 日记日期快捷导航

- [x] 新增 `Alt+J/K/L/;` 日期导航：`J` 前一天、`;` 后一天、`K` 后一周、`L` 前一周。快捷键仅在日记记录页面显示时响应；主窗口在键盘隧道路由阶段捕获，并优先按物理键识别，兼容编辑控件焦点、输入法和键盘布局。继续复用紧凑日历原有日期切换与自动保存保护，不提供会与平台返回手势冲突的 `Alt+方向键`。核心 CDP 覆盖四键导航、非日记页面作用域和标题输入框焦点场景，16/16 通过，报告为 `ui-core-full-2026-08-27T05-41-36-769Z.json`。

## 2026-08-27 空日期首次进入有事项日期性能优化

- [x] 日记页右侧详情区改为在页面生命周期内固定持有单一 `WorkEditorView`；事项切换只更换 `DataContext` 并同步解绑/绑定 ViewModel。初始日期为空时，页面稳定 250 ms 后在后台绑定一个不进入列表、不保存的轻量占位 ViewModel，以透明且不可交互的方式完成真实模板、Tracker 子区和布局预实现；首个真实事项接管后退出预热状态并释放占位模型。
- [x] 日期加载改为先在内存构建并排序工作项编辑器，再一次性替换 `DailyWorks`，避免逐条添加导致左侧列表卡片重复创建和布局；不改变工作项、Tracker 或数据库结构。
- [x] 增加 `date-cold-performance` 隔离场景和 4 步 CDP 套件，预置“今天为空、昨天 12 条且部分带标签、附加字段和备注”的数据。Linux 当前桌面 `DISPLAY=:1` 下 4 个全新进程的冷切换为 264.54–331.12 ms，中位数 295.48 ms；比纯单实例中位数 354.92 ms 再降低约 16.7%，比上一版丢弃式预热约 630.95 ms 降低约 53.2%。
- [x] Headless 测试覆盖单实例在 ViewModel 间解绑和重绑，排序测试覆盖内存快照顺序与源集合不变；App 测试 277/277 通过。25,920 条 SQLite 数据的常规 120 次切换 P50/P95/P99 为 58.13/114.55/159.37 ms，主文件和 WAL 在浏览阶段未发生业务写入。
- [x] 新增 `ui-work-item-performance` 同日事项切换专项，覆盖首次逐项接管、多轮热切换、快速连续切换、虚拟化列表、单实例稳定性和 SQLite 无写入。4 个新进程的无 Tracker 热切换 P50/P95 中位数为 35.37/64.91 ms；48 条事项、Jira 本地 Tracker 和约 20% 本地绑定下为 37.14/54.20 ms，首次逐项 P50/P95 为 67.44/87.27 ms。

## 2026-08-26 程序脚本独立运行入口

- [x] 主窗口设置菜单新增“运行程序脚本…”；默认场景无需启用开发者功能或显示脚本管理导航，即可打开轻量选择器。
- [x] 选择器仅列出构建成功、配置完整的 `ScriptEntryKind.Application` 脚本，继续复用现有参数对话框、参数记忆、Preview、超时、Worker 执行、历史记录和全局结果通知；Automation、Query 与 Editor 保持原有入口边界。
- [x] 增加选择器 ViewModel 单元测试和 `ui-core-full` 默认场景断言；Debug 解决方案构建 0 警告、0 错误，脚本相关定向测试 13/13 通过。默认场景目标 UI 步骤已通过，完整套件随后停在既有月份标题右键菜单断言，与本次入口无关。

## 2026-08-26 自定义对话框视觉统一

- [x] 统一重设计关于、数据库配置、标准消息、标签共享导出、标签附加字段、复制整天、数据库迁移、事项附加信息和批量同步预览：复用 `PageHeader`、`DetailPane`、`SectionCard`、`SubtleCard` 与 `DialogFooter`，保持原命令、控件名称、按钮文案和滚动可达性。
- [x] 标准消息和单字段数据库配置改为内容自适应高度；标签共享导出去除内嵌遮罩的重复卡片边框；事项附加信息在整体或单字段只读时隐藏清空入口，已同步内容仍可查看。
- [x] 崩溃报告独立进程加载主界面基础样式，使用可拖动的应用内标题栏，并按错误摘要、Dump、日志和敏感信息提示分组；异常消息与路径保持可选择复制，独立启动和低业务依赖边界不变。
- [x] Debug 解决方案构建 0 警告、0 错误；`ui-settings-full` 9/9、`ui-extra-fields-full` 8/8 通过，并完成目标对话框和崩溃报告窗口截图复核。

## 2026-08-26 通知中心单层边框


- [x] 通知中心 Flyout Presenter 改为透明、无边框、无内边距，只保留内部面板的单层圆角轮廓；通知卡片轻边框不受影响。
- [x] “清空全部”使用 Semi 主题的危险前景色，与“清空已读”的普通主色操作明确区分；核心 CDP 通过真实会话通知验证危险样式类，并以正式应用截图复核最终视觉结果，Linux X11 实测 16/16 通过，报告为 `ui-core-full-2026-08-27T04-33-52-452Z.json`。
## 2026-08-26 Time 附加字段环形编辑器

- [x] 工作事项和字段默认值中的 `Time` 类型改用紧凑 24 小时表盘，小时环显示每 3 小时一个的 8 个方向数字；小时松开后切换到分钟，分钟支持按住拖动并在松开时立即应用和关闭。
- [x] 分钟保持 1 分钟精度；每 5 分钟显示常驻数字，选中其他分钟时强制绘制准确数字。`DateTime` 使用日历与同一环形控件组合；保留清空、只读、弹层外取消和 `Esc` 取消语义。
- [x] 补充 Headless 控件测试和 CDP 指针拖动验证；`ClockTimePickerTests` 与字段模型定向测试 18/18、`ui-extra-fields-full` 8/8 通过。
## 2026-08-26 通知中心与历史保留策略

- [x] Toast 和 Notification 增加 `Transient`、`Session`、`Persistent` 保留策略；普通 Toast 默认不进入历史，普通 Notification 默认跨重启保留，调用方可以按业务影响覆盖。
- [x] 状态栏增加固定通知铃铛、未读数和最高未读级别状态点；Flyout 使用不透明主题表面、轻边框卡片和左侧语义色条展示通知标题、正文、时间、重复次数、已读层级和可选操作，并支持全部已读、单条删除、清空已读和确认清空全部。
- [x] 通知历史限制为最近 100 条和 30 天，10 分钟内相同通知合并并重新变为未读；会话通知仅保存在内存，持久通知使用版本化 JSON 原子写入并在退出时等待刷新。
- [x] 持久化前限制标题/正文长度并脱敏常见密钥字段；未知或带无效参数的操作不会执行，完整异常继续写日志。
- [x] 普通复制、标签/脚本导入导出和迁移成功分类为会话通知；无数据、详情查看等低价值提示保持瞬时；数据库、更新、备份还原和脚本异常保持持久，并为适用场景增加设置、日志或本地文件入口。

## 2026-08-26 统一应用状态栏

- [x] 状态栏移除源代码、用户名和计算机名常驻项，左侧固定显示数据库与 Tracker 状态，右侧保留紧凑日期；临时消息、后台任务和更新状态仅在有内容时出现。
- [x] 新增线程安全的统一状态服务，数据库重连和 Tracker 配置变化后即时刷新；Toast、通知和未捕获的 UI 操作错误提供短时摘要，错误摘要可直接打开当前日志。
- [x] 更新检查、增量/完整包下载准备、数据库备份/还原验证和脚本扩展导入接入后台任务；任务 Flyout 展示名称、说明和可用进度，更新状态补充下载比例、文件数和实际下载大小。
- [x] 状态项沿用信息、成功、警告和错误语义色；数据库、Tracker 与更新项可分别打开对应设置或检查入口，日期继续打开只读日历。
- [x] 新增状态服务/ViewModel 单元测试，并扩展 `ui-core-full` 对数据库、Tracker 和日期状态入口的结构断言。

## 2026-08-26 主导航冷热切换性能测试

- [x] 增加 `navigation-performance` 隔离场景，同时开启调查与开发者功能，固定覆盖日记、查询、统计、调查和脚本五个核心导航页，并可加载已有 Tracker 动态管理页。
- [x] 增加 CDP 单进程性能套件，区分新进程内首次访问与重复热切换，记录输入派发、页面可见、视觉树稳定、CPU、工作集和进程 I/O。
- [x] 增加跨平台 Node 编排器，支持多次新进程运行、轮换冷访问顺序、core/full 模式和 Tracker 动态页强制要求，输出逐样本 JSON 与汇总 Markdown。
- [x] 主窗口导航改为每个可缓存 ViewModel 实例复用一个 View，窗口空闲时离屏预热核心页面；`ViewModelBase` 缓存资格使用默认关闭的虚属性，只有主导航页面显式加入缓存，`WorkEditorViewModel`、对话框和 Tracker 编辑区域继承安全默认值。
- [x] Linux X11 复测 5 个新进程、20 次首次访问和 75 次热切换全部成功；查询、调查、脚本和日记热切换 P50 分别下降 51.4%、53.4%、28.9% 和 33.9%，统计页首次挂载仍是后续可细分分析的主要成本。
- [x] 将离屏预热升级为 `NavigationViewHost` 真实视觉树持久挂载，页面切换不再 reparent；统计页签改为显示后按需查询并延迟创建图表/明细树，CDP 同步传播祖先有效可见性。5 进程复测热切换 P50 收敛到 32–37 ms，core 14/14 通过。
- [x] 为缓存页面增加可选预加载契约；脚本管理页在后台复用启动期目录结果并提前整理列表模型，进入页面后再应用 UI 数据，执行历史和最多 2000 条会话日志按页签延迟生成。最终 Linux 三进程空目录复测中，脚本页数据就绪冷切换 P50 为 110 ms、热切换 P50 为 34 ms，UI 数据应用仅 4–7 ms，core 14/14 通过；真实 Windows 脚本目录可通过新增分阶段日志继续判断 I/O、编译或 UI 应用成本。
- [x] ReadyToRun `win-x64` 交叉发布验证通过，并为 nng.NET 的 `runtimes/any` 多 TFM 载荷增加排除规则；local Bash/PowerShell 工具提供显式实验开关。由于完整 ZIP 从约 83.2 MB 增至约 115.0 MB，普通 local 包和 CI 默认保持关闭。

## 2026-08-26 日历脚本菜单空状态

- [x] 日历日期和年月右键菜单在首个脚本子菜单前增加分隔符，将内置同步、统计、调查操作与脚本操作分组。
- [x] 日、周、上周、月、季度和年度没有可运行脚本时仍显示对应子菜单，展开后提供禁用的“暂无”项；存在脚本时只显示实际脚本。
- [x] core UI 自动化覆盖脚本分组分隔符，应用单元测试覆盖禁用“暂无”空状态。

## 2026-08-26 RID 发布显式还原 Script Worker

- [x] Linux Python 本地打包、Windows local 工具、Tag Release 和手动构建工作流在解决方案 RID 还原后显式还原 `Diary.Script.Worker`，修复 `NETSDK1047`。
- [x] 同步发布操作指引，并使用本地 `win-x64/python313` local 打包发布流程验证。

## 2026-08-26 信息说明可发现性优化

- [x] 清点全项目 5 处 `mdi-information-outline`：保留 2 处带完整按钮文字且命中区域充足的装饰图标，处理 3 处依赖小图标 Tooltip 的说明。
- [x] 备注区移除与“仅本地”状态重复的信息图标；标签编辑器页头直接显示“标签、元数据、自动化与附加字段”，当前标签标题直接显示“编辑标签配置”。
- [x] smoke UI 自动化验证标签编辑器说明直接可见，并重新采集、裁切受影响的用户手册图片。

## 2026-08-26 V2 参数辅助说明精简

- [x] V2 类型化参数表单的字段辅助说明统一为“参数键 · 类型”，移除容易挤压截断的值来源文字；内部来源跟踪、默认值恢复和上次参数记忆行为保持不变。
- [x] 更新 ViewModel 单元测试、Linux CDP 扩展套件断言、用户手册和 V2 参数设计文档。

## 2026-08-26 用户手册全面复核与图文细化

- [x] 按数据版本 `1.0.1` 和当前主分支重新核对安装、首次使用、日记、标签、模板、Tracker、查询、统计、设置、数据库、更新、调查、脚本、MCP 与故障排查内容，移除固定构建号口径并补足可直接照做的操作步骤。
- [x] 补充事项状态与只读边界、周/月同步跳过条件、标签共享包冲突处理、附加字段默认值与排序、模板三种入口、五种标签匹配、V2 参数记忆与校验、更新频道和包类型等容易误解的细节。
- [x] 使用当前 `1.0.1-r564` 隔离测试界面更新标签、模板、程序设置和脚本参数图片；新图片均裁到目标控件或对话框主体，去除完整主窗口背景、无关导航和多余留白。
- [x] 重新生成并检查单文件 HTML 与 PDF 手册，确认新增章节、图片引用、版本口径和发布文件名正确。
- [x] 修正 PDF 的中文字体名称，避免 Typst 找不到字体后回退到默认衬线字体；HTML 主题字体保持不变，并确认重新生成 PDF 时不再出现未知字体警告。

## 2026-08-25 事项编辑器模板操作

- [x] 在日期行“使用今天”后增加同形式的单个“从模板更新”分段按钮，并复用当前模板列表；主体操作用于更新，右侧下拉菜单提供“应用模板”。
- [x] 主体更新操作仅补空标题和零工时，且只有当前无标签时才添加模板标签；菜单中的应用操作覆盖标题与工时、先清空后添加标签。锁定事项不开放模板操作。
- [x] 现有“从模板新建”复用同一应用逻辑；单元测试覆盖空值覆盖、保留已填写值、标签更新边界和已有事项数据库标签替换。


## 2026-08-25 脚本运行日志复制

- [x] 脚本管理页的当前会话运行日志改为只读多行文本框，支持鼠标选择、`Ctrl+A` 和 `Ctrl+C`，保留等宽字体与横纵滚动。
- [x] 增加“复制全部”按钮和统一日志文本聚合，执行历史原有的单条“复制日志”入口保持不变；extended UI 自动化同步检查日志框与复制入口。
- [x] 主程序日志使用 `[MM-dd HH:mm:ss] [LVL] [SourceContext]` 紧凑前缀；脚本日志框使用 `[MM-dd HH:mm:ss] [LVL] [ScriptId]`，隐藏长执行 GUID，完整关联信息继续保留在应用日志和执行历史中。

## 2026-08-25 标签列表过滤与字段排序反馈

- [x] 标签编辑器左侧增加按名称即时过滤，忽略大小写并自动去除过滤词首尾空白；过滤后尽量保留当前选中标签，选中项不可见时切换到第一条可见标签。
- [x] 标签列表项同时显示附加字段数量和元数据数量，两个计数随集合新增、删除即时更新。
- [x] 新增或编辑附加字段确认后立即按 `SortOrder`、`FieldKey` 升序稳定重排，不必先保存整个标签设置；ViewModel 定向测试 2/2、Linux X11 `ui-extra-fields-full` 8/8 通过。

## 2026-08-25 脚本 API 版本文档整理

- [x] 新建脚本向导默认选择 V2，同时允许创建 V1；向导内展示参数契约、兼容用途和未来可能弃用的简要说明，三语言按选择生成对应 C# 基类或 metadata。
- [x] 增加统一的 V1/V2 更新内容、差异、版本声明、迁移步骤和未来兼容策略，明确 V1 当前仍可创建和运行，以及两个版本共享 Host API 与权限边界。
- [x] 用户手册增加版本速查；C#、Lua、Python Reference 建立统一入口，并修正 C# 快速入门的参数 UI、SDK 基类和版本枚举等过时说明。
- [x] C#、Lua、Python 快速入门示例迁移到 V2 参数契约和当前推荐 API 命名，避免向导创建 V2 后继续按未声明自由参数运行。

## 2026-08-25 脚本 API V2 参数 UI 完成

- [x] 脚本管理列表在脚本名称右侧显示紧凑 API 版本标识，当前为 V1/V2；标签按枚举数值生成，后续新增版本无需补充 UI 分支。
- [x] 管理页手动运行、Editor 入口和管理页默认参数配置复用同一套 V2 类型化表单；V1 继续使用自由 `key=value`，无参数 Editor 脚本继续立即执行。
- [x] 管理页 `defaultArguments` 只保存相对 descriptor 默认值的 metadata 覆盖项，支持脚本默认、配置覆盖、上次值、本次输入和已清空来源提示，以及单字段/整体恢复默认值；Automation 必填默认值不完整时显示“待配置”且不进入调度。
- [x] 有人值守参数按脚本、入口和 Editor 目标类型保存到独立原子本地文件，支持 schema 迁移、容量限制、当前作用域清除、删除脚本联动清理和设置页全局清除；后台 Automation 不读写参数历史。
- [x] Suggestions 改为同行软候选，Date/DateTime 应用控件边界并处理本地夏令时跳空/重复偏移，数值显示单位与步长，文本接近上限时按 Unicode 字符显示计数；Binder 继续作为最终校验源并提供字段级范围、步长和长度错误。
- [x] 点击运行/保存后聚焦首个错误；运行对话框支持单行 Enter、Ctrl+Enter 和 Escape，管理页默认参数支持 Ctrl+Enter 保存，错误文本使用可访问性 live region。
- [x] App 参数表单/历史定向回归 16 项和脚本参数/目录/示例/文档定向回归 36 项通过；Linux X11 1280×800 `ui-extended-full` 11/11 通过并覆盖真实 V2 类型化表单、参数执行、设置入口和执行历史，32 参数由 ViewModel 回归覆盖。平台 DPI/主题视觉抽样继续归入全局 UI 发布检查单。

## 2026-08-25 脚本 API V2 加载期参数契约

- [x] 增加 String、MultilineString、Integer、Number、Boolean、Date、DateTime 和 Choice 八种参数定义；C# 通过四类 V2 SDK 基类声明，Lua/Python 通过相邻 metadata 或包 manifest 声明，脚本加载后即可从 Catalog 读取完整契约。
- [x] 宿主在进入 Worker 前统一合并 descriptor 默认值、metadata `defaultArguments` 和本次输入，完成必填、未知参数、类型、Choice、长度与总大小校验，并向三种语言传递规范化字符串值。
- [x] C#、Lua、Python Worker 同时支持 V1/V2 和混合版本执行；Automation 用户参数与事件数据已分离，V1 保留兼容镜像，V2 不再让事件字段污染 `Arguments`。
- [x] 增加参数 Binder、目录加载、执行上下文、三语言引擎、共享包和真实 Worker 进程回归测试，并提供三语言参数化汇总示例；类型化参数 UI 和创建向导默认 V2 已在后续工作完成，日志复制保持独立 TODO。

## 2026-08-25 界面密度、日历与 Redmine 回归修复

- [x] 在不改变布局、功能入口、文字尺寸和视觉层级的前提下，系统收紧公共页面、卡片、主从面板、设置分组、表单、工具栏和常用对话框间距；主窗口页面四周约 4px，同级主卡片保留 6px、小型列表卡片保留 4px；日记页左右主卡片直接使用 4px 间距并移除重复分隔线。Linux X11 Debug 构建 0 警告、0 错误，设置 9/9、核心 14/14、扩展 11/11、Survey 8/8、数据库异常 8/8、附加字段 8/8 和 Redmine 只读视觉 5/5 通过。
- [x] Survey 条件卡改用显式 8px 外边距，避免扩展条件行隐藏后仍叠加前后两个 Grid 行间距；兼容和扩展查询模式的卡片间隔现已一致，`ui-survey-full` 8/8 通过。
- [x] AI 上下文只读预览框改为水平/垂直拉伸并顶部对齐，生成前的提示文本与生成后的 Markdown 使用同一预览区域尺寸；扩展 UI 套件增加生成前后高度不变断言并以 11/11 通过。
- [x] 日记事项备注编辑框改为显式水平/垂直拉伸并保持顶部内容对齐，随备注卡片自动占满剩余高度；核心 UI 套件增加编辑框高度和卡片底边间距断言，Linux X11 `ui-core-full` 14/14 通过，实测高度 250px、底边间距 11px。
- [x] 日记事项编辑器按可用 Tracker 编辑扩展控制“Tracker 关联”卡片可见性，无启用实例时不再保留空标题卡；运行中新增、启用、禁用或重配 Tracker 后，当前日期工作项立即按批量绑定重建编辑扩展并更新可见性，同一实例未保存的选择会迁移到新扩展，无需切换日期。Tracker 卡片使用 6px 内边距，标题、页签及 Redmine/Jira 表单间距同步收紧并横向填满内容区；ViewModel 测试覆盖注册表从空到有、从有到空和重建状态保留，隔离 Jira 性能场景 CDP 实测卡片为 866×152px、Issue 下拉框宽 714px，UI smoke 通过。
- [x] 紧凑周历年月按钮统一显示年度周次，沿用日期右键菜单的周一首日、`CalendarWeekRule.FirstDay` 口径；标题随翻周、选日和回到今天同步更新，Linux X11 `ui-core-full` 14/14 通过，实测今天为 `2026年8月 第35周`、滚轮上一周为第 34 周、跨月浏览为 `2026年7月 第31周`。
- [x] 精简 Redmine 管理页视觉层级，移除与动态导航重复的外层“Redmine 管理”说明卡和与页签重复的“基本信息”说明卡；内容直接从页签、用户信息、活动列表和已导入问题开始，Linux X11 `ui-redmine-style` 5/5 通过。
- [x] 紧凑周历月份标题对鼠标右键、`Shift+F10` 和系统上下文请求显式打开月/季度/年度菜单，避免同一按钮的完整月历 Flyout 截断上下文菜单路由；Linux X11 `ui-core-full` 原始断言 14/14 通过。
- [x] 完整月历选择使用控件已提交的真实日期，避免相邻月份日期按钮复用后读取到下一月同位置日期；点击日期后立即关闭 Flyout，每次展开均恢复当前选中日期所在的月视图。Linux X11 `ui-core-full` 14/14 通过，并真实覆盖 2026 年 8 月点击“3”后选中 2026 年 9 月 3 日。
- [x] 页面头和区块标题说明改为优先横向底部对齐，精简可见说明并将长说明保留在信息图标或 Tooltip 中，减少日记、查询、统计、设置、脚本、标签、模板、Jira/Redmine 页面高度；事项状态胶囊增加未保存、待同步、已同步、失败和待确认语义配色，未配置 Tracker 与迁移只读保持中性。Linux X11 核心 14/14、设置 9/9、扩展 11/11、Survey 8/8 通过。
- [x] Redmine 空关键字项目列表统一读取兼容 `projects` 与 `results` 的项目集合，`/projects.json` 不再被误判为空；新增本地 HTTP 回归测试。
- [x] Redmine Issue 启停改为数据库成功后原位替换 UI 集合项并同步开放 Issue 列表，不再依赖清空重载整个 DataGrid；真实 Redmine `ui-redmine-full` 原始断言 12/12 通过。

## 2026-08-24 统一 UI 重设计

- [x] 将日记页日期导航改为固定一周：左右按钮、PageUp/PageDown 和滚轮逐周浏览，月份标题打开按内部模板自然测量的完整月历，不依赖固定宽高且完整显示四周边框；“回到今天”和“复制记录”分别贴齐日期操作区左右边缘；右键紧凑日期会先选中目标日期并提供日/周/上周操作与脚本，今天使用圆形浅色强调底与描边、当前选中使用圆形主色高亮背景，悬停和按下反馈同样保持圆形且两种状态可叠加；右键月份标题提供月/季度/年度操作与脚本，完整月历仅保留选择和定位；方向键选日及跨月“回到今天”继续可用。
- [x] 修复日期标题/操作区拥挤、未选中事项时空状态胶囊，以及事项编辑器日期、标题、耗时输入框左边缘不一致；Windows 150% DPI 截图和 `ui-core-full` 14/14 已复核。

- [x] 修复日历只翻月、不改变当天选中状态时“回到今天”不会恢复当前月份的问题；显示月份与选中日期现在显式同步，`ui-core-full` 已覆盖从 2026 年 8 月翻到 7 月再返回 8 月的真实 CDP 回归路径。
- [x] 在 `feat/unified-ui-redesign` 分支完成日记、查询、统计、程序设置、标签、事项模板、数据模板、Tracker 配置/状态、Jira/Redmine 编辑与实例配置，以及 Redmine 基本信息、问题、项目和新建问题子页面的统一布局。
- [x] 保留全部命令、快捷键、控件名称、页签文字、数据绑定和远程副作用边界；Windows CDP 已覆盖设置、smoke、核心、扩展、数据库异常、Survey、附加字段、Redmine 全功能和只读视觉回归。
- [x] 修复高 DPI CDP 截图直接以物理像素写入 96 DPI PNG，以及 Windows overlay 子内容被重复缩放的问题；Windows 改用 `PrintWindow` 捕获真实窗口表面，并按 `Page.getLayoutMetrics` 自动推导当前窗口倍率、归一化为逻辑 1×/96 DPI，物理原图保留在 `raw-physical/`，同时增加 125%/150% PNG 单元测试和报告尺度字段；修复后全量报告 `ui-full-test-2026-08-24T14-44-09-118Z.json` 为 9/9 套件通过。

## 2026-08-24 MCP 发布包体优化

- [x] `Diary.Mcp` 从独立自包含单文件改为按目标 RID 发布的自包含多文件 apphost，并安全合并到主应用发布目录；247 个相同的运行时、Roslyn 和脚本依赖通过大小及 SHA-256 校验后复用，同名不同内容和 Windows 大小写冲突会中止发布。
- [x] Windows Python 3.13 完整包从 138,518,435 bytes 降至 100,141,220 bytes，减少 27.71%；Linux/Windows 实际发布、Linux MCP 启动探针、ZIP 契约、更新服务器校验及合并工具测试均通过。

## 2026-08-24 Redmine 标签自动化规则布局

- [x] 标签管理页的 Redmine 自动化规则改为“启用、活动、问题、删除”水平排列；标签作用域由左侧当前标签确定并隐藏重复的标签选择，同时保留 Tracker 全局设置中跨标签编辑规则的标签下拉框。

## 2026-08-24 旧数据库悬空标签关系兼容

- [x] DiaryToolpp 5.0.0 数据迁移在源 `work_item_tags` 引用已删除工作记录或标签时跳过无效关系并报告分类数量，不修改源数据库，也不影响其余工作记录、标签和有效关系的事务式导入；使用包含 45 条缺失工作记录关系、53 条缺失标签关系的真实旧 SQLite 样本完成隔离导入验证。

## 2026-08-24 local 发布流程对齐 CI

- [x] Bash/PowerShell local 发布在打包前使用 Quarto 渲染用户手册，将稳定文件名的 HTML/PDF 注入应用包，并启用 `--require-user-manual` 校验；Release 配置还原、应用与更新器发布、运行时裁剪、PDB 排除和 Python 3.13.15 哈希规则继续与 Tag/手动 CI 保持一致。
- [x] AI MCP apphost/多文件发布按目标 RID 而不是构建主机判断 `.exe` 后缀，使 Linux/WSL 能交叉构建 CI 同款 `win-x64` local 包，同时保持 Windows/Linux CI 原生发布路径不变。
- [x] local 通道仍只生成更新服务器需要的 `win-x64/standard` 或 `win-x64/python313` 运行包，不额外生成 GitHub Release 专用的调试符号、metadata 和版本化手册附件。
- [x] Linux 本机端到端发布 `local/win-x64/python313` sequence `20260824015426`，424 文件的 ZIP 通过运行时、Python 和用户手册门禁，上传后 latest 回读的 SHA-256 为 `0ca462f404ad995f9b95d4dc50e46941dd98d02ca06f2f17912d0c6ab89e718d`；Release 全量测试 824 项中 814 成功、10 项按外部服务/工具版本条件跳过、0 失败。

## 2026-08-24 统计图表与调查结果默认状态

- [x] 统计页工时分布增加柱状图/饼图切换，饼图复用现有主标签统计快照并显示标签图例，不会因切换重复查询数据库。
- [x] 调查结果的层级树默认折叠到根节点，保留节点摘要可见，避免多节点、多标签结果初次显示时全部展开；补充图表模式与折叠状态回归测试。

## 2026-08-23 Worker 取消状态稳定性

- [x] 取消宽限期超时后的强制回收结果增加 `WORKER_CANCEL_GRACE_EXPIRED` 警告，使 `Cancelled + Failed` 与无关 Worker 故障可区分。
- [x] 跨 C#/Lua/Python 的真实进程取消测试不再依赖固定调度窗口：优雅取消验证 Worker 继续 `Ready`，强制回收则验证警告码并重新握手恢复到 `Ready`；单元测试覆盖无响应 Worker 的确定性回收分支。

## 2026-08-23 发布版内置用户手册

- [x] 在左上角应用图标菜单的“关于”后增加“用户手册”；仅 Release 编译且随包文件存在时显示，优先打开 HTML，缺失时回退 PDF，Debug 目录无需携带手册。
- [x] Tag 和手动发布任务等待手册构建，将稳定文件名的 HTML/PDF 注入 Windows、Linux 和 Windows Python 应用 ZIP；发布目录检查及 ZIP 校验器会拒绝缺少手册的官方发布包，版本化 HTML/PDF 仍作为独立 Release 附件提供。
- [x] 服务、窗口绑定、Debug CDP 菜单隐藏和发布包契约均有自动化覆盖；系统默认浏览器/PDF 阅读器启动保留发布前原生检查。

## 2026-08-23 UI 视觉统一与 Linux 回归

- [x] 建立页面标题、副标题、空状态、对话框底部操作区、工作台展开分组、搜索工具栏和分页公共样式；统一程序设置、主要功能页、旧式对话框和 Redmine 工作台的标题层级、按钮语义、命名与控件对齐。
- [x] 新增 5 步 `ui-redmine-style` 只读视觉套件，覆盖 Tracker 配置、插件状态、Redmine 基本信息、问题管理和项目管理截图；两个筛选 CheckBox 与搜索框中心线偏差均为 0px，套件不触发远程写入。
- [x] 在 Linux X11 1280×800 下完成设置、smoke、核心、扩展、脚本编辑器、Survey、数据库异常、附加字段和 Redmine 只读复检，共 67 个实际执行的结构化步骤和 smoke 通过；更新程序设置、标签、模板及 Tracker/Redmine 用户手册截图。

## 2026-08-22 P1-P3 代码审查修复

- [x] 修复年份菜单范围、标签重命名持久化，以及工作项删除、标签移除/清空失败后仍修改 UI 的问题；SQLite/PostgreSQL 标签更新契约和编辑器失败分支已补回归测试。
- [x] 退出清理失败后允许重试；全局 UI 异常只吞可恢复数据库异常；统计刷新改为后台快照、UI 原子应用和 generation 防旧结果覆盖；自动化事件幂等缓存限制为 4096 项。
- [x] 敏感配置升级为安装级随机主密钥派生的 AES-256-GCM，Windows 主密钥由当前用户 DPAPI 保护，Unix 使用仅用户读写权限，并兼容读取旧 `Salted__` AES-CBC 文件；篡改和旧格式兼容测试已覆盖。
- [x] Jira/Redmine 客户端纳入 API 与 Tracker UI 贡献生命周期，重新注册和退出时释放；数据库扩展加载失败写入可定位诊断；测试命令改用 MTP `--solution`；版本详情不再嵌入实时构建时间、机器名或可能异常的提交日期。

## 2026-08-22 UI 自动化补充

- [x] 标签共享包支持按需选择导出范围：默认全选，可全选、清空或逐项勾选；服务层只输出所选标签及其元数据、附加字段和 Tracker 规则，并补充选择区与部分导出测试；选择区内嵌于标签编辑器，避免 150% DPI 下嵌套 overlay 缩放裁切。
- [x] 新增并细化面向最终用户的 Quarto 用户手册，覆盖首次使用、日记、标签、模板、查询、统计、Tracker、调查、脚本、数据库维护和故障排查；补充局部截图、编号标记和状态流程图，在 Windows 150% DPI 最大化窗口下以真实窗口像素复核 overlay 边界；普通 CI 验证 HTML/PDF，手动与 Tag 发布将两种格式附加到 Release，仓库仅提交源文件和截图。
- [x] 标签附加字段完整 CDP 自动化：新增 `extra-fields` 隔离场景和 8 步套件，覆盖 9 类字段定义、非法 FieldKey、不可变项、类型化编辑、三态布尔、清空/重设、切换事项持久化、停用历史值和迁移只读事项；停用字段保留历史值并单字段只读，迁移工具导入事项因来源版本不具备附加字段而隐藏附加信息入口。Linux X11 隔离场景 8/8 通过。
- [x] 修复带 Tracker 事项执行“复制最近”时因目标 Issue/活动选项未初始化而整单回滚的问题；复制命令保留标题、备注、优先级、标签和附加字段，但不复制 Tracker 绑定，也不因标签自动规则派生新绑定；普通重复事项仍会在初始化目标 Tracker 选项后复制设置。
- [x] Windows 全量 UI 自动化扩充为 9 套件：最终报告 `ui-full-test-2026-08-22T06-09-21-453Z.json` 达到 9/9 套件、72/72 结构化步骤和 smoke 通过；设置菜单、标签选择和脚本编译检查增加可观察状态与有限重试。

## 2026-08-21 TODO 清理归档

- [x] Windows 全量 UI 自动化：按 default/extended/survey/database-error/plugins 隔离场景建立初始 8 套件编排，覆盖主外壳、日记、查询、统计、设置、标签模板、脚本、数据库异常、Survey 和 Redmine；后续扩充到 9 套件、72 个结构化步骤，见上方 2026-08-22 记录。

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
- [x] 已将日记、Tracker、系统交互和日志能力分别整合为 `IDiaryApi`、`ITrackerApi`、`ISysApi` 和 `ILogApi`；旧名称 `SysApi` 标记为 deprecated 并由宿主继续注册以兼容现有 C# 脚本。
- [x] 系统交互 API 增加 `ui.window.raise`，C#、Lua 和 Python 脚本均可请求显示并激活 DiaryApp 主窗口；该调用采用请求语义，不承诺绕过操作系统的前台焦点策略。
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
- [x] 日历右键菜单提供日、周、月、季度和年目标（周目标含上一周），并按紧凑日期与月份标题分组承载，不提供自定义日期范围。
- [x] 自定义日期范围不再作为编辑器扩展入口；编辑器脚本使用宿主自动注入的目标范围。
- [x] 工作项列表右键菜单面向当前工作项执行脚本。
- [x] 脚本管理页提供列表、概览、诊断、执行历史、运行日志、重载、源码入口和删除确认。
- [x] 只展示已加载且构建成功的可执行脚本，并明确显示加载、构建、执行、取消、超时和 Worker 故障状态。
- [x] 编辑器脚本由日历的日、周、月、季度、年上下文和工作项菜单触发，目标由宿主自动注入。
- [x] 编辑器脚本按日期或工作项上下文运行，使用宿主注入的结构化目标和安全快照；未保存工作项不开放脚本操作，已锁定工作项只允许只读执行。
- [x] 提供 C#、Lua、Python 脚本创建向导、源码模板和内置编辑器；编辑器按当前脚本语言提供 API Reference 文档入口，脚本创建校验稳定 ID、文件名和目标目录，并通过原子写入避免产生不完整脚本包。

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
- [x] 富数据日期切换改为按日期批量预取附加字段，并让 Redmine/Jira 编辑器共享开放 Issue/活动选项；只有历史失效绑定创建局部占位列表。本机 14 条事项、250 个 Issue、Tracker 绑定、2 个标签和 9 类附加字段场景下，热身后的 12 次连续切换中位数约由 149 ms 降至 80 ms，P95 约由 218 ms 降至 135 ms。
- [x] 日记页提供“复制昨天”“复制最近”和“复制整天”：整天复制支持选择源日期，执行前显示来源、条数/耗时和目标日期并要求确认；复制只带入本地字段和标签，不复用远程 Tracker 绑定。
- [x] 工时编辑提供 15/30 分钟、1/2/4/8 小时和清零快捷项，并支持 `30m`、`1h30m`、`1小时30分钟` 等自然时间表达式；新建事项标签列表优先展示当天已有记录中最近使用的标签，最近项目已持久化到应用配置。
- [x] 查询页结果摘要显示记录数和耗时合计，并提供按日期、主标签的紧凑汇总；结果可复制汇总文本，也可导出 CSV 或 Markdown，导出字段包含主标签。
- [x] 调查协议在保留 DiaryToolpp 兼容的 v1/9721 日期查询基础上增加 v2/9722 自定义统计查询（关键词、标签、标签模式和优先级），扩展查询只发送到新版节点；已支持 v2 能力发现、标签/日期/优先级分组和最多 500 条结果明细展示。

## 阶段 10 续：脚本 Worker 可靠性、进度、自动化与 Query 入口

- [x] Worker 心跳与启动/握手、宿主调用响应超时已生产接线：App 为三个 supervisor 显式开启心跳（30s 间隔/15s 超时，默认关闭；仅在 `Ready` 且抢到执行门时 ping，杜绝 Pong 被 Busy 执行接收循环截走）；握手超时（默认 10s）→`Failed`+`WORKER_HANDSHAKE_TIMED_OUT`+停 transport；宿主调用超时（默认 30s）→`Failed`+停进程+`WORKER_HOST_CALL_TIMED_OUT`（视为 worker 故障不重试；超时前可能已产生的追加副作用不可回滚，靠幂等键防线）；`CheckHealthAsync` 新增 timeout 参数（默认 5s）；应用退出 `PreShutdownAsync` 调用 `IWorkerScriptExecutor.StopAllAsync()` 优雅停 worker，修复孤儿进程。
- [x] Worker 真实进程测试已统一 Windows/Linux 工件定位：移除核心用例的 Linux-only 跳过，按平台解析 dotnet、App Worker apphost 和 Python 解释器；CI 固定 Python 3.10，并通过 `DIARY_REQUIRE_PYTHON_TESTS=1` 将运行时缺失从跳过提升为失败。
- [x] 执行进度上报接入管理页：新增 `ScriptProgressTracker`（内存，最近 20 次执行、每次最多 50 条时间线），worker 路径 dispatcher 的 progressReporter 与进程内路径 `ScriptExecutionContext` 的 progressReporter 均已接线；管理页底部运行栏显示进度条与文本，执行历史条目日志追加「进度：」时间线；`IWorkerScriptExecutor.ExecuteAsync` 新增 `Guid? executionId` 参数并经 ScriptManager 透传 metadata.ExecutionId，使 worker 模式 outcome.ExecutionId 与进度回调 executionId 一致。
- [x] 自动化脚本 Scheduled+Startup 已实现：`ScriptFileMetadata`/`ScriptPackageManifest` 新增 `Schedule`（"daily HH:mm"，仅 Automation 入口合法）与 `RunOnStartup`，`ScriptDirectoryEntry` 新增 `Metadata`，加载时校验，非法（或非 Automation 入口携带）→`SCRIPT_SCHEDULE_INVALID` 构建失败不注册；新增 `ScriptAutomationSchedule`（TryParse+GetNextDue，lastRun 为空且当天已过→立即到期）；`ScriptAutomationContextFactory.FromRequest` 按 Source 生成 Trigger（Automation→Scheduled、Startup→Startup），替换 worker 内联三元式，Lua/Python worker 的 context 新增 `automation`（trigger/eventData/idempotencyKey）；`ScriptAutomationScheduler` 以 30 秒 tick + `SemaphoreSlim` 串行 + 内存 last-run 表防重调度，启动补跑一轮 RunOnStartup 与今日到期脚本，并生成请求级幂等键（Scheduled=`auto:{scriptId}:{yyyy-MM-dd HH:mm}`、Startup=`startup:{scriptId}:{yyyy-MM-dd}`）；新建向导提供「自动化脚本」模板（EntryKind=Automation、Schedule="daily 09:00"）。metadata/manifest 已支持 `Triggers`（WorkItemCreated、WorkItemSaved、TagAdded），事件型自动化可不配置 schedule；调度器按 `scriptId + trigger + eventId` 防重并生成事件幂等键，工作项创建/保存和标签添加入口已接入，草稿标签在首次保存后按顺序补发；新建向导和管理页均可配置三种事件触发。
- [x] Query 入口已落地：ScriptBase 新增 `IQueryScriptV1` 接口与 `QueryScript` 抽象基类（Scope=Application、EntryKind=Query、上下文 `IScriptApplicationContext`），`ScriptProgramAdapter` 三处增加 Query 分支，C# 引擎类型识别支持 `IQueryScriptV1`；创建向导提供「查询脚本」模板（Lua/Python 使用 `query_main`、C# 使用 `QueryScript` 子类），管理页可直接运行（CanRun 已放行 Application scope）。
- [x] 决策记录：执行历史与执行进度保持会话内存态（历史 30 条、进度最近 20 次），持久化经用户决策明确延期。

## 阶段 10.1：脚本 API V2 参数 UI 第一阶段

完成日期：2026-08-25。设计见 [`ScriptApiV2ParameterDesign.md`](ScriptApiV2ParameterDesign.md) 和 [`ScriptApiV2ParameterUiDesign.md`](ScriptApiV2ParameterUiDesign.md)。

- [x] V2 参数契约增加 Suggestions、数值/日期范围、步长、文本长度和单位提示，加载期 schema 与执行前 Binder 统一强制校验并返回字段归属 Issue。
- [x] 管理页手动运行和 Editor 入口接入 V1/V2 双模式类型化表单；Editor 有参数菜单显示省略号并按 Day/Week/Month/Quarter/Year/WorkItem 隔离历史值。
- [x] 有人值守运行参数使用独立本地原子文件保存，支持 schema 指纹迁移、200 项/4 MiB 限制、当前作用域清除和删除脚本联动清理；取消、校验失败和 Rejected 不覆盖旧记录。
- [x] Automation 必填默认值不完整时保留 descriptor 并进入“待配置”，不注册调度；创建向导默认选择 V2，同时允许生成 V1/V2 对应基类或 metadata。

## 阶段 10.2：标签附加字段默认值与核心数据版本 1.0.1

完成日期：2026-08-25。设计见 [`TagExtraFieldDesign.md`](TagExtraFieldDesign.md)、[`TagImportExportDesign.md`](TagImportExportDesign.md) 和 [`DatabaseCompatibilityDesign.md`](DatabaseCompatibilityDesign.md)。

- [x] 标签附加字段增加类型化默认值编辑与校验；默认值仅用于新建事项或新增标签预填，不回填历史数据，用户可以修改或清空。
- [x] 核心数据版本提升到 `1.0.1`，SQLite/PostgreSQL 登记 `1.0.0 -> 1.0.1` 正式迁移并保留旧版初始化 SQL。
- [x] 标签共享包 version 1 携带字段默认值并兼容缺少 `defaultValue` 的旧包；非法类型值和非法单选默认值在导入预览前拒绝。
- [x] MCP `diary_list_extra_fields` 披露字段定义默认值；C#/Lua/Python 工作项脚本查询同时提供实际值和定义默认值，并保持两者语义分离；双 provider、工作项预填、克隆、导入导出、脚本和 MCP 契约均补充测试。
