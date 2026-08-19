# 导出脚本 API 用户视角审查记录

## 1. 文档信息

- 审查日期：2026-08-19
- 审查范围：脚本交互式导出 API、XLSX/CSV/DOCX 导出插件、模板导出契约及 C#/Lua/Python 文档
- 审查方式：独立 Agent 用户视角审查，结合实现代码、设计文档和三语言 API 文档交叉核对
- 文档状态：审查问题已于 2026-08-19 完成修复；保留原始问题和建议作为决策记录
- 关联设计：[ScriptSpreadsheetExportDesign.md](ScriptSpreadsheetExportDesign.md)

## 2. 总体结论

导出 API 的基础方向是合理的：脚本不能直接写入任意路径，目录选择通过短期令牌完成，导出文件通过 `file_id` 管理；格式和模板支持动态发现；XLSX、CSV、DOCX 共享类型化表格模型，也具备基础的取消、无数据和打开失败处理。

审查时的主要问题集中在三个方面：

1. 用户按照文档传参时，存在实际不能工作的契约不一致。
2. 错误信息没有保留足够的行号、列名或模板绑定键，用户难以修正脚本。
3. 某些公开字段和能力会被静默忽略，模板表格绑定也容易被理解成“循环复制模板行”。

## 3. 问题清单

优先级说明：

- **高**：可能直接导致用户无法完成导出，或导致错误数据。
- **中高**：可以导出，但错误提示、重试判断或结果可靠性明显不足。
- **中**：主要影响可发现性、预期一致性和后续维护成本。

### 3.1 已修复：XLSX 工作表名称参数与 wire 契约不一致

设计文档和语言文档采用 `snake_case`，用户自然会传入：

```json
{
  "format_options": {
    "values": {
      "sheet_name": "加班明细"
    }
  }
}
```

修复前，XLSX 处理器只识别大小写敏感的 `sheetName`。因此用户按照公共命名约定使用 `sheet_name` 时，会得到格式选项无效错误。格式描述符目前仍没有返回选项 schema，用户无法通过 `list_formats` 发现正确键名。

证据：

- [XlsxTableExportHandler.cs](../Diary.Export.Xlsx/XlsxTableExportHandler.cs) 的格式选项解析
- [ScriptExportApi.cs](../Diary.ScriptHost/ScriptExportApi.cs) 的 JSON 命名策略
- [ScriptSpreadsheetExportDesign.md](ScriptSpreadsheetExportDesign.md) 中的 `sheet_name` 示例

处理结果（2026-08-19）：

1. wire 层正式键名已统一为区分大小写的 `sheet_name`。
2. 由于尚未创建模板或形成外部脚本兼容负担，不兼容 `sheetName`；旧键会返回 `EXPORT_FORMAT_OPTION_UNKNOWN`。
3. 已增加处理器回归测试，覆盖 `sheet_name` 生效和 `sheetName` 被拒绝。
4. 格式 descriptor 已增加选项 schema，并补充共享导出参考和 C#、Lua、Python 可运行示例。

### 3.2 已修复：无效单元格值被误报为可重试的宿主故障

当脚本把无法转换的值写入类型化列时，例如把 `"abc"` 写入 `integer` 或把格式错误的日期写入 `date`，底层转换逻辑已经能得到行号和列名。但服务层的通用异常处理会把它转换成类似下面的结果：

```text
EXPORT_FAILED
category = Host
retryable = true
```

用户看不到应该修改哪一行，也会误以为稍后重试即可解决。XLSX、CSV、DOCX 都会受到影响。

证据：

- [ScriptExportApi.cs](../Diary.ScriptHost/ScriptExportApi.cs) 的值规范化逻辑
- [ScriptExportService.cs](../Diary.App/Services/ScriptExportService.cs) 的异常映射
- [XlsxTableExportHandler.cs](../Diary.Export.Xlsx/XlsxTableExportHandler.cs)、[CsvExportPlugin.cs](../Diary.Export.Csv/CsvExportPlugin.cs)、[DocxExportPlugin.cs](../Diary.Export.Docx/DocxExportPlugin.cs) 的列值转换调用

修改建议：

1. 在通用异常捕获之前单独处理值格式错误。
2. 返回稳定的非重试错误码，例如 `EXPORT_VALUE_INVALID`。
3. 在 `error.details` 中保留 `row`、`column`、`expected_type` 和原始值是否为空等诊断信息。
4. 文档明确区分“修改输入后重试”和“宿主故障后重试”。

### 3.3 已修复：重复列名可能造成合计静默计算错误

当前列名不是唯一键。若表格有两个同名列，例如两个“金额”列，聚合配置按名称查找时会选择第一个匹配列。导出可能成功，但合计的是错误列，属于静默数据错误。

证据：

- [ScriptExportApi.cs](../Diary.ScriptHost/ScriptExportApi.cs) 的共享表格校验
- 三个格式处理器的聚合列查找逻辑：
  [XlsxTableExportHandler.cs](../Diary.Export.Xlsx/XlsxTableExportHandler.cs)、
  [CsvExportPlugin.cs](../Diary.Export.Csv/CsvExportPlugin.cs)、
  [DocxExportPlugin.cs](../Diary.Export.Docx/DocxExportPlugin.cs)

修改建议：

1. 短期在共享校验器中禁止忽略大小写后的重复列名。
2. 长期为列增加稳定的 `key`，聚合引用 `key`，列名只负责显示。
3. 对空列名、重复列名和不存在的聚合列分别返回不同诊断码。

### 3.4 已修复：模板绑定诊断生成后没有完整返回

模板绑定校验器已经能够生成诊断码和 `BindingKey`，但服务层只拼接通用消息，最终用户可能只看到：

```text
缺少必填模板数据。
```

当缺少多个绑定或类型不匹配时，用户无法知道具体应该补充哪个键，也无法可靠地在脚本中定位错误。

修改建议：

保留稳定的顶层错误码 `EXPORT_TEMPLATE_BINDING_INVALID`，同时在 `error.details.diagnostics` 返回结构化诊断：

```json
{
  "code": "EXPORT_TEMPLATE_BINDING_REQUIRED",
  "binding_key": "customer_name",
  "message": "缺少必填模板数据。"
}
```

三语言文档也应展示如何遍历这些诊断，而不是只打印顶层消息。

### 3.5 已修复：模板表格绑定与“循环模板行”之间的语义没有说明清楚

公开模型中的 `tables` 容易让用户期待如下能力：模板中定义一行样板，然后对数组循环，复制每个元素的内容、样式和公式。

当前 XLSX 实现更接近“从锚点写入一个二维表格”：在目标位置写入表头和数据行，并不等同于完整的模板行复制。文档没有明确说明以下行为：

- 锚点单元格代表表头起点、数据起点还是模板区域起点；
- 是否会生成表头；
- 是否会插入新行或覆盖已有内容；
- 是否复制模板行的样式、公式、行高和合并关系；
- 数据行数变化时，锚点下面原有内容如何处理；
- XLSX、CSV、DOCX 分别支持哪些 binding kind。

修改建议：

1. 如果当前只支持二维区域写入，应在文档中明确称为 `table binding`，不要描述成循环模板行。
2. 如果需要逐行复制模板，应另行设计 `repeat row binding`，明确样板行、数组路径、样式/公式复制和冲突处理规则。
3. 增加模板导出的端到端示例，从模板发现、schema 查看、数据绑定到导出和打开文件完整演示。

### 3.6 已修复：部分公开字段和 capability 会被静默忽略

当前有些字段看起来可以配置，但实际输出不会使用：

- `style=compact/report` 在部分处理器中没有生效；
- `ExportAggregateColumn.Label` 未被使用，输出统一为“合计”；
- XLSX descriptor 宣称有 `BackgroundColor` 能力，但公共请求模型没有对应颜色字段；
- DOCX 的基础样式声明与实际可配置字段不完全对应。

这种行为比直接拒绝更难排查，因为脚本看起来执行成功，用户却发现配置没有效果。

修改建议：

1. 已公开且有意义的字段应实现并增加回归测试。
2. 暂不支持的字段应从公共契约移除，或在请求校验阶段明确拒绝。
3. capability 应区分“处理器内部使用的效果”和“脚本可以配置的能力”。
4. `list_formats` 的结果应能帮助用户决定哪些字段可以安全发送。

### 3.7 已修复：三语言文档没有完整说明导出失败模型

目前文档存在以下不一致：

- Lua/Python 关于“失败返回结果而不是抛异常”的 API 列表没有明确包含 `exports.export`；
- 导出示例基本只有成功分支；
- C# 通用错误章节使用 `ApiError.Code` 的写法，但 `ExportResult` 实际字段是 `Error`。

建议为三种语言统一说明：

```text
result.success == true  -> 使用 result.file_id、result.file_name、result.item_count
result.success == false -> 使用 result.error.code/message/category/retryable/details
```

同时说明 Wire 参数无法反序列化、HostCall 通道错误等异常与普通导出失败的区别。

### 3.8 已修复：目录令牌和文件 ID 的生命周期没有写入用户文档

`DirectorySelectionId` 和导出后的 `FileId` 实际绑定当前 `ExecutionId`、`WorkerId`，并且只有短期有效。它们不能当作跨脚本执行的持久化资源。

文档应明确推荐在同一次执行中完成：

```text
选择目录 → 导出 → 处理 ExportResult → 询问是否打开 → 打开文件
```

过期或跨执行使用时，应提示用户重新选择目录或重新导出，而不是继续复用旧 ID。

## 4. 文档补充计划

### 4.1 共享导出模型参考

建议在 `Docs/ScriptApi` 下增加共享章节，统一描述：

- `ExportRequest`、`ExportResult` 和 `ScriptApiError`；
- `table`、`document`、`values`、`tables`、`documents` 的结构；
- 列类型、空值、日期时间、Duration 和聚合；
- 合并单元格坐标、边界和 `item_count` 语义；
- 目录令牌、文件 ID 和生命周期。

三语言文档只补充语言特有的调用语法和字段命名，避免三份文档再次出现契约漂移。

### 4.2 格式能力矩阵

建议把 `list_formats` 的结果整理成用户可读的能力矩阵，至少覆盖：

| 能力 | XLSX | CSV | DOCX | 是否可配置 |
| --- | --- | --- | --- | --- |
| 表格导出 | 是 | 是 | 是 | 是 |
| 文档块导出 | 否/待确认 | 否/待确认 | 是 | 是 |
| 合并单元格 | 是 | 否 | 是 | 是 |
| 合计 | 是 | 是 | 是 | 是 |
| 模板标量绑定 | 是 | 是 | 是 | 按模板 descriptor |
| 模板多行表格绑定 | 是，锚点二维区域写入 | 否 | 否 | 按模板 descriptor |
| 用户可配置样式 | `default/compact/report` | 仅 `default` | `default/compact/report` | 按 capability |
| `number_format` | 是 | 否 | 否 | 按 capability |
| 格式选项 schema | `sheet_name` | 空 | 空 | 是 |

当前没有插件声明 `document` 模板绑定。XLSX 的 table binding 不插入行，也不复制样板行样式、公式、行高或合并关系。

### 4.3 必补示例

建议按以下顺序补充：

1. 三语言完整 XLSX 表格导出：格式发现、目录取消、类型化列、失败分支、`retryable` 判断和打开文件。
2. CSV 表格导出：UTF-8 BOM、公式注入防护、合计和格式限制。
3. DOCX 文档导出：标题、段落、表格、合并和文档块。
4. 模板端到端导出：模板发现、版本/schema、`values`/`tables`/`documents`、默认值、缺失绑定、类型错误和打开结果。

## 5. 推荐修复顺序与验收标准

### 第一阶段：修正会导致失败或错误数据的问题

- `sheet_name` 为唯一正式键且 `sheetName` 被拒绝，相关行为有测试覆盖；
- 非法值返回非重试的结构化错误，并带行号、列名和目标类型；
- 重复列名在共享校验阶段被拒绝；
- 聚合引用不存在、类型不支持时返回明确错误。

### 第二阶段：修正模板和能力边界

- 模板诊断返回 `code`、`binding_key`、`message`；
- 文档明确 `table binding` 是否复制模板行；
- 每种格式的 binding kind、样式、合并和格式选项均有能力矩阵；
- 被忽略的字段要么实现，要么拒绝，要么从契约移除。

### 第三阶段：补齐用户文档和示例

- C#、Lua、Python 均有可复制运行的成功和失败示例；
- 示例使用当前真实字段名，不依赖未公开的内部 DTO；
- 文档说明目录令牌和文件 ID 只能在规定生命周期内使用；
- 相关 API 测试、设计文档和 `Docs/TODOS.md` 的完成状态同步更新。

## 6. 修复完成记录

- 非法值统一返回非重试的 `EXPORT_VALUE_INVALID`，`details` 包含 `row`、`column`、`expected_type`、`value_was_null`。
- 空列名、重复列名、聚合列不存在、类型不支持、标签冲突和缺少标签列均有独立错误码。
- 模板绑定失败在 `details.diagnostics` 返回完整 `code`、`binding_key`、`message` 数组。
- `ExportAggregateColumn.Label` 已在 XLSX、CSV、DOCX 生效；XLSX/DOCX 已实现 `compact/report`，CSV 和不支持的 `number_format` 会明确拒绝。
- XLSX descriptor 已移除不可由脚本配置的 `background_color`，并增加 `number_format` 与 `sheet_name` schema。
- 新增 [ScriptApi/Export.md](ScriptApi/Export.md) 作为共享契约，三语言文档补充成功、失败、诊断遍历和生命周期说明。
- 新增加班明细导出示例：[C#](ScriptApi/Examples/OvertimeExport.cs)、[Lua](ScriptApi/Examples/OvertimeExport.lua)、[Python](ScriptApi/Examples/OvertimeExport.py)。

## 7. 相关文件

- [ScriptSpreadsheetExportDesign.md](ScriptSpreadsheetExportDesign.md)
- [ScriptApi/CSharp.md](ScriptApi/CSharp.md)
- [ScriptApi/Lua.md](ScriptApi/Lua.md)
- [ScriptApi/Python.md](ScriptApi/Python.md)
- [ScriptApi/Export.md](ScriptApi/Export.md)
- [TODOS.md](TODOS.md)

