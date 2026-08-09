# Python 脚本 API Reference

Python 使用 `ScriptApiVersion.V1`。脚本在独立 Python Worker 中执行，通过 `context.diary` 访问宿主 API。入口必须是同步函数，Worker 不支持 `async def` 或返回 awaitable。

## 1. 脚本入口和上下文

入口函数由脚本的 `entryKind` 决定：`application_main(context)`、`editor_main(context)`、`automation_main(context)`，以及当前仅预留的 `query_main(context)`。Worker 不再通过通用 `main(context)` 或 `execute(context)` 隐式判断场景。

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
| `dateRange` | 年、季度、月、日目标的日期范围；事项目标为 `None`。 |
| `workItem` | 事项目标的不可变事项快照；其他目标为 `None`。 |
| `getDateRange()` | 获取当前目标日期范围；无范围时返回 `None`。 |
| `items.stream()` | 按当前日期范围分页迭代事项。 |
| `log` | 调试日志 API。 |

请求、参数和结果字段使用 camelCase，例如 `startDate`、`endDate`、`normalizedQuery`。脚本自动化只能追加工作记录，不提供删除或直接改写历史记录；`idempotencyKey` 当前只在宿主进程生命周期内有效。
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
        "tags": [{"id": 1, "name": "开发", "color": 0, "level": 0, "disabled": False}],
    }],
    "normalizedQuery": {...},
    "error": None,
}
```

查询是只读的。返回的字典是 JSON 数据，不是可写入宿主数据库的对象。

## 3. 创建日志项

### 流式查询大量明细

```python
for item in context.diary.workItems.stream(
    startDate="2026-01-01",
    endDate="2026-12-31",
    pageSize=500,
):
    print(item["date"], item["comment"])
```

Python 生成器按需调用 `workItems.query`，一页消费完后才拉取下一页。`pageSize` 必须在 1 到 500 之间。非流式查询单次最多返回 1000 条。

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

失败时返回 `succeeded = False` 和 `error`。错误代码包括 `InvalidInput`、`DatabaseUnavailable`、`ProviderFailure`、`Cancelled`。该 API 只返回新建项的安全 DTO。

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

`defaultTitle`、`defaultHours` 和 `defaultWorkTagIds` 只描述模板默认值，不提供模板写入能力。

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

返回的 `instance` 包含 `pluginId`、`instanceId`、`displayName`、`icon`、`isConfigured`。`context.diary.trackerInstances.list()` 返回当前已启用实例的同一 DTO 列表，并按显示名称稳定排序。错误代码为 `InvalidInput` 或 `InstanceUnavailable`。不暴露 Tracker 客户端、配置、数据库或 DI。

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

所有 HostCall 失败都会抛出 `HostCallError`，其 `code` 属性可用于分类处理：

```python
try:
    context.diary.logItems.create({"date": "bad", "hours": 1, "title": "测试"})
except HostCallError as error:
    print(error.code)
```

## 7. Worker API 和沙箱

| Python API | Worker HostCall |
| --- | --- |
| `context.diary.workItems.query` | `workItems.query` |
| `context.diary.templates.list` | `templates.list` |
| `context.diary.host.list` | `host.capabilities.list` |
| `context.diary.logItems.create` | `logItems.create` |
| `context.diary.trackerInstances.get` | `trackerInstances.get` |
| `context.diary.clipboard.get` | `clipboard.get` |
| `context.diary.clipboard.set` | `clipboard.set` |
| `context.diary.ui.notify` | `ui.notify` |
| `context.diary.ui.confirm` | `ui.confirm` |
| `context.log.*` | `log.write` |

Python Worker 禁止导入模块、文件访问、动态代码执行、运行时自省、输入和双下划线属性。仅允许安全内置函数；`print` 重定向到 Worker 日志流并受大小限制。脚本不能直接访问网络、进程、数据库、DI 或 UI 控件。

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

会抛出异常的 HostCall 可以捕获 `HostCallError`，其 `code` 使用同一套大写错误码：

```python
try:
    context.diary.ui.confirm("继续", "是否继续？")
except HostCallError as error:
    if error.code == "CANCELLED":
        return
    print(error.code, str(error))
```

调用方取消、执行超时或 Worker 被终止时，脚本执行结果分别表现为 `Cancelled`、`TimedOut` 或 `WORKER_TERMINATED` 诊断；它们不是普通的 `PROVIDER_FAILURE`，带副作用的操作不能自动重试。
