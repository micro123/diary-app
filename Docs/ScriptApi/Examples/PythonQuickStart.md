# Python 5 分钟入门：查询并追加日志项

在脚本创建向导中选择 Python 和 Application 入口，将下面代码保存为脚本。入口必须命名为 `application_main(context)`；Worker 不支持 `async def`。

```python
def application_main(context):
    date = context.arguments.get("date", "2026-08-09")
    query = context.diary.workItems.query({
        "startDate": date,
        "endDate": date,
        "limit": 100,
    })
    if not query["succeeded"]:
        error = query.get("error") or {}
        raise RuntimeError(error.get("message", "查询失败"))

    context.progress.report(0.5, f"已查询 {len(query['items'])} 条工作项")

    append = context.diary.logItems.create({
        "date": date,
        "hours": 0.5,
        "title": f"{date} 工作摘要",
        "note": f"当天共有 {len(query['items'])} 条工作项。",
        "idempotencyKey": f"daily-summary:{date}",
        "preview": context.preview,
    })
    if not append["succeeded"]:
        error = append.get("error") or {}
        raise RuntimeError(error.get("message", "追加日志项失败"))

    print(f"追加结果：{append['item']['id']}")
```

使用说明：

- 通过 `context.arguments["date"]` 传入日期；示例默认使用 `2026-08-09`。
- `context.diary.workItems.query` 是只读查询，`context.diary.logItems.create` 只追加新记录。
- 使用同一业务动作的 `idempotencyKey`，重复执行不会再次追加。
- `context.preview` 会让宿主只返回预览结果，不写入数据库。
- 返回型 API 通过 `succeeded` 和 `error.code` 判断失败；抛出的 HostCall 使用 `HostCallError.code`。

相关章节：[Python API Reference](../Python.md)。
