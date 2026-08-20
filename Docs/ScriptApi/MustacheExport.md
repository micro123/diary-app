# Mustache 文本数据模板

Mustache 导出器是独立的纯文本模板插件，不改变 XLSX、CSV、DOCX 的简易标记协议。模板文件扩展名为 `.mustache`，输出默认使用 `.txt`，也可以指定 `.md`、`.html` 或 `.csv`。

## 支持的语法

```mustache
{{title}}

{{#items}}
{{name}},{{id}},{{description}}
{{/items}}

{{^items}}
没有数据
{{/items}}
```

支持：

- `{{name}}`：变量，默认执行 HTML 转义；
- `{{{name}}}`、`{{& name}}`：不转义变量；
- `{{user.name}}`：点号路径；
- `{{#items}}...{{/items}}`：列表、对象或布尔真值区块；
- `{{^items}}...{{/items}}`：空列表、缺失值或布尔假值区块；
- `{{.}}`：当前上下文值；
- `{{! comment}}`：注释。

当前不支持局部模板 `{{> partial}}`、Lambda 和自定义分隔符。模板导入时会检查区块是否正确嵌套和闭合。

未提供的变量按空字符串处理，未提供的普通区块不输出内容，未提供的反向区块会输出内容，与 Mustache 的缺失值语义一致。

## 数据映射

`template.values` 直接成为 Mustache 根上下文。`template.tables.items` 会转换成对象数组，每行对象使用列名作为字段：

```mustache
{{#items}}
姓名：{{name}}
工时：{{hours}}
{{/items}}
```

每个表格行对象还包含 `cells` 数组，可以输出动态列数的 M×N 数据：

```mustache
{{#items}}
{{#cells}}{{.}},{{/cells}}
{{/items}}
```

根上下文自动提供 `item_count`，其值为本次请求中所有表格绑定的数据行总数。

## 脚本请求

先通过 `list_templates("mustache")` 获取模板 ID 和绑定，再执行导出：

```json
{
  "format_id": "mustache",
  "directory_selection_id": "short_lived_token",
  "file_name": "report.md",
  "template": {
    "template_id": "mustache.work_report",
    "template_version": "1.0.0",
    "values": {
      "title": "加班报告",
      "show_details": true
    },
    "tables": {
      "items": {
        "kind": "table",
        "columns": [
          { "name": "name", "type": "text" },
          { "name": "hours", "type": "decimal" }
        ],
        "rows": [
          ["唐国利", 2.5]
        ]
      }
    }
  }
}
```

Mustache 模板使用 `context` 绑定描述区块或变量所需的动态上下文。该绑定可以由 `values` 中的标量、布尔值或对象提供，也可以由 `tables` 中的列表提供，但同一个键不能同时出现在两处。
