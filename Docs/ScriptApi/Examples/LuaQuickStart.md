# Lua 5 分钟入门：查询并追加日志项

在脚本创建向导中选择 Lua 和 Application 入口，将下面代码保存为脚本。入口必须命名为 `application_main(context)`；宿主 API 从全局 `diary` 表访问。

```lua
function application_main(context)
    local date = context.arguments.date or "2026-08-09"
    local query = diary.workItems.query({
        startDate = date,
        endDate = date,
        limit = 100
    })
    if not query.succeeded then
        error((query.error and query.error.message) or "查询失败")
    end

    context.progress.report(0.5, "已查询 " .. tostring(#query.items) .. " 条工作项")

    local append = diary.logItems.create({
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

- 通过 `context.arguments.date` 传入日期；示例默认使用 `2026-08-09`。
- `diary.workItems.query` 是只读查询，`diary.logItems.create` 只追加新记录。
- 使用同一业务动作的 `idempotencyKey`，重复执行不会再次追加。
- `context.preview` 会让宿主只返回预览结果，不写入数据库。
- Lua 的 HostCall 失败会抛出异常；需要区分错误时，读取错误文本中的稳定错误代码并按 API Reference 处理。

相关章节：[Lua API Reference](../Lua.md)。
