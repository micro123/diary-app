# V2 参数化工时汇总

该示例展示脚本 API V2 的加载期参数契约。宿主在加载脚本后即可读取参数名称、类型、必填状态、Choice、Suggestions、范围、步长、长度和默认值；执行前会合并默认值并把参数规范化为字符串。

示例提供三种语言版本：

- C#：`ParameterizedSummary.cs`，参数直接由 `QueryScriptV2.Parameters` 声明；
- Lua：`ParameterizedSummary.lua` + `ParameterizedSummary.lua.json`；
- Python：`ParameterizedSummary.py` + `ParameterizedSummary.py.json`。

三个版本声明相同的参数：

| 参数 | 类型 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `range` | `Choice` | `thisWeek` | 在本周和本月之间选择。 |
| `minimumHours` | `Number` | `0` | 只统计达到该工时的事项。 |
| `includeZero` | `Boolean` | `false` | 是否包含零工时事项。 |
| `titlePrefix` | `String` | `工时汇总` | 最长 20 个字符，提供软候选但允许自由输入。 |

`minimumHours` 限制在 0 到 24 之间、步长为 0.5，界面显示“小时”但最终值不包含单位。`range` 的 Choice 是严格候选；`titlePrefix` 的 Suggestions 只是输入建议，候选列表外的文本仍然合法。

脚本收到的值仍是字符串。其中 Boolean 固定为小写 `true`/`false`，Number 使用 invariant culture，Choice 必须精确匹配 metadata 中的 `value`。管理页和 Editor 入口会根据 V2 契约生成类型化表单，并在宿主接受执行后记住该入口上次使用的参数；后台自动化不会读写这份历史。

运行时可在类型化表单中填写等价参数：

```text
range=thisMonth
minimumHours=1.5
includeZero=true
titlePrefix=团队周报
```

管理页“默认参数”保存的只是相对脚本 descriptor 的 metadata 覆盖项；恢复脚本默认值后，对应覆盖项会从 metadata 中移除。Automation 缺少必填默认值时显示“待配置”，补全前不会进入调度器。
