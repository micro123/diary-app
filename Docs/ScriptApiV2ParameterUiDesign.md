# 脚本 API V2 参数 UI/UX 设计

状态：**已实现**

日期：2026-08-25

依赖：[`ScriptApiV2ParameterDesign.md`](ScriptApiV2ParameterDesign.md)

## 1. 背景

脚本 API V2 核心运行时已经支持加载期参数发现、默认值合并、执行前校验、三语言 Worker 和 V1 兼容。本文记录类型化参数 UI 的最终实现，替代了 V2 原有的自由格式 `key=value` 输入：

- 管理页手动运行和 Editor 参数脚本复用类型化表单；
- 管理页 `defaultArguments` 使用内嵌类型化编辑并只保存 metadata 覆盖项；
- 参数错误归属到字段并自动聚焦首个错误；
- Automation 缺少必填默认值时保留参数契约并显示“待配置”；
- 有人值守运行支持分作用域参数记忆、单项/整体重置和本地历史清除。

本设计只处理参数相关 UI/UX，不改变 V2 参数协议、Host API 权限、脚本副作用策略或 Worker 隔离边界。

## 2. 设计目标

1. V2 脚本在手动运行、Editor 入口和默认参数配置中使用同一套类型化表单。
2. V1 保持现有自由 `key=value` 模式，不强制迁移。
3. 无参数脚本不增加额外交互；Editor 无参数脚本继续点击后立即执行。
4. 参数表单保持紧凑，避免每个字段都使用高卡片或长说明文本。
5. 明确区分脚本默认值、metadata 覆盖值、本次运行值和“未设置”。
6. 错误显示在对应字段附近，执行前即可定位、修正并聚焦首个错误。
7. Automation 即使因必填默认值不完整而不可调度，也必须能在管理页看到参数并完成配置。
8. 所有最终值仍由 `ScriptParameterBinder` 校验，UI 不能形成第二套不一致的协议规则。
9. 有人值守运行恢复上次已被宿主接受的参数，减少重复输入，同时允许恢复默认值和清除记忆。
10. 静态候选建议、数值/日期范围、步长和文本长度在控件上提供直接反馈，并由 Binder 最终强制执行。

## 3. 非目标

- 本阶段不增加 Secret、文件、目录、Tracker、标签等新参数类型。
- 不支持参数间动态联动、条件显示或远程加载 Choice。
- 不支持 Regex/Pattern、动态候选源或依赖 Tracker/标签等本地对象的候选列表。
- 不在日历右键菜单内嵌复杂控件。
- 不重做整个脚本管理页的信息架构，只调整参数相关区域。
- 不把执行日志复制功能混入参数表单；日志复制继续作为独立 TODO。

## 4. 交互原则

### 4.1 无参数时不打扰

- V2 Editor 脚本没有参数：保持立即执行。
- V2 Application、Query、Automation 从管理页手动运行：仍打开现有运行对话框，因为还需要 Preview、超时和幂等键；参数区域不显示空卡片。
- V1 Editor 脚本：宿主不知道自由参数契约，保持立即执行。

### 4.2 参数说明不占固定高度

字段使用“左侧标签 + 右侧控件”的紧凑布局。较长 `Description` 通过信息图标和 Tooltip 展示，不在每个字段下方常驻一行说明。

只有以下文本允许占用额外行：

- 当前字段的校验错误；
- Choice 当前值需要解释且 Label 与 Value 差异明显；
- DateTime 的时区或夏令时歧义提示。

### 4.3 不仅靠颜色表达状态

必填、错误、默认来源和覆盖状态必须同时使用文字或图标表达，颜色只作为辅助。

### 4.4 Binder 是最终真相

控件可以提供即时输入限制，但点击运行或保存时必须再次调用 Binder。UI 不通过解析英文诊断文本猜测字段，而应取得结构化的参数名、错误码和消息。

## 5. 共用参数表单

新增可复用的 `ScriptParameterFormView`，由三处使用：

1. 脚本管理页“运行脚本”对话框；
2. Editor 右键脚本参数对话框；
3. 脚本管理页“默认参数”配置区。

建议的 ViewModel 分层：

```text
ScriptParameterFormViewModel
  +-- Mode: Run / MetadataDefaults
  +-- Fields: ScriptParameterFieldViewModel[]
  +-- Validate()
  +-- ResetAll()
  +-- BuildArguments()

ScriptParameterFieldViewModel
  +-- Definition
  +-- Value / typed projections
  +-- ValueSource
  +-- HasMetadataOverride
  +-- WasRestoredFromLastRun
  +-- ValidationMessage
  +-- type visibility properties
```

`ValueSource` 至少区分：

- `Unset`：没有 descriptor 默认值，也没有 metadata 覆盖；
- `DescriptorDefault`：当前显示脚本源码或 manifest 中的默认值；
- `MetadataOverride`：当前值由 `defaultArguments` 覆盖；
- `LastRun`：当前值来自该脚本、入口和目标类型对应的上次执行参数；
- `RunInput`：用户在本次运行中修改过；
- `Cleared`：用户明确清空，用于覆盖已有默认值。

表单不能只保存“当前显示值”，否则无法区分继承默认值和 metadata 覆盖值。

## 6. 参数控件映射

| 参数类型 | 控件 | 空值行为 | 规范化提示 |
| --- | --- | --- | --- |
| `String` | 单行 `TextBox`；有 `Suggestions` 时使用可编辑 `ComboBox`/`AutoCompleteBox` | 可选参数允许空字符串；清空已有默认值表示显式空值 | 禁止换行，候选列表外输入仍合法 |
| `MultilineString` | `TextBox`，`AcceptsReturn=True`，默认高 88-96px | 可选参数允许空字符串 | 换行统一为 `\n` |
| `Integer` | 可清空的 `NumericUpDown`，默认步进 1 | 空表示未提供 | 使用 invariant 十进制整数；应用 Minimum/Maximum/Step |
| `Number` | 可清空的 `NumericUpDown` | 空表示未提供 | 不显示千位分隔符，不接受 NaN/Infinity；应用范围和步长 |
| `Boolean` | 必填时使用二态选择；可选且无默认值时使用“未设置/是/否”三态 `ComboBox` | “未设置”不传值 | 输出 `true` / `false` |
| `Date` | `CalendarDatePicker`，允许清空 | 空表示未提供 | 输出 `yyyy-MM-dd`；应用日期范围 |
| `DateTime` | 日期选择器 + 时间选择器 + 时区摘要 | 任一部分为空视为未提供 | 输出带偏移 ISO 8601；应用时刻范围 |
| `Choice` | `ComboBox`，显示 Label | 可选参数首项提供“未设置” | 传递精确 Value，不传 Label |

### 6.1 DateTime 时区

DateTime 行使用以下紧凑结构：

```text
[日期选择器] [时间选择器]  UTC+08:00
```

- 已有值带有偏移时，首次显示保留原偏移；
- 用户修改日期或时间后，按本机时区重新计算该日期的偏移；
- 本地时间落入夏令时跳空区间时显示字段错误，不自动挪动时间；
- 本地时间存在两个偏移时，显示一个紧凑偏移选择器，默认保留原偏移，否则选择本机时区返回的标准偏移；
- Tooltip 明确说明最终传给脚本的是带时区偏移字符串。

### 6.2 Choice

ComboBox 主文本显示 `Label`。当 `Label` 与 `Value` 不同时，在 Tooltip 中显示 `值：{Value}`，避免界面长期增加第二行。

### 6.3 Suggestions 与静态约束

- `Choice` 是严格枚举，只能选择声明的值；`Suggestions` 是软候选，用户可以输入候选列表外的字符串。
- Suggestions 主文本显示 Label，选中后写入 Value；自由输入时直接使用用户文本。
- NumericUpDown 从 `Minimum`、`Maximum`、`Step` 初始化，但点击运行或保存时仍调用 Binder，防止粘贴、恢复历史或控件边界差异绕过约束。
- Date/DateTime 控件禁用范围外日期；手工输入仍显示字段错误而不是静默截断。
- String/MultilineString 可以显示剩余长度摘要，但仅接近 `MaxLength` 或超限时出现，避免常驻占高。
- `Unit` 放在输入框尾部短文本；空间不足时改为 Tooltip，不把单位写入最终参数值。

约束错误示例：

```text
最低工时          [ 30 小时 ]
                  必须在 0 到 24 之间

标题              [ 周报草稿…… ]
                  最多 80 个字符
```

### 6.4 必填标记

左侧标签显示：

```text
统计范围 *  (i)
range · Choice
```

第二行使用 11-12px 弱化文本；在特别紧凑的对话框中可只显示 `range`，类型放入 Tooltip。星号同时设置可访问性说明“必填”。

## 7. 手动运行对话框

### 7.1 模式

现有 `ScriptRunDialogView` 改为 V1/V2 双模式：

- V1：继续显示自由 `key=value` 多行输入框和现有解析规则；
- V2：显示 `ScriptParameterFormView`；
- V2 无参数：隐藏整个参数区。

### 7.2 推荐布局

```text
┌ 运行脚本                                      V2 ┐
│ 参数化工时汇总                                   │
├─────────────────────────────────────────────────┤
│ 参数                                             │
│ 统计范围 *        [ 本周                    ▾ ]  │
│ 最低工时          [ 0                         ]  │
│ 包含零工时        [ 未设置 / 是 / 否          ]  │
│                    字段错误仅在此处出现           │
│                                                 │
│ ▸ 更多执行选项                                   │
├─────────────────────────────────────────────────┤
│ □ 预览执行                         [取消] [运行] │
└─────────────────────────────────────────────────┘
```

尺寸建议：

- 默认宽度 620px；最小宽度 520px；最大宽度 760px；
- 外边距 12px，区块间距 8px，普通字段间距 4-6px；
- 内容最大高度 720px，参数区独立滚动，标题和底部操作固定；
- 单行字段目标高度 32-36px；
- 不为每个参数创建独立 Card。

### 7.3 执行选项

Preview 是安全相关的常用选项，始终显示在固定底栏左侧。

幂等键和超时时间收纳到默认折叠的“更多执行选项”：

- metadata 配置了非默认超时或当前填写了幂等键时自动展开；
- 折叠标题显示摘要，例如 `更多执行选项 · 300 秒`；
- 展开内容仍使用左右字段布局，不使用说明卡片；
- Preview 的完整说明移到信息图标 Tooltip，删除当前常驻的长说明段落。

### 7.4 恢复默认值与参数记忆

参数区标题右侧提供“恢复默认值”：

- 恢复到 descriptor + metadata 合并后的有效默认值；
- 不影响 Preview、超时和幂等键；
- 单字段在有修改时显示小型重置按钮，Tooltip 为“恢复此参数的默认值”。

当表单恢复了历史值时，在参数区标题旁显示弱化状态“已填入上次使用值”，并在更多菜单提供“清除记忆”：

- “恢复默认值”只改变当前表单，不立即删除历史；
- “清除记忆”删除当前脚本和当前入口作用域的记录，并立即恢复 descriptor + metadata 默认值；
- Preview、超时、幂等键和 `AutomationEventData` 不属于记忆内容；
- 只有 Binder 校验通过且执行请求被宿主接受后才更新记忆；取消、校验失败和 `Rejected` 不覆盖旧记录；
- 请求进入实际执行后，即使最终 `Failed`、`Cancelled` 或 `TimedOut`，也视为参数已使用并更新记忆。

## 8. Editor 右键脚本

### 8.1 菜单反馈

参数化 V2 脚本的菜单标题追加省略号，表示执行前还有一步：

```text
对当天运行：生成日报…
```

无参数脚本不追加省略号并继续立即执行。

### 8.2 参数对话框

Editor 参数对话框复用 `ScriptParameterFormView`，但不显示幂等键、超时和 Preview，保持一次轻量确认：

```text
┌ 运行编辑器脚本                                  ┐
│ 生成日报 · 2026-08-25 · 当天                    │
├────────────────────────────────────────────────┤
│ 输出格式          [ Markdown                ▾ ] │
│ 包含备注          [ 是 / 否                   ] │
├────────────────────────────────────────────────┤
│                                      [取消] [运行]│
└────────────────────────────────────────────────┘
```

目标摘要规则：

- Day：`2026-08-25 · 当天`；
- Week：`2026-08-24 至 2026-08-30 · 当前周`；
- Month：`2026年8月 · 当前月份`；
- Quarter：`2026年第3季度`；
- Year：`2026年`；
- WorkItem：`事项 #42 · 标题`，标题过长时省略并提供 Tooltip。

对话框按 `ScriptId + EntryKind + EditorTargetKind` 恢复上次有效参数，避免 Day、Week、Month、Quarter、Year 和 WorkItem 入口互相污染。默认超时仍取 metadata，Preview 为 false，幂等键沿用现有 Editor 请求行为，这些执行选项不进入参数记忆。

### 8.3 事项保存状态

当前事项目标仍要求事项已经保存；未保存事项的脚本菜单继续禁用并显示“请先保存”。参数对话框不承担自动保存职责。

## 9. 脚本管理页默认参数

### 9.1 V1

保留现有“默认执行参数”多行 `key=value` 文本框。

### 9.2 V2

将多行文本框替换为“默认参数”类型化表单。该表单编辑的是 metadata `defaultArguments` 覆盖，而不是修改 descriptor：

```text
默认参数                                      [全部使用脚本默认]
统计范围 *       [ 本月 ▾ ]     已覆盖      [↶]
最低工时         [ 0       ]     脚本默认
备注前缀         [         ]     未设置
```

状态语义：

- `脚本默认`：metadata 未保存该键，当前值来自 descriptor；
- `已覆盖`：metadata 保存了该键；
- `未设置`：两层默认值都不存在；
- `运行时必填`：非 Automation 的必填参数允许留给每次运行填写；
- `自动化必填`：Automation 必须配置有效默认值，显示错误并禁止保存非法配置。

编辑一个继承值时自动转为 metadata 覆盖。点击行尾重置按钮删除该键的 metadata 覆盖并回到 descriptor 默认值。保存时只写入覆盖项，不把所有有效值展开复制到 metadata，避免脚本升级后被旧值永久遮蔽。

默认参数配置模式不读取上次执行参数，也不显示“已填入上次使用值”。它只编辑可持久化、可随脚本配置迁移的 metadata 覆盖，不能把本机运行历史误保存为脚本默认值。

### 9.3 参数契约摘要

脚本列表在名称文本后紧跟醒目的主题强调色 `V1`、`V2` 等 API 版本标识，不将标识对齐到列表项右侧。标识由实际枚举数值生成，不对当前两个版本写死分支，因此后续增加 V3 时会自然显示为 `V3`；完整含义保留在 Tooltip 中。

概览区增加一行紧凑摘要：

```text
API V2 · 3 个参数 · 1 个必填
```

默认参数表单本身已经展示 Label、Name、类型、必填和说明，因此不再增加重复的“参数契约”大卡片。

## 10. Automation 的可配置状态

### 10.1 已解决的问题

原加载器会在 V2 Automation 的必填默认值不完整时返回构建失败并丢弃 Program。当前实现已将构建状态与配置状态分离，保留源码 descriptor，使管理页可以展示参数表单并补全配置。

### 10.2 状态模型

将“代码构建结果”和“运行配置可用性”分开：

```text
代码构建失败       -> 加载失败，不能配置参数契约
代码构建成功
  +-- 配置有效     -> 已加载，可手动运行和调度
  +-- 配置不完整   -> 待配置，不注册调度，但保留发现的 Descriptor
```

`ScriptDirectoryEntry` 保留构建阶段发现的 descriptor，即使后续 runtime configuration 校验失败，并提供：

- `DiscoveredDescriptor`；
- `ConfigurationState`：Ready / NeedsConfiguration / Invalid；
- `ConfigurationDiagnostics`。

管理页状态显示“待配置”，使用琥珀色而不是红色“加载失败”。用户可以打开默认参数表单，补全参数并保存；保存后自动重新加载，状态转为“已加载”。

### 10.3 调度门禁

- `NeedsConfiguration` 不进入 Automation Scheduler；
- 手动运行按钮禁用，Tooltip 为“请先补全自动化必填默认参数”；
- 参数字段错误仍使用 Binder 错误码；
- Schedule、RunOnStartup、Triggers 的现有校验规则保持不变。

## 11. 校验和错误反馈

### 11.1 触发时机

- 初次打开不立即显示红色错误；
- 字段失焦后校验该字段；
- 用户第一次点击“运行”或“保存”后，校验全部字段；
- 第一次全量校验后，后续输入实时更新错误状态；
- 全局大小限制、schema 变化等非字段错误显示在表单底部。

### 11.2 呈现方式

- 字段错误紧贴控件下方，使用系统错误前景色；
- 不在顶部重复列出同一批字段错误；
- 点击运行后自动滚动并聚焦第一个错误；
- Run/Save 按钮保持可点击，由点击动作触发明确反馈，避免用户面对禁用按钮却不知道原因；
- 校验进行中时按钮显示忙碌状态并防止重复提交。

### 11.3 结构化错误

建议为 Runtime 内部绑定结果增加参数归属，不修改 Worker wire 协议：

```csharp
public sealed record ScriptParameterBindingIssue(
    string Code,
    string Message,
    string? ParameterName = null);
```

`ScriptParameterBindingResult` 同时保留现有 `Diagnostics`，供执行结果和日志使用；UI 使用 `Issues.ParameterName` 定位字段，不解析错误消息文本。

## 12. 键盘和可访问性

- `Ctrl+Enter`：在任意字段中运行或保存；
- `Enter`：单行字段中运行，MultilineString 中插入换行；
- `Esc`：关闭对话框；
- Tab 顺序与参数声明顺序一致，随后进入执行选项和底部按钮；
- Choice 使用方向键选择，Date/Time 使用控件原生键盘行为；
- 每个控件的可访问名称使用 Label，帮助文本使用 Description；
- 必填、错误、默认来源不只依赖颜色；
- 错误出现后由屏幕阅读器播报，但不在每次按键时反复播报相同错误。

## 13. 响应式与密度

### 13.1 宽度充足

宽度不低于 560px 时使用两列字段：左侧标签 140-170px，右侧控件占剩余空间。

### 13.2 窄窗口

宽度不足 520px 时切换为上下布局：Label 位于控件上方，字段间距 6px。MultilineString、DateTime 和长 Choice 始终允许占满整行。

### 13.3 参数数量

- 1-6 个参数：自然高度显示；
- 7 个及以上：参数区滚动，底部操作固定；
- 最多 32 个参数，沿用 Runtime 限制；
- 不使用分页或多步骤向导，避免小型脚本运行流程过重。

## 14. ViewModel、本地状态与服务建议

### 14.1 上次参数本地状态

建议新增 `IScriptLastArgumentsStore` / `ScriptLastArgumentsStore`，使用独立本地文件：

```text
FsTools.GetApplicationDataDirectory()/ScriptState/last-arguments.json
```

建议记录模型：

```csharp
public sealed record ScriptLastArgumentsEntry(
    string ScriptId,
    ScriptEntryKind EntryKind,
    ScriptEditorTargetKind? EditorTargetKind,
    string? SchemaFingerprint,
    IReadOnlyDictionary<string, string>? Arguments,
    string? LegacyArgumentsText,
    DateTimeOffset UpdatedAtUtc);
```

作用域规则：

- Editor：`ScriptId + EntryKind + EditorTargetKind`；
- Application、Query 和管理页手动 Automation：`ScriptId + EntryKind`；
- 后台 Scheduled、Startup、Event Automation 不读取或写入该存储；
- V2 保存 Binder 输出的规范化字典；V1 保存原始 `key=value` 文本，以保留用户换行和排列方式。

V2 记录包含 schema fingerprint。fingerprint 包含 API version、参数 Name、Type、Required、`Choice.Value` 和全部合法性约束；不包含 Label、Description、Placeholder、DefaultValue、Suggestion Label 和 Unit 等不改变参数兼容性的展示或默认信息。

schema 变化时不简单丢弃整条记录：同名、同类型且仍满足当前 Choice/范围/长度/步长约束的值可以迁移；已删除、类型变化或不再合法的字段忽略，新字段回退到 descriptor + metadata 默认值。Suggestions 变化不会使自由字符串失效。

存储要求：

- 原子写入，损坏文件安全忽略并记录 warning，不阻止脚本运行；
- 最多保留 200 个作用域记录，总文件上限 4 MiB，按 `UpdatedAtUtc` 做 LRU 淘汰；
- 删除脚本时清除该 ScriptId 的记录；设置页提供“清除脚本执行参数历史”的全局入口；
- 不写入 AppConfig、数据库、同步、脚本共享包、日志、执行历史或诊断导出；
- 未来 Secret 参数不得持久化；当前普通 String/MultilineString 应提示不要输入 Token 或密码。

### 14.2 ViewModel 与服务

建议新增：

- `ScriptParameterFormViewModel`：表单状态、重置、校验和输出；
- `ScriptParameterFieldViewModel`：单字段类型化状态；
- `ScriptParameterFormService`：descriptor/defaultArguments 与 UI 状态互转；
- `IScriptLastArgumentsStore`：恢复、保存、迁移和清除有人值守运行参数；
- `ScriptParameterFormView.axaml`：共用表单；
- `EditorScriptRunDialogViewModel`：目标摘要和本次参数；
- `EditorScriptRunDialogView.axaml`：轻量 Editor 对话框。

调整：

- `ScriptRunDialogViewModel.Initialize` 接收 descriptor 和 metadata，不再只接收脚本名称；
- `ScriptListItem` 保存 API 版本、参数摘要和发现的 descriptor；
- `CreateEditorScriptCommand` 在 V2 且 `Parameters.Count > 0` 时打开参数对话框，否则沿用立即执行；
- `ScriptMetadataEditor` 对 V2 只保存表单生成的 metadata 覆盖字典；
- `ScriptDirectoryEntry` 保留 `DiscoveredDescriptor` 和配置状态，解决 Automation 待配置问题。

不建议把参数控件直接堆入 `ScriptRunDialogViewModel`，否则管理页默认参数与 Editor 对话框会复制同一套类型判断和校验逻辑。

## 15. 数据流

### 15.1 手动运行

```text
Descriptor + metadata defaults + 当前作用域上次参数
    -> ScriptParameterFormViewModel 初始化
    -> 用户修改
    -> UI 生成字符串字典
    -> ScriptParameterBinder 最终校验和规范化
    -> ScriptRunOptions.Arguments
    -> ScriptManager 再次防御性绑定
    -> 请求被接受并进入执行
    -> 保存本次规范化参数
    -> Worker / 最终执行结果
```

取消对话框、Binder 失败或最终结果为 `Rejected` 时不写入；进入执行后的 `Succeeded`、`Failed`、`Cancelled`、`TimedOut` 都更新。若后续执行 API 能直接暴露“请求已接受”事件，应在该事件发生时写入，而不是等待最终结果。

### 15.2 默认参数保存

```text
Descriptor defaults + metadata overrides
    -> 默认参数表单（保留来源）
    -> 用户修改/重置
    -> 只生成 metadata overrides
    -> 保存 sidecar/manifest
    -> 自动重新加载
    -> 更新 Ready / NeedsConfiguration 状态
```

## 16. 测试范围

### 16.1 ViewModel 单元测试

- 八种类型的初始值、编辑、清空和规范化；
- descriptor 默认值、metadata 覆盖、上次参数、本次输入和重置语义；
- 可选 Boolean 三态；
- Choice 显示 Label、提交 Value；
- Suggestions 显示 Label、选中提交 Value，并允许候选外自由输入；
- Minimum/Maximum/Step、MinLength/MaxLength 和 Unit 的控件映射与字段错误；
- DateTime 偏移、夏令时跳空和歧义处理；
- 字段错误映射和首个错误聚焦；
- V1 文本模式保持现有解析行为；
- metadata 保存只包含覆盖项。

### 16.2 入口测试

- 管理页 V1、V2 有参数、V2 无参数三种运行模式；
- Editor 无参数立即执行，有参数打开对话框；
- Editor 取消不执行，确认后传入规范化参数；
- Binder 通过且请求被接受后更新记忆；取消、校验失败和 `Rejected` 不覆盖旧记录；
- `Failed`、`Cancelled`、`TimedOut` 已进入执行时仍更新记忆；
- Editor 各 TargetKind 隔离，Application/Query/手动 Automation 按 EntryKind 隔离，后台 Automation 不使用记忆；
- schema 变化时迁移仍合法字段，Choice/范围/长度失效字段回退默认值；
- 恢复默认值不删除历史，清除记忆删除当前作用域记录，全局清除删除全部记录；
- 未保存 WorkItem 仍禁用；
- Automation 缺必填默认值显示“待配置”，补全后可调度；
- 保存默认参数后目录自动重载且不丢失当前选择。

### 16.3 CDP/UI 回归

- 1280×800 下 1、6、12、32 参数布局；
- MultilineString、DateTime、长 Choice Label 和长 Description；
- Suggestions、带单位数值、边界值、范围错误和长度计数；
- 字段错误出现后的高度、滚动和聚焦；
- 125%、150% DPI；
- 深色/浅色主题；
- 键盘完成整个运行流程；
- V1 原有运行对话框视觉和行为不回退。

## 17. 实施顺序

截至 2026-08-25，步骤 1-9 均已完成。类型化表单支持八种参数类型、值来源提示、单字段重置、静态约束、字段错误、首错聚焦、键盘提交、参数记忆和 Editor 目标隔离；Automation 已支持“待配置”状态。Linux X11 1280×800 CDP 扩展套件已覆盖 V2 参数表单、设置页历史清理入口和执行历史链路，32 参数由 ViewModel 回归覆盖。Windows 125%/150% DPI 与主题视觉抽样继续作为全局 UI 发布检查单的一部分，不再作为 V2 功能实现阻塞项。

1. 增加静态约束 DTO、schema/Binder 校验、结构化绑定 Issue 和三语言契约测试。
2. 增加参数表单 ViewModel、约束控件映射和纯单元测试。
3. 实现上次参数本地存储、schema 迁移、容量限制和清除服务。
4. 将脚本管理页手动运行对话框接入 V1/V2 双模式和参数记忆。
5. 将管理页默认参数区接入 V2 类型化表单和覆盖来源，确保不混入参数记忆。
6. 拆分 Automation 构建状态与配置状态，支持“待配置”。
7. 接入 Editor 参数对话框、目标类型隔离和菜单省略号提示。
8. 更新创建向导，默认选择 V2 并生成 V2 基类或 metadata，同时保留 V1 选项和简要差异说明。
9. 完成 CDP、键盘和最大参数数量回归，并将平台 DPI/主题视觉抽样并入全局 UI 发布检查单。

## 18. 待评审决策

本设计建议确认以下选择：

1. 手动运行对话框中 Preview 始终可见，幂等键和超时折叠到“更多执行选项”。
2. Editor 有参数时只显示参数，不额外显示 Preview、超时和幂等键。
3. 可选 Boolean 使用“未设置/是/否”三态，避免把未提供误当作 false。
4. 默认参数配置只保存 metadata 覆盖项，重置后继续继承脚本默认值。
5. Automation 缺必填默认值显示“待配置”，而不是笼统的“加载失败”。
6. 参数说明优先使用信息图标和 Tooltip，字段错误才占用常驻第二行。
7. Run/Save 按钮不因字段错误静默禁用，而是在点击后聚焦并解释第一个错误。
8. `Choice` 表示严格候选，`Suggestions` 表示允许自由输入的软候选；数值/日期范围、步长和文本长度由 Binder 强制执行。
9. 仅在 Binder 通过且请求被宿主接受后保存上次参数；后台自动化不读取或更新该状态。
10. Editor 参数按 TargetKind 隔离；管理页默认参数配置不读取上次参数。
