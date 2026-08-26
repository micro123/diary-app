# 脚本 API V2 加载期参数契约设计

状态：**已实现**
日期：2026-08-25

## 1. 背景

V1 的 `ScriptExecutionRequest.Arguments` 已允许 C#、Lua、Python 脚本接收字符串键值，但宿主在脚本加载后并不知道脚本需要哪些参数、参数类型、是否必填及默认值。因此当前存在以下问题：

- 脚本管理页只能显示自由格式的 `key=value` 文本框；
- Editor 右键脚本没有参数输入界面；
- `defaultArguments` 只预填手动运行对话框，没有统一参与 Editor 和 Automation 执行；
- 参数错误通常只能在脚本开始执行后由脚本自行发现；
- 三种语言只能依赖文档约定参数名称和格式。

V2 将参数定义提升为脚本描述符的一部分。宿主必须在目录加载和构建成功时取得完整参数契约，不执行脚本入口、不调用 Host API，也不依赖一次“试运行”。

## 2. 目标

1. 脚本加载成功后，宿主立即知道完整参数列表和类型。
2. 参数定义同时适用于 Application、Editor、Automation 和 Query 入口。
3. C#、Lua、Python 使用同一参数类型、规范化格式和错误码。
4. 宿主根据描述符生成参数表单，在进入 Worker 前完成默认值合并、必填检查、类型转换和规范化。
5. V1 脚本保持现有行为，不强制迁移，也不改变现有自由 `key=value` 输入。
6. 自动化事件数据与用户配置的脚本参数分离，避免事件字段被误判为未知参数。
7. 参数契约可声明静态候选建议、数值/日期范围和文本长度等约束，并由加载期校验和执行前 Binder 共用。
8. 有人值守运行可以恢复上次已被宿主接受的参数，同时与 metadata 默认值、执行历史和无人值守参数保持隔离。

## 3. 非目标

- V2 第一阶段不允许参数定义执行任意代码或动态查询数据库。
- 不根据前一个参数动态增删参数或动态生成 Choice 选项。
- 不提供文件、目录、Tracker 实例、标签等宿主对象选择器；后续可在不改变基础值协议的前提下增加 UI hint。
- 不提供动态候选源、参数间条件约束或依赖远程服务的校验。
- 不把参数值自动写入执行历史或应用日志。
- “运行日志复制”是独立 UI 修复，不与 V2 协议耦合。

## 4. 加载期发现边界

```text
脚本目录扫描
    |
    +-- C#：编译并实例化入口，只读取 Descriptor
    |
    +-- Lua/Python：读取相邻 metadata 或包 manifest 的 DescriptorHint
    v
ScriptBuildService
    |
    +-- 校验 API 版本、参数 schema 和默认值
    v
ScriptCatalog
    |
    +-- 保存完整 ScriptDescriptor.Parameters
    +-- UI 生成参数表单
    +-- Scheduler 校验无人值守参数
    v
执行前 ScriptParameterBinder
    |
    +-- 合并默认值与本次输入
    +-- 拒绝缺失、未知或非法值
    +-- 输出规范化字符串字典
    v
Worker
```

参数发现不得调用 `ExecuteAsync`、`application_main`、`editor_main`、`automation_main` 或 `query_main`。Lua/Python 不通过执行源码读取描述符，继续以 metadata/manifest 为身份和参数定义的权威来源。

## 5. 版本和兼容策略

共享契约增加：

```csharp
public enum ScriptApiVersion
{
    V1 = 1,
    V2 = 2,
}
```

实现完成后，宿主和三种 Worker 的支持集合为 `[V1, V2]`。`ScriptApiVersions.Current` 可改为 `V2`，但构建服务不能再用“只等于 Current”的判断拒绝 V1，而应检查显式支持集合。

- V1：`Parameters` 必须为空；保留自由字符串参数和现有 UI。
- V2：参数定义来自加载期描述符；未知参数、缺失必填参数和非法类型在进入 Worker 前拒绝。
- V1 Worker 执行语义保持不变。
- V2 不改变现有 Host API 白名单和副作用策略，本次版本提升只建立参数契约并拆分自动化事件数据。

## 6. 参数契约

建议新增以下共享类型：

```csharp
public enum ScriptParameterType
{
    String = 1,
    MultilineString = 2,
    Integer = 3,
    Number = 4,
    Boolean = 5,
    Date = 6,
    DateTime = 7,
    Choice = 8,
}

public sealed record ScriptParameterChoice(
    string Value,
    string Label);

public sealed record ScriptParameterConstraints(
    string? Minimum = null,
    string? Maximum = null,
    string? Step = null,
    int? MinLength = null,
    int? MaxLength = null,
    IReadOnlyList<ScriptParameterChoice>? Suggestions = null,
    string? Unit = null);

public sealed record ScriptParameterDefinition(
    string Name,
    string Label,
    ScriptParameterType Type,
    bool Required = false,
    string? Description = null,
    string? DefaultValue = null,
    IReadOnlyList<ScriptParameterChoice>? Choices = null,
    string? Placeholder = null,
    ScriptParameterConstraints? Constraints = null);
```

`ScriptDescriptor` 和 `ScriptDescriptorHint` 末尾增加可选的 `Parameters`。使用末尾可选字段可以保持现有 C# 构造调用源代码兼容。

### 6.1 字段语义

| 字段 | 语义 |
| --- | --- |
| `Name` | 稳定机器名，也是传给脚本的字典键 |
| `Label` | 参数表单显示名称 |
| `Type` | 宿主表单、校验和规范化依据 |
| `Required` | 合并所有默认值后仍必须存在且有效 |
| `Description` | 表单帮助文本或 Tooltip |
| `DefaultValue` | 脚本作者提供的规范化默认值 |
| `Choices` | 仅 `Choice` 使用，值与显示名分离 |
| `Placeholder` | 仅作为输入提示，不是默认值 |
| `Constraints` | 可选静态约束和 UI 提示；合法性由参数类型决定 |

### 6.2 初始限制

- 单个脚本最多 32 个参数；
- `Name` 使用 `^[A-Za-z][A-Za-z0-9_.-]{0,63}$`；
- 名称按 `StringComparer.Ordinal` 唯一；
- `Label` 最多 128 字符，`Description` 最多 1,024 字符；
- 单值最多 16 KiB，全部参数规范化后最多 64 KiB；
- `Choice` 必须包含 1 到 100 个选项，选项值唯一；
- 非 `Choice` 参数不得声明 `Choices`；
- 默认值必须在加载期通过该类型的同一套校验。

### 6.3 静态约束

`Constraints` 是 V2 的可选兼容扩展，不需要提升到 V3。其 wire 值继续使用规范化字符串，使 C#、Lua、Python metadata 具有相同表达，并避免 JSON number 精度和区域格式差异。

| 约束 | 适用类型 | 语义 |
| --- | --- | --- |
| `Minimum` / `Maximum` | `Integer`、`Number`、`Date`、`DateTime` | 闭区间边界；按参数类型解析和比较 |
| `Step` | `Integer`、`Number` | 相对 `Minimum`（未设置时相对零）的合法步长 |
| `MinLength` / `MaxLength` | `String`、`MultilineString` | 按 Unicode 标量值计数，不按 UTF-16 code unit 计数 |
| `Suggestions` | `String` | 静态输入建议；显示 Label、写入 Value，但允许输入列表外的值 |
| `Unit` | `Integer`、`Number` | 仅用于 UI 尾部短提示或 Tooltip，不参与合法性判断 |

`Choice` 与 `Suggestions` 的语义必须保持不同：

- `Choice` 是严格枚举，最终值必须精确匹配某个 `Choices.Value`；
- `Suggestions` 是软候选，只帮助输入和解释常见值；
- `Choice` 不得声明 `Suggestions`，其他类型也不得复用 `Choices` 表达软候选。

加载期 schema 校验规则：

- `Minimum` 不得大于 `Maximum`；`Step` 必须大于零；
- `MinLength`、`MaxLength` 必须非负，且最小长度不得大于最大长度；
- 每种约束只能用于上表允许的参数类型；
- `Suggestions` 为 1 到 100 项，Value 唯一，并沿用 Choice 的单项文本长度限制；
- `Unit` 只允许短纯文本，建议最多 32 字符；
- descriptor 默认值和 metadata `defaultArguments` 必须满足全部约束；
- 不支持 Regex/Pattern、动态候选源和参数间条件约束，避免正则超时、跨语言差异及加载期边界被绕过。

## 7. 值格式和规范化

Worker 协议继续传输 `Dictionary<string, string>`，但 V2 的字符串必须由宿主规范化：

| 类型 | 输入和规范化结果 |
| --- | --- |
| `String` | 单行 UTF-8 文本；拒绝 CR/LF |
| `MultilineString` | 文本；换行统一为 `\n` |
| `Integer` | 使用 invariant culture 的十进制有符号整数，例如 `-12` |
| `Number` | 使用 invariant culture 的有限十进制数；拒绝 NaN 和 Infinity |
| `Boolean` | 只输出小写 `true` 或 `false` |
| `Date` | `yyyy-MM-dd` |
| `DateTime` | ISO 8601，必须包含时区偏移；UI 使用本地时区并输出偏移 |
| `Choice` | 必须精确等于某个 `ScriptParameterChoice.Value` |

脚本仍可通过现有入口读取参数：

- C#：`context.Arguments["name"]`；
- Lua：`context.arguments.name` 或 `context.arguments["name"]`；
- Python：`context.arguments["name"]`。

V2 首版不增加语言专属的隐式类型转换，以保证三种语言观察到相同数据。后续可以提供纯 SDK helper，但 wire 值仍保持上述规范化字符串。

## 8. 默认值和优先级

执行前按以下顺序合并，后者覆盖前者：

```text
ScriptParameterDefinition.DefaultValue
    < metadata/manifest.defaultArguments
    < 本次手动或宿主调用提供的 Arguments
```

规则：

- V2 的 `defaultArguments` 只能包含描述符已声明的参数；
- metadata 默认值在脚本加载时校验；
- 本次输入在每次执行前校验；
- 类型解析成功后继续校验范围、步长和长度约束；UI 控件限制不能替代 Binder；
- 合并后缺少必填参数，返回 `SCRIPT_ARGUMENT_REQUIRED`；
- 空字符串是否有效由类型决定：String/MultilineString 在非必填时可为空；其他类型的空字符串视为未提供；必填文本不得为空白。

### 8.1 有人值守运行的上次参数

“上次执行参数”是 UI 初始化来源，不是新的 Binder 默认层。手动运行表单按以下顺序建立初始值，后者覆盖前者：

```text
descriptor default
    < metadata defaultArguments
    < 当前脚本与入口作用域的上次执行参数
```

用户确认后，表单生成普通 `Arguments`，仍按本节既有执行前合并规则进入 Binder。后台 Schedule、Startup 和 Event Automation 不读取或更新上次参数；管理页手动运行 Automation 属于有人值守运行，可以使用该能力。

只有 Binder 校验通过且 `ScriptManager` 接受请求进入实际执行，才保存或更新上次参数。取消对话框、校验失败或执行结果为 `Rejected` 时不得覆盖旧记录；请求已进入执行后，即使最终为 `Failed`、`Cancelled` 或 `TimedOut`，也视为本次参数已实际使用，可以更新记录。

## 9. 自动化事件数据分离

V1 当前把自动化事件数据同时放入 `request.Arguments` 和 `Automation.EventData`。V2 如果直接沿用，会让 `eventId`、事项 ID、标签 ID 等事件字段与脚本声明参数发生冲突。

建议为内部执行请求增加独立事件数据：

```csharp
public sealed record ScriptExecutionRequest(
    ScriptEditorTarget? Target = null,
    ImmutableDictionary<string, string>? Arguments = null,
    ScriptExecutionSource Source = ScriptExecutionSource.Unknown,
    ScriptEntryKind? EntryKind = null,
    string? IdempotencyKey = null,
    bool Preview = false,
    ImmutableDictionary<string, string>? AutomationEventData = null);
```

- V2：`Arguments` 只放已声明并绑定的参数；`Automation.EventData` 来自 `AutomationEventData`。
- V1：为兼容旧脚本，事件执行时仍可将 `AutomationEventData` 镜像到 `Arguments`，保持现有观察结果。
- 事件数据不能覆盖用户参数；两个命名空间完全分离。

## 10. 各执行入口行为

### 10.1 脚本管理页手动运行

- V1：继续显示自由 `key=value` 多行编辑框。
- V2：按参数定义生成类型化表单。
- 没有参数时不显示参数区域。
- 有参数时优先恢复当前脚本和入口的有效上次参数；用户可恢复默认值或清除记忆。
- 点击“运行”时先绑定和校验，不启动 Worker 即可显示字段级错误。

### 10.2 Editor 右键脚本

- V2 无参数：保持当前点击后立即运行。
- V2 有参数：先打开紧凑参数对话框，按目标类型隔离并恢复有效上次参数，确认后运行。
- V1：保持当前立即运行，不新增对话框。
- Editor 记忆作用域至少包含 `ScriptId + EntryKind + EditorTargetKind`，避免 Day、Week、Month 等入口互相复用不合适的值。

### 10.3 Automation

- `defaultArguments` 作为无人值守执行的配置参数。
- 启用了 Schedule、RunOnStartup 或事件 Trigger 的 V2 Automation，如果存在无法由默认值满足的必填参数，则脚本配置不可调度，并产生加载诊断。
- 事件触发只提供 `Automation.EventData`，不能隐式满足用户参数。
- 手动运行 Automation 时仍可通过生成表单覆盖默认值。

### 10.4 Query

Query 与 Application 使用相同参数表单和绑定规则。参数契约不改变 Query 的只读约束。

## 11. 三语言声明方式

### 11.1 C#

增加 V2 typed interface/基类，避免修改 V1 基类语义：

```csharp
public sealed class MonthlySummary : QueryScriptV2
{
    public override string Id => "monthly-summary";
    public override string Name => "月度汇总";

    public override IReadOnlyList<ScriptParameterDefinition> Parameters =>
    [
        new("month", "月份", ScriptParameterType.Date, Required: true,
            Description: "选择目标月份中的任意日期"),
        new("format", "输出格式", ScriptParameterType.Choice,
            DefaultValue: "markdown",
            Choices:
            [
                new("markdown", "Markdown"),
                new("csv", "CSV"),
            ]),
    ];

    public override ValueTask<ScriptExecutionResult> ExecuteAsync(
        IScriptApplicationContext context,
        CancellationToken cancellationToken = default)
    {
        var month = context.Arguments["month"];
        // ...
    }
}
```

计划提供 `ApplicationScriptV2`、`EditorScriptV2`、`AutomationScriptV2` 和 `QueryScriptV2`。它们生成 `ApiVersion=V2` 的描述符；底层仍可适配到统一执行器，不要求复制 Host API。

### 11.2 Lua/Python

Lua/Python 继续通过相邻 metadata 或脚本包 manifest 声明，因为当前加载器不会执行源码来发现身份信息：

```json
{
  "apiVersion": "V2",
  "id": "monthly-summary",
  "name": "月度汇总",
  "engine": "python",
  "scope": "Application",
  "entryKind": "Query",
  "parameters": [
    {
      "name": "month",
      "label": "月份",
      "type": "Date",
      "required": true,
      "description": "选择目标月份中的任意日期"
    },
    {
      "name": "format",
      "label": "输出格式",
      "type": "Choice",
      "defaultValue": "markdown",
      "choices": [
        { "value": "markdown", "label": "Markdown" },
        { "value": "csv", "label": "CSV" }
      ]
    }
  ],
  "defaultArguments": {
    "format": "markdown"
  }
}
```

metadata 与 manifest 解析后写入 `ScriptDescriptorHint.Parameters`，引擎创建的 `ScriptDescriptor` 必须完整保留；Worker 不从 Lua/Python 源码重新推断参数。

## 12. 加载期校验

`ScriptBuildService` 在程序进入 `ScriptCatalog` 前执行统一校验：

1. API 版本属于宿主支持集合；
2. V1 不声明非空参数列表；
3. V2 参数名称、数量、文本长度和类型合法；
4. 参数名称、Choice 值和 Suggestions 值唯一；
5. 静态约束适用于参数类型，边界和步长关系合法；
6. 默认值可以由同一 Binder 解析、规范化并满足静态约束；
7. Lua/Python 构建结果与 metadata 参数定义完全一致；
8. 自动化无人值守入口具备所有必填参数的有效默认值。

建议诊断码：

| 阶段 | 错误码 |
| --- | --- |
| 加载 | `SCRIPT_PARAMETER_SCHEMA_INVALID` |
| 加载 | `SCRIPT_PARAMETER_CONSTRAINT_INVALID` |
| 加载 | `SCRIPT_PARAMETER_DUPLICATE` |
| 加载 | `SCRIPT_PARAMETER_DEFAULT_INVALID` |
| 加载 | `SCRIPT_PARAMETERS_REQUIRE_V2` |
| 执行前 | `SCRIPT_ARGUMENT_UNKNOWN` |
| 执行前 | `SCRIPT_ARGUMENT_REQUIRED` |
| 执行前 | `SCRIPT_ARGUMENT_TYPE_INVALID` |
| 执行前 | `SCRIPT_ARGUMENT_CHOICE_INVALID` |
| 执行前 | `SCRIPT_ARGUMENT_RANGE_INVALID` |
| 执行前 | `SCRIPT_ARGUMENT_LENGTH_INVALID` |
| 执行前 | `SCRIPT_ARGUMENT_STEP_INVALID` |
| 执行前 | `SCRIPT_ARGUMENTS_TOO_LARGE` |

执行前参数错误返回 `Rejected`，不创建 Worker 执行请求；执行历史仍记录拒绝结果，但不得记录实际参数值。

## 13. Worker 协议

- C#、Lua、Python Worker 握手均声明 `[V1, V2]`；
- 由宿主按脚本描述符选择实际 API 版本，不应只选择双方最高版本后用于所有脚本；
- `WorkerExecutePayload` 已携带描述符和请求，可追加参数定义而不增加新的发现消息；
- Worker 对 API 版本和描述符执行防御性检查，但参数绑定的权威实现位于宿主；
- V2 参数定义不属于 Host API，不影响 `supportedHostApis`。

需要特别修改当前握手模型：现有 supervisor 为每种语言只协商一个最高 API 版本。V2 实现应把握手结果解释为“该进程支持的版本集合”，或至少允许同一 Worker 接收描述符明确标识的 V1/V2 脚本，不能因为协商到 V2 就拒绝 V1 脚本。

## 14. UI 建议

| 类型 | Avalonia 控件 |
| --- | --- |
| `String` | 单行 `TextBox` |
| `MultilineString` | 自动增高的多行 `TextBox` |
| `Integer` | `NumericUpDown`，只允许整数 |
| `Number` | `NumericUpDown` |
| `Boolean` | `CheckBox` |
| `Date` | 日期选择器，展开时始终为月视图 |
| `DateTime` | 日期选择器 + 时间输入 |
| `Choice` | `ComboBox` |

额外约束映射：

- 数值范围和步长配置到 `NumericUpDown`，但提交时仍由 Binder 复检；
- Date/DateTime 控件限制可选范围，手工输入和恢复值仍需 Binder 复检；
- String 的 `Suggestions` 使用可编辑 `ComboBox` 或 `AutoCompleteBox`，不得阻止候选列表外输入；
- `Unit` 显示在输入框尾部或 Tooltip，避免增加常驻说明高度；
- 长度和范围错误显示在对应字段下方，例如“必须在 0 到 24 之间”“最多 80 个字符”。

表单使用现有紧凑设置行风格：标签在左，输入部件在右；短说明使用信息图标和 Tooltip，字段错误显示在对应输入下方。参数较多时整体滚动，不让单个字段创建嵌套滚动区域。

脚本详情页可只读展示参数名称、类型、必填和默认值，便于用户在执行前理解脚本要求。

## 15. 安全和隐私

- 参数定义和默认值属于可导出的脚本包内容；不得把 Token、密码等敏感值作为默认参数打包。
- V2 第一阶段不提供 Secret 类型，避免产生“看似安全但仍进入 metadata、剪贴板或导出包”的误解。
- 参数值不自动进入日志、执行历史、通知或崩溃报告。
- 上次执行参数仅保存在本机独立状态文件中，不进入 AppConfig、脚本包、同步数据或诊断导出；删除脚本和用户主动清除时应删除对应记录。
- V1 原始参数文本和 V2 规范化参数都可能包含敏感信息；当前没有 Secret 类型，UI 必须明确提醒不要把 Token、密码写入普通参数，未来 Secret 参数默认禁止记忆。
- 参数数量和总大小在进入 Worker 前限制，防止协议和 UI 滥用。
- 参数定义仍是不可信脚本内容；UI 只按固定控件渲染，不解析富文本、命令或动态表达式。

## 16. 测试范围

### 16.1 契约

- V1/V2 枚举值稳定；
- 参数定义 JSON 使用稳定命名和枚举字符串；
- 旧 V1 descriptor/metadata 可继续反序列化。

### 16.2 加载

- 三语言 V2 脚本加载后 Catalog 立即包含参数定义；
- 加载过程不执行脚本入口；
- 重复名称、错误默认值、非法 Choice 和超限 schema 被拒绝；
- 非法范围、步长、长度、Suggestions 及不满足约束的默认值被拒绝；
- V1 声明参数被拒绝，V1 无参数脚本保持通过。

### 16.3 绑定

- 八种类型的成功、空值、非法值和规范化结果；
- 默认值优先级、未知参数、缺失必填、范围、步长、长度和大小限制；
- Automation 用户参数与事件数据互不覆盖。

### 16.4 UI

- 手动 V2 表单按类型生成并显示字段级错误；
- Editor 有参数时先显示表单，无参数时直接执行；
- V1 仍显示自由 `key=value` 输入；
- Automation 缺少无人值守必填默认值时不可调度；
- 参数值不出现在运行日志和执行历史详情。
- 上次参数仅在 Binder 通过且执行请求被接受后更新；取消、校验失败和 `Rejected` 保留旧记录。
- schema 变化后仅恢复同名、同类型且仍满足当前约束的值，失效字段回退到当前默认值。

### 16.5 Worker

- 同一语言 Worker 可先后执行 V1 和 V2；
- 三语言收到相同的规范化参数；
- 不支持 V2 的旧 Worker 给出明确握手诊断，不表现为通道关闭。

## 17. 实施状态

截至 2026-08-25，核心协议、静态约束和参数 UI 已完成：

1. 已增加共享 V2 参数 DTO、schema 校验器和 Binder，构建服务同时接受 V1/V2。
2. metadata/manifest、DescriptorHint、Catalog 和 C# 四类 V2 基类已支持加载期参数发现。
3. C#、Lua、Python Worker 同时声明 V1/V2，单个 Worker 可按执行载荷运行不同 API 版本。
4. 参数默认值在进入 Worker 前按 descriptor、metadata、本次输入顺序合并并规范化；V2 严格拒绝未知、缺失和非法参数。
5. Automation 用户参数和事件数据已分离；V1 保留事件字段镜像到 `Arguments` 的兼容行为。
6. 三语言加载、绑定、真实 Worker 进程混合版本和脚本共享包均有回归测试，并提供参数化示例。
7. 已实现 Suggestions、数值/日期范围、步长、文本长度和单位提示；schema 与 Binder 使用同一约束规则并返回结构化字段 Issue。
8. 管理页手动运行和 Editor 入口已接入 V1/V2 双模式类型化表单，Editor 按目标类型隔离参数记忆。
9. 上次参数使用独立本地原子文件保存，支持 schema 迁移、容量限制、当前作用域清除和删除脚本联动清理。
10. Automation 必填默认值不完整时保留发现的 descriptor 并显示“待配置”，不注册调度；新建脚本默认选择 V2，同时允许创建 V1。
11. 管理页 `defaultArguments` 已改为内嵌类型化表单，只保存相对 descriptor 默认值的 metadata 覆盖项，不读取上次运行参数。
12. 设置页提供全局清除参数历史入口；字段辅助说明只显示参数键和类型，值来源保留在内部状态中以支持单字段或整体恢复默认值。
13. Date/DateTime 控件应用日期边界，DateTime 显示本机偏移、拒绝夏令时跳空并允许在重复时段选择偏移；Suggestions 使用同一行软候选，文本接近上限时显示 Unicode 字符计数。
14. 点击运行或保存会聚焦首个字段错误；运行对话框支持单行 Enter、Ctrl+Enter 和 Escape，管理页默认参数支持 Ctrl+Enter 保存。
15. 单元测试覆盖 metadata 覆盖差异、参数记忆隔离、约束、值来源、单字段重置和 32 参数；Linux X11 CDP 扩展套件覆盖类型化表单、设置入口和执行链路。平台 DPI/主题视觉抽样归入全局 UI 发布检查单。

## 18. 已确认决策

1. 首版使用八种参数类型，暂不加入宿主对象选择器。
2. V2 严格拒绝未知参数。
3. Editor 脚本声明参数后先显示类型化表单；无参数脚本继续立即执行。
4. 无人值守 Automation 的必填参数必须由 descriptor 或 metadata 默认值满足。
5. V2 的 `Arguments` 与 `Automation.EventData` 完全分离，V1 保持兼容镜像。
6. 有人值守运行记忆上次已被宿主接受的参数；后台自动化不读取或更新该状态。
7. `Choice` 是严格候选，`Suggestions` 是允许自由输入的软候选；范围、步长和长度由 Binder 强制执行。
8. 首版不提供 Secret 参数；敏感配置需要独立安全存储设计，未来 Secret 参数默认禁止记忆。
