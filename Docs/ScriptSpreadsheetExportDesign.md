# 脚本交互式导出能力设计（Excel、CSV、DOCX）

状态：导出插件化第一阶段已实现（XLSX、CSV、DOCX 通用处理器及模板链路已接入；模板能力按插件提供）

## 1. 背景

用户希望从日历或工作项编辑器的右键菜单运行脚本，整理指定时间范围内的加班记录，并导出为 Excel 文件，随后用于 OA 系统提交加班申请。

目标流程固定为：

```text
右键菜单触发脚本并获得时间范围
        ↓
脚本收集、筛选和整理工作项
        ↓
无数据：脚本正常结束，不产生交互
有数据：询问导出目录
        ↓
脚本向宿主提交电子表格导出请求
        ↓
宿主通过导出插件生成目标文件（XLSX/CSV/DOCX）
        ↓
脚本请求宿主询问是否打开结果文件
        ↓
用户选择打开或不打开
```

本设计重点解决以下问题：

- 让脚本作者可以用较少代码完成“查询—整理—导出”流程；
- 不向脚本开放任意文件系统写入能力；
- 让目录选择、打开文件等交互统一属于系统 API；
- 让具体格式的生成、样式、公式和文件生命周期由宿主控制；
- 通过通用导出模型支持 XLSX、CSV、DOCX，并为后续 OA 固定模板保留扩展空间；
- C#、Lua、Python 三种脚本提供语义一致的接口。

## 2. 当前能力和缺口

### 2.1 当前已有能力

当前编辑器脚本可以从结构化目标获取日期范围，并通过脚本宿主 API 查询工作项。查询结果包含：

- 日期；
- 标题；
- 耗时；
- 备注；
- 标签；
- 标签元数据；
- 标签附加字段。

当前查询接口支持 `StartDate`、`EndDate`、标签 ID 和标签匹配模式，编辑器上下文也提供 `GetDateRange()` 与 `StreamItemsAsync()`，因此脚本可以直接使用右键菜单提供的范围，不需要重新询问月份或自行计算月初月末。

当前 `SysApi` 已提供：

- 剪贴板读写；
- 通知；
- 确认对话框。

### 2.2 当前缺口

当前脚本系统仍不允许脚本直接访问文件系统；当前已通过宿主 HostCall 提供受限目录选择、统一插件导出和结果文件打开询问。当前剩余缺口主要是更完整的 UI 端到端覆盖、示例脚本和通用导出注册诊断：

- C# 禁止 `System.IO` 命名空间和相关引用；
- Lua Worker 禁用 `io`、`os`、`package`、`require` 等能力；
- Python Worker 禁止导入模块、`open`、动态执行和文件访问；
- Worker 的文件写入仅限宿主内部缓存、脚本元数据或协议实现，不能由脚本调用。

当前应用内已有 CSV/Markdown 等非脚本导出能力；面向脚本的 CSV/DOCX 处理器已经通过独立插件接入。本设计不把 API 命名为 Excel 专用 API。

## 3. 设计目标

### 3.1 必须满足

1. 右键菜单运行的编辑器脚本自动获得日期范围。
2. 脚本可以流式查询范围内的工作项。
3. 脚本可以自行筛选“加班”标签并整理列数据。
4. 无数据时直接正常结束，不弹目录选择框、不弹确认框、不生成空 Excel。
5. 有数据时通过系统 API 选择导出目录。
6. 脚本提交结构化导出请求，宿主负责生成 `.xlsx`。
7. 宿主至少支持合并单元格、背景色、中文、基本数字/日期格式和公式。
8. 导出完成后脚本可以请求宿主询问用户是否打开结果文件。
9. 用户选择不打开不应被视为脚本失败。
10. C#、Lua、Python 的 API 语义一致。
11. 文件选择、导出、打开均经过当前脚本执行上下文和取消令牌校验。

### 3.2 非目标

第一阶段不包括：

- 向脚本开放 `System.IO`、任意文件写入或任意目录遍历；
- 让脚本直接引用 ClosedXML、Open XML SDK 或其他 Office 库；
- 在脚本中直接创建 Avalonia 文件对话框；
- 自动登录或自动提交 OA；
- 直接操作 OA 网络接口；
- 支持 Excel 的全部高级特性；
- 用脚本编辑已有任意 Excel 文件中的任意单元格；
- 允许后台自动化脚本弹出交互式目录选择框。

## 4. 核心使用场景

### 4.1 右键导出本月加班明细

用户在日历右键选择“本月运行脚本”。宿主将目标构造成：

```text
ScriptEditorTarget
- Kind = Month
- Year = 2026
- Month = 7
```

脚本通过编辑器上下文获得：

```text
2026-07-01 至 2026-07-31
```

脚本查询该范围内的工作项，筛选包含“加班”标签的记录，生成以下列：

```text
日期 | 工作内容 | 加班时长 | 备注 | 项目编号
```

如果没有任何记录，脚本正常结束。如果有记录，脚本让用户选择导出目录，然后提交 Excel 导出请求。

### 4.2 右键导出本周或自定义范围

同一个脚本不绑定“月份”概念，只依赖 `GetDateRange()`。因此日历右键选择本周、上周或本季度时，脚本都可以复用。当前编辑器目标不提供自定义日期范围；自定义范围只通过 `Application`/`Query` 脚本的 `Arguments` 传入 `startDate`/`endDate`。工作项编辑器目标当前没有日期范围；若脚本从该入口运行，应按“缺少日期范围”拒绝，不能自行猜测月份。

文件名由脚本根据返回的日期范围生成，例如：

```text
加班明细-2026-07.xlsx
加班明细-2026-W30.xlsx
加班明细-2026-07-01至2026-07-31.xlsx
```

### 4.3 通用导出和模板导出并存

导出 API 保留两条明确但共享基础设施的路径：

```text
通用导出（不使用模板）
脚本整理 ExportContent
        ↓
宿主按 format_id 选择格式插件
        ↓
生成标准表格/文档

模板导出
脚本查询模板目录并选择 template_id
        ↓
脚本整理模板声明的业务绑定值
        ↓
宿主按 template_id + version 选择模板插件
        ↓
宿主加载用户导入的只读模板并由模板插件填充
        ↓
另存为新的申请文件
```

两条路径共用目录选择、文件名校验、作用域检查、取消、临时文件清理、`FileId` 和打开询问；只有内容来源不同：通用导出由脚本提供 `ExportContent`，模板导出由用户导入的模板提供布局，插件负责校验和渲染，脚本只提供模板声明的语义字段。模板不是通用导出的替代品，脚本可以继续在没有任何模板时导出标准 XLSX/CSV/DOCX。

## 5. API 分层

### 5.1 API 入口

现有脚本门面为：

```csharp
var api = context.Api();
```

第一阶段已增加：

```csharp
api.Exports
```

最终门面结构为：

```text
api.Diary       查询、日志项和模板相关能力
api.Tracker     Tracker 只读能力
api.System      系统交互能力
api.Exports     文件导出能力
api.Log         脚本日志能力
```

电子表格导出属于“应用导出能力”，不放入 `SysApi`，避免系统 API 变成所有业务功能的杂合入口。

### 5.2 系统交互 API

#### 5.2.1 选项选择对话框

除了目录选择外，脚本还可能需要让用户从多个选项中选择，例如：

- 选择导出模板；
- 选择导出格式或报告类型；
- 选择是否覆盖已有文件；
- 选择 OA 申请类型；
- 选择下一步操作。

这类交互不能复用只返回布尔值的 `ConfirmAsync()`。建议增加统一的选项选择 API：

```csharp
public interface SysApi
{
    ValueTask<OptionDialogResult> SelectOptionAsync(
        OptionDialogRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record OptionDialogRequest
{
    public string Title { get; init; } = "请选择";
    public string? Message { get; init; }
    public required IReadOnlyList<DialogOption> Options { get; init; }
    public DialogDismissPolicy DismissPolicy { get; init; } = DialogDismissPolicy.AllowCancel;
    public string? DefaultOptionId { get; init; }
}

public sealed record DialogOption(
    string Id,
    string Label,
    string? Description = null,
    bool IsDestructive = false);

public enum DialogDismissPolicy
{
    AllowCancel,
    RequireChoice,
}

public enum OptionDialogStatus
{
    Selected,
    Cancelled,
}

public sealed record OptionDialogResult(
    OptionDialogStatus Status,
    string? OptionId = null);
```

请求校验：

- `Options` 至少包含一个选项；
- `DialogOption.Id` 在当前对话框内必须唯一且非空；
- `Label` 必须非空；
- `DefaultOptionId` 如果提供，必须指向现有选项；
- `RequireChoice` 不允许通过缺省值自动提交，默认选项只能表示键盘焦点或视觉高亮；
- 选项 ID 是脚本协议值，显示文本由宿主按脚本传入的 `Label` 展示，但宿主仍应限制长度和控制字符。

两种关闭策略：

| 策略 | 右上角关闭 | Escape/Alt+F4 | 用户结果 | 使用场景 |
| --- | --- | --- | --- | --- |
| `AllowCancel` | 允许 | 允许 | 取消返回 `Cancelled` | 可选模板、目录前置确认、用户可以放弃的操作 |
| `RequireChoice` | 禁用/隐藏 | 禁用 | 必须返回某个 `OptionId` | 必须明确回答的二选一或多选一问题 |

`RequireChoice` 的语义是：只要应用和脚本执行仍然有效，对话框就不能通过右上角关闭、Escape、Alt+F4 或点击遮罩层结束；只有选项按钮可以完成 HostCall。不能把“第一个选项”当作关闭时的隐式默认答案。

异常防护必须优先于“必须回答”：如果 CancellationToken 被取消、Worker 已终止、HostCall 通道断开、宿主窗口退出或响应无法发送，宿主必须通过一次性结算逻辑关闭对话框并清理等待状态，不能继续阻塞 UI，也不能伪造 OptionId。用户点击、外部取消和 Worker 终止可能并发发生时，只允许一个路径完成 HostCall；响应发送失败只记录通信错误并释放资源，不无限重试。

UI 实现约束：

- `RequireChoice` 必须使用宿主自有的模态对话框壳，不依赖无法控制关闭按钮的通用 MessageBox；
- 窗口的 `Closing`/关闭事件必须拦截并取消，只有选项按钮设置“允许关闭”状态后才能真正关闭；
- 对话框必须保持宿主窗口所有权、模态和键盘焦点，选项按钮必须可通过键盘访问；
- 默认选项只能作为焦点或视觉高亮，不能因为窗口显示或失焦而自动提交；
- 无障碍辅助技术仍必须能够读取问题、选项标签和当前焦点。

外部取消不受该策略阻止：

- 脚本的 `CancellationToken` 被取消时，宿主关闭对话框并中止 HostCall；
- Worker 超时、Worker 终止或应用退出时，宿主清理对话框并返回取消/宿主不可用结果；
- 这些情况不是用户选择，不能伪造成某个 `OptionId`。

`AskToOpenExportedFileAsync()` 应按 `RequireChoice` 实现为内置选项对话框：

```text
导出完成：加班明细-2026-07.xlsx

是否立即打开？
[打开] [不打开]
```

“打开”和“不打开”是两个明确选项，禁止右上角关闭。选择“不打开”返回 `UserDeclined`，而不是 `Cancelled`；只有脚本取消、Worker 终止或应用退出才走外部取消路径。

`PickDirectoryAsync()` 仍然允许取消，因为系统目录选择器的职责是让用户选择或放弃一个目录，不能强制用户选择目录。

#### 5.2.2 选择目录

第一阶段只实现目录选择：

```csharp
public interface SysApi
{
    ValueTask<DirectorySelection?> PickDirectoryAsync(
        DirectoryPickerOptions options,
        CancellationToken cancellationToken = default);
}

public sealed record DirectorySelection(
    string SelectionId,
    string DisplayName);
```

请求模型：

```csharp
public sealed record DirectoryPickerOptions
{
    public string Title { get; init; } = "选择目录";
    public string? SuggestedDirectory { get; init; }
}
```

返回语义：

```text
成功选择：返回 DirectorySelection（SelectionId + DisplayName）
用户取消：返回 null，不视为 API 错误
宿主不可用：返回结构化 API 错误或抛出宿主调用异常
```

`SelectionId` 是宿主生成的不可猜测选择令牌，不是目录路径。令牌绑定当前脚本执行、Worker 和短期有效期；脚本不能通过修改返回值把导出目录改成任意路径。`DisplayName` 只用于脚本日志或界面提示，不用于后续导出定位。

脚本不需要选择完整文件路径。文件名由脚本提供，扩展名由宿主校验和补充。

#### 5.2.3 请求打开导出文件

导出结果不直接返回任意路径，而返回宿主登记的短期文件引用。调用名称明确包含“询问”，避免误解为无条件打开：

```csharp
public interface SysApi
{
    ValueTask<OpenExportedFileResult> AskToOpenExportedFileAsync(
        string file_id,
        CancellationToken cancellationToken = default);
}
```

结果模型：

```csharp
public enum OpenExportedFileStatus
{
    Opened,
    UserDeclined,
    Failed,
}

public sealed record OpenExportedFileResult(
    OpenExportedFileStatus Status,
    ScriptApiError? Error = null);
```

宿主收到请求后显示通用文案：

```text
导出完成：
加班明细-2026-07.xlsx

是否立即打开？
[打开] [不打开]
```

宿主可以根据 `FormatId` 显示更具体的格式名称，但 `AskToOpenExportedFileAsync()` 不绑定 Excel。

行为约束：

- 用户选择“打开”时，宿主调用系统默认程序打开文件；
- 用户选择“不打开”时返回 `UserDeclined`；
- 用户选择“不打开”不影响导出成功状态；
- `FileId` 找不到或文件已删除时返回 `Failed` + `EXPORTED_FILE_NOT_FOUND`；
- 系统默认程序启动失败时返回 `Failed` + `EXPORTED_FILE_OPEN_FAILED`；
- 脚本不应再次调用 `ConfirmAsync()`，避免出现重复确认对话框；
- `UserDeclined` 不是脚本失败。

#### 5.2.4 交互 API 作用域策略

第一阶段允许所有有人值守的手动执行：编辑器右键脚本、手动运行的 Application 脚本和手动运行的 Query 脚本；Startup、Scheduled 和事件触发的无人值守脚本禁止调用交互式导出能力：

| 入口 | 执行来源 | 选项对话框 | 目录选择 | 格式导出 | 询问打开 |
| --- | --- | ---: | ---: | ---: | ---: |
| `Editor` | `Editor` | 允许 | 允许 | 允许 | 允许 |
| `Application` | `Manual` | 允许 | 允许 | 允许 | 允许 |
| `Query` | `Manual` | 允许 | 允许 | 允许 | 允许 |
| `Automation` | `Startup`/`Scheduled`/事件 | 禁止 | 禁止 | 禁止 | 禁止 |

交互 API 的判断依据是“是否为有人值守的手动执行”，而不是是否属于编辑器入口。编辑器右键执行、管理页手动运行的 Application 脚本和管理页手动运行的 Query 脚本都允许交互；Startup、Scheduled、工作项事件和标签事件等无人值守自动化入口禁止交互。

宿主必须根据 `ScriptExecutionMetadata.EntryKind` 和 `Source` 在每次 HostCall 时检查策略，不能只依赖能力列表或脚本自身声明。

### 5.3 通用导出 API

#### 5.3.1 设计原则

导出能力分为五层，通用导出和模板导出使用同一个脚本入口：

```text
脚本门面 IExportApi
        ↓
统一 ExportRequest / ExportResult
        ↓
ExportContent（通用内容）或 TemplateExportSource（模板绑定）
        ↓
ExportHandlerRegistry 按 format_id / template_id 选择插件
        ↓
插件生成目标文件，宿主登记 FileId
```

核心原则：

1. **脚本面向内容和格式，而不是具体第三方库。** 脚本只描述表格、文档和格式 ID，不引用 ClosedXML、CsvHelper、Open XML SDK 或 DocX 类型。
2. **格式使用稳定字符串 ID。** 使用 `xlsx`、`csv`、`docx` 等格式 ID，而不是把格式做成只能修改宿主代码的 C# 枚举。
3. **内容模型与文件格式解耦。** `table` 是可被 XLSX、CSV 和未来 DOCX 表格处理器消费的内容模型；`document` 是由段落、标题和文档表格组成的文档模型。
4. **能力差异显式声明。** 每种格式按内容类型声明支持的特性。请求包含目标格式不支持的特性时返回错误，不静默丢弃数据或样式。
5. **公共能力统一，格式特性隔离。** 目录令牌、文件名、冲突策略、结果引用和打开询问属于公共导出流程；工作表、CSV 分隔符、段落和 Word 样式等属于格式处理器。
6. **HostCall 保持稳定。** 通用导出和模板导出都使用 `exports.export`；模板发现使用 `exports.templates.list`，不新增 XLSX/CSV/DOCX 专用导出入口。
7. **模板布局不进入脚本协议。** 脚本只提交模板 ID、精确版本和语义绑定值，不能提交任意单元格地址、文件路径、公式或模板二进制。

#### 5.3.2 统一 API 入口

跨语言和协议的核心抽象统一为 `ExportAsync()`：

```csharp
public interface IExportApi
{
    ValueTask<ExportResult> ExportAsync(
        ExportRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ExportFormatDescriptor>> ListFormatsAsync(
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ExportTemplateDescriptor>> ListTemplatesAsync(
        string? formatId = null,
        CancellationToken cancellationToken = default);
}
```

`ExportRequest` 是脚本门面和 HostCall 的规范模型；C#、Lua、Python 可以提供更符合语言习惯的构造辅助，但不能形成第二套独立协议。

`ScriptApiFacade` 暴露通用名称：

```csharp
public IExportApi Exports =>
    context.GetRequiredApi<IExportApi>();
```

如果以后希望提供 C# 便捷方法，可以实现为门面扩展：

```csharp
ExportRequest ForTable(...)
ExportRequest ForDocument(...)
```

这些方法只负责构造 `ExportRequest`，最终仍调用 `ExportAsync()` 和 `exports.export`。模板目录查询使用 `ListTemplatesAsync()`/`exports.templates.list`；脚本不通过模板 API 读取模板文件路径。

#### 5.3.3 公共请求和内容模型

```csharp
public sealed record ExportRequest
{
    public required string FormatId { get; init; }
    public required string DirectorySelectionId { get; init; }
    public required string FileName { get; init; }

    // 通用导出：Content 与 Template 二选一。保留 Content 以兼容当前 V1 请求。
    public ExportContent? Content { get; init; }
    public TemplateExportSource? Template { get; init; }
    public ExportFormatOptions? FormatOptions { get; init; }
}

public abstract record ExportContent
{
    public abstract ExportContentKind Kind { get; }
}

public sealed record TemplateExportSource
{
    public required string TemplateId { get; init; }
    public required string TemplateVersion { get; init; }
    public IReadOnlyDictionary<string, object?> Values { get; init; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, ExportTableContent> Tables { get; init; } =
        new Dictionary<string, ExportTableContent>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, ExportDocumentContent> Documents { get; init; } =
        new Dictionary<string, ExportDocumentContent>(StringComparer.Ordinal);
}

public enum ExportSourceKind
{
    Content,
    Template,
}

public enum ExportBindingKind
{
    Scalar,
    Table,
    Document,
}

public enum ExportScalarType
{
    Text,
    Integer,
    Decimal,
    Date,
    Time,
    Duration,
    DateTime,
    Boolean,
}
```

表格内容模型：

```csharp
public sealed record ExportTableContent : ExportContent
{
    public override ExportContentKind Kind => ExportContentKind.Table;
    public string? Title { get; init; }
    public required IReadOnlyList<ExportColumn> Columns { get; init; }
    public required IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; }
    public IReadOnlyList<ExportAggregateColumn> Aggregates { get; init; } = [];
    public IReadOnlyList<TableCellMerge> Merges { get; init; } = [];
    public ExportTableStyle Style { get; init; } = ExportTableStyle.Default;
}
```

通用请求的 `ExportRequest.Content` 在 `Content.Kind=Table` 时携带 `ExportTableContent`，序列化为 `content.kind=table`；模板请求使用 `template.template_id`、`template.template_version` 和 `template.values`/`tables`/`documents`。`Content` 与 `Template` 必须恰好存在一个。C# 类型系统中的 `ExportContent` 可以使用 `ExportTableContent`/`ExportDocumentContent` 的受控联合实现；HostCall JSON 使用明确的 `content.kind` 和 `template` 判别结构，不使用 .NET 类型名称或 `$type`。

公共列类型：

```csharp
public sealed record ExportColumn(
    string Name,
    ExportColumnType Type = ExportColumnType.Text,
    string? NumberFormat = null);

public enum ExportColumnType
{
    Text,
    Integer,
    Decimal,
    Date,
    Time,
    DateTime,
    Boolean,
}
```

聚合是内容层语义，不直接暴露 Excel 公式字符串：

```csharp
public sealed record ExportAggregateColumn(
    string ColumnName,
    ExportAggregation Aggregation = ExportAggregation.Sum,
    string? Label = null);

public enum ExportAggregation
{
    Sum,
}
```

公共合并模型使用逻辑表格坐标，不使用最终 Worksheet 坐标：

```csharp
public sealed record TableCellMerge(
    int StartRow,
    int StartColumn,
    int RowSpan,
    int ColumnSpan);
```

坐标规则：

- 行号和列号从 1 开始；
- 坐标相对于 `ExportTableContent` 的逻辑表格，不包含处理器自动添加的标题、表头或合计行；
- `RowSpan`、`ColumnSpan` 必须大于 0；
- 合并区域不能超出逻辑表格边界，区域之间不能重叠；
- 合并区域只有左上角单元格允许有值；
- XLSX 处理器负责把逻辑坐标映射到最终 Worksheet 坐标；
- CSV 不支持合并时返回 `EXPORT_UNSUPPORTED_FEATURE`；DOCX 表格处理器负责映射到 Word 单元格合并。

公共样式模型：

```csharp
public enum ExportTableStyle
{
    Default,
    Compact,
    Report,
}
```

`Default` 表示基本内容布局，所有格式都必须能接受；`Compact` 和 `Report` 是可选视觉样式，格式不支持时返回 `EXPORT_UNSUPPORTED_FEATURE`。XLSX 可以将它们映射为背景色、加粗、冻结表头和筛选，DOCX 可以映射为标题/表头样式，CSV 不提供视觉样式。

格式选项带有明确的格式 ID，避免 `sheet_name` 等 XLSX 选项污染其他格式：

```csharp
public sealed record ExportFormatOptions(
    string FormatId,
    IReadOnlyDictionary<string, object?> Values);
```

规则：

- `FormatOptions.FormatId` 必须等于 `ExportRequest.FormatId`；
- 选项只由对应格式处理器解释；
- 未知选项、拼写错误或格式不匹配的选项返回 `EXPORT_INVALID_REQUEST`；
- 不允许处理器静默忽略未知选项。

#### 5.3.4 DOCX 文档内容模型

DOCX 后续使用同一个 `ExportRequest`，但 `Content.Kind=document`：

```csharp
public sealed record ExportDocumentContent : ExportContent
{
    public override ExportContentKind Kind => ExportContentKind.Document;
    public string? Title { get; init; }
    public required IReadOnlyList<ExportDocumentBlock> Blocks { get; init; }
    public ExportTableStyle Style { get; init; } = ExportTableStyle.Default;
}

public abstract record ExportDocumentBlock;

public sealed record ExportHeadingBlock(
    string Text,
    int Level = 1) : ExportDocumentBlock;

public sealed record ExportParagraphBlock(
    string Text) : ExportDocumentBlock;

public sealed record ExportTableBlock(
    ExportTableContent Table) : ExportDocumentBlock;
```

文档表格复用公共列类型、值编码和 `TableCellMerge`，但不使用工作表名称、工作表行号或 Excel 公式。DOCX 处理器将 `ExportAggregation.Sum` 转换为计算后的显示值，不承诺保留可计算公式。

#### 5.3.5 格式目录和能力声明

```csharp
public sealed record ExportContentCapabilities(
    ExportContentKind ContentKind,
    IReadOnlyList<ExportFeature> Features);

public sealed record ExportBindingDescriptor(
    string Key,
    ExportBindingKind Kind,
    ExportScalarType? ScalarType = null,
    bool Required = true,
    bool HasDefaultValue = false,
    object? DefaultValue = null,
    string? Description = null);

public sealed record ExportTemplateDescriptor(
    string TemplateId,
    string TemplateVersion,
    string PluginId,
    string FormatId,
    string TemplateFileExtension,
    string DisplayName,
    string? Description,
    IReadOnlyList<ExportBindingDescriptor> Bindings,
    IReadOnlyList<ExportFeature> Features);

public sealed record ExportTemplateValidationContext(
    string FileExtension,
    string FileName);

public sealed record ExportDiagnostic(
    string Code,
    string Message,
    string? BindingKey = null);

public sealed record ExportTemplateValidationResult(
    bool IsValid,
    string? TemplateName,
    string? DisplayName,
    string? Description,
    string? TemplateVersion,
    IReadOnlyList<ExportBindingDescriptor> Bindings,
    IReadOnlyList<ExportFeature> Features,
    IReadOnlyList<ExportDiagnostic> Diagnostics);

public sealed record ExportFormatDescriptor(
    string FormatId,
    string DisplayName,
    string DefaultExtension,
    IReadOnlyList<string> AllowedExtensions,
    IReadOnlyList<ExportContentCapabilities> ContentCapabilities,
    bool SupportsTemplates = false);

public enum ExportContentKind
{
    Table,
    Document,
}

public enum ExportFeature
{
    UnicodeText,
    TypedValues,
    BackgroundColor,
    MergeCells,
    GeneratedAggregate,
    BasicStyle,
    Paragraphs,
    DocumentTables,
}
```

格式目录规划如下：

| `FormatId` | 内容类型 | 通用导出 | 模板导出 | 主要能力 |
| --- | --- | ---: | ---: | --- |
| `xlsx` | `table` | 已实现 | 已实现 | 中文、类型值、基础样式、背景色、合并、生成 `SUM` 公式、合计行 |
| `csv` | `table` | 已实现 | 已实现（文本占位符模板） | UTF-8 BOM、表头、类型格式化文本、计算后的合计行、公式注入防护；不支持视觉样式和合并 |
| `docx` | `document`/`table` | 已实现 | 已实现（文档占位符模板） | 段落、文档表格、基础样式、中文、表格合并 |

运行时的 `ListFormatsAsync()` 只返回实际注册的格式；`ListTemplatesAsync()` 返回模板注册表中已导入、已校验、已启用且兼容的模板描述。能力声明必须同时区分通用内容和模板绑定，脚本不能只根据格式 ID 猜测模板是否可用。模板目录只返回 `template_id`、精确版本、模板扩展名、绑定 schema 和能力，不返回物理模板路径或模板二进制。

#### 5.3.6 导出处理器注册

宿主内部使用处理器注册表。下面的请求模型是宿主内部对公共 `ExportRequest` 的直接消费，不是另一套脚本协议。一个处理器可以同时支持通用内容和它声明的模板；模板布局、绑定 schema 和资源版本属于插件，不属于脚本：

```csharp
public interface IExportHandler
{
    string FormatId { get; }
    IReadOnlyList<ExportContentCapabilities> ContentCapabilities { get; }
    IReadOnlyList<ExportTemplateDescriptor> Templates { get; }

    ValueTask<ExportRenderResult> RenderAsync(
        ExportRequest request,
        ExportExecutionContext context,
        CancellationToken cancellationToken = default);
}

public sealed record ExportExecutionContext(
    string OutputPath,
    Func<CancellationToken, ValueTask<Stream>> OpenTemplateAsync,
    CancellationToken CancellationToken,
    Action<string>? Log = null);

public sealed record ExportRenderResult(
    int? ItemCount);
```

第一阶段及后续处理器：

```text
XlsxTableExportHandler      -> ClosedXML -> xlsx/table
CsvTableExportHandler       -> CSV writer -> csv/table
DocxTableExportHandler      -> Open XML -> docx/table
DocxDocumentExportHandler   -> Open XML -> docx/document
```

处理器必须只负责格式生成，并返回 `ExportRenderResult`；`FileId`、最终 `ExportResult` 和临时文件提交由宿主生成。处理器不负责：

- 选择目录；
- 判断脚本入口是否允许交互；
- 解析或持久化数据库数据；
- 决定是否打开文件；
- 绕过文件名、路径和 `FileId` 安全校验。

#### 5.3.7 导出结果

```csharp
public sealed record ExportResult(
    bool Succeeded,
    string FormatId,
    ExportContentKind ContentKind,
    string? FileId,
    string? FileName,
    int? ItemCount,
    ScriptApiError? Error);
```

对于 `table`，`ItemCount` 表示数据行数；对于 `document`，表示文档块数量。`FileId` 是宿主登记的短期引用，只能用于 `AskToOpenExportedFileAsync()`。脚本不应依赖宿主内部的临时路径结构。

#### 5.3.8 新格式接入规则

增加一种格式时，不修改已有脚本的公共流程，按以下步骤接入：

1. 分配稳定的 `FormatId` 和允许扩展名；
2. 注册按内容类型拆分的 `ExportContentCapabilities`；
3. 实现独立的 `IExportHandler`，只负责把公共内容模型写成目标格式；
4. 加入 `exports.formats.list` 的返回值和三语言文档示例；
5. 增加格式处理器的结构化输出、中文、取消和不支持特性测试；
6. 不新增格式专用目录选择 API、打开文件 API 或脚本权限策略。

CSV 处理器的第一版约定：

- 使用 UTF-8；默认带 UTF-8 BOM，便于 Windows/Excel 识别中文；
- 使用逗号分隔，字段包含逗号、双引号或换行时按 RFC 4180 风格转义；
- 使用 CRLF 换行；
- `null` 输出为空字段，日期/时间/持续时间按公共协议格式化为文本；
- 文本字段经过必要的公式注入防护：以 `=`, `+`, `-`, `@` 开头的文本必须在首字符前增加单引号 `'` 再按 RFC 4180 转义，不能被 Excel 等程序解释为公式；该防护由 CSV 处理器统一执行，脚本不负责预处理；
- 有 `Title` 时输出：标题、表头、数据；没有 `Title` 时不生成空白行，表头从第一行开始；
- `ExportAggregation.Sum` 输出计算后的合计值，不输出公式字符串；
- 第一版不开放 CSV `FormatOptions`，固定为 UTF-8 BOM、逗号分隔、CRLF 和 RFC 4180 转义；非空 `FormatOptions` 返回 `EXPORT_INVALID_REQUEST`；
- 不支持背景色、字体、冻结窗格和合并，视觉样式或 `Merges` 请求返回 `EXPORT_UNSUPPORTED_FEATURE`。

DOCX 模板使用专用元数据段：正文文本中前三个非空行依次为 `[[diary.export.template]]`、`template_name: <snake_case>` 和 `version: <version>`；其余正文中的 `{{binding_key}}` 作为标量绑定，宿主导出时替换占位符并保留其余 Word 结构和样式。

CSV 模板使用 UTF-8 文本头：前三行依次为 `# diary.export.template`、`# template_name: <snake_case>` 和 `# version: <version>`；其余行中的 `{{binding_key}}` 作为标量绑定，`# binding: key|scalar|type|required|default` 可声明类型和默认值。

DOCX 处理器的第一版约定：

- 使用文档块模型，包括标题、段落和文档表格；
- 表格块复用公共列类型和值编码，不把 XLSX 工作表坐标直接暴露给 DOCX；
- `ExportTableStyle` 映射为 Word 的标题、表头和基础表格样式，并由 `BasicStyle` 能力声明覆盖；
- `TableCellMerge` 映射为 Word 表格单元格合并；
- 聚合只保留计算后的显示值；
- 第一版不开放 DOCX `FormatOptions`，普通文档使用公共 `Title`、`Blocks` 和 `Style`；未来新增选项必须通过 `FormatId=docx` 的命名空间校验；
- OA 固定模板另走模板导出模型，不把模板文件路径混入普通 `ExportRequest`。

#### 5.3.9 模板导出契约

模板导出不是第二个脚本 API，而是同一个 `ExportRequest` 的另一种数据来源：

- `Content != null` 且 `Template == null`：通用导出，不使用模板；
- `Content == null` 且 `Template != null`：模板导出；
- 两者同时为空或同时存在：返回 `EXPORT_INVALID_REQUEST`。

模板请求示例：

```json
{
  "format_id": "xlsx",
  "directory_selection_id": "dirsel-01J...",
  "file_name": "overtime-2026-07.xlsx",
  "template": {
    "template_id": "overtime.standard",
    "template_version": "1.2.0",
    "values": {
      "employee_name": "张三",
      "period": "2026-07"
    },
    "tables": {
      "overtime_items": {
        "columns": [{"name": "日期", "type": "date"}],
        "rows": [["2026-07-01"]]
      }
    }
  }
}
```

模板绑定规则：

- `TemplateId` 由宿主根据插件 ID 和插件校验返回的模板名组合生成，推荐使用 `xlsx.work_report`；插件 ID 和模板名均使用全小写 `snake_case`，宿主负责校验完整 ID 合法性和唯一性；不同插件可以使用相同的模板名，例如 `xlsx.work_report` 与 `docx.work_report`；脚本不能提交路径、URL 或模板文件内容；
- `TemplateVersion` 必须精确匹配模板目录中的版本，导出过程不自动将旧版本替换为新版本；
- `Values` 只允许模板 schema 声明的标量键，`Tables`/`Documents` 只允许声明的集合键；未知键、没有默认值的缺少必填键、类型不匹配、违反空值策略或超过集合限制时返回 `EXPORT_TEMPLATE_BINDING_INVALID`，错误结果必须列出缺失或非法的绑定键；
- 模板需要哪些导出数据由插件校验模板文件后返回 `ExportTemplateValidationResult.Bindings`，宿主保存为模板 descriptor 并在真正渲染前再次校验；不能仅依赖脚本作者自行阅读说明；
- 模板只能通过语义绑定填充，脚本不能传入单元格地址、任意范围、公式、书签内部 ID 或 XML 路径；
- 模板文件由宿主管理的模板库以只读方式保存，插件只通过受限流读取，输出始终另存为新文件；
- 模板处理器可以保留模板中的样式、合并、打印设置和保护区域，但不能绕过宿主文件名、路径、临时文件和 `FileId` 约束；
- 模板插件不得执行模板内宏、外部链接或脚本。需要保留宏时必须由格式插件显式声明能力，并由宿主单独设置受信任策略，第一版默认拒绝宏执行和外部资源访问。

`exports.templates.list` 只用于发现已经通过插件校验的模板和绑定 schema；用户可以先用 `SelectOption` 选择模板，再构造 `TemplateExportSource`。如果对应扩展名的插件卸载、模板校验状态失效、模板版本被撤回或依赖不满足，列表中不返回该模板，直接导出已保存的 `template_id + version` 时返回 `EXPORT_TEMPLATE_UNAVAILABLE`，不回退到其他模板。

#### 5.3.10 插件化导出处理器（建议方案）

脚本面对的 `IExportApi` 仍是宿主统一 API，不直接暴露插件对象。插件化边界放在宿主的“格式处理器和模板处理器”层：通用请求按 `format_id` 选择处理器，模板请求按 `format_id + template_id + template_version` 选择模板注册记录及其扩展名对应的处理器。这样可以在不改变 C#、Lua、Python 脚本契约的前提下增加 CSV、DOCX、PDF 或 OA 固定模板格式，同时保留不使用模板的标准导出。

当前版本先复用 `Diary.ScriptHost` 中已经稳定的脚本导出 DTO 和插件契约；后续若需要独立分发契约，再拆出 `Diary.ExportBase`，不改变脚本协议：

```text
Diary.ScriptHost
  ExportContent / ExportRequest 的格式无关契约
  ExportFormatDescriptor / ExportTemplateDescriptor / ExportFeature
  IExportPlugin / IExportHandler / IExportTemplateHandler
  ExportExecutionContext

Diary.Export.Xlsx
  ClosedXML 实现的 XLSX 表格处理器

Diary.App
  ExportPluginHost / ExportHandlerRegistry
  目录令牌、FileId、文件名校验、冲突策略和打开询问

Diary.ScriptHost
  IExportApi、三语言门面和 Worker HostCall
```

建议新增插件入口；处理器沿用前文 5.3.6 定义的 `IExportHandler`，不再为通用导出和模板导出各起一套处理器接口：

`ExportPluginManifest` 复用现有插件 manifest 的 ID、版本、API 版本和依赖语义，但独立声明导出插件能力；不需要 Tracker 实例配置、数据库迁移或 UI 贡献。

```csharp
public sealed record ExportPluginManifest(
    string Id,
    string Version,
    int ApiVersion = 1);

public interface IExportPlugin
{
    ExportPluginManifest Manifest { get; }
    IEnumerable<IExportHandler> GetExportHandlers();
    IEnumerable<IExportTemplateHandler> GetTemplateHandlers();
}

public interface IExportTemplateHandler
{
    string PluginId { get; }
    string FormatId { get; }
    IReadOnlyList<string> SupportedTemplateExtensions { get; }

    ValueTask<ExportTemplateValidationResult> ValidateAsync(
        Stream templateStream,
        ExportTemplateValidationContext context,
        CancellationToken cancellationToken = default);

    ValueTask<ExportRenderResult> RenderAsync(
        ExportRequest request,
        ExportExecutionContext context,
        CancellationToken cancellationToken = default);
}
```

`ExportExecutionContext` 由宿主创建，只包含宿主分配的输出路径、只读模板流打开回调、取消令牌和受限日志回调；插件不得从上下文反查 `App`、DI、数据库或脚本执行对象。模板文件不随插件发布，插件只注册它能识别的模板扩展名、校验器和渲染器。当前这些稳定契约暂位于 `Diary.ScriptHost`，后续若拆分 `Diary.ExportBase`，不改变脚本协议。

插件处理器的职责边界：

- 只接收宿主已经完成作用域、文件名、行列数量、值类型、能力和合并区域校验的请求；
- 只能写入宿主分配的 `OutputPath`，不能自行选择目录、生成 `FileId`、打开文件或弹出 UI；
- 不接收 `App`、`IServiceProvider`、数据库连接、Worker 对象或脚本执行上下文；
- 必须支持取消、失败时不留下可见的半成品文件，并把异常转换为宿主结构化错误；
- `format_id` 和模板扩展名必须全局唯一；`template_id` 由 `plugin_id + template_name` 组合而成，因此不同插件可以有同名模板，但同一插件内的模板名不能重复；`template_id + template_version` 在模板注册表中必须唯一；插件加载或模板导入时发现冲突、模板绑定键冲突或能力声明不一致时拒绝注册/导入，不静默覆盖；
- 模板扩展名必须以点号开头、全小写、比较时不区分大小写，例如 `.xlsx`、`.docx`；一个扩展名只能由一个已注册模板插件负责，冲突时拒绝插件加载；
- 插件只注册扩展名和模板处理能力，不提供内置模板文件；宿主导入文件时按扩展名选择插件，再将模板流交给 `ValidateAsync`；
- `ValidateAsync` 必须判断文件结构、格式和安全约束，并返回模板是否有效、显示信息、版本、支持能力以及完整绑定 schema；校验失败的模板不得进入可用目录；
- 模板导出前，宿主根据保存的 schema 校验所有必填值、类型、空值策略和行数限制；插件渲染前仍可执行最终校验，但不能绕过宿主的必填数据检查。

插件加载建议分两阶段：

1. 启动时发现插件 manifest，检查导出插件 API 版本、依赖、格式和模板扩展名声明；
2. 兼容插件注册通用处理器和模板处理器，建立按 `format_id`/扩展名/`template_id` 排序的只读目录；
3. 单个插件加载失败只使其格式或对应扩展名模板不可用，不影响脚本系统、通用导出或核心日记。

与现有 Tracker 插件相比，导出插件不需要实例配置、数据库迁移或 Tracker UI。第一阶段可先使用独立的 `ExportPluginHost`，复用 manifest 兼容性和依赖检查规则；长期再把 Tracker/Export 的公共插件发现、诊断和生命周期抽象为通用 `IPlugin`，避免两套加载器继续分叉。

迁移顺序：

1. 先把当前 `ScriptExportService.WriteXlsx` 提取为 `Diary.Export.Xlsx` 的通用 `XlsxTableExportHandler`，确保不使用模板的请求继续可用；
2. `ScriptExportService` 改为目录/FileId/安全校验外观层，并依赖 `IExportHandlerRegistry`；
3. 在 XLSX 插件中增加模板扩展名声明、模板校验器、绑定 schema 生成和渲染器；
4. 已以同一注册表接入 `CsvTableExportHandler`、`Docx*ExportHandler` 及其模板扩展名、校验器和渲染器；后续新增格式沿用相同流程；
5. 插件未加载、格式不可用或模板版本不可用时，`exports.formats.list`/`exports.templates.list` 不返回对应项，导出返回稳定错误，不自动回退到其他格式或模板。

安全边界：当前插件程序集与主程序同进程，插件属于受信任扩展，不能把插件化误认为沙箱。若未来允许来源不受信任的第三方格式插件，应把 `IExportHandler` 调整为独立 exporter Worker，通过受限协议传递已校验内容，主进程继续掌握输出路径和 `FileId` 生命周期。

### 5.4 三语言门面和 Worker 代理

三种语言使用相同的核心语义和 HostCall 名称：

| 能力 | C# | Lua | Python | HostCall |
| --- | --- | --- | --- | --- |
| 选择选项 | `api.System.SelectOptionAsync(request)` | `diary.ui.select_option(request)` | `context.diary.ui.select_option(request)` | `ui.options.select` |
| 选择目录 | `api.System.PickDirectoryAsync(options)` | `diary.ui.pick_directory(options)` | `context.diary.ui.pick_directory(options)` | `ui.directory.pick` |
| 通用/模板导出 | `api.Exports.ExportAsync(request)` | `diary.exports.export(request)` | `context.diary.exports.export(request)` | `exports.export` |
| 查询格式 | `api.Exports.ListFormatsAsync()` | `diary.exports.list_formats()` | `context.diary.exports.list_formats()` | `exports.formats.list` |
| 查询模板 | `api.Exports.ListTemplatesAsync(formatId)` | `diary.exports.list_templates(format_id)` | `context.diary.exports.list_templates(format_id)` | `exports.templates.list` |
| 询问打开 | `api.System.AskToOpenExportedFileAsync(fileId)` | `diary.ui.ask_to_open_exported_file(file_id)` | `context.diary.ui.ask_to_open_exported_file(file_id)` | `ui.exported_file.open` |

约束如下：

- C#、Lua、Python 都使用 `ExportRequest` 的 `content.kind` 区分 `table` 和 `document`；
- Lua/Python 使用语言友好的字典/表，但字段名和 JSON 协议保持一致；
- Lua/Python 不接收绝对目录路径，只接收 `selection_id`、`display_name`、`file_id` 和 `file_name`；
- 非结果类交互调用的宿主故障按现有 Worker 约定抛出/转为 HostCall 错误；导出结果类调用返回 `succeeded` 与 `ScriptApiError`；
- 用户取消目录选择在三种语言中都映射为 `null`/`nil`，不抛出错误；
- 用户拒绝打开在三种语言中都返回 `UserDeclined` 状态，不改变导出结果；
- `ScriptHostApiCatalog.All`、Worker 握手的 `supportedHostApis`、三语言代理和 API 文档必须同时增加 `ui.options.select`、`exports.export`、`exports.formats.list` 和相关 UI HostCall，避免“能力已发现但代理缺失”。

## 6. 加班导出脚本的推荐流程

### 6.1 C# 伪代码

下面的签名与当前 `EditorScript`/`IEditorScriptV1` 契约一致；`Export*` 类型是本设计拟增加的 API 类型，尚未存在于当前代码中。

```csharp
public override async ValueTask<ScriptExecutionResult> ExecuteAsync(
    IScriptEditorContext context,
    CancellationToken cancellationToken = default)
{
    var range = context.GetDateRange();
    if (range is null)
    {
        return new(
            ScriptExecutionStatus.Rejected,
            [new ScriptDiagnostic(
                "EDITOR_DATE_RANGE_REQUIRED",
                "该脚本必须从带日期范围的编辑器右键菜单运行。",
                ScriptDiagnosticSeverity.Error,
                ScriptDiagnosticCategory.Validation)]);
    }

    var rows = new List<IReadOnlyList<object?>>();
    await foreach (var item in context.StreamItemsAsync(cancellationToken))
    {
        if (!item.Tags.Any(tag => tag.Name == "加班"))
            continue;

        rows.Add([
            item.Date,
            item.Comment,
            item.Hours,
            item.Note ?? string.Empty,
        ]);
    }

    // 无数据时必须在交互之前结束。
    if (rows.Count == 0)
        return ScriptExecutionResult.Succeeded();

    var api = context.Api();
    var directorySelection = await api.System.PickDirectoryAsync(
        new DirectoryPickerOptions
        {
            Title = "选择加班明细导出目录",
        },
        cancellationToken);

    if (directorySelection is null)
        return ScriptExecutionResult.Cancelled();

    var export = await api.Exports.ExportAsync(
        new ExportRequest
        {
            FormatId = "xlsx",
            DirectorySelectionId = directorySelection.SelectionId,
            FileName = $"加班明细-{range.StartDate[..7]}.xlsx",
            Content = new ExportTableContent
            {
                Title = $"加班明细（{range.StartDate} 至 {range.EndDate}）",
                Columns =
                [
                    new ExportColumn("日期", ExportColumnType.Date),
                    new ExportColumn("工作内容"),
                    new ExportColumn("加班时长", ExportColumnType.Decimal),
                    new ExportColumn("备注"),
                ],
                Rows = rows,
                Aggregates =
                [
                    new ExportAggregateColumn("加班时长"),
                ],
            },
            FormatOptions = new ExportFormatOptions(
                "xlsx",
                new Dictionary<string, object?>
                {
                    ["sheet_name"] = "加班明细",
                }),
        },
        cancellationToken);

    if (!export.Succeeded || export.FileId is null)
    {
        return new(
            ScriptExecutionStatus.Failed,
            [new ScriptDiagnostic(
                export.Error?.Code ?? "EXPORT_FAILED",
                export.Error?.Message ?? "导出失败。",
                ScriptDiagnosticSeverity.Error,
                ScriptDiagnosticCategory.Host)]);
    }

    // 宿主负责显示“是否打开”对话框；用户拒绝打开不影响导出成功。
    await api.System.AskToOpenExportedFileAsync(
        export.FileId,
        cancellationToken);

    return ScriptExecutionResult.Succeeded();
}
```

伪代码仅用于表达 API 形状。实际脚本可以封装为更短的 `ExportOvertimeReportAsync()` 辅助函数。目录路径不进入脚本，也不应被脚本拼接或修改。

### 6.2 无数据行为

无数据是正常业务结果，不是错误：

```text
rows.Count == 0
    ↓
不调用 PickDirectoryAsync
不调用 `ExportAsync`
不调用 `AskToOpenExportedFileAsync`
返回 Succeeded
```

脚本日志可以记录一条低级别信息，例如：

```text
当前范围内没有找到加班记录。
```

但不应弹出目录选择、确认或通知对话框，除非脚本作者显式调用通知 API。宿主现有的非交互执行记录或成功提示如果仍会产生，不属于本流程中的“询问”，但应避免把它实现成目录或打开确认。

### 6.3 用户取消行为

用户取消目录选择：

```text
API 返回 null
不生成文件
不调用打开文件 API
脚本返回 ScriptExecutionResult.Cancelled()
管理页显示为“已取消”，不是“执行失败”
```

用户拒绝打开文件：

```text
文件已成功生成
脚本整体仍然 Succeeded
OpenExportedFileResult.Status = UserDeclined
```

### 6.4 导出成功但打开失败

导出和打开是两个独立阶段：

```text
导出成功 + 打开失败
    = 导出成功，打开阶段记录 Failed
```

不能因为系统默认程序不存在、文件关联缺失或打开过程失败而回滚已经生成的导出文件。

## 7. 交互 API 的执行边界

### 7.1 第一阶段允许的脚本上下文

目录选择、格式导出和询问打开文件都属于交互式宿主能力。第一阶段允许以下有人值守组合：

| `EntryKind` | `Source` | 允许能力 |
| --- | --- | --- |
| `Editor` | `Editor` | 目录选择、格式导出、询问打开 |
| `Application` | `Manual` | 目录选择、格式导出、询问打开 |
| `Query` | `Manual` | 目录选择、格式导出、询问打开 |

其中：

- `Editor + Editor` 覆盖日历的日、周、月、季度、年度目标和工作项编辑器右键菜单；
- `Application + Manual` 覆盖脚本管理页或应用命令触发的手动执行；
- `Query + Manual` 覆盖脚本管理页手动运行的查询脚本；
- Query/Application 脚本没有编辑器的 `GetDateRange()` 保证；脚本应从手动执行参数 `Arguments`、查询快捷范围或自身配置中取得 `startDate`/`endDate`；
- 如果手动执行没有提供日期范围且脚本又需要日期范围，应返回参数校验诊断，而不是拒绝目录选择或导出 API；
- 本阶段不新增通用日期选择对话框 API。若以后需要由 Application/Query 脚本询问日期范围，再单独设计 `PickDateRangeAsync()`。

### 7.2 禁止的脚本上下文

以下入口第一阶段不允许调用目录选择、格式导出或询问打开文件：

- `Automation` + `Startup`；
- `Automation` + `Scheduled`；
- `Automation` + `WorkItemCreated`；
- `Automation` + `WorkItemSaved`；
- `Automation` + `TagAdded`；
- 任意 `Unknown` 或不匹配的入口/来源组合。

调用时返回稳定 API 错误：

```text
SCRIPT_API_SCOPE_NOT_SUPPORTED
```

宿主必须在每一次 HostCall 分发前，根据当前执行元数据中的 `EntryKind` 和 `Source` 检查策略，不能只依赖能力列表、脚本元数据声明或 Worker 端主动隐藏 API。这样可以避免脚本绕过门面直接发送 HostCall。

如果入口符合策略但当前没有可用窗口或 UI 线程，则返回：

```text
UI_UNAVAILABLE
```

### 7.3 UI 线程和取消

HostCall 收到文件交互请求后必须切换到 Avalonia UI 线程显示对话框。脚本 Worker 线程不能直接创建 Avalonia 对象。

对话框实现必须把 `DialogDismissPolicy` 映射到真实窗口行为：

- `AllowCancel` 显示关闭按钮、Escape 和取消路径；
- `RequireChoice` 隐藏或禁用右上角关闭、Escape、Alt+F4 和遮罩层关闭，只保留选项按钮；
- `RequireChoice` 不能通过默认选项、超时或窗口失焦自动提交答案；
- 对话框按钮点击后才完成 `ui.options.select` 或内置的打开文件询问 HostCall。

取消和结束行为：

- 脚本取消时关闭或取消当前文件/选项对话框；
- `AllowCancel` 的用户取消返回 `Cancelled` 或 API 约定的 `null`；
- `RequireChoice` 不产生用户关闭结果，外部取消只映射为脚本取消/宿主不可用；
- UI 对话框关闭后必须完成 HostCall 响应，不能遗留未完成请求；
- Worker 超时或终止时，宿主要清理活动对话框、目录令牌和文件引用。

## 8. 路径和文件安全

### 8.1 目录选择结果

目录选择 API 不把绝对路径返回给脚本，而是返回 `DirectorySelection`：

- `SelectionId`：宿主生成的不可猜测令牌；
- `DisplayName`：用于展示的目录名称，不承担定位职责。

宿主在创建选择令牌时：

- 规范化并保存用户明确选择的绝对路径；
- 检查目录存在且为目录；
- 检查是否可写；
- 处理网络目录或只读介质失败；
- 拒绝空路径和非法路径；
- 不把真实路径写入返回给 Lua/Python Worker 的协议响应。

导出 HostCall 只接受 `DirectorySelectionId`，并重新确认令牌属于当前执行。脚本不能提交另一个执行、另一个 Worker 或凭空构造的目录令牌。

### 8.2 文件名校验

脚本只能提供文件名，宿主负责：

- 拒绝路径分隔符、控制字符、`.`、`..` 和路径穿越；不做静默替换或清洗；
- 限制文件名长度；
- `FileName` 可以不带扩展名；不带扩展名时根据格式目录追加 `DefaultExtension`；
- 带扩展名时必须与格式目录的 `AllowedExtensions` 匹配，扩展名比较不区分大小写；
- 不自动替换不匹配的扩展名，例如 `format_id=csv` 与 `report.xlsx` 直接返回 `EXPORT_INVALID_REQUEST`；
- 格式目录统一保存带点的扩展名，例如 `.xlsx`、`.csv`、`.docx`；
- 拒绝重复扩展名、空扩展名和路径分隔符，避免生成“内容格式”和文件名不一致的文件；
- 处理 Windows 保留名称；
- 同名文件使用覆盖、自动编号或询问策略。

第一阶段默认采用自动编号，避免脚本静默覆盖用户已有文件：

```text
加班明细-2026-07.xlsx
加班明细-2026-07 (1).xlsx
```

### 8.3 文件引用和生命周期

`FileId` 由宿主生成，映射到本次脚本执行创建的导出文件。第一阶段固定以下生命周期：

- 绑定 `ExecutionId` 和 `WorkerId`，只允许当前脚本执行使用；
- 有效期为导出成功后的 10 分钟；
- Worker 重启、应用重启或脚本重新执行后不继续有效；
- 导出失败、取消或文件未实际落盘时不登记 `FileId`；
- 只允许打开由当前宿主生成的文件；
- 不允许脚本凭空构造 `FileId`；
- 不把数据库、配置、日志和任意已有文件注册为可打开导出文件；
- 导出文件被删除、移动或不可访问时，打开请求返回 `EXPORTED_FILE_NOT_FOUND`；
- 生命周期到期后清理引用，是否删除实际文件由宿主临时文件策略决定，但脚本不能再访问它。

## 9. HostCall 和跨语言协议

### 9.1 HostCall 方法

当前已增加以下 HostCall；格式处理器均通过同一 `exports.export` 入口调用：

```text
ui.options.select
ui.directory.pick
ui.exported_file.open
exports.formats.list
exports.export
```

`host.capabilities.list` 应能发现这些方法，但能力发现不能代替上下文校验。实现时还必须同步更新 `ScriptHostApiCatalog.All`、C# Worker 代理、Lua/Python Worker 代理和握手中的 `supportedHostApis`；HostCall 分发器不得只依赖“已注册”判断，还要执行第 7 节的入口策略检查。

HostCall 分发必须接收不可伪造的执行上下文，至少包括 `ExecutionId`、`WorkerId`、`ScriptId`、`EntryKind` 和 `Source`。该上下文由宿主执行器建立并传递，不能从 Worker 提交的普通参数中读取。

### 9.2 请求和响应

HostCall 使用统一外层响应：

- 外层 `success` 表示 HostCall 是否完成协议级处理；
- `result` 承载业务结果，协议级失败时为 `null`；
- 外层 `error` 只承载协议级错误，例如 JSON 无法解析、HostCall 不存在或入口不允许；
- 导出业务失败通过 `result.succeeded=false` 和 `result.error` 返回；
- 用户取消目录选择是正常业务结果：外层 `success=true`、`result=null`、`error=null`。

协议枚举值统一使用小写稳定字符串或 snake_case：

```text
format_id: xlsx / csv / docx
content.kind: table / document
column.type: text / integer / decimal / date / time / duration / datetime / boolean
aggregation: sum
feature: unicode_text / typed_values / generated_aggregate / merge_cells
```

C# 内部可以使用 PascalCase 枚举，但 Worker 序列化器必须统一转换为上述协议值；Lua/Python 不应同时兼容多套大小写拼写。

选项选择请求：

```json
{
  "title": "导出方式",
  "message": "请选择要生成的文件类型",
  "dismissPolicy": "require_choice",
  "default_option_id": "xlsx",
  "options": [
    {
      "id": "xlsx",
      "label": "Excel 工作簿",
      "description": "适合继续编辑和提交 OA"
    },
    {
      "id": "csv",
      "label": "CSV 文本",
      "description": "适合导入其他系统"
    }
  ]
}
```

选择成功响应：

```json
{
  "success": true,
  "result": {
    "status": "selected",
    "optionId": "xlsx"
  },
  "error": null
}
```

允许取消的对话框取消响应：

```json
{
  "success": true,
  "result": {
    "status": "cancelled",
    "optionId": null
  },
  "error": null
}
```

`dismissPolicy=require_choice` 时不允许通过 UI 关闭，因此不会产生普通的 `cancelled` 结果；脚本取消、Worker 终止或应用退出仍按外部取消处理。

目录选择请求：

```json
{
  "title": "选择加班明细导出目录",
  "suggestedDirectory": null
}
```

目录选择成功响应：

```json
{
  "success": true,
  "result": {
    "selection_id": "dirsel-01J...",
    "display_name": "Exports"
  },
  "error": null
}
```

用户取消响应：

```json
{
  "success": true,
  "result": null,
  "error": null
}
```

格式目录响应：

```json
{
  "success": true,
  "result": [
    {
      "format_id": "xlsx",
      "display_name": "Excel 工作簿",
      "defaultExtension": ".xlsx",
      "allowedExtensions": [".xlsx"],
      "contentCapabilities": {
        "table": [
          "unicode_text",
          "typed_values",
          "basic_style",
          "background_color",
          "merge_cells",
          "generated_aggregate"
        ]
      }
    }
  ],
  "error": null
}
```

完整规划中的格式目录能力如下；第一阶段运行时仍只返回实际已注册的 `xlsx`：

```json
[
  {
    "format_id": "xlsx",
    "contentCapabilities": {
      "table": ["unicode_text", "typed_values", "basic_style", "background_color", "merge_cells", "generated_aggregate"]
    }
  },
  {
    "format_id": "csv",
    "contentCapabilities": {
      "table": ["unicode_text", "typed_values", "generated_aggregate"]
    }
  },
  {
    "format_id": "docx",
    "contentCapabilities": {
      "table": ["unicode_text", "typed_values", "basic_style", "merge_cells", "generated_aggregate"],
      "document": ["unicode_text", "basic_style", "paragraphs", "document_tables"]
    }
  }
]
```

CSV 和 DOCX 完成处理器后才出现在运行时格式目录中。脚本应先检查目标 `content.kind` 对应的能力，再提交导出请求。

通用表格导出请求（XLSX）：

```json
{
  "format_id": "xlsx",
  "directory_selection_id": "dirsel-01J...",
  "file_name": "加班明细-2026-07.xlsx",
  "formatOptions": {
    "format_id": "xlsx",
    "values": {
      "sheet_name": "加班明细"
    }
  },
  "content": {
    "kind": "table",
    "title": "加班明细（2026-07-01 至 2026-07-31）",
    "columns": [
      { "name": "日期", "type": "date" },
      { "name": "工作内容", "type": "text" },
      { "name": "加班时长", "type": "decimal" },
      { "name": "备注", "type": "text" }
    ],
    "rows": [
      ["2026-07-03", "修复线上问题", 2.5, "紧急处理"]
    ],
    "aggregates": [
      { "columnName": "加班时长", "aggregation": "sum" }
    ],
    "merges": [],
    "style": "default"
  }
}
```

DOCX 文档请求示例：

```json
{
  "format_id": "docx",
  "directory_selection_id": "dirsel-01J...",
  "file_name": "加班说明.docx",
  "content": {
    "kind": "document",
    "title": "加班说明",
    "blocks": [
      { "kind": "heading", "level": 1, "text": "加班说明" },
      { "kind": "paragraph", "text": "本月加班明细如下。" },
      {
        "kind": "table",
        "table": {
          "columns": [
            { "name": "日期", "type": "date" },
            { "name": "加班时长", "type": "decimal" }
          ],
          "rows": [["2026-07-03", 2.5]]
        }
      }
    ]
  }
}
```

导出成功响应：

```json
{
  "success": true,
  "result": {
    "succeeded": true,
    "format_id": "xlsx",
    "contentKind": "table",
    "file_id": "export-01J...",
    "file_name": "加班明细-2026-07.xlsx",
    "itemCount": 1,
    "error": null
  },
  "error": null
}
```

导出业务失败响应：

```json
{
  "success": true,
  "result": {
    "succeeded": false,
    "format_id": "csv",
    "contentKind": "table",
    "file_id": null,
    "file_name": null,
    "itemCount": null,
    "error": {
      "code": "EXPORT_UNSUPPORTED_FEATURE",
      "message": "csv 不支持 merge_cells"
    }
  },
  "error": null
}
```

协议级失败响应：

```json
{
  "success": false,
  "result": null,
  "error": {
    "code": "HOSTCALL_INVALID_ARGUMENT",
    "message": "content.kind 缺失或无效"
  }
}
```

C# 门面请求、Lua/Python 字典和 HostCall JSON 的转换关系固定为：

```text
ExportRequest
    ├── FormatId
    ├── DirectorySelectionId
    ├── FileName
    ├── FormatOptions
    └── Content
          ├── ExportTableContent  -> content.kind=table
          └── ExportDocumentContent -> content.kind=document
```

这套映射由三语言 Worker 代理统一维护，不允许 C#、Lua、Python 各自定义不同字段名。绝对路径不返回给 Worker；脚本只需要选择令牌、格式 ID、内容类型、`file_id` 和 `file_name`。

#### 9.2.1 单元格值编码

第一阶段不依赖当前区域设置，使用以下 JSON 编码：

| 列类型 | JSON 值 | 约束 |
| --- | --- | --- |
| `text` | 字符串或 `null` | `null` 生成空单元格 |
| `boolean` | `true`/`false` 或 `null` | 不接受字符串布尔值 |
| `integer` | JSON 整数或 `null` | 不接受带千分位的字符串 |
| `decimal` | JSON 数字或 `null` | 宿主使用不变文化解析并保存为数值 |
| `date` | `yyyy-MM-dd` 字符串或 `null` | 不接受本地化日期文本 |
| `time` | `HH:mm:ss` 字符串或 `null` | 表示一天中的时刻，不允许隐式转换为持续时间 |
| `duration` | JSON 数字（秒）、`HH:mm:ss` 字符串或 `null` | 表示持续时间，允许配置 `sum`；宿主统一转换为时长值 |
| `datetime` | ISO 8601、带偏移字符串或 `null` | 宿主先按应用配置的本地时区转换；Worker 不得按自身时区隐式转换 |

`content.rows` 中每一行的单元格数量必须等于 `content.columns` 数量，否则返回 `EXPORT_INVALID_REQUEST`。未定义或无法转换的值不进行猜测转换，而是返回带列名和行号的校验错误。C#、Lua、Python 不直接序列化抽象 `ExportContent`；统一使用带 `content.kind` 判别字段的 wire DTO，并由显式 converter/映射将枚举序列化为全小写或 snake_case。

#### 9.2.2 合并坐标

`content.merges` 使用逻辑表格坐标，行号和列号从 1 开始：

- 坐标相对于 `content` 中的表格数据，不包含 XLSX 处理器自动添加的标题、表头或合计行；
- `rowSpan`、`columnSpan` 必须大于 0；
- 合并区域不能超出逻辑表格边界；
- 合并区域之间不能重叠；
- 合并区域内只有左上角单元格允许有值；
- XLSX/DOCX 处理器负责映射到各自的最终文件坐标；
- CSV 处理器收到非空 `merges` 时返回 `EXPORT_UNSUPPORTED_FEATURE`。

校验失败统一返回 `EXPORT_INVALID_REQUEST`，不创建部分文件。

### 9.3 消息大小

月度加班明细通常较小，可以使用单次 `exports.export` HostCall。

如果未来导出数万条工作项，不能无限增大单条 Worker 消息。可以增加会话式导出协议：

```text
exports.export.begin
exports.export.appendItems
exports.export.complete
exports.export.cancel
```

第一阶段不实现分块协议，但导出服务和 HostCall 分发层应避免把数据模型写死为只能单次传输。单次请求仍必须受宿主配置的行数、列数和消息大小上限约束；超过上限返回 `EXPORT_TOO_LARGE`，不生成部分文件。

## 10. XLSX 生成规则

### 10.1 生成库

宿主使用 ClosedXML 生成 `.xlsx`。脚本只提交 JSON 可序列化的数据，不引用 ClosedXML 类型。

### 10.2 默认工作表

默认创建一个工作表：

```text
工作表名称：`formatOptions.sheet_name`，缺省为“明细”
第一行：标题（如果提供）
第二行：表头
第三行开始：明细数据
最后一行：合计（如果配置 Aggregates）
```

### 10.3 标题和合并

当提供 `Title` 时：

- 标题占用从第一列到最后一列；
- 自动合并标题区域；
- 使用报告样式背景色；
- 标题行加粗并居中。

### 10.4 背景色

第一阶段只提供宿主统一样式：

- 标题行：主题色；
- 表头：深色背景、浅色文字；
- 合计行：浅色强调背景；
- 普通数据行：不强制交替色。

脚本不需要传输 RGB 结构或字体对象。未来确实需要个性化时，再增加受限的 `ExportTableStyle`，不直接暴露全部 ClosedXML API。

### 10.5 公式

`Aggregates` 由宿主根据实际数据行范围生成公式。例如：

```excel
=SUM(C3:C20)
```

宿主负责：

- 将列名映射到列号；
- 生成正确的起止行；
- 设置数字格式；
- 在保存前进行必要的公式计算或设置 Excel 重算标志。

第一阶段只支持 `Sum` 聚合。`null` 单元格被忽略；指定列没有任何有效数值时合计为 0。第一阶段只允许对 `Integer`、`Decimal` 和 `Duration` 列配置聚合，不允许对 `Time`、文本、布尔或日期列求和。XLSX 的持续时间使用 `[h]:mm:ss` 等不会在 24 小时处回绕的格式。对于 XLSX，宿主将其落地为 `SUM` 公式。

不允许脚本直接传入任意公式字符串作为第一阶段能力，避免公式注入、跨版本兼容和错误引用。后续如确有需要，再增加经过校验的公式表达式能力。

### 10.6 中文和日期

单元格文本使用 UTF-8/JSON 传输，宿主写入 Unicode 字符串。默认字体由宿主样式决定，可以在 Windows 使用中文字体，在 Linux 使用可用的 CJK 字体回退。

日期、时间和日期时间使用明确的列类型和格式，不依赖脚本当前区域设置：

```text
Date：yyyy-MM-dd
Time：HH:mm:ss
DateTime：ISO 8601、带偏移；宿主按应用配置的本地时区转换后写入 Excel 日期时间值
Decimal：按样式显示但保存为数值，不使用字符串拼接
null：空单元格
```

## 11. 模板导出设计

### 11.1 模板和通用导出的边界

模板导出用于布局、字段位置、打印设置或 OA 约束已经固定的场景；通用导出用于脚本需要完全控制列、行、表格块和内容布局的场景。两者不能互相隐式转换：

| 项目 | 通用导出 | 模板导出 |
| --- | --- | --- |
| 布局来源 | 脚本提交 `ExportContent` | 用户导入模板文件；插件提供扩展名、校验器、binding schema 和渲染器 |
| 脚本可控制内容 | 列、行、标题、文档块、聚合 | schema 声明的值、表格和文档绑定 |
| 脚本可控制位置 | 逻辑表格坐标和公共合并模型 | 不可传单元格地址或任意范围 |
| 文件来源 | 格式插件新建文件 | 宿主管理的用户模板另存为新文件 |
| 适用场景 | 标准报表、CSV 明细、自由文档 | OA 固定格式、公司模板、固定打印版式 |
| 没有模板时 | 正常可用 | 返回模板不可用，不回退为通用布局 |

### 11.2 模板目录和版本

本方案不提供插件内置模板。模板文件由用户通过宿主管理页面导入，插件只提供：

- 能识别的模板文件扩展名；
- 模板结构和安全校验功能；
- 模板需要的导出数据 schema；
- 根据 schema 将数据写入模板的渲染功能。

脚本只能看到经过宿主导入和插件校验后的描述信息：

```csharp
public interface IExportApi
{
    ValueTask<IReadOnlyList<ExportTemplateDescriptor>> ListTemplatesAsync(
        string? formatId = null,
        CancellationToken cancellationToken = default);
}
```

模板描述至少包含：

- 宿主根据插件 ID 和模板名生成并校验后的全局唯一 `template_id`；
- 精确的 `template_version`；
- 目标 `format_id`；
- 模板文件扩展名 `template_file_extension`；
- 显示名称和说明；
- 插件校验返回的标量、表格、文档绑定键、类型、是否必填、空值策略和说明；
- 支持的特性，如合并、保留打印设置、保护区域或批量行；
- 负责该扩展名的插件 ID 和插件版本，供管理页面诊断显示。

模板校验结果必须提供 `template_name`。宿主将插件 manifest 中稳定的 `plugin_id` 与该名称组合为 `template_id`，推荐形式为 `plugin_id.template_name`，例如 `xlsx.work_report`。宿主校验 `plugin_id`、`template_name` 和完整 ID 的合法性，并拒绝同一插件下的重复模板 ID；不同插件使用相同的 `template_name` 不冲突。模板 ID 一经发布不可复用；删除模板时只做归档/禁用，历史导出记录仍可用 `template_id + template_version` 追溯。

模板版本由宿主管理并且不可覆盖。导入同一模板文件的新修订时创建新的 `template_version`；如果绑定 schema 不兼容，则创建新的逻辑模板 ID。一次导出在开始前解析精确版本，并在整个过程中固定该版本；插件更新不能让正在执行的请求中途切换模板。脚本保存的导出任务或自动化配置必须保存 `template_id + template_version`，不能只保存“当前模板”。

### 11.3 模板导入、扩展名识别和校验

导入流程如下：

```text
用户在“导出模板”页面选择模板文件
                ↓
宿主规范化并检查文件扩展名
                ↓
按扩展名查找唯一的 IExportTemplateHandler
                ↓
以只读流调用 ValidateAsync
                ↓
校验通过后由宿主根据 plugin_id + template_name 生成并校验 template_id，再持久化模板
                ↓
模板进入可用目录，脚本可以通过 exports.templates.list 查询
```

扩展名规则：

- 插件必须声明一个或多个模板文件扩展名，例如 `.xlsx`、`.docx`；
- 扩展名统一保存为全小写、以点号开头，比较时不区分大小写；
- 一个扩展名只能映射到一个模板插件；注册冲突时拒绝后加载的插件，不静默选择；
- 扩展名只用于选择校验/渲染插件，不代表模板已经有效；不能仅凭 `.xlsx` 或 `.docx` 判断文件是可用模板；
- 插件可以在扩展名之外检查文件签名、容器结构、MIME 和格式版本，防止用户把普通文件改名后绕过识别。

当前 XLSX 插件的第一版模板约定为隐藏/专用工作表 `__diary_template`：`A1` 为 `diary.export.template`，`A2` 为全小写 `snake_case` 模板名，`A3` 为模板版本，`A4/A5` 为显示名称和说明；从第 8 行开始依次声明 `key`、`kind`、`scalar_type`、`required`、`default_value`、`target` 和说明。宿主不直接理解这些地址，只保存插件校验返回的 schema；该约定属于 XLSX 插件实现，不是通用脚本协议。

`ValidateAsync` 是模板进入目录前的必要步骤。插件必须检查模板是否“可作为本插件的有效模板”，至少包括：

- 文件容器和格式结构是否正确；
- 必需的工作表、文档节点、命名区域、标记或其他模板锚点是否存在；
- 模板绑定是否完整、无重复、无歧义；
- 模板中的公式、宏、外部链接、嵌入对象和远程资源是否符合宿主安全策略；
- 模板声明的能力是否能由当前插件版本实际渲染；
- 模板大小、复杂度、最大重复区域和资源消耗是否在限制内。

校验结果至少包含：

- `is_valid`；
- `template_name`：用于与插件 ID 组合生成 `template_id`；
- 模板显示名称、说明和版本信息；
- `bindings`：完整的导出数据 schema；
- 支持的模板特性；
- 面向管理页面的结构化诊断信息。

校验失败的模板不得生成 `template_id`，不得进入脚本模板目录，也不得被导出调用。插件更新后，宿主可以重新校验已保存模板；重新校验失败时将模板标记为不可用，不删除模板文件，也不自动替换为其他模板。

### 11.4 是否需要模板管理页面

需要，而且在“不提供内置模板”的方案中，管理页面是模板进入系统的必要入口，而不是可选功能。它负责模板文件导入、校验、存储和生命周期管理；脚本 API 只负责发现模板、提交模板 ID、版本和导出数据。

建议页面放在“设置/导出模板”下，第一阶段提供：

- **导入模板**：选择本地模板文件，按扩展名匹配插件并显示校验进度；
- **列表和筛选**：按显示名称、`format_id`、模板扩展名、插件、状态和版本筛选；显示模板 ID、版本、文件扩展名、插件来源、绑定摘要和最近校验结果；
- **详情**：查看插件报告的模板信息、所需导出数据、类型、必填状态、空值策略、最大行数和支持特性；
- **校验诊断**：展示缺失工作表、无效绑定、格式不兼容、宏/外链等结构化错误；
- **生命周期操作**：启用、禁用、重新校验、归档、查看版本；
- **安全限制**：不显示或复制外部绝对路径，不开放脚本读取模板文件，不允许直接编辑模板 ID、扩展名、绑定键或模板二进制。

模板管理页面和运行时 `ExportTemplateCatalog` 共用同一持久化注册表：

```text
用户模板文件
    ↓
扩展名 → IExportTemplateHandler
    ↓ ValidateAsync
ExportTemplateCatalog
    ↙                 ↘
管理页面          IExportApi.ListTemplatesAsync()
                         ↓
             脚本提供 template_id + version + values/tables/documents
```

### 11.5 模板绑定和必填导出数据

模板需要哪些内容由插件对具体模板文件执行校验后返回，不能只由插件类型或文件扩展名推断。绑定 schema 使用语义键，不暴露单元格地址或文档内部路径：

```text
values.employee_name       -> scalar text, required
values.period              -> scalar text, default="current_month"
tables.overtime_items      -> table, required, allow_empty=false
documents.summary          -> document, optional
```

每个绑定至少声明：

- `key`：全小写 `snake_case` 语义键；
- `kind`：`scalar`、`table` 或 `document`；
- 标量类型、表格列定义或文档块定义；
- `required`：没有提供值且没有默认值时，是否必须存在；
- `default_value`：省略该绑定时由宿主填充的默认值，必须与绑定类型匹配；默认值属于 schema，不由脚本请求覆盖；
- 是否允许 `null`、空字符串或空集合；
- 最大长度、最大行数和其他资源限制；
- 面向脚本和管理页面的说明。

宿主在调用插件渲染前必须执行完整绑定校验：

1. 模板请求必须指定有效的 `template_id` 和精确 `template_version`；
2. 请求省略且 schema 提供 `default_value` 的绑定，由宿主先填充默认值；
3. 请求省略、没有默认值且 `required=true` 的绑定直接拒绝；
4. 请求省略、没有默认值且 `required=false` 的绑定按 schema 作为缺省/空值处理；
5. 未知绑定键直接拒绝；
6. 标量类型、表格列名/类型、日期/时间/`Duration` 编码必须匹配；
7. `null`、空字符串、空集合和最大行数必须符合 schema；
8. 模板声明不允许的合并、聚合、公式、样式或格式选项直接拒绝。

缺少数据或数据不符合 schema 时返回 `EXPORT_TEMPLATE_BINDING_INVALID`，并在结构化错误中列出缺少、未知、类型错误和超限的绑定键。默认值只对省略字段生效；脚本显式提供的值或显式 `null` 仍按正常类型和空值规则校验。宿主必须在调用插件前完成默认值填充和必填校验，不能调用插件后才发现数据不完整。

### 11.6 模板资源和安全

- 模板文件由宿主管理的模板库保存，插件只能通过宿主提供的只读流读取；
- 模板输出必须写入宿主分配的临时文件，成功后再登记 `FileId`；失败或取消时删除临时文件；
- 插件不得通过模板流反查宿主路径、数据库、UI 或脚本执行对象；
- 模板中的宏、外部链接、嵌入脚本、远程图片或任意外部资源第一阶段默认拒绝；
- XLSX/DOCX 模板中的保护、隐藏列、打印区域和样式只能在插件校验结果声明支持后保留；不支持的特性返回 `EXPORT_TEMPLATE_UNSUPPORTED`，不能静默丢弃；
- 模板校验器和渲染器必须支持取消，并限制文件大小、容器解压大小、重复行数和处理耗时；
- 当前插件属于同进程受信任代码，插件化不等于沙箱；不受信任插件或模板以后需要独立 Worker。

### 11.7 模板选择和执行流程

推荐流程：

1. 用户先在模板管理页面导入并校验模板；
2. 脚本调用 `exports.templates.list(format_id)`；
3. 脚本用 `ui.options.select` 让用户选择模板，或从手动参数中读取已经确认的 `template_id + template_version`；
4. 脚本根据模板 descriptor 组装 `TemplateExportSource`，提供全部必填 `values`/`tables`/`documents`；
5. 宿主校验模板状态、版本、绑定 schema、必填数据和格式能力；
6. 插件通过只读模板流校验上下文并渲染到宿主分配的输出路径；
7. 宿主登记 `FileId`，脚本可继续调用 `ui.exported_file.open`。

模板目录查询本身不需要 UI 作用域；目录选择、模板选择对话框、导出和询问打开仍遵循第 7 节有人值守策略。无人值守脚本可以使用预先固定且已验证的模板配置，但不能临时弹出模板选择或目录选择对话框。

## 12. 错误模型

建议复用现有脚本 API 错误分类，并增加以下稳定错误码：

| 错误码 | 含义 | 是否可重试 |
| --- | --- | --- |
| `SCRIPT_API_SCOPE_NOT_SUPPORTED` | 当前入口不允许交互/导出 | 否 |
| `UI_UNAVAILABLE` | 没有可用 UI 宿主 | 否或稍后重试 |
| `UI_DIALOG_INVALID_OPTIONS` | 选项对话框选项为空、重复或参数不合法 | 否 |
| `UI_DIALOG_UNEXPECTEDLY_CLOSED` | 必须回答的对话框被宿主异常关闭 | 否 |
| `DIRECTORY_INVALID` | 目录不存在或不可用 | 否 |
| `DIRECTORY_SELECTION_INVALID` | 目录选择令牌不存在、过期、跨执行或作用域不匹配 | 否 |
| `EXPORT_INVALID_REQUEST` | 导出请求不合法 | 否 |
| `EXPORT_TOO_LARGE` | 数据或文件超过限制 | 否 |
| `EXPORT_FAILED` | 目标格式生成或保存失败 | 视错误而定 |
| `EXPORT_UNSUPPORTED_FEATURE` | 目标格式不支持请求中的内容或特性 | 否 |
| `EXPORT_FORMAT_UNAVAILABLE` | 格式插件未加载、被阻止或处理器不可用 | 否 |
| `EXPORT_TEMPLATE_UNAVAILABLE` | 模板不存在、版本被撤回或插件不可用 | 否 |
| `EXPORT_TEMPLATE_BINDING_INVALID` | 模板绑定缺失、未知、类型不匹配或超出 schema | 否 |
| `EXPORT_TEMPLATE_UNSUPPORTED` | 模板或模板插件不支持请求的特性 | 否 |
| `EXPORTED_FILE_NOT_FOUND` | 导出文件不存在 | 否 |
| `EXPORTED_FILE_OPEN_FAILED` | 系统默认程序打开失败 | 否 |
| `HOSTCALL_NOT_FOUND` | HostCall 方法未注册 | 否 |
| `HOSTCALL_INVALID_ARGUMENT` | HostCall 请求结构或参数无法解析 | 否 |
| `HOSTCALL_SCOPE_NOT_SUPPORTED` | 当前执行入口不允许该 HostCall | 否 |
| `HOSTCALL_UNAVAILABLE` | HostCall 宿主服务不可用 | 视错误而定 |

“用户取消目录选择”通过 `PickDirectoryAsync()` 返回 `null`，再由脚本返回当前契约的 `ScriptExecutionResult.Cancelled()`；不包装成错误。目录选择令牌失效统一返回 `DIRECTORY_SELECTION_INVALID`，不与用户主动取消混淆。

“用户不打开结果文件”不是错误，应通过 `UserDeclined` 返回。`OperationCanceledException` 统一映射到当前脚本取消状态；目录、导出和打开 API 的宿主故障使用 `ScriptApiError`，不要另造未定义的 `Error` 字符串字段。

## 13. 幂等和重复导出

导出文件不是数据库副作用，不应复用日志项创建的 `IdempotencyKey` 语义。

默认同名文件不覆盖，而是自动生成编号文件名：

```text
加班明细-2026-07.xlsx
加班明细-2026-07 (1).xlsx
```

未来如果需要稳定重导，可以增加：

```csharp
OverwriteExisting = false;
ConflictMode = ExportConflictMode.AutoRename;
```

第一阶段只使用自动重命名，避免用户误覆盖已提交 OA 的文件。

## 14. 权限与审计边界

当前脚本系统不提供单独用户授权门禁，宿主只注册可用 API。但文件交互仍必须受以下边界约束：

- 交互 API 检查脚本入口和执行来源；
- 导出 API 检查文件名、目录和数据大小；
- 导入模板限制为 20 MiB、最多 2048 个压缩包条目和 100 MiB 解压总量；
- OpenXML 模板拒绝外部关系、宏、ActiveX、OLE 和嵌入对象，XLSX/DOCX 再检查危险公式或字段指令；
- 只有宿主生成的 `FileId` 可以请求打开；
- 不能从脚本直接打开任意系统文件；
- 导出失败、取消和打开失败写入脚本执行诊断；
- 日志不记录密码、Token 或完整敏感路径；
- 文件路径若需要记录，只记录文件名或脱敏后的目录摘要。

C# Worker 的静态限制不是完整安全沙箱，因此本设计不把文件写入能力交给 C# 引擎，而是通过宿主 HostCall 统一控制。

## 15. 测试设计

### 15.1 通用 API 契约测试

- C#、Lua、Python 能发现目录选择、格式目录和通用导出能力；
- `FormatId`、`DefaultExtension`、`AllowedExtensions` 和按内容类型的能力声明一致；
- `ExportRequest` 到 `content.kind` 的映射在三种 Worker 中一致；
- `DirectorySelectionId` 当前执行可用，跨执行、跨 Worker、过期、重启和取消后均被拒绝；
- 未注册 API 返回稳定的 API 不可用错误；
- 参数 JSON 在三种 Worker 中保持一致；
- `host.capabilities.list` 显示新增 HostCall；
- `ListFormatsAsync()` 的能力声明与处理器实际接受的能力一致；
- 协议级失败与业务级失败分别落在外层 `error` 和结果 `error`；
- 不支持的 `contentKind`、格式特性、格式选项和扩展名返回明确错误；
- 取消返回和错误分类一致；
- `RequireChoice` 和 `AllowCancel` 的 HostCall JSON/行为与 `DialogDismissPolicy` 一致。

### 15.2 交互测试

- 编辑器右键月目标能取得正确日期范围；
- 无数据时不显示目录选择框；
- 无数据时不调用导出和打开；
- 有数据时只显示一次目录选择框；
- 选项对话框返回选中的 `OptionId`，不返回显示文本作为协议值；
- `RequireChoice` 对话框没有右上角关闭、Escape 或遮罩层关闭路径；
- `AllowCancel` 对话框可以取消并返回取消结果；
- Application/Query 手动执行可以通过 `Arguments` 提供日期范围后使用导出 API；
- 用户取消目录后不生成文件；
- 用户拒绝打开文件时导出仍为成功；
- 打开失败不回滚已生成的文件；
- 无人值守自动化脚本调用交互 API 被拒绝；手动 Application/Query 脚本调用交互 API 被允许。

### 15.3 XLSX 输出测试

- 标题合并正确；
- 表头和标题背景色正确；
- 中文不乱码；
- 日期、时间和小数类型正确；
- 合计公式引用实际数据行；
- 空数据不产生空文件；
- 同名文件自动编号；
- 非法文件名和路径穿越被拒绝；
- 文件无法写入时返回 `EXPORT_FAILED`；
- 生成的 XLSX 文件可以被 Excel 或 LibreOffice 打开。

### 15.4 CSV 输出测试

- UTF-8 BOM、CRLF 和中文内容正确；
- 逗号、双引号和换行正确转义；
- `null`、日期、时间和 Decimal 格式稳定；
- 标题、表头和计算后的 `Sum` 合计行位置正确；
- 合并、视觉样式和未知格式选项返回 `EXPORT_UNSUPPORTED_FEATURE` 或 `EXPORT_INVALID_REQUEST`；
- 同名文件和扩展名校验正确；
- 有标题和无标题时的 CSV 行布局分别正确；
- `style=default` 成功，`style=compact/report` 返回 `EXPORT_UNSUPPORTED_FEATURE`；
- 未知 CSV 选项返回 `EXPORT_INVALID_REQUEST`；以 `=`, `+`, `-`, `@` 开头的文本不会被导出为可执行公式。
- CSV 文本模板按字段解析后再插值，插入值中的逗号、双引号、CR/LF 和公式前缀会重新转义，重复绑定或无效引号结构在导入阶段拒绝。

### 15.5 DOCX 输出测试

- `document` 内容可以包含标题、段落和表格块；
- `docx/table` 和 `docx/document` 的能力声明与处理器一致；
- 表格合并、中文字体、空文档和空表格处理正确；
- 聚合输出为计算后的显示值，不生成伪公式；
- 生成文件可以被 Word 或 LibreOffice 打开；
- 普通文档导出与 OA 模板导出边界清晰；
- `docx/table` 与 `docx/document` 的能力目录和处理器一致。

### 15.6 模板导出测试

- 模板目录只返回注册表中已启用模板的 `template_id`、精确版本、模板扩展名和绑定 schema，不返回模板路径；
- 模板文件按扩展名匹配唯一插件，扩展名冲突时拒绝插件注册；
- 模板导入必须调用插件 `ValidateAsync`，校验失败不生成 `template_id`、不进入可用目录；
- 校验结果能够发现无效结构、缺失锚点、超限压缩包、外部关系、宏/ActiveX/OLE/嵌入对象和不支持的模板能力，并返回结构化诊断；
- 通用 `Content` 请求不需要模板也能成功，模板缺失不能影响通用导出；
- 模板请求缺少 `template_id`、版本、必填绑定或包含未知绑定键时返回 `EXPORT_TEMPLATE_BINDING_INVALID`；
- 模板版本不可用时返回 `EXPORT_TEMPLATE_UNAVAILABLE`，不自动回退到最新版本或通用导出；
- 标量、表格、文档绑定按 schema 正确写入，Duration、日期、时间和中文保持类型/编码正确；
- 缺少没有默认值的必填绑定、出现未知绑定或数据类型/空值/行数不符合 schema 时，在调用渲染器前返回 `EXPORT_TEMPLATE_BINDING_INVALID`；
- 省略带默认值的绑定时，宿主使用 schema 中的默认值；显式传入的值不被默认值覆盖；
- 模板声明的重复表格区域、最大行数、合并和保护区域约束有效；
- XLSX 外部工作簿引用及 `WEBSERVICE`/`FILTERXML`/`HYPERLINK`/`RTD`/`DDE` 等危险公式、DOCX 外部字段指令，以及 OpenXML 外部关系、宏和嵌入对象返回 `EXPORT_TEMPLATE_UNSUPPORTED`；
- 模板处理失败、取消或 Worker 终止时清理临时文件，不登记 `FileId`；
- 同一插件同时提供通用处理器和模板处理器时，`exports.formats.list` 与 `exports.templates.list` 的能力描述一致。

### 15.7 数据场景测试

- 本月有单条加班记录；
- 本月有多条加班记录；
- 同一天多条记录；
- 加班标签不存在；
- 记录包含附加字段；
- 备注为空；
- 耗时为小数；
- 迁移导入只读工作项只参与查询，不被脚本修改；
- 查询期间工作项数据变化时，脚本仍能完成或收到可诊断错误。

## 16. 实施阶段

### 阶段一：通用导出核心和 XLSX 表格

1. 增加 `IExportApi`、通用 `ExportRequest`/`ExportContent`/`ExportResult` 和格式目录；
2. 增加 `SelectOptionAsync()`/`ui.options.select`、`PickDirectoryAsync()`、`exports.formats.list`、`exports.export` 和 `ui.exported_file.open`；
3. 增加 `XlsxTableExportHandler`，由 ClosedXML 生成标准明细表；
4. C#、Lua、Python Worker 增加通用导出代理和门面；
5. 编写加班明细导出示例脚本；
6. 同步 CSharp/Lua/Python API 文档。

### 阶段二：导出插件契约和通用 XLSX 迁移

1. [x] 增加模板 `IExportPlugin`/`IExportTemplateHandler` 契约、按扩展名发现和 XLSX 模板插件；
2. [x] 增加宿主模板目录，导入时调用插件校验，按 `plugin_id.template_name` 生成并校验 `template_id`；
3. [x] 增加绑定 schema、默认值填充、必填数据校验和 `exports.templates.list` Worker 代理；
4. [x] 增加导出模板管理页面，支持导入、重新校验、启用/禁用和归档；
5. [x] 将通用 XLSX 处理器迁移到统一导出插件注册表，保留后台生成语义，并增加 CSV、DOCX 插件；
6. [x] 增加 OpenXML 模板大小、压缩包膨胀、外部关系、宏和嵌入对象校验，并为 XLSX/DOCX 增加危险公式与字段指令检查。

### 阶段三：CSV 表格导出

1. [x] 增加 `CsvTableExportHandler`；
2. [x] 实现并验证 UTF-8 BOM、表头、分隔符、引号和 CRLF 换行策略；
3. [x] 对 CSV 不支持的合并、背景色和原始公式返回结构化错误，并执行公式注入防护；
4. [x] 增加 CSV 文件内容、中文兼容性和模板导入渲染测试，覆盖模板插值后的逗号、双引号、换行和公式前缀转义。

### 阶段四：DOCX 文档和模板导出

1. [x] 落地 `ExportDocumentContent` 和文档块模型；
2. [x] 注册 DOCX 通用处理器，支持表格、段落、标题、合计和表格合并；
3. [x] 为 DOCX 插件增加 `.docx` 模板扩展名、校验器、binding schema 生成和占位符渲染器；
4. [x] 通过统一 `exports.templates.list` 和模板绑定校验暴露 DOCX 模板；
5. [x] 保留导入模板的原有样式和文档结构；
6. [x] 增加 DOCX 文件结构、模板渲染和危险外部字段拒绝测试；更完整的跨 Word 版本兼容矩阵仍作为后续增强。

### 阶段五：批量和大数据导出

1. 增加分块导出 HostCall；
2. 增加分块导出、跨阶段取消和大文件临时文件清理；
3. 增加大数据量内存和耗时限制；
4. 增加导出进度报告；
5. 增加失败恢复和临时文件清理测试。

## 17. 验收标准

完成第一阶段后，用户可以：

1. 在日历中右键选择本月运行加班导出脚本；
2. 脚本自动获得本月日期范围；
3. 脚本筛选并整理加班数据；
4. 无加班数据时直接结束，不弹任何询问；
5. 有数据时选择导出目录；
6. 得到包含标题、表头、明细、背景色和合计公式的 `.xlsx` 文件；
7. 导出后选择打开或不打开文件；
8. 取消目录选择不会产生错误文件；
9. 不需要给脚本开放任意文件读写权限；
10. 后续增加 CSV 或 DOCX 时，不需要修改脚本的目录选择、结果打开和执行作用域模型；
11. 后续可以在不改变脚本查询逻辑的情况下接入 OA 固定模板；没有模板时通用导出仍然可独立使用。
