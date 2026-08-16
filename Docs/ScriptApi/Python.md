# Python 脚本 API Reference

Python 使用 `ScriptApiVersion.V1`。脚本在独立 Python Worker 中执行，通过 `context.diary` 访问宿主 API。入口必须是同步函数，Worker 不支持 `async def` 或返回 awaitable。

## 1. 脚本入口和上下文

入口函数由脚本的 `entryKind` 决定：`application_main(context)`、`editor_main(context)`、`automation_main(context)`、`query_main(context)`。Worker 不再通过通用 `main(context)` 或 `execute(context)` 隐式判断场景。

最小应用脚本示例：

~~~python
def application_main(context):
    result = context.diary.workItems.query(
        startDate="2026-08-01",
        endDate="2026-08-31",
        limit=100,
    )
    if not result["succeeded"]:
        raise RuntimeError(result["error"]["message"])

    for item in result["items"]:
        print(f"{item['date']}: {item['comment']}")
~~~

`context` 同时支持属性访问和字典式访问：

| 字段 | 说明 |
| --- | --- |
| `request` | 完整执行请求字典。 |
| `entryKind` | 当前入口类型。 |
| `arguments` | 执行参数字典。 |
| `target` | 编辑器目标字典；包含 `kind` 和目标对应的字段。 |
| `source` | 执行来源名称。 |
| `idempotencyKey` | 追加式写入的业务幂等键；已提交结果由宿主共享存储持久化，应用重启后仍可识别重复请求。 |
| `preview` | 是否只预览而不写入。 |
| `isCancelled()` | 查询当前执行是否已请求取消。 |
| `progress.report(fraction, message)` | 报告 0 到 1 之间的执行进度。 |
| `diary` | 宿主 API 根对象。 |
| `dateRange` | 年、季度、月、周、日目标的日期范围；事项目标为 `None`。 |
| `workItem` | 事项目标的不可变事项快照；其他目标为 `None`。 |
| `getDateRange()` | 获取当前目标日期范围；无范围时返回 `None`。 |
| `items.stream()` | 按当前日期范围分页迭代事项。 |
| `log` | 调试日志 API。 |

请求、参数和结果字段使用 camelCase，例如 `startDate`、`endDate`、`normalizedQuery`。脚本自动化只能追加工作记录，不提供删除或直接改写历史记录；真实写入使用 provider 事务，失败时回滚；`preview=True` 在数据库访问前返回投影且不修改数据库或幂等存储；`idempotencyKey` 对已提交结果持久有效。

`target` 按 `kind` 提供不同字段（字典访问）：`Year` → `year`（1-9999）；`Quarter` → `year` + `quarter`（1-4）；`Month` → `year` + `month`（1-12）；`Week` → `weekStart`（周一的 `yyyy-MM-dd`，范围周一至周日）；`Day` → `date`；`WorkItem` → `workItem`（不可变事项快照，`dateRange`、`getDateRange()` 和 `items.stream()` 不可用）。`context.source` 是 `Manual`、`Editor`、`Startup`、`Automation`、`WorkItemCreated`、`WorkItemSaved` 或 `TagAdded`。目标字段由宿主校验，不合法的目标在执行前以 `Rejected` 状态拒绝。

`context.progress.report(fraction, message)` 报告执行进度：`fraction` 必须为 0 到 1 之间的数字，`message` 必须为非空字符串，否则抛 `ValueError`。进度只用于界面展示，不写入脚本日志，也不写入数据库；管理页运行脚本时进度会实时显示在底部运行区，并写入执行历史条目详情（会话内存态，重启即失）。

完整示例：[Python 5 分钟入门：查询并追加日志项](Examples/PythonQuickStart.md)。
查询指定时间范围内“加班”工作项的示例：[OvertimeWorkItems](Examples/OvertimeWorkItems.md)。
自动化脚本示例：[每日自查补录](Examples/AutomationDailyCheck.md)；查询脚本示例：[本月工时汇总](Examples/QueryMonthlySummary.md)。

## 2. 查询工作项

调用：`context.diary.workItems.query(params=None, **kwargs)`。可以传字典、关键字参数或同时传入两者：

```python
result = context.diary.workItems.query({"limit": 100}, text="Worker")
```

| 参数 | 类型 | 说明 |
| --- | --- | --- |
| `startDate` / `endDate` | str | 包含边界，格式 `yyyy-MM-dd`。 |
| `tagIds` | list[int] | 标签 ID，最多 100 个。 |
| `tagFilter` | str | `Ignore`、`Any`、`All`、`None` 或 `Exact`。 |
| `text` | str | 文本过滤条件。 |
| `priority` | int | 0 到 9。 |
| `limit` | int | 默认 100，最大 1000。 |
| `offset` | int | 默认 0，最大 1,000,000。 |
| `range` | str | 日期范围快捷值：`today`、`yesterday`、`thisWeek`、`thisMonth`；提供时覆盖 `startDate`/`endDate`。 |

成功返回：

```python
{
    "succeeded": True,
    "items": [{
        "id": 1,
        "date": "2026-08-08",
        "comment": "实现 Worker",
        "hours": 2.5,
        "priority": 0,
        "note": "备注",
        "tags": [{"id": 1, "name": "开发", "color": 0, "level": 0,
                  "isPrimary": True, "disabled": False,
                  "metadata": {"projectNumber": "PRJ-2026-001"}}],
    }],
    "normalizedQuery": {...},
    "error": None,
}
```

查询是只读的。返回的字典是 JSON 数据，不是可写入宿主数据库的对象。

`normalizedQuery` 是宿主规范化后的查询参数回显，字段与查询参数一致：`limit` 补全默认值 100，`offset` 补全 0，`tagFilter` 补全 `Ignore`；`range` 快捷值已被解析为 `startDate`/`endDate`，不再回显 `range`。可以用它确认宿主实际生效的过滤条件。

### 流式查询大量明细

```python
for item in context.diary.workItems.stream(
    startDate="2026-01-01",
    endDate="2026-12-31",
    pageSize=500,
):
    print(item["date"], item["comment"])
```

Python 生成器按需调用 `workItems.query`，一页消费完后才拉取下一页。`pageSize` 必须在 1 到 500 之间，默认 500；除 `pageSize` 外还支持查询参数中的 `offset` 和全部过滤字段。查询期间数据变化可能影响 offset 分页边界。某一页查询领域失败（`succeeded=False`）时生成器抛出 `HostCallError` 结束迭代，不会静默截断结果。非流式查询单次最多返回 1000 条。

`context.diary.items.stream()` 按当前目标日期范围分页迭代，仅日期目标可用——事项目标没有日期范围，调用会抛 `HostCallError`。需要按自定义范围迭代时使用 `context.diary.workItems.stream(...)` 手动传日期。

## 3. 创建日志项

调用 `context.diary.logItems.create(params)` 会创建一个新工作项，不能修改或删除已有工作项：

```python
result = context.diary.logItems.create({
    "date": "2026-08-08",
    "hours": 2.5,
    "title": "完善 Python Worker API",
    "note": "补充日志项、剪贴板和用户交互",
})
if not result["succeeded"]:
    raise RuntimeError(result["error"]["message"])

created = result["item"]
print(created["id"])
```

| 参数 | 类型 | 约束 |
| --- | --- | --- |
| `date` | str | `yyyy-MM-dd`。 |
| `hours` | float | 大于 0 且不超过 24。 |
| `title` | str | 非空，最多 500 字符。 |
| `note` | str | 可选，最多 10000 字符。 |
| `preview` | bool | 可选，为 `true` 时只返回投影记录和副作用摘要，不写入数据库。 |
| `idempotencyKey` | str | 可选，重复提交同一业务动作时返回已提交结果，不重复追加。 |

失败时返回 `succeeded = False` 和 `error`。错误代码包括 `InvalidInput`、`DatabaseUnavailable`、`ProviderFailure`、`Cancelled`。该 API 只返回新建项的安全 DTO。

### 返回结构

成功：

```python
{
    "succeeded": True,
    "item": {"id": 42, "date": "2026-08-08", "comment": "标题", "hours": 2.5,
             "priority": 0, "note": None, "tags": [...]},
    "effects": {
        "appendedCount": 1,        # 实际追加条数；预览或幂等重放时为 0
        "preview": False,          # 是否预览执行
        "idempotencyKey": "daily-summary:2026-08-09",  # 未提供时为 None
        "createdWorkItemIds": [42],                    # 本次新建的工作项 ID
        "remoteEffects": None,     # 预留，当前恒为 None
    },
    "duplicate": False,            # True 表示结果来自幂等重放
}
```

失败（值返回，不抛异常）：

```python
{
    "succeeded": False,
    "error": {"code": "InvalidInput", "message": "日期必须是 yyyy-MM-dd 格式。"},
    "apiError": {
        "code": "INVALID_ARGUMENT", "message": "日期必须是 yyyy-MM-dd 格式。",
        "category": "Validation", "retryable": False,
    },
}
```

- `preview=True` 时 `item` 是 `id=0` 的投影项：`effects["preview"]=True`、`appendedCount=0`、`createdWorkItemIds` 为空列表，数据库未写入。
- `duplicate=True` 表示同一 `idempotencyKey` 已提交过：不重复追加，`appendedCount=0`，`item` 与 `createdWorkItemIds` 保留首次创建的结果。
- 失败时 `item`、`effects` 为 `None`，序列化时省略。

## 3.1 按模板创建日志项

```python
result = context.diary.templateLogItems.create({
    "date": "2026-08-08",
    "templateId": "00000000-0000-0000-0000-000000000001",
    "hours": 2.5,
    "title": None,
    "note": "按模板记录",
})
if not result["succeeded"]:
    raise RuntimeError(result["error"]["message"])
```

标题为空时使用模板默认标题，模板默认标签应用到新建项。日期必须为 `yyyy-MM-dd`，模板 ID 必须为 UUID。

模板只读发现：

```python
for template in context.diary.templates.list():
    print(template["id"], template["name"])
```

`defaultTitle`、`defaultHours` 和 `defaultWorkTagIds` 只描述模板默认值，不提供模板写入能力。每个模板包含：`id`(UUID)、`name`、`defaultTitle`、`defaultHours`(float)、`defaultWorkTagIds`(list[int])；`defaultHours` 只描述默认值，调用 `templateLogItems.create` 时仍需显式传 `hours`。

宿主能力发现：

```python
for capability in context.diary.host.list():
    print(capability)
```

返回当前执行上下文实际注册的 Worker HostCall 名称，按序稳定排列；能力列表只用于发现，不替代宿主的权限、作用域和参数校验。

## 4. Tracker 实例目录

```python
result = context.diary.trackerInstances.get({
    "pluginId": "tracker.memory",
    "instanceId": "company",
})
if result["succeeded"]:
    print(result["instance"]["displayName"])
```

返回的 `instance` 包含 `pluginId`、`instanceId`、`displayName`、`icon`、`isConfigured`。`context.diary.trackerInstances.list()` 返回当前已启用实例的同一 DTO 列表，并按显示名称稳定排序。错误代码为 `InvalidInput` 或 `InstanceUnavailable`；失败时值返回 `{"succeeded": False, "error": {"code": ..., "message": ...}, "apiError": {...}}`，不抛异常。不暴露 Tracker 客户端、配置、数据库或 DI。

## 5. 剪贴板

```python
previous = context.diary.clipboard.get()
changed = context.diary.clipboard.set("复制到系统剪贴板的文本")
```

`get()` 返回 str 或 `None`；`set(text)` 返回 bool。只支持文本剪贴板。

## 6. 用户交互

```python
context.diary.ui.notify("脚本完成", "日志项已经创建")
confirmed = context.diary.ui.confirm("继续操作", "是否继续？")
if confirmed:
    print("用户确认")
```

`notify(title, body)` 无返回值；`confirm(title, body)` 返回 bool。

`context.log.debug(message)`、`context.log.info(message)`、`context.log.warning(message)` 和 `context.log.error(message)` 将调试信息写入宿主日志。单条日志受大小限制，不能输出敏感配置。

非结果类 HostCall（剪贴板、用户交互、日志和列表类）失败会抛出 `HostCallError`，其 `code` 属性可用于分类处理；返回结果的 API（`workItems.query`、`logItems.create`、`templateLogItems.create`、`trackerInstances.get`）失败时返回 `succeeded=False` 结果对象，不抛异常：

```python
try:
    context.diary.clipboard.set("复制到系统剪贴板的文本")
except HostCallError as error:
    print(error.code)
```

`HostCallError.code` 与领域错误名的对应关系：`InvalidInput → INVALID_ARGUMENT`、`PermissionDenied → PERMISSION_DENIED`、`DatabaseUnavailable → SCRIPT_API_HOST_NOT_CONFIGURED`、`InstanceUnavailable → INSTANCE_UNAVAILABLE`、`ProviderFailure → PROVIDER_FAILURE`、`Cancelled → CANCELLED`；其他全大写码原样直通，无法识别时兜底 `PROVIDER_FAILURE`。

## 7. Worker API 和沙箱

| Python API | Worker HostCall |
| --- | --- |
| `context.diary.workItems.query` | `workItems.query` |
| `context.diary.workItems.stream` | `workItems.query`（分页） |
| `context.diary.templates.list` | `templates.list` |
| `context.diary.host.list` | `host.capabilities.list` |
| `context.diary.logItems.create` | `logItems.create` |
| `context.diary.templateLogItems.create` | `templateLogItems.create` |
| `context.diary.trackerInstances.get` | `trackerInstances.get` |
| `context.diary.trackerInstances.list` | `trackerInstances.list` |
| `context.diary.clipboard.get` | `clipboard.get` |
| `context.diary.clipboard.set` | `clipboard.set` |
| `context.diary.ui.notify` | `ui.notify` |
| `context.diary.ui.confirm` | `ui.confirm` |
| `context.log.*` | `log.write` |
| `context.progress.report` | `script.progress` |

Python Worker 禁止导入模块、文件访问、动态代码执行、运行时自省、输入和双下划线属性。允许的内置函数（SAFE_BUILTINS）：`abs`、`all`、`any`、`bool`、`dict`、`enumerate`、`Exception`、`HostCallError`、`float`、`int`、`isinstance`、`len`、`list`、`max`、`min`、`next`、`print`、`range`、`set`、`sorted`、`str`、`sum`、`tuple`、`type`、`ValueError`、`RuntimeError`、`zip`。除此之外没有其他内置函数；`__builtins__`、`__import__`、`eval`、`exec`、`open`、`getattr`、`globals`、`setattr`、`vars`、`compile`、`breakpoint`、`input`、`help`、`quit` 等名称在执行前由 AST 静态扫描拒绝。取消通过逐行 trace 注入，运行中的脚本会收到 `CancelledExecution`。`print` 按行转发到脚本日志（Info 级），与 `context.log.info` 一样显示在管理页「运行日志」Tab 和宿主日志中；每条打印占用一次宿主调用，计入宿主调用次数上限，打印密集型脚本请改用 `context.log` 或合并输出。输出总量超过 1MB 时脚本执行失败，异常 traceback 仍写入 Worker stderr。脚本不能直接访问网络、进程、数据库、DI 或 UI 控件。

## 8. 错误、取消、超时和 Worker 终止

查询、创建等返回结果的 API 使用 `apiError["code"]` 提供稳定的大写错误码；`error["code"]` 是 Python 可读的领域错误名。

```python
result = context.diary.workItems.query({"limit": 0})
if not result["succeeded"]:
    code = (result.get("apiError") or {}).get("code", "PROVIDER_FAILURE")
    if code == "INVALID_ARGUMENT":
        print("请修正查询参数")
    elif code == "CANCELLED":
        return
```

非结果类 HostCall（剪贴板、用户交互、日志和列表类）以及未知方法、宿主未配置等意外场景失败时抛出 `HostCallError`，其 `code` 使用同一套大写错误码。分页流 `items.stream()` 在分页查询领域失败时同样抛出 `HostCallError`：

```python
try:
    context.diary.ui.confirm("继续", "是否继续？")
except HostCallError as error:
    if error.code == "CANCELLED":
        return
    print(error.code, str(error))
```

调用方取消、执行超时或 Worker 被终止时，脚本执行结果分别表现为 `Cancelled`、`TimedOut` 或 `WORKER_TERMINATED` 诊断；它们不是普通的 `PROVIDER_FAILURE`，带副作用的操作不能自动重试。

## 9. 类型参考

### request 字典结构

`context.request` 是完整执行请求（`ScriptExecutionRequest`）的 JSON 字典：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `target` | dict 或 None | 编辑器目标；应用/自动化入口为 None。 |
| `arguments` | dict | 执行参数字典。 |
| `source` | str | `Manual`、`Editor`、`Startup`、`Automation`、`WorkItemCreated`、`WorkItemSaved` 或 `TagAdded`。 |
| `entryKind` | str | `Application`、`Editor`、`Automation` 或 `Query`。 |
| `idempotencyKey` | str 或 None | 业务幂等键。 |
| `preview` | bool | 是否预览。 |

枚举字段以字符串形式出现。`context.workItem` 是事项目标的不可变快照，字段与查询结果中的 `item` 相同（见附录 C）。

### 入口返回值约定

入口必须是同步函数（不支持 `async def`）：

- 返回 awaitable → 抛 `RuntimeError`，执行失败。
- 正常返回（或返回可 JSON 序列化的值）→ 执行成功（`Succeeded`）；返回值会放进 worker 执行结果的 `value` 字段，但宿主侧当前不消费（**返回值不参与执行状态**）。例外：若返回宿主 API 的结果字典（如 `logItems.create` 的返回值），其中的 `effects` 字段会被 Worker 提取并随执行结果传回宿主，显示在管理页执行历史与完成通知中（追加条数、预览、幂等重放、新建 ID）。
- 抛异常 → `Failed` + `PYTHON_EXECUTION_FAILED` 诊断（附行号）。
- `HostCallError` 未被捕获 → `Failed` + `PYTHON_HOST_CALL_FAILED`。
- 执行已取消 → `Cancelled`。
- 超时、Worker 终止由宿主报告，脚本无需处理。

### 自动化触发器上下文

Python 自动化脚本的上下文额外提供 `context.automation` 字典：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `trigger` | str | `Scheduled`、`Startup`、`WorkItemCreated`、`WorkItemSaved`、`TagAdded` 或 `Unknown`。 |
| `eventData` | dict | 事件数据（当前为执行参数字典）。 |
| `idempotencyKey` | str 或 None | 自动化执行幂等键。 |

自动化脚本放 `application` 目录，metadata 的 `entryKind` 写 `Automation`；`schedule` 字段（`"daily HH:mm"`）配置每日定时，`runOnStartup`（true/false）配置启动补跑，`triggers` 数组配置 `WorkItemCreated`、`WorkItemSaved`、`TagAdded`。事件型自动化可省略 `schedule`；事件数据通过 `context.automation["eventData"]` 提供，工作项事件包含 `workItemId`、`date`、`comment`、`time`、`priority`，标签事件额外包含 `tagId`、`tagName`、`tagLevel`、`tagSource`、`sequence`。

## 附录 A. `apiError` 错误码总表

`apiError` 结构：`code`（str，稳定大写码）、`message`（str）、`category`（`Validation`、`Permission`、`Host`、`Provider` 或 `Cancellation`）、`retryable`（bool）、`details`（可选字典，当前未使用）。

当前 API 实际产生的错误码：

| `apiError["code"]` | 来源（`error["code"]`） | category | retryable | 说明 |
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
| `PYTHON_EXECUTION_FAILED` | Python 脚本运行时异常。 |
| `PYTHON_ENTRYPOINT_MISSING` | 入口函数缺失。 |
| `PYTHON_SYNTAX_ERROR` | Python 语法错误。 |
| `PYTHON_API_FORBIDDEN` | 使用被禁止的导入、名称或属性。 |
| `PYTHON_HOST_CALL_FAILED` | HostCall 抛出 `HostCallError`。 |
| `PYTHON_WORKER_BUSY` | Python Worker 已有执行在进行。 |
| `SCRIPT_DESCRIPTOR_INVALID` | descriptor 与入口不一致。 |
| `SCRIPT_ENTRY_KIND_MISMATCH` | 入口类型与作用域/目标不匹配。 |
| `SCRIPT_TARGET_INVALID` | 编辑器目标校验失败。 |
| `WORKER_TERMINATED` | Worker 进程异常退出或通道断开。 |
| `SCRIPT_EXECUTION_TIMED_OUT` | 执行超过时限。 |
| `WORKER_HOST_CALL_LIMIT` | 宿主调用次数超限。 |
| `WORKER_MESSAGE_TOO_LARGE` | Worker 消息超过大小限制。 |

## 附录 C. DTO 字段总表

- `item`（工作项）：`id`(int)、`date`、`comment`、`hours`(float)、`priority`(int，0-9)、`note`(str 或 None)、`tags`(list)。
- `tag`：`id`(int)、`name`、`color`(int)、`level`(int)、`isPrimary`(bool)、`disabled`(bool)、`metadata`(dict[str, str])。`metadata` 是只读字符串键值对，推荐使用 `projectNumber` 保存项目编号；推荐使用 `isPrimary` 判断主标签，`level` 保留用于兼容。
- `instance`（Tracker 实例）：`pluginId`、`instanceId`、`displayName`、`icon`、`isConfigured`(bool)。
- `template`：`id`、`name`、`defaultTitle`、`defaultHours`(float)、`defaultWorkTagIds`(list[int])。
