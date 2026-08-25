# Lua 5 分钟入门：查询并追加日志项

在脚本创建向导中选择 Lua 和 Application 入口，将下面代码保存为脚本。入口必须命名为 `application_main(context)`；宿主 API 从全局 `diary` 表访问。向导默认创建 V2，请在相邻的 `.lua.json` metadata 中声明脚本使用的日期参数。下面使用 `daily-summary-lua` 作为示例 ID；如果向导中填写了其他 ID 或名称，应保留向导生成的值，只补充 `parameters`：

```json
{
  "apiVersion": "V2",
  "id": "daily-summary-lua",
  "name": "每日摘要",
  "engine": "lua",
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

```lua
function application_main(context)
    local date = context.arguments.date
    local query = diary.work_items.query({
        startDate = date,
        endDate = date,
        limit = 100
    })
    if not query.succeeded then
        error((query.error and query.error.message) or "查询失败")
    end

    context.progress.report(0.5, "已查询 " .. tostring(#query.items) .. " 条工作项")

    local append = diary.log_items.create({
        date = date,
        hours = 0.5,
        title = date .. " 工作摘要",
        note = "当天共有 " .. tostring(#query.items) .. " 条工作项。",
        idempotencyKey = "daily-summary:" .. date,
        preview = context.preview
    })
    if not append.succeeded then
        error((append.error and append.error.message) or "追加日志项失败")
    end

    print("追加结果：" .. tostring(append.item.id))
end
```

使用说明：

- 管理页根据 V2 metadata 显示必填日期控件，校验后通过 `context.arguments.date` 提供规范化值。
- `diary.work_items.query` 是只读查询，`diary.log_items.create` 只追加新记录。
- 使用同一业务动作的 `idempotencyKey`，重复执行不会再次追加。
- `context.preview` 会让宿主只返回预览结果，不写入数据库。
- Lua 的返回结果类 HostCall（查询、创建、Tracker 实例）通过 `succeeded`/`error` 字段判断成败，失败不抛异常；非结果类调用（剪贴板、用户交互、日志和列表类）失败才抛出异常，错误文本含稳定错误代码，按 API Reference 处理。

相关章节：[Lua API Reference](../Lua.md)。
