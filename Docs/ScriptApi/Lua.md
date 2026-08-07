# Lua 脚本 API 参考

## 脚本入口

Lua 脚本必须定义同步函数 `main(context)` 或 `execute(context)`，返回值可省略。

```lua
function main(context)
    local result = diary.workItems.query({
        startDate = "2026-08-01",
        endDate = "2026-08-31",
        limit = 100
    })

    if not result.succeeded then
        error(result.error.message)
    end
end
```

## 执行上下文

`context` 是包含以下字段的 Lua 表：

| 字段 | 含义 |
| --- | --- |
| `request` | 完整的、可转换为 JSON 的执行请求。 |
| `arguments` | 执行参数表；未传参数时为空表。 |

请求使用 camelCase 字段名。编辑器日期范围位于 `context.request.target.editor`，其中包含 `startDate`、`endDate` 和 `granularity`；执行来源位于 `context.request.source`。

## 查询工作项

全局函数 `diary.workItems.query(params)` 默认可用，不需要单独申请权限。`params` 支持：

| 字段 | 含义 |
| --- | --- |
| `startDate`、`endDate` | 包含边界的 ISO 日期范围，格式为 `yyyy-MM-dd`。 |
| `tagIds` | 数字标签 ID 数组。 |
| `tagFilter` | `Ignore`、`Any`、`All`、`None` 或 `Exact`。 |
| `text` | 文本筛选条件。 |
| `priority` | 数字优先级筛选。 |
| `limit`、`offset` | 分页参数。 |

返回表包含 `succeeded`、`items`、`normalizedQuery` 和 `error`。工作项包含 `id`、`date`、`comment`、`hours`、`priority`、`note` 和 `tags`。

宿主调用失败会抛出 Lua 错误。需要自行处理数据库不可用或权限拒绝时，可以使用 `pcall`。

## 沙箱限制

- 禁用 `io`、`os`、`debug`、`package`、`require`、`dofile`、`loadfile`、动态加载和 CLR 访问。
- 脚本在隔离 Worker 中运行，不应依赖其他执行留下的状态。
- 宿主数据必须可转换为 JSON，不会向脚本暴露 .NET 对象或依赖注入服务。
