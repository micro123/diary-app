# Lua 脚本 API Reference

Lua 使用 `ScriptApiVersion.V1`。脚本在独立 Lua Worker 中执行，通过全局 `diary` 表访问宿主 API。所有宿主调用参数和返回值都是 JSON 可转换的 Lua 值。

## 1. 脚本入口和上下文

入口函数由脚本的 `entryKind` 决定，必须使用以下固定名称之一：

| 入口类型 | 函数名 | 是否有编辑器目标 |
| --- | --- | --- |
| Application | `application_main(context)` | 否 |
| Editor | `editor_main(context)` | 是 |
| Automation | `automation_main(context)` | 否 |
| Query | `query_main(context)` | 否；当前仅预留 |

最小应用脚本示例：

~~~lua
function application_main(context)
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
~~~

完整示例：[Lua 5 分钟入门：查询并追加日志项](Examples/LuaQuickStart.md)。

`context` 字段：

| 字段 | 说明 |
| --- | --- |
| `request` | 完整执行请求，包含 `entryKind`、`source`、`arguments`、`idempotencyKey` 和 `preview`。 |
| `entryKind` | 当前入口类型。 |
| `arguments` | 执行参数表；未传参数时为空表。 |
| `target` | 编辑器目标；包含 `kind` 以及目标对应的字段。 |
| `dateRange` | 年、季度、月、日目标的 `{ startDate, endDate }`；事项目标为 `nil`。 |
| `workItem` | 事项目标的不可变事项快照；其他目标为 `nil`。 |
| `isCancelled()` | 查询当前执行是否已请求取消；长循环应在批次之间主动轮询。 |
| `progress.report(fraction, message)` | 报告 0 到 1 之间的执行进度。 |
| `getDateRange()` | 获取当前目标日期范围；无范围时返回 `nil`。 |
| `items.stream()` | 按当前日期范围分页迭代事项。 |
| `log` | 调试日志 API。 |

取消状态只在脚本主动轮询时可见；宿主调用仍会由 Worker 绑定当前执行的取消生命周期。长循环应在批次之间检查：

~~~lua
function application_main(context)
    for i = 1, 1000 do
        if context.isCancelled() then
            return
        end
        -- 处理一小批工作
    end
end
~~~

`target.kind` 为 `Year`、`Quarter`、`Month`、`Day` 或 `WorkItem`。季度使用自然季度：1-3、4-6、7-9、10-12 月。`context.request.source` 是 `Manual`、`Editor`、`Startup` 或 `Automation`。

脚本自动化只能追加工作记录，不提供删除或直接改写历史记录；创建 API 的 `idempotencyKey` 和 `preview` 用于控制重复提交和预览副作用。已提交的幂等结果由宿主共享存储持久化，应用重启后仍能识别重复请求。
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
| `range` | string | 日期范围快捷值：`today`、`yesterday`、`thisWeek`、`thisMonth`；提供时覆盖 `startDate`/`endDate`。 |

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

`diary.log.debug(message)`、`diary.log.info(message)`、`diary.log.warning(message)` 和 `diary.log.error(message)` 将调试信息写入宿主日志。单条日志受大小限制，不能输出敏感配置。

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
| `preview` | boolean | 可选，为 `true` 时只返回投影记录和副作用摘要，不写入数据库。 |
| `idempotencyKey` | string | 可选，重复提交同一业务动作时返回已提交结果，不重复追加。 |

成功返回 `succeeded = true` 和新建的 `item`；失败返回 `succeeded = false` 及 `error.code`：`InvalidInput`、`DatabaseUnavailable`、`ProviderFailure` 或 `Cancelled`。Lua 没有工作项更新和删除 API。

## 3.1 按模板创建日志项

```lua
local result = diary.templateLogItems.create({
    date = "2026-08-08",
    templateId = "00000000-0000-0000-0000-000000000001",
    hours = 2.5,
    title = nil,
    note = "按模板记录"
})
if not result.succeeded then error(result.error.message) end
```

标题为空时使用模板默认标题，模板默认标签应用到新建项。日期必须为 `yyyy-MM-dd`，模板 ID 必须为 UUID。

模板只读发现：

```lua
for _, template in ipairs(diary.templates.list()) do
    print(template.id .. ": " .. template.name)
end
```

`template.defaultTitle`、`template.defaultHours` 和 `template.defaultWorkTagIds` 只描述模板默认值，不提供模板写入能力。

宿主能力发现：

```lua
for _, capability in ipairs(diary.host.list()) do
    print(capability)
end
```

返回当前执行上下文实际注册的 Worker HostCall 名称，按序稳定排列；能力列表只用于发现，不替代宿主的权限、作用域和参数校验。

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

返回的 `instance` 字段包括 `pluginId`、`instanceId`、`displayName`、`icon`、`isConfigured`。`diary.trackerInstances.list()` 返回当前已启用实例的同一 DTO 列表，并按显示名称稳定排序。错误代码为 `InvalidInput` 或 `InstanceUnavailable`。不暴露 Tracker 客户端、配置和数据库。

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
| `diary.templates.list` | `templates.list` |
| `diary.host.list` | `host.capabilities.list` |
| `diary.logItems.create` | `logItems.create` |
| `diary.trackerInstances.get` | `trackerInstances.get` |
| `diary.clipboard.get` | `clipboard.get` |
| `diary.clipboard.set` | `clipboard.set` |
| `diary.ui.notify` | `ui.notify` |
| `diary.ui.confirm` | `ui.confirm` |
| `diary.log.*` | `log.write` |

Worker 禁用 `io`、`os`、`debug`、`package`、`require`、动态加载和 CLR 访问。脚本不能直接访问文件、网络、进程、数据库、DI 或 UI 控件。`print` 只能写入隔离的脚本输出流，并受到大小限制。

## 8. 错误、取消、超时和 Worker 终止

查询、创建等返回结果的 API 使用 `apiError.code` 提供稳定的大写错误码，例如 `INVALID_ARGUMENT`、`CANCELLED` 和 `PROVIDER_FAILURE`。领域结果中的 `error.code` 保留 Lua 可读的领域错误名。

```lua
local result = diary.workItems.query({ limit = 0 })
if not result.succeeded then
    local code = result.apiError and result.apiError.code or "PROVIDER_FAILURE"
    if code == "INVALID_ARGUMENT" then
        print("请修正查询参数")
    elseif code == "CANCELLED" then
        return
    end
end
```

对会抛出错误的同步 HostCall，错误文本使用 `[ERROR_CODE] message` 格式，可以在 `pcall` 中提取代码：

```lua
local ok, value = pcall(function()
    return diary.ui.confirm("继续", "是否继续？")
end)
if not ok then
    local code = tostring(value):match("^%[([^%]]+)%]") or "PROVIDER_FAILURE"
    if code == "CANCELLED" then return end
end
```

调用方取消、执行超时或 Worker 被终止时，Lua 脚本不应把异常当作普通业务失败重试；最终执行结果由宿主报告 `Cancelled`、`TimedOut` 或 `WORKER_TERMINATED`。
