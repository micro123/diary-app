# 脚本 API V1 与 V2

当前脚本运行时同时支持 V1 和 V2，`ScriptApiVersions.Current` 为 V2。“脚本管理 → 新建脚本”默认选择 V2，但也允许为 C#、Lua 和 Python 创建 V1 脚本。V2 是新脚本的推荐版本；V1 当前仍受支持，主要用于兼容旧脚本或保留自由参数调用方式。

V2 不是通过增加更多 Host API 来替代 V1。两个版本使用相同的日记、Tracker、导出、系统交互和日志 API，也遵守相同的权限、Preview 和副作用限制。V2 的主要变化是把自由字符串参数升级为加载期可发现、执行前可校验的参数契约，并将自动化事件数据与用户参数分开。

## 1. 差异速查

| 项目 | V1 | V2 |
| --- | --- | --- |
| 推荐用途 | 兼容旧脚本或动态自由参数 | 新脚本优先选择 |
| 新建向导 | 可以选择 | 默认选择 |
| Host API 与权限 | 与 V2 相同 | 与 V1 相同 |
| 参数声明 | 无；允许自由 `key=value` | 在 descriptor 或 metadata 中声明 `parameters` |
| 参数界面 | 多行自由文本 | 根据类型生成表单 |
| 参数校验 | 合并默认值和本次输入，不拒绝未知名称 | 执行前校验未知名称、必填值、类型、候选和静态约束 |
| 脚本读取参数 | `context.Arguments` / `context.arguments` | 相同，但值已按契约规范化 |
| 自动化事件数据 | 为兼容旧脚本，同时镜像到参数字典 | 只放在 `Automation.EventData` / `automation.eventData` |
| 上次参数 | 保留自由文本输入 | 按脚本、入口和 Editor 目标保存通过校验的类型化值 |

因此，V2 覆盖 V1 的主要业务能力，但不是严格的行为超集：依赖任意未声明参数，或从参数字典读取自动化事件字段的 V1 脚本，需要调整后才能迁移。

## 2. V2 更新了什么

V2 更新的是脚本契约和参数交互，不是 Host API 权限。主要变化包括：

1. **加载时发现参数**：宿主无需执行脚本，即可读取参数名称、类型、默认值、候选和约束。
2. **类型化参数界面**：管理页和 Editor 入口根据契约显示文本、数字、日期、布尔或候选控件，不再要求用户手写所有 `key=value`。
3. **执行前统一校验**：未知参数、缺失必填值、非法类型、越界值和非法候选会在 Worker 启动前被拒绝。
4. **规范化跨语言值**：C#、Lua、Python 收到相同格式的布尔、数字、日期和时间字符串。
5. **自动化数据隔离**：用户参数与工作项、标签等触发事件字段分开，避免名称冲突。
6. **参数记忆和默认值管理**：有人值守运行可保存通过校验的上次参数，metadata 可覆盖脚本默认值。

以下能力没有因 V2 改变：

- 可调用的日记、Tracker、模板、导出、系统交互和日志 API；
- Preview、权限检查、Query 只读限制和 Automation 副作用限制；
- Application、Editor、Automation、Query 四类入口及其上下文用途。

## 3. 如何声明版本

### 3.1 C#

使用 SDK 基类即可确定版本：

| 入口 | V1 基类 | V2 基类 |
| --- | --- | --- |
| Application | `ApplicationScript` | `ApplicationScriptV2` |
| Editor | `EditorScript` | `EditorScriptV2` |
| Automation | `AutomationScript` | `AutomationScriptV2` |
| Query | `QueryScript` | `QueryScriptV2` |

V2 基类允许重写 `Parameters`。如果脚本没有参数，可以保留空列表，仍然使用 V2。

### 3.2 Lua 和 Python

版本由相邻 metadata 或共享包 `manifest.json` 决定：

```json
{
  "apiVersion": "V2",
  "id": "daily-summary",
  "name": "每日摘要",
  "engine": "python",
  "scope": "Application",
  "entryKind": "Application",
  "parameters": []
}
```

Lua 将 `engine` 写为 `lua`。省略 `apiVersion` 时按 V1 处理，因此手工创建 Lua/Python 脚本时应显式写出目标版本；使用新建向导时会按所选版本自动生成。

## 4. V2 参数契约

V2 支持 String、MultilineString、Integer、Number、Boolean、Date、DateTime 和 Choice。宿主按以下顺序合并参数：

1. 脚本声明的 `defaultValue`；
2. metadata 的 `defaultArguments` 覆盖；
3. 本次运行输入。

合并后，宿主在 Worker 启动前完成必填、类型、候选、范围、步长和文本长度校验。脚本收到的仍是字符串字典，但 Boolean、数字、日期和时间值已经使用跨语言一致的格式规范化。未在 `parameters` 中声明的输入会被拒绝。

完整定义和三语言示例见[参数化工时汇总](Examples/ParameterizedSummary.md)。

## 5. 从 V1 迁移到 V2

迁移前先确认脚本实际读取了哪些执行参数和自动化事件字段，然后按以下步骤处理：

1. C# 将基类替换为对应的 `*ScriptV2`；Lua/Python 在 metadata 中将 `apiVersion` 改为 `V2`。
2. 为每个用户参数增加定义，包括名称、类型、是否必填、默认值和必要约束。
3. 删除不再使用的自由参数；V2 会拒绝任何未声明名称。
4. Automation 脚本将事件字段读取位置从参数字典改为 `Automation.EventData`、`automation.eventData`。
5. 在脚本管理页重新加载，确认版本标签为 V2，并用参数表单执行一次 Preview。

不要只修改版本号。若没有同步声明参数，原来可自由传入的值会被 V2 判定为未知参数；若仍从参数字典读取事件字段，事件触发时将无法取得这些值。

### 5.1 按语言迁移

| 语言 | 版本切换 | 参数契约位置 | 迁移重点 |
| --- | --- | --- | --- |
| C# | 将 `ApplicationScript` 等基类改为对应的 `*ScriptV2` | 重写 `Parameters` | 保持原 `ExecuteAsync` 上下文，逐项声明 `Arguments` 中使用的值 |
| Lua | metadata 的 `apiVersion` 改为 `V2` | metadata/manifest 的 `parameters` | 入口函数名不变，继续从 `context.arguments` 读取规范化值 |
| Python | metadata 的 `apiVersion` 改为 `V2` | metadata/manifest 的 `parameters` | 入口函数名不变，继续从 `context.arguments` 读取规范化值 |

迁移完成后建议先执行 Preview，再检查真实写入、导出和 Automation 触发路径。迁移失败时可以把基类或 metadata 版本恢复为 V1，不需要转换数据库或用户数据。

## 6. 何时保留 V1

已有 V1 脚本可以继续运行，不要求批量迁移。以下情况可以暂时保留 V1：

- 脚本由旧工具生成，并依赖调用方动态传入不固定名称的参数；
- Automation 脚本尚未改为从独立事件数据读取字段；
- 需要先保持现有调用方式，再分阶段补充参数定义和 UI。

需要新增参数、候选列表、数值范围或执行前校验时，应迁移到 V2，而不是继续扩展 V1 自由参数。

## 7. V1 的后续兼容策略

V1 目前不是禁用或待立即删除的功能，创建向导和运行时都会继续支持。未来如果决定逐步弃用 V1，应按以下顺序推进：

1. 先在发布说明、向导和脚本列表中标记为 Deprecated；
2. 提供足够的迁移周期，并保持 V1 脚本可加载、检查和导出；
3. 补充自动迁移提示或检查工具，指出未声明参数和旧事件字段读取位置；
4. 只有在迁移路径稳定且内部脚本完成迁移后，才评估停止创建或停止运行 V1。

在正式发布弃用计划前，用户可以继续创建和维护 V1 脚本；界面中的“兼容”表示推荐优先使用 V2，不表示 V1 已失效。
