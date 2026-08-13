# 查询指定时间范围内的“加班”工作项

这些是 Python、Lua 和 C# Editor 脚本示例。它们从日期或工作项右键菜单获取查询范围，按标签名称精确匹配“加班”，并使用对话框显示结果；没有匹配项时显示“无”。

```python
TAG_NAME = "加班"


def primary_tag_name(item):
    for tag in item.get("tags", []):
        if tag.get("isPrimary", False):
            return tag.get("name") or "无"
    return "无"


def editor_main(context):
    date_range = context.getDateRange()
    if date_range is not None:
        items = context.items.stream()
    elif context.workItem is not None:
        work_item_date = context.workItem["date"]
        items = context.diary.workItems.stream(
            startDate=work_item_date,
            endDate=work_item_date,
            pageSize=500,
        )
    else:
        raise RuntimeError("请从日期或工作项右键菜单执行此脚本。")

    matched_items = []
    for item in items:
        if any(tag.get("name") == TAG_NAME for tag in item.get("tags", [])):
            matched_items.append(item)

    if not matched_items:
        message = "无"
    else:
        lines = [
            "日期 | 标题 | 主标签 | 工时",
            *[
                f"{item['date']} | "
                f"{item.get('comment') or '（无标题）'} | "
                f"{primary_tag_name(item)} | "
                f"{item.get('hours', 0)} 小时"
                for item in matched_items
            ],
        ]
        message = "\n".join(lines)

    context.diary.ui.notify("加班工作项", message)
```

## 使用方式

1. 在脚本创建向导中选择目标语言、**编辑器脚本**，入口选择 **Editor**。
2. 复制对应示例文件中的代码：
   - Python：`Docs/ScriptApi/Examples/OvertimeWorkItems.py`
   - Lua：`Docs/ScriptApi/Examples/OvertimeWorkItems.lua`
   - C#：`Docs/ScriptApi/Examples/OvertimeWorkItems.cs`
3. 如果使用 C#，请将脚本 ID 设置为 `overtime-work-items`；或者同步修改 C# 源码中的 `Id` 属性和脚本 metadata。
4. 在日历的日期、周、月、季度或年份右键菜单中执行脚本，会查询对应目标范围。
5. 在工作项上右键执行脚本时，会查询该工作项所在日期的工作项。

编辑器右键菜单由宿主自动注入目标范围，不需要手工传入 `startDate` 或 `endDate`。如果需要任意自定义日期范围，应继续使用 Application 脚本并通过参数传入日期。

脚本使用分页流式查询，因此不会受到单次 1000 条查询上限的影响；标签按名称精确匹配，不依赖预先知道“加班”标签的 ID。
