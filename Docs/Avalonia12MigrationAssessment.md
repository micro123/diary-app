# Avalonia 12 迁移评估与验证结果

## 1. 文档状态

本文记录 DiaryApp 在 `v12` 分支中从 Avalonia 11.3.x 迁移到 Avalonia 12 的实际方案、兼容性处理和验证结果。

- 迁移前评估日期：2026-08-23。
- 迁移验证日期：2026-08-23。
- 迁移分支：`v12`。
- 迁移前基线提交：`b7b46c8 docs: 添加 Avalonia 12 迁移评估`。
- 当前结论：迁移成功，可以继续作为 Avalonia 12 基线；尚未自动覆盖的原生桌面边界保留发布前人工门禁。

本文原先的迁移前风险已通过分支实测收敛。下文版本和结果均描述本次实际迁移，不再表示“尚未开始”。

## 2. 结论摘要

DiaryApp 已成功迁移到 Avalonia 12，并保持现有页面结构、主要 UI 交互和业务功能。迁移不是简单修改 NuGet 版本号，实际完成了以下兼容性调整：

- 统一 Avalonia Desktop、Headless、DataGrid、ColorPicker、Semi、Ursa、AvaloniaEdit、TreeDataGrid、SVG 和 Behaviors 的 Avalonia 12 依赖；
- 将停止维护的 Projektanker 图标包替换为 Optris 兼容分支，保留现有图标值和 XAML 使用方式；
- 因 LiveCharts 稳定包仍会引入 Avalonia 11 与 SkiaSharp 2.88，将统计柱状图迁移到 ScottPlot；
- 将 Debug CDP 从 `Chrome.DevTools.Avalonia.v11` 替换为 Avalonia 12 通用包；
- 移除 Avalonia 12 已删除的旧 `Avalonia.Diagnostics` 和直接 `SkiaSharp 2.88.9` 锁定；
- 适配 Ursa 2.x 标题栏、Overlay、MessageBox、PathPicker 和 Semi 主题 API；
- 适配 Avalonia 12 的 Placeholder、Clipboard、ContextRequested、字体集合和 Headless 生命周期 API；
- 修复 Linux 目标发布时 MCP 宿主文件名按构建宿主系统判断的问题；
- 更新 CDP 自动化对 Avalonia 12 模板部件、焦点激活和显式控件名称的依赖。

迁移后 Debug/Release 构建、自动化测试、Windows CDP 本地套件、Linux WSLg 原生运行和 Windows/Linux 发布均通过。未发现需要回滚到 Avalonia 11 的阻断项。

## 3. 实际依赖基线

### 3.1 框架与控件

| 组件 | 迁移后版本 | 用途或说明 |
| --- | ---: | --- |
| `Avalonia.Desktop` | 12.1.1 | 桌面应用和平台后端 |
| `Avalonia` | 12.1.1 | 公共 UI 项目基础引用 |
| `Avalonia.Headless` | 12.1.1 | UI/数据库测试 |
| `Avalonia.Controls.ColorPicker` | 12.1.1 | 颜色选择器 |
| `Avalonia.Controls.DataGrid` | 12.1.2 | 表格 |
| `TreeDataGrid.Avalonia` | 12.0.0 | 社区 MIT 兼容包，层级统计和调查结果 |
| `Semi.Avalonia` | 12.1.0.1 | 应用主题和基础样式 |
| `Semi.Avalonia.ColorPicker` | 12.1.0.1 | ColorPicker 主题 |
| `Semi.Avalonia.DataGrid` | 12.1.0.1 | DataGrid 主题 |
| `Semi.Avalonia.AvaloniaEdit` | 12.0.0 | 脚本编辑器主题 |
| `AvaloniaEdit.TextMate` | 12.0.0 | 脚本语法高亮 |
| `Irihi.Ursa` | 2.2.0 | 窗口、Overlay、MessageBox、PathPicker 等 |
| `Irihi.Ursa.Themes.Semi` | 2.2.0 | Ursa Semi 主题 |
| `Svg.Controls.Skia.Avalonia` | 12.0.0.15 | SVG 图标 |
| `Xaml.Behaviors.Avalonia` | 12.0.7 | 事件触发和拖动重排 |
| `Xaml.Behaviors.Interactivity` | 12.0.7 | Behavior 基础组件 |

### 3.2 替换依赖

| 迁移前组件 | 迁移后组件 | 决策 |
| --- | --- | --- |
| `Projektanker.Icons.Avalonia* 9.6.2` | `Optris.Icons.Avalonia* 12.0.7` | Optris 保持兼容命名空间和图标值，减少 XAML 交互变化 |
| `LiveChartsCore.SkiaSharpView.Avalonia 2.0.5` | `ScottPlot.Avalonia 5.1.59` | 避免混入 Avalonia 11 和 SkiaSharp 2.88；仅复刻现有柱状图，不新增原应用没有的图表切换 |
| `Chrome.DevTools.Avalonia.v11 0.1.0-preview.30` | `Chrome.DevTools.Avalonia 0.1.0-preview.34` | 仅 Debug 条件引用，继续使用现有 CDP 生命周期和脚本入口 |
| `Avalonia.Diagnostics 11.3.18` | 移除 | Avalonia 12 已移除旧包；现有 CDP 已满足项目自动化需求 |
| 直接 `SkiaSharp 2.88.9` | 移除直接锁定 | 由 Avalonia 12/SVG/ScottPlot 依赖图统一解析到 SkiaSharp 4.148.0 |

### 3.3 未引入的商业工具依赖

本次迁移没有将 `AvaloniaUI.DiagnosticsSupport` 或 Avalonia Developer Tools 作为应用运行、构建、测试或发布的强制依赖。现有 Debug CDP 自动化继续独立运行，Release 包不包含 CDP 或诊断组件。

## 4. 主要兼容性处理

### 4.1 Ursa 2.x

- 主窗口继续使用 `UrsaWindow` 和原有左上角应用菜单；没有新增与现有交互不一致的标题栏按钮。
- 标题栏模板改用 Avalonia 12/Ursa 2.x 的 `WindowDecorationProperties.ElementRole`：外层空白区域标记为 `TitleBar`，内容区及应用图标、版本、主题、设置控件标记为 `User`；不再使用覆盖交互层的 `WindowThumb`，保留空白区域拖动语义的同时恢复各按钮鼠标命中。
- Ursa 2.2 的主题选择器会优先控制最近的 `ThemeVariantScope`。标题栏为保持反差使用独立反色 scope，因此主题按钮的点击事件显式切换 `Application.RequestedThemeVariant`，避免只改变标题栏而不改变主内容。
- Avalonia 12 的最小化、最大化和关闭按钮位于独立 `WindowDrawnDecorations` 层，不继承自定义标题栏的反色 scope。Ursa 2.2 暂未提供将该装饰层与自定义反色标题栏联动的公开入口；当前保留原生窗口按钮角色和交互，但在反色标题栏下可能出现左右主题不一致，列为后续兼容项。
- Overlay 对话框迁移到新的 `OverlayDialogOptions` 和 Show API。
- MessageBox 调用适配 Ursa 2.x 的结果、按钮和图标类型。
- Ursa Semi 主题注册改为 Avalonia 12/Ursa 2.x 类型。

### 4.2 Avalonia 12 控件与平台 API

- 输入提示属性从旧模板语义迁移为 `PlaceholderText`，自动化不再依赖 `PART_Watermark`，改为显式控件名或 `PART_Placeholder`。
- Clipboard 通过主窗口的 Avalonia 12 Clipboard API 读取和写入。
- 右键上下文请求适配新的事件参数。
- Headless 测试会话改为异步释放，避免同步 `Dispose()` 在 Avalonia 12 下挂起。

### 4.3 自定义字体

`UserFontCollection` 改为延迟创建内部 `FontCollectionBase`：

- 平台初始化前仍可读取和解析字体配置；
- 首次实际请求字形时才创建运行时字体集合；
- 外部字体文件使用 Avalonia 12 正式的 `TryAddGlyphTypeface(Stream, out GlyphTypeface)` 路径；
- 随包字体、系统字体、外部字体和运行时切换测试继续通过。

Linux 中文、Emoji 和中英文 2:1 等宽回退的专项视觉门禁仍属于 `Docs/TODOS.md` 中的独立任务，不因本次应用可启动而宣称完成。

### 4.4 统计图表

统计页保留原有“工时分布”柱状图及其刷新、筛选和数据语义。ScottPlot 仅替代渲染实现：

- 不新增饼图或图表类型切换；
- 不改变统计页签、日期范围和“重新统计”交互；
- 不改变标签明细、TreeDataGrid 和自定义统计逻辑；
- 统计刷新仍使用后台快照和 UI 原子应用。

### 4.5 Debug/CDP 自动化

- `CdpServer.Start()`、`CdpServer.Stop()`、`DIARY_CDP_PORT`、隔离 profile 和现有工具入口保持不变。
- 为关键输入控件增加稳定名称：`ScriptSearchInput`、`ExtendedConditionsExpander`、`SurveyGroupByInput`。
- 自动化脚本不再依赖 Avalonia 11 的 TextBox 内部模板部件。
- 点击辅助在必要时先 `DOM.focus` 再发送输入，兼容 Avalonia 12 和 Linux X11 的焦点行为。

## 5. 构建与自动化验证

### 5.1 构建

| 配置 | 结果 |
| --- | --- |
| Debug | 0 警告，0 错误 |
| Release | 0 警告，0 错误 |

Debug 和 Release 均使用对应配置重新 restore，避免条件包引用复用错误的资产文件。

### 5.2 自动化测试

| 配置 | 总计 | 通过 | 环境跳过 | 失败 |
| --- | ---: | ---: | ---: | ---: |
| Debug | 839 | 723 | 116 | 0 |
| Release | 822 | 706 | 116 | 0 |

Release 比 Debug 少 17 项是 Debug 条件测试，不是测试丢失或失败。环境跳过项保持原有外部服务、平台或工具边界。

### 5.3 Windows CDP 全量本地套件

最终报告：`.build-tmp/ui-test/reports/ui-full-test-2026-08-23T14-24-51-233Z.json`。

| 套件 | 结果 |
| --- | ---: |
| `ui-settings-full` | 9/9 |
| `ui-smoke` | passed |
| `ui-core-full` | 14/14 |
| `ui-extended-full` | 11/11 |
| `ui-script-editor` | 4/4 |
| `ui-database-error` | 8/8 |
| `ui-survey-full` | 8/8 |
| `ui-extra-fields-full` | 8/8 |
| `ui-redmine-full` | `blocked-external`，未提供加密 Redmine seed profile |

8 个无外部依赖的本地套件全部通过，共 62 个结构化步骤加 smoke。Redmine 套件没有伪造通过，也没有进行远程写入。

### 5.4 Windows 标题栏与窗口状态

应用保持迁移前的自绘标题栏和左上角应用菜单交互：

- 全新隔离 profile 下，`ui-core-full` 通过 CDP 真实鼠标事件依次打开左上角应用菜单、版本菜单和设置菜单，14/14 通过；报告为 `.build-tmp/ui-test/reports/ui-core-full-2026-08-23T15-59-13-109Z.json`；
- 三个菜单本轮打开等待分别为 40.61 ms、29.82 ms 和 25.63 ms；该数据用于本机同构建趋势观察，不作为跨机器发布阈值；
- 通过 CDP 激活“最大化”，Win32 `GetWindowPlacement` 返回 `showCmd=3`；
- 通过 CDP 激活“最小化”，Win32 `GetWindowPlacement` 返回 `showCmd=2`；
- 通过 CDP 激活“退出”，应用进程结束；
- 标题栏根区域使用 `ElementRole=TitleBar`，应用图标、版本、主题、设置及内容承载区使用 `ElementRole=User`，避免窗口拖动命中覆盖真实交互控件；
- 全新隔离 profile 的 `ui-smoke` 通过真实鼠标点击完成应用级暗色到亮色切换，主内容平均亮度从 27.70 变为 253.30，差值 225.61；报告为 `.build-tmp/ui-test/reports/ui-smoke-2026-08-23T16-19-42-614Z.json`。随后 `ui-core-full` 14/14 通过，报告为 `.build-tmp/ui-test/reports/ui-core-full-2026-08-23T16-19-58-051Z.json`。
- 后续原生截图复核确认，CDP 页面截图不包含独立窗口装饰合成层，早期热切换截图不足以证明冷启动稳定；当前右上角窗口按钮仍跟随应用主题，而自定义标题栏使用反色主题，因此该视觉一致性问题尚未关闭。

真实鼠标拖动、标题栏双击最大化/还原以及多显示器边界需要操作系统物理指针和桌面前台，不由当前 CDP 合成输入可靠控制，保留发布前 `Manual-Native` 检查。迁移没有改变这些交互的实现方式。

### 5.5 Linux 原生运行

在 WSLg 的真实 Avalonia X11 会话中启动 Linux 自包含 Debug/Release 包：

- `ui-core-full`：14/14 通过；报告为 `.build-tmp/ui-test/reports/ui-core-full-2026-08-23T14-29-57-775Z.json`；
- 全新 Linux profile 上 `ui-smoke` 通过；报告为 `.build-tmp/ui-test/reports/ui-smoke-2026-08-23T14-31-29-175Z.json`。

首次复用 core profile 执行 smoke 时，事项列表虚拟化使可见性断言超时；改用全新隔离 profile 后完整通过，确认不是应用回归。

## 6. 发布与依赖检查

Windows `win-x64` 和 Linux `linux-x64` framework-dependent Release 发布均成功。迁移验证还生成并运行了 Linux 自包含包。

发布依赖扫描结果：

- 不包含 Avalonia 11 程序集；
- 不包含 LiveCharts；
- 不包含 Projektanker；
- 不包含 SkiaSharp 2.88；
- SkiaSharp 统一为 4.148.0；
- Windows 仅一个 `libSkiaSharp.dll`；
- Linux 仅一个 `libSkiaSharp.so`；
- Release 不包含 `Chrome.DevTools.*`、CDP 或旧 Diagnostics；
- Windows MCP 宿主为 `Diary.Mcp.exe`；
- Linux MCP 宿主为 `Diary.Mcp`。

Linux MCP 文件名判断已改为依据目标 `RuntimeIdentifier`，不再依据执行构建的宿主操作系统。

## 7. UI 与功能保持原则

本次迁移遵循“必要兼容修改最小化”原则：

- 保留主窗口布局、导航、设置入口、标题栏应用菜单和主题切换；
- 保留日记编辑、查询、统计、Survey、脚本管理、标签、模板、附加字段和 Tracker 插件入口；
- 保留快捷键、Overlay 对话框、通知和消息确认语义；
- 图标库替换不改变用户可见图标含义；
- 图表库替换只复刻现有柱状图，不借迁移增加新交互；
- 自动化所需的稳定控件名称不会改变用户可见 UI。

## 8. 许可证与发布边界

本次新增或替换的直接 UI 依赖均采用项目可接受的开源许可证边界：

- Avalonia、Semi、Ursa、AvaloniaEdit、Optris、ScottPlot、SVG、Xaml.Behaviors 和社区 TreeDataGrid 包按各自开源许可证使用；
- Optris 和 ScottPlot 替代项均为 MIT 许可；
- 随包 Noto Sans Mono CJK 字体继续携带 OFL 文本；
- 未将 Avalonia Developer Tools 商业授权产品纳入运行时或发布包；
- `Chrome.DevTools.Avalonia` 仅用于 Debug，本地监听必须显式启用。

发布前仍应由现有依赖和包内容检查确认许可证文件、调试程序集和原生库数量符合发布契约。

## 9. 剩余人工与外部门禁

以下项目不是本次迁移失败项，但当前自动化不能替代真实平台或外部环境：

- Windows/Linux 标题栏真实拖动、双击最大化/还原和多显示器行为；
- 系统托盘显示、隐藏、恢复和退出；
- 原生文件/目录选择器；
- 系统剪贴板与系统默认程序打开；
- Linux 专项字体回退视觉验收；
- Jira 真实服务、权限矩阵和自托管版本差异；
- Redmine 全量远程写入套件所需的加密 seed profile；
- Xvfb headless 全量编排和 CI 稳定性门禁。

这些边界在 `Docs/TODOS.md`、`Docs/UiAutomationCoverage.md` 和 `Docs/UiAutomationTesting.md` 中继续跟踪。

## 10. 最终判定

Avalonia 12 迁移满足本次目标：

- Restore、Debug/Release 构建和自动化测试通过；
- Windows 无外部依赖 UI 套件通过；
- Linux WSLg 原生 core 与 smoke 通过；
- Windows/Linux Release 发布通过；
- 发布依赖中不存在 Avalonia 11、LiveCharts、Projektanker 或 SkiaSharp 2.88 混用；
- 现有主要 UI 交互和功能没有因迁移被删除或重新设计；
- 自定义标题栏交互区和应用级主题切换已恢复；右上角独立窗口装饰与反色标题栏的一致配色仍是已知 Ursa 2.2/Avalonia 12 兼容项；
- 剩余项均为已明确的人工原生或外部环境门禁。

因此 `v12` 分支可以作为后续 Avalonia 12 开发和合并评审基线。
