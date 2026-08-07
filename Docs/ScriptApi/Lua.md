# Lua 脚本 API Reference

Lua 使用 `ScriptApiVersion.V1`。脚本在独立 Lua Worker 中执行，通过全局 `diary` 表访问宿主 API。所有宿主调用参数和返回值都是 JSON 可转换的 Lua 值。

## 1. 脚本入口和上下文

脚本必须定义同步函数 `main(context)` 或 `execute(context)`，不能依赖上一次执行留下的 Lua 全局状态。

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

    for _, item in ipairs(result.items) do
        print(item.date .. ": " .. item.comment)
    end
end
```

`context` 字段：

| 字段 | 说明 |
| --- | --- |
| `request` | 完整执行请求。字段名使用 camelCase。 |
| `arguments` | 执行参数表；未传参数时为空表。 |

`context.request.target.editor` 包含 `startDate`、`endDate`、`granularity`；`context.request.source` 是 `Manual`、`Editor`、`Startup` 或 `Automation`。

## 2. 查询工作项

调用：`diary.workItems.query(params)`。

| 参数 | 类型 | 说明 |
| --- | --- | --- |
| `startDate` / `endDate` | string | 包含边界，格式 `yyyy-MM-dd`。 |
| `tagIds` | number[] | 标签 ID 数组，最多 100 个。 |
| `tagFilter` | string | `Ignore`、`Any`、`All`、`None` 或 `Exact`。 |
| `text` | string | 文本过滤条件。 |
| `priority` | number | 0 到 9。 |
| `limit` | number | 默认 100，最大 1000。 |
| `offset` | number | 默认 0，最大 10000。 |

返回：

```lua
{
    succeeded = true,
    items = {
        {
            id = 1,
            date = "2026-08-08",
            comment = "实现 Worker",
            hours = 2.5,
            priority = 0,
            note = "备注",
            tags = {
                { id = 1, name = "开发", color = 0, level = 0, disabled = false }
            }
        }
    },
    normalizedQuery = { ... },
    error = nil
}
```

查询是只读的。结果中的工作项和标签都是普通 Lua 表，不能通过 API 修改或删除。

## 3. 创建日志项

### 流式查询大量明细

```lua
for item in diary.workItems.stream({
    startDate = "2026-01-01",
    endDate = "2026-12-31",
    pageSize = 500
}) do
    print(item.date .. ": " .. item.comment)
end
```

迭代器按需调用 `workItems.query`，一页消费完后才拉取下一页。`pageSize` 必须在 1 到 500 之间。不要在循环中把所有项重新保存到一个大表，否则会失去流式处理的内存优势。

调用 `diary.logItems.create(params)` 会新建一个工作项，不会修改已有工作项。

```lua
local result = diary.logItems.create({
    date = "2026-08-08",
    hours = 2.5,
    title = "完善 Lua Worker API",
    note = "补充日志项、剪贴板和用户交互"
})

if not result.succeeded then
    error(result.error.message)
end

print("created work item: " .. result.item.id)
```

| 参数 | 类型 | 约束 |
| --- | --- | --- |
| `date` | string | `yyyy-MM-dd`。 |
| `hours` | number | 大于 0 且不超过 24。 |
| `title` | string | 非空，最多 500 字符。 |
| `note` | string | 可选，最多 10000 字符。 |

成功返回 `succeeded = true` 和新建的 `item`；失败返回 `succeeded = false` 及 `error.code`：`InvalidInput`、`DatabaseUnavailable`、`ProviderFailure` 或 `Cancelled`。Lua 没有工作项更新和删除 API。

## 4. Tracker 实例目录

调用 `diary.trackerInstances.get({ pluginId = "tracker.memory", instanceId = "company" })`。

```lua
local result = diary.trackerInstances.get({
    pluginId = "tracker.memory",
    instanceId = "company"
})
if result.succeeded then
    print(result.instance.displayName)
end
```

返回的 `instance` 字段包括 `pluginId`、`instanceId`、`displayName`、`icon`、`isConfigured`。错误代码为 `InvalidInput` 或 `InstanceUnavailable`。不暴露 Tracker 客户端、配置和数据库。

## 5. 剪贴板

```lua
local previous = diary.clipboard.get()
local ok = diary.clipboard.set("复制到系统剪贴板的文本")
```

`get()` 返回文本或 `nil`；`set(text)` 返回布尔值。只支持文本剪贴板。

## 6. 用户交互

```lua
diary.ui.notify("脚本完成", "日志项已经创建")
local confirmed = diary.ui.confirm("继续操作", "是否继续？")
if confirmed then
    -- 继续后续操作
end
```

`notify(title, body)` 无返回值；`confirm(title, body)` 返回布尔值。宿主调用失败会抛出 Lua 错误，可使用 `pcall` 捕获：

```lua
local ok, result = pcall(function()
    return diary.logItems.create({ date = "2026-08-08", hours = 1, title = "测试" })
end)
if not ok then
    print("host call failed: " .. tostring(result))
end
```

## 7. Worker API 和沙箱

| Lua API | Worker HostCall |
| --- | --- |
| `diary.workItems.query` | `workItems.query` |
| `diary.logItems.create` | `logItems.create` |
| `diary.trackerInstances.get` | `trackerInstances.get` |
| `diary.clipboard.get` | `clipboard.get` |
| `diary.clipboard.set` | `clipboard.set` |
| `diary.ui.notify` | `ui.notify` |
| `diary.ui.confirm` | `ui.confirm` |

Worker 禁用 `io`、`os`、`debug`、`package`、`require`、动态加载和 CLR 访问。脚本不能直接访问文件、网络、进程、数据库、DI 或 UI 控件。`print` 只能写入隔离的脚本输出流，并受到大小限制。
