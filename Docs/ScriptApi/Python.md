# Python 脚本 API Reference

Python 使用 `ScriptApiVersion.V1`。脚本在独立 Python Worker 中执行，通过 `context.diary` 访问宿主 API。入口必须是同步函数，Worker 不支持 `async def` 或返回 awaitable。

## 1. 脚本入口和上下文

```python
def main(context):
    result = context.diary.workItems.query(
        startDate="2026-08-01",
        endDate="2026-08-31",
        limit=100,
    )
    if not result["succeeded"]:
        raise RuntimeError(result["error"]["message"])

    for item in result["items"]:
        print(f"{item['date']}: {item['comment']}")
```

`context` 同时支持属性访问和字典式访问：

| 字段 | 说明 |
| --- | --- |
| `request` | 完整执行请求字典。 |
| `arguments` | 执行参数字典。 |
| `target` | 执行目标字典；编辑器日期范围在 `target["editor"]`。 |
| `source` | 执行来源名称。 |
| `diary` | 宿主 API 根对象。 |

请求、参数和结果字段使用 camelCase，例如 `startDate`、`endDate`、`normalizedQuery`。

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
| `offset` | int | 默认 0，最大 10000。 |

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

## 4. Tracker 实例目录

```python
result = context.diary.trackerInstances.get({
    "pluginId": "tracker.memory",
    "instanceId": "company",
})
if result["succeeded"]:
    print(result["instance"]["displayName"])
```

返回的 `instance` 包含 `pluginId`、`instanceId`、`displayName`、`icon`、`isConfigured`。错误代码为 `InvalidInput` 或 `InstanceUnavailable`。不暴露 Tracker 客户端、配置、数据库或 DI。

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
| `context.diary.logItems.create` | `logItems.create` |
| `context.diary.trackerInstances.get` | `trackerInstances.get` |
| `context.diary.clipboard.get` | `clipboard.get` |
| `context.diary.clipboard.set` | `clipboard.set` |
| `context.diary.ui.notify` | `ui.notify` |
| `context.diary.ui.confirm` | `ui.confirm` |

Python Worker 禁止导入模块、文件访问、动态代码执行、运行时自省、输入和双下划线属性。仅允许安全内置函数；`print` 重定向到 Worker 日志流并受大小限制。脚本不能直接访问网络、进程、数据库、DI 或 UI 控件。
