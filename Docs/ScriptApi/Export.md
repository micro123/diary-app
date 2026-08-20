# 脚本导出 API 共享参考

本文是 C#、Lua、Python 共用的导出契约。语言文档只说明各自调用语法；wire 字段、错误码、格式能力和生命周期以本文为准。

## 1. 推荐调用流程

真实交互式导出只允许有人值守的 `Editor+Editor` 和 `Application+Manual` 执行。`Query+Manual` 是只读入口，只能在 Preview / `validate_only` 下执行导出预检：

```text
list_formats / list_templates
    → pick_directory
    → exports.export
    → 检查 ExportResult
    → ask_to_open_exported_file
```

目录选择令牌和导出文件 ID 都绑定当前 `execution_id` 与 `worker_id`，有效期为 10 分钟。它们不能保存后跨执行复用。收到 `DIRECTORY_SELECTION_INVALID` 时应重新选择目录；文件 ID 失效时应重新导出，不应猜测或持久化宿主路径。

## 2. ExportRequest

wire JSON 使用全小写 `snake_case`：

```json
{
  "format_id": "xlsx",
  "directory_selection_id": "short_lived_token",
  "file_name": "report.xlsx",
  "validate_only": false,
  "format_options": {
    "format_id": "xlsx",
    "values": {
      "sheet_name": "加班明细"
    }
  },
  "content": {
    "kind": "table",
    "title": "加班报告",
    "columns": [
      { "name": "日期", "type": "date" },
      { "name": "时长", "type": "duration", "number_format": "[h]:mm:ss" }
    ],
    "rows": [
      ["2026-08-18", "02:30:00"]
    ],
    "aggregates": [
      { "column_name": "时长", "aggregation": "sum", "label": "总时长" }
    ],
    "merges": [],
    "style": "report"
  }
}
```

`content` 与 `template` 必须且只能提供一个。模板导出不接受通用 `format_options`，格式选项只作用于非模板导出。`validate_only=true` 时目录令牌可以为空；宿主仍执行格式、内容、模板版本和绑定校验，但不调用渲染插件、不创建文件或 FileId。管理页 Preview 会自动强制该字段，并为目录选择返回虚拟令牌。

### 2.1 表格

- 列名不能为空，并且忽略大小写后必须唯一；重复列名返回 `EXPORT_COLUMN_NAME_DUPLICATE`。
- 每行单元格数量必须与列数量一致。
- 支持的列类型：`text`、`integer`、`decimal`、`date`、`time`、`duration`、`date_time`、`boolean`。
- `date` 使用 `yyyy-MM-dd`，`time` 使用 `HH:mm:ss`，`duration` 使用秒数或 `HH:mm:ss`，`date_time` 使用可解析且包含时区语义的时间文本。
- `sum` 只支持 `integer`、`decimal`、`duration`；不存在的列和不支持的类型分别返回 `EXPORT_AGGREGATE_COLUMN_NOT_FOUND`、`EXPORT_AGGREGATE_TYPE_UNSUPPORTED`。
- `label` 是整条合计行的共享标签，写入第一个未参与聚合的列，缺省为“合计”。同一表格中的多个非空标签必须完全一致，否则返回 `EXPORT_AGGREGATE_LABEL_CONFLICT`；所有列都参与聚合而没有标签列时返回 `EXPORT_AGGREGATE_LABEL_COLUMN_MISSING`。
- 合并坐标从 1 开始，只覆盖数据行，不包含标题、表头和合计行；区域不能越界或重叠。
- `number_format` 目前只由 XLSX 支持；其他格式会返回 `EXPORT_UNSUPPORTED_FEATURE`，不会静默忽略。
- `style` 支持 `default`、`compact`、`report`。XLSX 和 DOCX 会应用对应的标题、表头、合计和字号样式；CSV 只接受 `default`。

### 2.2 文档

`document` 内容目前只由 DOCX 支持，可包含 `heading`、`paragraph`、`table` 块。文档 `style` 会影响标题和段落，并作为未显式设置样式的内嵌表格的默认样式。

## 3. ExportResult 与失败模型

普通导出失败返回结果，不因业务校验失败抛异常：

```text
succeeded = true, validated_only = false → file_id、file_name、item_count 可用
succeeded = true, validated_only = true  → 预检通过；file_id/file_name 为空
succeeded = false                         → error.code/message/category/retryable/details 可用
```

示例错误：

```json
{
  "succeeded": false,
  "error": {
    "code": "EXPORT_VALUE_INVALID",
    "message": "第 2 行的“数量”值无法转换为 Integer。",
    "category": "validation",
    "retryable": false,
    "details": {
      "row": 2,
      "column": "数量",
      "expected_type": "integer",
      "value_was_null": false
    }
  }
}
```

`retryable=false` 的 Validation 错误必须先修改请求；重复提交相同数据不会自行恢复。`retryable=true` 的 Host/Provider 错误才表示宿主或底层服务可能稍后恢复。Wire 反序列化失败、未知 HostCall、通道中断、Worker 终止和脚本运行时异常不属于普通 `ExportResult` 失败，语言运行时会按 HostCall 或执行异常报告。

模板绑定失败保持顶层错误码 `EXPORT_TEMPLATE_BINDING_INVALID`，并在 `details.diagnostics` 返回完整数组：

```json
{
  "code": "EXPORT_TEMPLATE_REQUIRED_BINDING_MISSING",
  "binding_key": "customer_name",
  "message": "缺少必填模板数据。"
}
```

脚本应遍历全部诊断，不要只显示顶层消息。

## 4. 格式发现和能力矩阵

`list_formats` 返回每个格式的 `content_capabilities`、`supports_templates` 和 `format_options`。只有 capability 或格式选项 schema 明确声明的字段才可发送。

| 能力 | XLSX | CSV | DOCX | Mustache |
| --- | --- | --- | --- | --- |
| `table` | 是 | 是 | 是 | 仅模板上下文 |
| `document` | 否 | 否 | 是 | 否 |
| 类型化值 | 是 | 是 | 是 | 转换为文本 |
| `number_format` | 是 | 否 | 否 | 否 |
| `compact/report` | 是 | 否，非默认值会拒绝 | 是 | 否 |
| 合并单元格 | 是 | 否 | 是 | 否 |
| `sum` 合计和标签 | 是 | 是 | 是 | 由脚本计算 |
| 格式选项 | `sheet_name` | 无 | 无 | 无 |
| 模板绑定 | scalar、table | scalar、table | scalar、table | context |

XLSX descriptor 不声明 `background_color`：插件内部预设颜色不等于脚本可以提交任意颜色。

`sheet_name` 是区分大小写的唯一正式键，缺省为“明细”；不支持 `sheetName`。非法工作表字符会被移除，名称最长 31 个字符。

## 5. 模板发现和绑定

脚本必须先调用 `list_templates(format_id)`，再按模板 descriptor 的精确 `template_id`、`template_version` 和 `bindings` 组装请求。不要根据扩展名猜测绑定能力。

普通用户制作 Excel、CSV 和 Word 模板的步骤与示例见 [ExportTemplateGuide.md](../ExportTemplateGuide.md)。
Mustache 纯文本模板的语法、数据映射和请求示例见 [MustacheExport.md](MustacheExport.md)。

```json
{
  "format_id": "xlsx",
  "directory_selection_id": "short_lived_token",
  "file_name": "report.xlsx",
  "template": {
    "template_id": "xlsx.overtime_report",
    "template_version": "1.0.0",
    "values": {
      "period": "2026-08"
    },
    "tables": {
      "overtime_items": {
        "kind": "table",
        "columns": [
          { "name": "日期", "type": "date" },
          { "name": "时长", "type": "duration" }
        ],
        "rows": [["2026-08-18", "02:30:00"]]
      }
    },
    "documents": {}
  }
}
```

当前 XLSX、CSV、DOCX 简易模板能力：

- 三种格式都支持 `scalar` 和 `table`。
- `{{变量名}}` 是标量替换；`{{items.字段}}` 是按模板行展开；`{{items.字段|column}}` 是按模板列展开；`{{items|matrix}}` 是按表格的 M 行 × N 列展开矩阵。
- 矩阵标记会占据一个区域，表格的 `Rows` 提供 M，`Columns` 提供 N；矩阵前后可以保留模板中的固定行，因此支持固定表头、矩阵数据、固定表底。
- 行循环和列循环区域不能混用不同的表格绑定；CSV 同一行不能同时使用两种循环方向，DOCX 同一表格行也不能同时使用两种循环方向。
- 模板名称由导入文件名推断，模板版本固定为 `1.0.0`；模板正文不再包含元数据头或专用元数据工作表。
- 当前没有插件声明 `document` 模板绑定。

Mustache 是独立的纯文本模板格式，使用 `.mustache` 模板和标准 Mustache 核心语法。输出默认扩展名为 `.txt`，也允许 `.md`、`.html` 和 `.csv`；区块绑定使用 `context`，可以由 `values` 中的标量、布尔值或对象提供，也可以由 `tables` 中的列表提供。

### 5.1 简易标记模板语义

模板文件只需要在正文中放置标记，脚本负责准备 `values` 和 `tables` 数据。例如：

```text
{{period}}
姓名,工时
{{items.name}},{{items.hours}}
横向,{{items.name|column}}
```

- 变量名、集合名和字段名使用小写字母、数字和下划线；未提供的标量替换为空；
- `{{items.field}}` 会复制所在的 XLSX 行、CSV 行或 DOCX 表格行，并按表格数据逐项替换；
- `{{items.field|column}}` 会复制所在的 XLSX 列、CSV 字段或 DOCX 表格单元格，并按表格数据逐项替换；
- `{{items|matrix}}` 必须独占一个 XLSX 单元格、CSV 行或 DOCX 表格行，使用同一个表格绑定的 `Rows × Columns` 一次性展开；矩阵区域前后的固定内容会保留；
- XLSX/DOCX 会尽量保留复制区域的样式；DOCX 循环标记不能放在合并单元格中；
- 模板至少要有一个标记；不存在的表格字段、缺失表格绑定和不匹配的循环方向会在导出时返回结构化绑定错误；
- XLSX 同一工作表暂不同时展开行循环和列循环；可以将两种布局放在不同工作表中。
- 矩阵可以在独立工作表中使用，也可以和固定内容放在同一工作表；矩阵区域暂不与同一工作表中的行循环/列循环混用。

若未来需要按样板行复制样式和公式，应使用独立的 repeat-row 契约，不能改变现有 table binding 的含义。

## 6. `item_count`

- 通用表格导出：数据行数，不包含标题、表头和合计行。
- DOCX document：文档块数量。
- 模板导出：由模板处理器返回；XLSX table binding 为写入的数据行总数，标量模板通常返回替换或输出项数量。
