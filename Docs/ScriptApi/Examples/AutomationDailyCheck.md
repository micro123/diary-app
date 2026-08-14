# 每日自查补录（自动化脚本）

这是一个 Python、Lua 和 C# 自动化脚本示例：每天定时检查「昨天是否有工作记录」，没有则自动补录一条并弹通知。

- 定时到点或启动补跑时由宿主自动执行，触发类型在 `context.automation.trigger` 中（`Scheduled` / `Startup`）
- 补录使用 `idempotencyKey`（`auto-daily-check:{昨天日期}`）：同一天即使重复触发（如崩溃后重启补跑）也不会重复追加
- 查询用 `range = "yesterday"` 快捷值，无需脚本自己计算日期（Lua 沙箱没有 os.date，快捷值是推荐做法）
- 结尾返回 create 结果（Python/Lua 返回结果表，C# 把 `Effects` 放进执行结果）：宿主会把其中的 `effects`（追加条数、幂等重放、新建 ID）显示在执行历史和完成通知中

```python
def automation_main(context):
    trigger = context.automation["trigger"]
    context.log.info(f"自动化触发：{trigger}")

    result = context.diary.workItems.query(limit=1, range="yesterday")
    if not result["succeeded"]:
        raise RuntimeError(result["error"]["message"])
    if result["items"]:
        context.log.info("昨日已有记录，跳过补录")
        return None

    yesterday = result["normalizedQuery"]["startDate"]
    append = context.diary.logItems.create({
        "date": yesterday,
        "hours": 0.5,
        "title": "昨日无记录自动补录",
        "note": "自动化脚本补录，请修改为实际工作内容。",
        "idempotencyKey": f"auto-daily-check:{yesterday}",
    })
    if not append["succeeded"]:
        raise RuntimeError(append["error"]["message"])

    context.diary.ui.notify(
        "自动化脚本",
        f"昨天（{yesterday}）没有工作记录，已自动补录一条，请核对并修改。",
    )
    return append
```

## 使用方式

1. 在脚本创建向导中选择目标语言、「应用脚本」类型、「自动化脚本」样板，设置调度时间（如 `daily 09:00`）与是否启动补跑。
2. 复制对应示例文件中的代码：
   - Python：`Docs/ScriptApi/Examples/AutomationDailyCheck.py`
   - Lua：`Docs/ScriptApi/Examples/AutomationDailyCheck.lua`
   - C#：`Docs/ScriptApi/Examples/AutomationDailyCheck.cs`
3. 如果使用 C#，请将脚本 ID 设置为 `automation-daily-check`；或者同步修改 C# 源码中的 `Id` 属性和脚本 metadata。
4. 保存后在脚本管理页重新加载；到点后应用会自动执行，也可在 metadata 中开启「启动补跑」立即验证。

## 自动化条件配置

调度与补跑配置在脚本 metadata（`<脚本ID>.<扩展名>.json`）中，也可以直接在脚本管理页的「metadata 设置」区修改：

```json
{
  "entryKind": 3,
  "schedule": "daily 09:00",
  "runOnStartup": true
}
```

- `schedule`：`daily HH:mm`，留空表示不定时、仅手动运行
- `runOnStartup`：应用启动后是否补跑一轮
- 应用未运行期间错过的时间点会在下次启动时自动补跑；同一天的同一触发点不会重复执行
