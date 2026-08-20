# Lua 脚本 API Reference

Lua 使用 `ScriptApiVersion.V1`。脚本在独立 Lua Worker 中执行，通过全局 `diary` 表访问宿主 API。公共门面统一使用 snake_case（如 `work_items`、`get_date_range()`）；请求和结果 DTO 保持宿主协议字段。旧 camelCase 门面保留为兼容别名，新脚本不再推荐。所有宿主调用参数和返回值都是 JSON 可转换的 Lua 值。

## 1. 脚本入口和上下文

入口函数由脚本的 `entryKind` 决定，必须使用以下固定名称之一：

| 入口类型 | 函数名 | 是否有编辑器目标 |
| --- | --- | --- |
| Application | `application_main(context)` | 否 |
| Editor | `editor_main(context)` | 是 |
| Automation | `automation_main(context)` | 否 |
| Query | `query_main(context)` | 否 |

最小应用脚本示例：

~~~lua
function application_main(context)
    local result = diary.work_items.query({
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
右键查询“加班”工作项示例：[OvertimeWorkItems.lua](Examples/OvertimeWorkItems.lua)。
自动化脚本示例：[每日自查补录](Examples/AutomationDailyCheck.md)；查询脚本示例：[本月工时汇总](Examples/QueryMonthlySummary.md)。

`context` 字段：

| 字段 | 说明 |
| --- | --- |
| `request` | 完整执行请求，包含 `entryKind`、`source`、`arguments`、`idempotencyKey` 和 `preview`。 |
| `entry_kind` | 当前入口类型。 |
| `arguments` | 执行参数表；未传参数时为空表。 |
| `target` | 编辑器目标；包含 `kind` 以及目标对应的字段。 |
| `date_range` | 年、季度、月、周、日目标的 `{ startDate, endDate }`；事项目标为 `nil`。 |
| `work_item` | 事项目标的不可变事项快照；其他目标为 `nil`。 |
| `is_cancelled()` | 查询当前执行是否已请求取消；长循环应在批次之间主动轮询。 |
| `progress.report(fraction, message)` | 报告 0 到 1 之间的执行进度。 |
| `get_date_range()` | 获取当前目标日期范围；无范围时返回 `nil`。 |
| `items.stream()` | 按当前日期范围分页迭代事项。 |
| `log` | 调试日志 API。 |

取消状态只在脚本主动轮询时可见；宿主调用仍会由 Worker 绑定当前执行的取消生命周期。长循环应在批次之间检查：

~~~lua
function application_main(context)
    for i = 1, 1000 do
        if context.is_cancelled() then
            return
        end
        -- 处理一小批工作
    end
end
~~~

`target.kind` 为 `Year`、`Quarter`、`Month`、`Week`、`Day` 或 `WorkItem`。季度使用自然季度：1-3、4-6、7-9、10-12 月。`Week` 目标使用 `weekStart` 字段（周一的 `yyyy-MM-dd`），范围为该周周一至周日。`context.request.source` 是 `Manual`、`Editor`、`Startup`、`Automation`、`WorkItemCreated`、`WorkItemSaved` 或 `TagAdded`。

`target` 按 `kind` 提供不同字段：

| `kind` | 字段 | 校验 |
| --- | --- | --- |
| `Year` | `year` | 1-9999。 |
| `Quarter` | `year` + `quarter` | 自然季度 1-4。 |
| `Month` | `year` + `month` | 1-12。 |
| `Week` | `weekStart` | 周一的 `yyyy-MM-dd`；范围为该周周一至周日。 |
| `Day` | `date` | `yyyy-MM-dd`。 |
| `WorkItem` | `workItem` | 不可变事项快照；`dateRange`、`getDateRange()` 和 `items.stream()` 不可用。 |

目标字段由宿主校验，不合法的目标在执行前以 `Rejected` 状态拒绝，脚本不会运行到一半才发现错误。

`context.progress.report(fraction, message)` 报告执行进度：`fraction` 必须是 0 到 1 之间的数字，`message` 必须为非空字符串；Lua 侧不校验，由宿主校验并拒绝非法进度。进度只用于界面展示，不写入脚本日志，也不写入数据库；管理页运行脚本时进度会实时显示在底部运行区，并写入执行历史条目详情（会话内存态，重启即失）。

脚本自动化只能追加工作记录，不提供删除或直接改写历史记录；创建 API 的 `idempotencyKey` 和 `preview` 用于控制重复提交和预览副作用。真实写入使用 provider 事务，失败时回滚；`preview=true` 在数据库访问前返回投影且不修改数据库或幂等存储。已提交的幂等结果由宿主共享存储持久化，应用重启后仍能识别重复请求。

## 2. 查询工作项

调用：`diary.work_items.query(params)`。

| 参数 | 类型 | 说明 |
| --- | --- | --- |
| `startDate` / `endDate` | string | 包含边界，格式 `yyyy-MM-dd`。 |
| `tagIds` | number[] | 标签 ID 数组，最多 100 个。 |
| `tagFilter` | string | `Ignore`、`Any`、`All`、`None` 或 `Exact`。 |
| `text` | string | 文本过滤条件。 |
| `priority` | number | 0 到 9。 |
| `limit` | number | 默认 100，最大 1000。 |
| `offset` | number | 默认 0，最大 1,000,000。 |
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
                { id = 1, name = "开发", color = 0, level = 0, isPrimary = true,
                  disabled = false, metadata = { projectNumber = "PRJ-2026-001" } }
            }
        }
    },
    normalizedQuery = { ... },
    error = nil
}
```

查询是只读的。结果中的工作项和标签都是普通 Lua 表，不能通过 API 修改或删除。每个工作项还可能包含 `extraFields` 数组；字段项提供 `fieldId`、全局唯一 `fieldKey`、`tagId`、`tagName`、`label`、`type` 和 `value`。脚本通过 `fieldKey` 读取，例如：

```lua
for _, field in ipairs(item.extraFields or {}) do
    if field.fieldKey == "meeting.participants" then
        print(field.value)
    end
end
```

附加字段只读，编辑字段不会触发脚本执行。

`normalizedQuery` 是宿主规范化后的查询参数回显，字段与查询参数一致：`limit` 补全默认值 100，`offset` 补全 0，`tagFilter` 补全 `Ignore`；`range` 快捷值已被解析为 `startDate`/`endDate`，不再回显 `range`。可以用它确认宿主实际生效的过滤条件。

### 流式查询大量明细

```lua
for item in diary.work_items.stream({
    startDate = "2026-01-01",
    endDate = "2026-12-31",
    pageSize = 500
}) do
    print(item.date .. ": " .. item.comment)
end
```

迭代器按需调用 `workItems.query`，一页消费完后才拉取下一页。`pageSize` 必须在 1 到 500 之间，默认 500；除 `pageSize` 外还支持查询参数中的 `offset` 和全部过滤字段。查询期间数据变化可能影响 offset 分页边界。某一页查询领域失败（`succeeded = false`）时迭代器抛出 Lua 错误结束迭代，不会静默截断结果。不要在循环中把所有项重新保存到一个大表，否则会失去流式处理的内存优势。

`context.items.stream()` 按当前目标日期范围分页迭代，仅日期目标可用——事项目标没有日期范围，调用会报错。需要按自定义范围迭代时使用 `diary.work_items.stream(params)` 手动传日期。

## 3. 创建日志项

调用 `diary.log_items.create(params)` 会新建一个工作项，不会修改已有工作项。

`diary.log.debug(message)`、`diary.log.info(message)`、`diary.log.warning(message)` 和 `diary.log.error(message)` 将调试信息写入宿主日志。单条日志受大小限制，不能输出敏感配置。

```lua
local result = diary.log_items.create({
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

### 返回结构

成功：

```lua
{
    succeeded = true,
    item = {
        id = 42, date = "2026-08-08", comment = "标题", hours = 2.5,
        priority = 0, note = nil,
        tags = { { id = 1, name = "开发", color = 0, level = 0, isPrimary = true,
                  disabled = false, metadata = {} } }
    },
    effects = {
        appendedCount = 1,       -- 实际追加条数；预览或幂等重放时为 0
        preview = false,         -- 是否预览执行
        idempotencyKey = "daily-summary:2026-08-09",  -- 未提供时为 nil
        createdWorkItemIds = { 42 },                  -- 本次新建的工作项 ID
        remoteEffects = nil,     -- 预留，当前恒为 nil
    },
    duplicate = false            -- true 表示结果来自幂等重放
}
```

失败（值返回，不抛异常）：

```lua
{
    succeeded = false,
    error = { code = "InvalidInput", message = "日期必须是 yyyy-MM-dd 格式。" },
    apiError = {
        code = "INVALID_ARGUMENT", message = "日期必须是 yyyy-MM-dd 格式。",
        category = "Validation", retryable = false
    },
}
```

- `preview = true` 时 `item` 是 `id = 0` 的投影项：`effects.preview = true`、`appendedCount = 0`、`createdWorkItemIds` 为空数组，数据库未写入。
- `duplicate = true` 表示同一 `idempotencyKey` 已提交过：不重复追加，`effects.appendedCount = 0`，`item` 与 `effects.createdWorkItemIds` 保留首次创建的结果。
- 失败时 `item`、`effects` 为 `nil`，序列化时省略。

## 3.1 按模板创建日志项

```lua
local result = diary.template_log_items.create({
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

`template.defaultTitle`、`template.defaultHours` 和 `template.defaultWorkTagIds` 只描述模板默认值，不提供模板写入能力。每个模板包含：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | string | 模板 UUID。 |
| `name` | string | 模板名称。 |
| `defaultTitle` | string | 默认标题；调用时 `title` 为 nil 时使用。 |
| `defaultHours` | number | 默认工时；调用 `templateLogItems.create` 时仍需显式传 `hours`。 |
| `defaultWorkTagIds` | number[] | 默认标签 ID，创建时自动应用到新建项。 |

宿主能力发现：

```lua
for _, capability in ipairs(diary.host.list()) do
    print(capability)
end
```

返回当前执行上下文实际注册的 Worker HostCall 名称，按序稳定排列；能力列表只用于发现，不替代宿主的权限、作用域和参数校验。

## 4. Tracker 实例目录

调用 `diary.tracker_instances.get({ pluginId = "tracker.memory", instanceId = "company" })`。

```lua
local result = diary.tracker_instances.get({
    pluginId = "tracker.memory",
    instanceId = "company"
})
if result.succeeded then
    print(result.instance.displayName)
end
```

返回的 `instance` 字段包括 `pluginId`、`instanceId`、`displayName`、`icon`、`isConfigured`。`diary.tracker_instances.list()` 返回当前已启用实例的同一 DTO 列表，并按显示名称稳定排序。错误代码为 `InvalidInput` 或 `InstanceUnavailable`；失败时值返回 `{ succeeded = false, error = { code, message }, apiError = { ... } }`，不抛异常。不暴露 Tracker 客户端、配置和数据库。

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

`notify(title, body)` 无返回值；`confirm(title, body)` 返回布尔值。非结果类宿主调用（剪贴板、用户交互、日志和列表类）失败会抛出 Lua 错误，可使用 `pcall` 捕获：

```lua
local ok, result = pcall(function()
    return diary.clipboard.get()
end)
if not ok then
    print("host call failed: " .. tostring(result))
end
```

### 6.1 交互式导出（第一阶段）

Lua API 使用全小写/snake_case；交互式能力只允许有人值守的 `Editor+Editor`、`Application+Manual` 和 `Query+Manual` 执行。

完整的共享模型、格式能力矩阵、模板绑定语义、错误详情和 10 分钟令牌生命周期见 [Export.md](Export.md)。调用前可用 `diary.exports.list_formats()` 读取 `content_capabilities` 和 `format_options`。

可直接复制的加班明细 Editor 脚本见 [Examples/OvertimeExport.lua](Examples/OvertimeExport.lua)。

```lua
local directory = diary.ui.pick_directory({ title = "选择导出目录" })
if directory == nil then
    return
end

local result = diary.exports.export({
    format_id = "xlsx",
    directory_selection_id = directory.selection_id,
    file_name = "report.xlsx",
    format_options = {
        format_id = "xlsx",
        values = { sheet_name = "加班明细" },
    },
    content = {
        kind = "table",
        columns = {
            { name = "项目", type = "text" },
            { name = "时长", type = "duration" },
        },
        rows = { { "加班", "25:30:00" } },
        aggregates = { { column_name = "时长", aggregation = "sum", label = "总时长" } },
        style = "report",
    },
})
if result.succeeded then
    diary.ui.ask_to_open_exported_file(result.file_id)
else
    local export_error = result.error
    print(export_error.code .. ": " .. export_error.message)
    if export_error.code == "EXPORT_VALUE_INVALID" then
        print("行=" .. tostring(export_error.details.row) .. "，列=" .. export_error.details.column)
    end
    for _, diagnostic in ipairs((export_error.details and export_error.details.diagnostics) or {}) do
        print(diagnostic.code .. " [" .. tostring(diagnostic.binding_key) .. "] " .. diagnostic.message)
    end
    if export_error.retryable then
        print("宿主故障可能稍后恢复；验证错误必须先修改输入。")
    end
end
```

模板导出先调用 `diary.exports.list_templates("xlsx")`，再按 descriptor 的精确 `template_id`、`template_version` 和 `bindings` 构造 `template = { values = ..., tables = ..., documents = ... }`。模板正文使用 `{{变量名}}`、`{{items.字段}}`、`{{items.字段|column}}` 和 `{{items|matrix}}` 简单标记；行循环复制模板行，列循环复制模板列，矩阵标记展开完整的 `Rows × Columns` 区域，脚本负责准备数据和其他逻辑。模板名称由文件名推断，版本为 `1.0.0`。

纯文本输出可以选择独立的 `mustache` 格式并导入 `.mustache` 模板，核心语法和上下文映射见 [MustacheExport.md](MustacheExport.md)。

`diary.ui.select_option()` 的 `require_choice` 策略禁止用户关闭对话框，但 Worker 终止、取消或宿主退出时会安全结束等待。`Time` 不参与合计；`Duration` 使用 `[h]:mm:ss`。CSV 的基础工具会对 RFC 4180 字段进行转义，并对以 `=`, `+`, `-`, `@` 开头的文本增加前置单引号。

## 7. Worker API 和沙箱

| Lua API | Worker HostCall |
| --- | --- |
| `diary.work_items.query` | `workItems.query` |
| `diary.work_items.stream` | `workItems.query`（分页） |
| `diary.templates.list` | `templates.list` |
| `diary.host.list` | `host.capabilities.list` |
| `diary.log_items.create` | `logItems.create` |
| `diary.template_log_items.create` | `templateLogItems.create` |
| `diary.tracker_instances.get` | `trackerInstances.get` |
| `diary.tracker_instances.list` | `trackerInstances.list` |
| `diary.clipboard.get` | `clipboard.get` |
| `diary.clipboard.set` | `clipboard.set` |
| `diary.ui.notify` | `ui.notify` |
| `diary.ui.confirm` | `ui.confirm` |
| `diary.ui.select_option` | `ui.options.select` |
| `diary.ui.pick_directory` | `ui.directory.pick` |
| `diary.ui.ask_to_open_exported_file` | `ui.exported_file.open` |
| `diary.exports.list_formats` | `exports.formats.list` |
| `diary.exports.list_templates` | `exports.templates.list` |
| `diary.exports.export` | `exports.export` |
| `diary.log.*` | `log.write` |
| `context.progress.report` | `script.progress` |

Worker 禁用 `io`、`os`、`debug`、`package`、`require`、动态加载和 CLR 访问。脚本不能直接访问文件、网络、进程、数据库、DI 或 UI 控件。`print` 按行转发到脚本日志（Info 级），与 `diary.log.info` 一样显示在管理页「运行日志」Tab 和宿主日志中；每条打印占用一次宿主调用，计入宿主调用次数上限，打印密集型脚本请改用 `diary.log` 或合并输出。

## 8. 错误、取消、超时和 Worker 终止

查询、创建等返回结果的 API 使用 `apiError.code` 提供稳定的大写错误码，例如 `INVALID_ARGUMENT`、`CANCELLED` 和 `PROVIDER_FAILURE`。领域结果中的 `error.code` 保留 Lua 可读的领域错误名。

```lua
local result = diary.work_items.query({ limit = 0 })
if not result.succeeded then
    local code = result.apiError and result.apiError.code or "PROVIDER_FAILURE"
    if code == "INVALID_ARGUMENT" then
        print("请修正查询参数")
    elseif code == "CANCELLED" then
        return
    end
end
```

返回结果的 API（`workItems.query`、`logItems.create`、`templateLogItems.create`、`trackerInstances.get`）即使失败也返回 `{ succeeded = false, error = { code, message }, apiError = { ... } }`，不抛异常。`diary.exports.export` 同样以结果表达普通失败，但其 `error` 本身就是稳定的 `ScriptApiError`，字段为 `code/message/category/retryable/details`，没有额外的 `apiError`。非结果类 HostCall（剪贴板、用户交互、日志和列表类）以及未知方法、宿主未配置等意外场景失败时会抛出错误，错误文本使用 `[ERROR_CODE] message` 格式，可以在 `pcall` 中提取代码：

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

非结果类 HostCall 抛出的 `[ERROR_CODE]` 与领域错误名的对应关系：

| `[ERROR_CODE]` | 来源领域码 | 说明 |
| --- | --- | --- |
| `INVALID_ARGUMENT` | `InvalidInput` | 参数不合法。 |
| `PERMISSION_DENIED` | `PermissionDenied` | 无权限执行该调用。 |
| `SCRIPT_API_HOST_NOT_CONFIGURED` | `DatabaseUnavailable` | 数据库/宿主未就绪。 |
| `INSTANCE_UNAVAILABLE` | `InstanceUnavailable` | Tracker 实例不可用。 |
| `PROVIDER_FAILURE` | `ProviderFailure` | 底层提供程序失败。 |
| `CANCELLED` | `Cancelled` | 调用已取消。 |

其他全大写代码原样直通；无法识别时兜底 `PROVIDER_FAILURE`。

## 9. 类型参考

### request 表结构

`context.request` 是完整执行请求（`ScriptExecutionRequest`）的 JSON 表：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `target` | table 或 nil | 编辑器目标；应用/自动化入口为 nil。 |
| `arguments` | table | 执行参数表。 |
| `source` | string | `Manual`、`Editor`、`Startup`、`Automation`、`WorkItemCreated`、`WorkItemSaved` 或 `TagAdded`。 |
| `entryKind` | string | `Application`、`Editor`、`Automation` 或 `Query`。 |
| `idempotencyKey` | string 或 nil | 业务幂等键。 |
| `preview` | boolean | 是否预览。 |

枚举字段以字符串形式出现（例如 `source = "Manual"`）。`context.work_item` 是事项目标的不可变快照，字段与查询结果中的 `item` 相同（见附录 C）。

### 入口返回值约定

入口函数的返回值本身不参与执行状态：正常返回（或返回任何值）即执行成功（`Succeeded`）；失败通过 `error()` 抛出异常表达：

- 抛异常 → `Failed` + `LUA_EXECUTION_FAILED` 诊断（附源码行/列）。
- 执行已取消 → `Cancelled`。
- 超时、Worker 终止由宿主报告，脚本无需处理。

例外：若返回宿主 API 的结果表（如 `diary.log_items.create` 的返回值），其中的 `effects` 字段会被 Worker 提取并随执行结果传回宿主，显示在管理页执行历史与完成通知中（追加条数、预览、幂等重放、新建 ID）。

### 自动化触发器上下文

Lua 自动化脚本的上下文额外提供 `context.automation` 表：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `trigger` | string | `Scheduled`、`Startup`、`WorkItemCreated`、`WorkItemSaved`、`TagAdded` 或 `Unknown`。 |
| `eventData` | table | 事件数据（当前为执行参数字典）。 |
| `idempotencyKey` | string 或 nil | 自动化执行幂等键。 |

自动化脚本放 `application` 目录，metadata 的 `entryKind` 写 `Automation`；`schedule` 字段（`"daily HH:mm"`）配置每日定时，`runOnStartup`（true/false）配置启动补跑，`triggers` 数组配置 `WorkItemCreated`、`WorkItemSaved`、`TagAdded`。事件型自动化可省略 `schedule`；事件数据通过 `context.automation.eventData` 提供，工作项事件包含 `workItemId`、`date`、`comment`、`time`、`priority`，标签事件额外包含 `tagId`、`tagName`、`tagLevel`、`tagSource`、`sequence`。

## 附录 A. `apiError` 错误码总表

`apiError`/导出 `error` 的公共结构：`code`（string，稳定大写码）、`message`（string）、`category`（`Validation`、`Permission`、`Host`、`Provider` 或 `Cancellation`）、`retryable`（boolean）、`details`（可选结构化字典；导出值错误和模板绑定错误会使用）。

当前 API 实际产生的错误码：

| `apiError.code` | 来源（`error.code`） | category | retryable | 说明 |
| --- | --- | --- | --- | --- |
| `INVALID_ARGUMENT` | `InvalidInput` | Validation | 否 | 参数不合法；修正参数后重试。 |
| `PERMISSION_DENIED` | `PermissionDenied`（仅查询） | Permission | 否 | 无权限执行该查询。 |
| `SCRIPT_API_HOST_NOT_CONFIGURED` | `DatabaseUnavailable` | Host | 是 | 数据库/宿主未就绪，可稍后重试。 |
| `INSTANCE_UNAVAILABLE` | `InstanceUnavailable`（仅 Tracker） | Host | 否 | Tracker 实例不存在或未启用。 |
| `PROVIDER_FAILURE` | `ProviderFailure` | Provider | 是 | 底层提供程序失败。 |
| `CANCELLED` | `Cancelled` | Cancellation | 否 | 调用已取消；不要重试带副作用的操作。 |

保留但当前 API 未产生的常量：`SCRIPT_API_UNAVAILABLE`、`SCRIPT_API_SCOPE_NOT_SUPPORTED`、`TIMEOUT`、`WORKER_TERMINATED`、`DUPLICATE_REQUEST`。

## 附录 B. 执行状态与常见诊断

执行状态：`Succeeded`、`Failed`、`Cancelled`、`Rejected`（入口、目标或 descriptor 校验不通过）、`TimedOut`。

常见诊断码：

| 诊断码 | 含义 |
| --- | --- |
| `LUA_EXECUTION_FAILED` | Lua 脚本运行时异常。 |
| `LUA_ENTRYPOINT_MISSING` | 入口函数缺失。 |
| `LUA_SYNTAX_ERROR` | Lua 语法错误。 |
| `LUA_RUNTIME_UNAVAILABLE` | Lua 运行时不可用。 |
| `SCRIPT_DESCRIPTOR_INVALID` | descriptor 与入口不一致。 |
| `SCRIPT_ENTRY_KIND_MISMATCH` | 入口类型与作用域/目标不匹配。 |
| `SCRIPT_TARGET_INVALID` | 编辑器目标校验失败。 |
| `WORKER_TERMINATED` | Worker 进程异常退出或通道断开。 |
| `SCRIPT_EXECUTION_TIMED_OUT` | 执行超过时限。 |
| `WORKER_HOST_CALL_LIMIT` | 宿主调用次数超限。 |
| `WORKER_MESSAGE_TOO_LARGE` | Worker 消息超过大小限制。 |

## 附录 C. DTO 字段总表

- `item`（工作项）：`id`(number)、`date`、`comment`、`hours`(number)、`priority`(number，0-9)、`note`(string 或 nil)、`tags`(数组)。
- `tag`：`id`(number)、`name`、`color`(number)、`level`(number)、`isPrimary`(boolean)、`disabled`(boolean)、`metadata`(table&lt;string, string&gt;)。`metadata` 是只读字符串键值表，推荐使用 `projectNumber` 保存项目编号；推荐使用 `isPrimary` 判断主标签，`level` 保留用于兼容。
- `instance`（Tracker 实例）：`pluginId`、`instanceId`、`displayName`、`icon`、`isConfigured`(boolean)。
- `template`：`id`、`name`、`defaultTitle`、`defaultHours`(number)、`defaultWorkTagIds`(number[])。
