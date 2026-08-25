# Python 5 分钟入门：查询并追加日志项

在脚本创建向导中选择 Python 和 Application 入口，将下面代码保存为脚本。入口必须命名为 `application_main(context)`；Worker 不支持 `async def`。向导默认创建 V2，请在相邻的 `.py.json` metadata 中声明脚本使用的日期参数。下面使用 `daily-summary-python` 作为示例 ID；如果向导中填写了其他 ID 或名称，应保留向导生成的值，只补充 `parameters`：

```json
{
  "apiVersion": "V2",
  "id": "daily-summary-python",
  "name": "每日摘要",
  "engine": "python",
  "scope": "Application",
  "entryKind": "Application",
  "parameters": [
    {
      "name": "date",
      "label": "日期",
      "type": "Date",
      "required": true
    }
  ]
}
```

```python
def application_main(context):
    date = context.arguments["date"]
    query = context.diary.work_items.query({
        "startDate": date,
        "endDate": date,
        "limit": 100,
    })
    if not query["succeeded"]:
        error = query.get("error") or {}
        raise RuntimeError(error.get("message", "查询失败"))

    context.progress.report(0.5, f"已查询 {len(query['items'])} 条工作项")

    append = context.diary.log_items.create({
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

- 管理页根据 V2 metadata 显示必填日期控件，校验后通过 `context.arguments["date"]` 提供规范化值。
- `context.diary.work_items.query` 是只读查询，`context.diary.log_items.create` 只追加新记录。
- 使用同一业务动作的 `idempotencyKey`，重复执行不会再次追加。
- `context.preview` 会让宿主只返回预览结果，不写入数据库。
- 返回型 API 通过 `succeeded` 和 `error.code` 判断失败；抛出的 HostCall 使用 `HostCallError.code`。

相关章节：[Python API Reference](../Python.md)。
