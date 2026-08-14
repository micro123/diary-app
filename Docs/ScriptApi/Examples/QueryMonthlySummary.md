# 本月工时按主标签汇总（查询脚本）

这是一个 Python、Lua 和 C# 查询脚本示例：统计本月全部工作项，按主标签聚合工时并输出到脚本日志。脚本只做只读查询，不追加任何记录。

- 查询入口（`query_main` / `QueryScript`）与应用脚本使用相同的宿主 API，区别在入口语义：查询脚本约定为只读用途，便于在管理页与执行历史中区分
- 用 `range = "thisMonth"` 快捷值拿到本月范围，用分页流式查询避免单次 1000 条上限
- 主标签通过 `isPrimary` 判断（`level` 保留兼容）

```python
def query_main(context):
    totals = {}
    for item in context.diary.workItems.stream(pageSize=500, range="thisMonth"):
        tag_name = "无"
        for tag in item.get("tags", []):
            if tag.get("isPrimary", False):
                tag_name = tag.get("name") or "无"
                break
        totals[tag_name] = totals.get(tag_name, 0.0) + item.get("hours", 0.0)

    lines = ["主标签 | 工时"]
    for name, hours in sorted(totals.items(), key=lambda pair: pair[1], reverse=True):
        lines.append(f"{name} | {hours:.2f} 小时")

    context.log.info("\n".join(lines))
    return None
```

## 使用方式

1. 在脚本创建向导中选择目标语言、「应用脚本」类型、「查询脚本」样板。
2. 复制对应示例文件中的代码：
   - Python：`Docs/ScriptApi/Examples/QueryMonthlySummary.py`
   - Lua：`Docs/ScriptApi/Examples/QueryMonthlySummary.lua`
   - C#：`Docs/ScriptApi/Examples/QueryMonthlySummary.cs`
3. 如果使用 C#，请将脚本 ID 设置为 `query-monthly-summary`；或者同步修改 C# 源码中的 `Id` 属性和脚本 metadata。
4. 在脚本管理页选中脚本后点「运行」，汇总结果输出到「运行日志」Tab；执行历史中该脚本的来源为「手动执行」、入口为「查询入口」。

## 注意

查询入口目前不强制只读——技术上网关不阻止查询脚本调用写入 API；「只读」是入口语义约定。需要运行时强制只读时，可以再为 Query 入口加写入 API 门禁。
