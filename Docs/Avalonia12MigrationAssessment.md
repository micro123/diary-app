# Avalonia 12 迁移评估

## 1. 文档目的

本文记录 DiaryApp 从 Avalonia 11.3.x 迁移到 Avalonia 12 的可行性、第三方控件兼容状态、许可证边界、主要风险、替代方案和建议实施顺序。

评估日期：2026-08-23。

本文是迁移前的技术评估，不代表迁移已经开始，也不代表文中版本号可以直接批量替换。正式实施时必须重新确认 Avalonia 12、第三方包和 .NET SDK 的最新稳定补丁版本，并在独立分支完成编译、自动化和跨平台验收。

## 2. 结论摘要

DiaryApp 可以迁移到 Avalonia 12，但不能通过只修改 NuGet 版本号安全完成迁移。

有利条件：

- 主要项目已经目标化到 `net10.0`，满足 Avalonia 12 仅支持 .NET 8 及以上版本的要求；
- UI 项目已经启用编译绑定；
- Semi、Ursa、AvaloniaEdit、TextMate、DataGrid、ColorPicker、TreeDataGrid 社区分支、SVG 和 Xaml.Behaviors 均已有 Avalonia 12 版本；
- 当前业务层、数据库层、脚本层和 Tracker 核心逻辑不直接依赖 Avalonia 11 的内部实现。

主要风险：

1. Ursa 2.x 对窗口标题栏、装饰按钮和 Overlay 对话框进行了破坏性调整；
2. 原 `Projektanker.Icons.Avalonia` 已停止维护，需要迁移到兼容分支或其他图标库；
3. LiveCharts 稳定包尚未明确以 Avalonia 12 为依赖基线；
4. 项目直接锁定了 SkiaSharp 2.88.9，需要与 Avalonia 12、SVG、图表和调试依赖重新对齐；
5. `Avalonia.Diagnostics` 在 Avalonia 12 中被移除，Debug/CDP 工具链需要调整；
6. 多处样式和交互依赖控件模板内部部件，编译成功不能替代视觉与交互回归。

综合判断：

- 技术可行性：高；
- 直接升级安全性：低；
- 有计划迁移后的成功概率：高；
- 预估工作量：约 4～8 个开发日，平台特定窗口或图表问题可能增加时间。

## 3. 当前项目基线

### 3.1 框架和项目

- 应用入口：`Diary.App`；
- 公共 UI：`Diary.GUIBase`；
- Tracker UI：`Diary.Jira.UI`、`Diary.RedMine.UI`；
- UI 测试：`Diary.AppTests`、`Diary.DbTests` 中的 Avalonia Headless 测试；
- 目标框架：主要项目为 `net10.0`；
- Avalonia 主版本：11.3.18；
- 编译绑定：`Diary.App`、Jira UI 和 Redmine UI 已启用 `AvaloniaUseCompiledBindingsByDefault`。

### 3.2 当前直接 UI 依赖

| 组件 | 当前版本 | 主要用途 |
| --- | ---: | --- |
| `Avalonia.Desktop` | 11.3.18 | 桌面应用和平台后端 |
| `Avalonia.Diagnostics` | 11.3.18 | Debug 诊断 |
| `Avalonia.Headless` | 11.3.18 | UI/数据库测试 |
| `Avalonia.Controls.ColorPicker` | 11.3.18 | 颜色选择器基础控件 |
| `Avalonia.Controls.DataGrid` | 11.3.13 | 表格 |
| `TreeDataGrid.Avalonia` | 11.3.1 | 层级统计和调查结果 |
| `Semi.Avalonia` | 11.3.14 | 应用主题和基础样式 |
| `Semi.Avalonia.ColorPicker` | 11.3.14 | Semi 颜色选择器主题 |
| `Semi.Avalonia.DataGrid` | 11.3.7.3 | Semi DataGrid 主题 |
| `Irihi.Ursa` | 1.15.1 | 窗口、Overlay、消息框、PathPicker 等 |
| `Irihi.Ursa.Themes.Semi` | 1.15.1 | Ursa Semi 主题 |
| `Semi.Avalonia.AvaloniaEdit` | 11.2.0.2 | 脚本编辑器主题 |
| `AvaloniaEdit.TextMate` | 11.2.0 | 脚本语法高亮 |
| `LiveChartsCore.SkiaSharpView.Avalonia` | 2.0.5 | 统计柱状图 |
| `Projektanker.Icons.Avalonia` | 9.6.2 | 图标控件和 provider |
| `Projektanker.Icons.Avalonia.FontAwesome` | 9.6.2 | Font Awesome 图标 |
| `Projektanker.Icons.Avalonia.MaterialDesign` | 9.6.2 | Material Design 图标 |
| `Svg.Controls.Skia.Avalonia` | 11.3.9.5 | 脚本语言 SVG 图标 |
| `Xaml.Behaviors.Avalonia` | 11.3.9.6 | 事件触发和拖动重排行为 |
| `Chrome.DevTools.Avalonia.v11` | 0.1.0-preview.30 | Debug CDP UI 自动化 |
| `SkiaSharp` | 2.88.9 | 字体文件校验及图形依赖 |

## 4. Avalonia 12 核心影响

Avalonia 12 于 2026-04-07 正式发布；截至本评估日期，官方稳定线已经进入 12.1.x。正式迁移应统一选择同一 Avalonia 12 补丁基线，不应混合多个不一致的 Avalonia 核心版本。

### 4.1 .NET 支持

Avalonia 12 移除了 .NET Framework 和 .NET Standard 支持，仅支持 .NET 8 及以上版本，并推荐 .NET 10。DiaryApp 已使用 `net10.0`，不需要先执行目标框架迁移。

### 4.2 Diagnostics

Avalonia 12 移除了旧 `Avalonia.Diagnostics` 包，官方迁移指引要求改用 `AvaloniaUI.DiagnosticsSupport`。

需要区分：

- `AvaloniaUI.DiagnosticsSupport` 是应用和新 Developer Tools 之间的连接支持；
- 新 Developer Tools 的使用受 Avalonia 工具产品授权约束；
- 该工具不属于发布版运行时的必要依赖；
- 项目现有 CDP 自动化可以优先迁移到支持 Avalonia 12 的 `Chrome.DevTools.Avalonia` 通用包。

### 4.3 窗口装饰

Avalonia 12 调整了窗口标题栏、绘制装饰和 Caption Buttons 相关结构。项目没有直接使用 Avalonia 原生标题栏模板，但通过 Ursa 重写了标题栏和 `CaptionButtons`，因此最终风险主要由 Ursa 2.x 迁移体现。

### 4.4 输入框占位文本

项目在多个 XAML 文件中使用 `Watermark`。Ursa 2.x 的迁移说明提到其控件将 Watermark 语义调整为 `PlaceholderText`。实施时必须区分 Avalonia 原生 `TextBox.Watermark` 与 Ursa 自有控件属性，不能进行无差别全局替换。

### 4.5 Clipboard

大部分剪贴板调用已经通过 `TopLevel.Clipboard` 获取服务，但 `AppClipboardScriptApi` 仍从 `MainWindow.Clipboard` 取值。迁移时应按 Avalonia 12 Clipboard 指引统一审计，确保脚本剪贴板 API 在窗口未创建、窗口关闭和 Headless 测试中保持既有行为。

## 5. 第三方组件兼容矩阵

| 组件 | Avalonia 12 状态 | 风险 | 建议 |
| --- | --- | --- | --- |
| Avalonia Desktop/Headless | 官方正式支持 | 低 | 所有 Avalonia 官方包统一升级到同一 12.x 补丁线 |
| Avalonia DataGrid/ColorPicker | 官方已有 12.x | 低到中 | 升级并执行表格、日期和颜色选择视觉回归 |
| Semi.Avalonia | 已有 12.1.0.1 | 低到中 | 与 Semi DataGrid、ColorPicker 和 AvaloniaEdit 主题成组升级 |
| Irihi.Ursa | 已有 2.2.0 | 高 | 按 Ursa 2.x breaking changes 迁移窗口、Overlay 和对话框 API |
| Irihi.Ursa.Themes.Semi | 已有 2.2.0 | 中 | 与 Ursa、Semi 使用相同兼容组合 |
| Semi.Avalonia.AvaloniaEdit | 已有 12.0.0 | 中 | 验证代码补全、Hover、缩放、保存和主题 |
| AvaloniaEdit.TextMate | 已有 12.0.0 | 中 | 与 AvaloniaEdit 12 同步升级 |
| TreeDataGrid.Avalonia | 社区分支已有 12.0.0 | 中 | 保持社区 MIT 分支，验证模板内部选择器和展开逻辑 |
| Svg.Controls.Skia.Avalonia | 已有 12.0.0.15 | 中 | 与 Avalonia.Skia 和 SkiaSharp 对齐 |
| Xaml.Behaviors.Avalonia | 已有 12.0.7 | 低到中 | 验证事件触发、命令参数和拖动重排 |
| Optris.Icons.Avalonia | 已有 12.0.7 | 低 | 作为 Projektanker 的首选替代 |
| LiveCharts Avalonia | 稳定包仍以 Avalonia 11 为依赖基线 | 中到高 | 先做迁移分支实测，失败则切换 ScottPlot |
| ScottPlot.Avalonia | 5.1.59 明确支持 Avalonia 12 | 中 | 作为柱状图和新增饼图的免费后备方案 |
| Chrome.DevTools.Avalonia | 通用包 preview.34 依赖 Avalonia 12.0.5 | 中 | 替换 `.v11` 包并验证 `CdpServer` API |

## 6. Ursa 2.x 迁移风险

Ursa 已支持 Avalonia 12，但 1.x 到 2.x 不是无破坏升级。

### 6.1 标题栏和 Caption Buttons

`MainWindow.axaml` 当前：

- 继承 `UrsaWindow`；
- 重写 `u|TitleBar` 控件模板；
- 直接创建 `u:CaptionButtons`；
- 使用 `TitleBarContent`；
- 在深浅主题切换时反转标题栏和状态栏 `ThemeVariantScope`。

Ursa 2.x 已移除原 CaptionButtons，并按照 Avalonia 12 的绘制窗口装饰重新组织标题栏。该区域需要重新实现，不能期待现有模板直接编译。

至少需要验证：

- 窗口拖动；
- 双击标题栏最大化/还原；
- 最小化、最大化和关闭按钮；
- Windows、Linux X11、Linux Wayland 的装饰差异；
- 深浅主题切换；
- 自定义标题内容和版本菜单；
- 最大化状态下的边距和缩放。

### 6.2 Overlay 和消息框

项目使用 `OverlayDialogHost` 并注册自定义 `ViewLocator`，同时存在多个：

```csharp
MessageBox.ShowOverlayAsync(...)
```

Ursa 2.x 将消息框入口改为：

```csharp
OverlayMessageBox.ShowAsync(...)
```

Overlay 默认安全边距和窗口装饰层级也发生变化。迁移后应重点检查确认框是否被标题栏按钮遮挡、对话框内容是否正确套用数据模板，以及关闭窗口时是否残留 Overlay。

### 6.3 PathPicker

项目在字体设置、普通路径设置和数据库迁移中使用 `PathPicker`。Ursa 2.x 调整了按钮内容、对话框标题和 Command 参数语义。当前主要使用 `SelectedPathsText`，预计改动有限，但仍需覆盖：

- 文件与目录选择；
- 取消选择；
- 建议起始目录；
- 字体文件路径；
- 数据库迁移源文件；
- Windows/Linux 原生文件选择器。

## 7. TreeDataGrid 选择和许可证边界

项目当前使用的是社区包：

```text
TreeDataGrid.Avalonia
```

该社区分支已发布 12.0.0，并继续使用 MIT License。建议保持这一包线。

不要在本次迁移中无意切换到：

```text
Avalonia.Controls.TreeDataGrid
```

Avalonia 官方 TreeDataGrid 12 属于商业组件产品线，并且 API 进行了较大重构。切换后不仅会引入许可证要求，还会影响 `HierarchicalTreeDataGridSource<T>`、泛型列和现有模板逻辑。

项目对 TreeDataGrid 的使用包含：

- 两个 `HierarchicalTreeDataGridSource<T>`；
- `HierarchicalExpanderColumn<T>`；
- 多个 `TemplateColumn<T>`；
- `TreeDataGridRow.TryGetCell()`；
- `TreeDataGridExpanderCell.IsExpanded`；
- `PART_CellsPresenter` 等模板内部选择器；
- 自定义交替行和表头样式。

因此，即使社区 12.0.0 保持源 API，仍必须执行统计页、调查结果页、展开/折叠、命令按钮和行样式回归。

## 8. 图标迁移方案

### 8.1 当前使用规模

项目当前约使用 39 个不同图标名称：

- Font Awesome：9 个；
- Material Design Icons：30 个；
- 涉及约 16 个 XAML/C# 文件；
- 图标既有固定字符串，也有 `NavigateInfo.Icon`、统计页和脚本语言图标等动态字符串绑定。

### 8.2 首选方案：Optris.Icons.Avalonia

推荐替换：

```text
Projektanker.Icons.Avalonia
→ Optris.Icons.Avalonia

Projektanker.Icons.Avalonia.FontAwesome
→ Optris.Icons.Avalonia.FontAwesome

Projektanker.Icons.Avalonia.MaterialDesign
→ Optris.Icons.Avalonia.MaterialDesign
```

理由：

- MIT License；
- 12.0.7 明确依赖 Avalonia 12；
- 延续原 Projektanker 项目；
- 保留现有 XAML URI；
- 现有 `mdi-*`、`fa-*` 字符串和动态绑定可以最大程度保留；
- 迁移主要集中在 PackageReference、C# 命名空间和 provider 注册。

### 8.3 其他免费替代

`Material.Icons.Avalonia` 3.0.2 和 `FluentIcons.Avalonia` 2.1.337 均为 MIT，并明确依赖 Avalonia 12，但需要重新映射全部图标名称和动态绑定类型。除非同时进行整体视觉风格重构，否则不建议在 Avalonia 12 迁移中采用。

也可以将实际使用的 SVG 或 `StreamGeometry` 内置到项目中，从而删除运行时图标库依赖。该方案依赖最少，但需要维护资源键映射、上游图标许可证和署名，首次迁移成本高于 Optris。

## 9. LiveCharts 和新增饼图

### 9.1 当前用法

当前统计页只使用：

- 一个 `CartesianChart`；
- 一个 `ColumnSeries<double>`；
- 分类横轴；
- 隐藏图例；
- 禁止缩放和平移；
- 禁用动画；
- 后台生成统计快照后在 UI 线程更新图表数据。

新增需求是在同一统计数据上增加饼图。

### 9.2 LiveCharts 准入条件

`LiveChartsCore.SkiaSharpView.Avalonia` 2.0.5 的稳定包仍声明 Avalonia 11 和 Avalonia.Skia 11 依赖。上游 Avalonia 12 跟踪问题中存在“应用可运行”的测试反馈，但截至本评估日期仍没有明确的稳定 v12 包承诺。

迁移分支可以先保留 LiveCharts，但必须满足以下条件后才能继续使用：

- Restore 不产生 11/12 Avalonia 依赖冲突；
- Windows 和 Linux 首次渲染正常；
- 页面切换和重复刷新不崩溃；
- 柱状图和新增饼图均正确更新；
- 深浅主题、字体和 SkiaSharp 版本对齐；
- Tooltip、标签和图例没有已知阻塞问题；
- 发布包不携带重复或不兼容的 Skia 原生库。

### 9.3 后备方案：ScottPlot.Avalonia

如果 LiveCharts 不能满足准入条件，推荐迁移到 `ScottPlot.Avalonia` 5.1.59 或后续兼容补丁版本。

选择理由：

- MIT License；
- NuGet 明确依赖 Avalonia 12 和 Avalonia.Skia 12；
- 5.1.59 发布说明明确增加 Avalonia 12 支持；
- 支持分类柱状图、饼图和环形图；
- 支持标签、图例、调色板和深色主题；
- 当前项目本来就在模型中创建实际图表控件，ScottPlot 的命令式 API 不会引入完全不同的架构模式。

建议把具体图表库封装在项目内的 `StatisticsChartView` 中，使统计模型只暴露标签、数值、总计和展示模式。这样可以避免未来更换图表库时再次影响统计业务逻辑。

### 9.4 饼图展示规则

柱状图适合比较绝对工时，饼图适合观察占比。建议在“工时分布”标题区提供“柱状图/饼图”切换，而不是长期并排显示两张图。

饼图应遵循：

- 使用与柱状图相同的统计快照，不重复查询数据库；
- 最多独立展示前 8 个主要标签；
- 第 9 项以后合并为“其他”；
- 未归入主要标签的时间显示为“未分类”或纳入“其他”；
- 零值不创建扇区；
- 图例显示名称、小时数和百分比；
- 扇区内文字应避免过密，必要时只显示百分比；
- 深浅主题使用稳定、可区分且色盲相对友好的调色板。

### 9.5 不采用的方案

Avalonia 官方 Charts 属于商业组件产品线。为了维持当前免费运行时依赖，本次迁移不采用该组件。

OxyPlot 本身为 MIT，但其 Avalonia 适配没有像 ScottPlot 5.1.59 一样提供清晰的 Avalonia 12 正式支持声明，不能有效降低本次迁移的不确定性。

项目内自绘柱状图和饼图在技术上可行，但需要自行实现扇区、标签碰撞、图例、Tooltip、命中测试、DPI、主题和无障碍支持。除非未来明确希望完全消除图表依赖，否则优先使用 ScottPlot。

## 10. SkiaSharp 和 SVG

`Diary.App` 当前直接固定 SkiaSharp 2.88.9，用于通过 `SKTypeface.FromFile()` 验证字体文件。Avalonia 12、SVG v12、ScottPlot 和新版 CDP 包均会影响 SkiaSharp 依赖解析。

迁移时不应继续无条件固定 2.88.9。应选择以下一种策略：

1. 删除直接版本锁定，让统一的 Avalonia 12 依赖图解析 SkiaSharp；
2. 明确固定为 Avalonia 12、SVG、图表和 CDP 都兼容的版本。

字体校验逻辑本身较简单，但必须验证：

- TTF/OTF 正常文件；
- 无效或损坏字体；
- 不存在和不可读取文件；
- Windows/Linux 原生库加载；
- 随包字体和外部字体切换。

## 11. Debug、CDP 和自动化

`Chrome.DevTools.Avalonia.v11` 的 NuGet 约束明确排除 Avalonia 12，不能继续保留。

迁移目标为不带 `.v11` 后缀的：

```text
Chrome.DevTools.Avalonia
```

通用包 preview.34 已依赖 Avalonia 12.0.5。迁移后必须重新编译并验证：

- `Avalonia.Diagnostics.Cdp` 命名空间；
- `CdpServer.Start()` 和 `CdpServer.Stop()`；
- `DIARY_CDP_PORT`；
- 隔离 UI 测试根目录；
- Windows/Linux CDP smoke 和 extended 套件；
- UI 测试退出后的端口和进程回收。

如果同时接入 `AvaloniaUI.DiagnosticsSupport`，应限制在 Debug 构建，并与现有 CDP 自动化职责分开，避免将商业 Developer Tools 变成发布或 CI 的强制依赖。

## 12. 免费和商业组件边界

按本文推荐路线，应用运行时可以继续只使用免费组件：

- Avalonia 核心：MIT；
- Semi、Ursa、AvaloniaEdit、TextMate：MIT；
- `TreeDataGrid.Avalonia` 社区分支：MIT；
- Optris Icons：MIT；
- LiveCharts 或 ScottPlot：MIT；
- SVG、Xaml.Behaviors、Chrome DevTools Avalonia：MIT。

需要避免误选：

- `Avalonia.Controls.TreeDataGrid`：官方商业 TreeDataGrid；
- Avalonia 官方 Charts：商业图表组件；
- 将新版 Developer Tools 许可证误认为 Avalonia 核心或发布版运行时许可证。

图标包自身采用 MIT 不代表其中所有上游图标资产使用完全相同的许可证。继续使用 Font Awesome 和 Material Design Icons 时，应保留对应上游许可证和必要署名；这不是 Avalonia 12 新增的问题，但迁移包时应一并复检发布包许可证清单。

## 13. 建议迁移阶段

### 阶段 1：建立可恢复的依赖基线

1. 建立独立迁移分支；
2. 记录当前 Restore 依赖图和发布包内容；
3. 统一 Avalonia 官方包到同一 12.x 补丁线；
4. 成组升级 Semi、Ursa、AvaloniaEdit、TextMate、SVG 和 Behaviors；
5. 将 Projektanker 替换为 Optris；
6. 将 CDP `.v11` 包替换为通用包；
7. 处理 SkiaSharp 版本锁定。

阶段目标：Restore 成功，不混入 Avalonia 11 程序集。

### 阶段 2：恢复编译

优先处理：

1. Ursa 标题栏和 Caption Buttons；
2. Overlay MessageBox API；
3. Diagnostics 和 CDP；
4. Clipboard；
5. TreeDataGrid 和模板选择器；
6. AvaloniaEdit/TextMate；
7. Watermark/PlaceholderText 等控件属性。

阶段目标：Debug、Release 和测试项目全部编译。

### 阶段 3：恢复运行和视觉

重点验证：

- 主窗口标题栏和窗口状态；
- Overlay、对话框和原生文件选择器；
- DataGrid、TreeDataGrid、ColorPicker；
- 代码编辑器；
- SVG 和图标；
- 系统托盘和 NativeMenu；
- 深浅主题和字体切换。

阶段目标：Windows/Linux 核心流程可人工运行。

### 阶段 4：图表和饼图

1. 先验证 LiveCharts 在 Avalonia 12 上的柱状图；
2. 加入饼图和“柱状图/饼图”切换；
3. 如果 LiveCharts 未通过准入条件，迁移到 ScottPlot；
4. 落实 Top 8、其他、未分类、图例和配色规则。

阶段目标：两种图表在相同统计快照下结果一致。

### 阶段 5：自动化和发布门禁

1. 执行全部单元、数据库和 Headless 测试；
2. 执行 Windows/Linux CDP 套件；
3. 验证原生窗口、托盘、剪贴板和文件选择器；
4. 检查发布包中 Avalonia、Skia 和第三方程序集版本；
5. 检查第三方许可证和图标资产许可证；
6. 完成 Windows/Linux 发布包 smoke。

阶段目标：满足正式合并和发布条件。

## 14. Go/No-Go 验收条件

只有同时满足以下条件，迁移才可以合入主线：

- Restore 图中不存在 Avalonia 11 运行时程序集；
- Debug 和 Release 均可编译；
- 所有现有自动化测试通过，或失败项已有明确的 v12 等价替代；
- Ursa 标题栏、Overlay 和消息框在 Windows/Linux 正常；
- TreeDataGrid 展开、模板单元格和命令按钮正常；
- AvaloniaEdit 的语法高亮、补全、Hover、保存和缩放正常；
- 图标无缺失，动态图标名称解析正常；
- 柱状图和饼图正常刷新，统计数值一致；
- 系统托盘、NativeMenu、Clipboard 和文件选择器正常；
- 发布包不存在重复或冲突的 Skia 原生库；
- 没有意外引入商业运行时组件；
- 第三方许可证清单完成复检。

以下任一情况应暂停迁移合并：

- LiveCharts 或替代图表在目标平台持续崩溃；
- Ursa 窗口装饰存在关闭、拖动或最大化阻塞；
- CDP 自动化无法迁移且没有可接受的替代门禁；
- 必须依赖商业组件才能恢复当前免费功能；
- Windows/Linux 发布包加载不同版本 Avalonia 或 SkiaSharp。

## 15. 推荐最终依赖路线

推荐路线：

```text
Avalonia 12.1.x
Semi.Avalonia 12.x
Irihi.Ursa 2.x
Semi.Avalonia.AvaloniaEdit 12.x
AvaloniaEdit.TextMate 12.x
TreeDataGrid.Avalonia 12.x（社区 MIT 分支）
Optris.Icons.Avalonia 12.x
Svg.Controls.Skia.Avalonia 12.x
Xaml.Behaviors.Avalonia 12.x
Chrome.DevTools.Avalonia（Debug only）
LiveCharts 通过实测后保留，否则 ScottPlot.Avalonia 5.1.59+
```

不推荐路线：

```text
Avalonia.Controls.TreeDataGrid（商业组件）
Avalonia 官方 Charts（商业组件）
Projektanker.Icons.Avalonia 9.6.2
Chrome.DevTools.Avalonia.v11
继续固定 SkiaSharp 2.88.9
混用 Avalonia 11 和 12 控件程序集
```

## 16. 参考资料

- [Avalonia 12 发布说明](https://avaloniaui.net/blog/avalonia-12/)
- [Avalonia 12 Breaking Changes](https://docs.avaloniaui.net/docs/avalonia12-breaking-changes)
- [Avalonia 12.1 发布说明](https://avaloniaui.net/blog/release-12-1)
- [Ursa 2.0 Breaking Changes](https://github.com/irihitech/Ursa.Avalonia/discussions/960)
- [Ursa 2.0 breaking changes 跟踪](https://github.com/irihitech/Ursa.Avalonia/issues/692)
- [TreeDataGrid.Avalonia 社区分支发布页](https://github.com/fidarit/TreeDataGrid.Avalonia/releases)
- [官方 TreeDataGrid v12 Breaking Changes](https://docs.avaloniaui.net/controls/data-display/structured-data/treedatagrid/breaking-changes-v12)
- [Optris.Icons.Avalonia NuGet](https://www.nuget.org/packages/Optris.Icons.Avalonia)
- [Material.Icons.Avalonia NuGet](https://www.nuget.org/packages/Material.Icons.Avalonia)
- [FluentIcons.Avalonia NuGet](https://www.nuget.org/packages/FluentIcons.Avalonia)
- [LiveCharts Avalonia 12 支持跟踪](https://github.com/Live-Charts/LiveCharts2/issues/2117)
- [ScottPlot 5.1.59 发布说明](https://github.com/ScottPlot/ScottPlot/releases/tag/5.1.59)
- [ScottPlot Avalonia NuGet](https://www.nuget.org/packages/ScottPlot.Avalonia)
- [Chrome.DevTools.Avalonia NuGet](https://www.nuget.org/packages/Chrome.DevTools.Avalonia)
