# V2 参数化工时汇总

该示例展示脚本 API V2 的加载期参数契约。宿主在加载脚本后即可读取参数名称、类型、必填状态、Choice 选项和默认值；执行前会合并默认值并把参数规范化为字符串。

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

脚本收到的值仍是字符串。其中 Boolean 固定为小写 `true`/`false`，Number 使用 invariant culture，Choice 必须精确匹配 metadata 中的 `value`。

当前类型化参数 UI 尚未接入。手动测试时仍可在现有参数文本框中输入：

```text
range=thisMonth
minimumHours=1.5
includeZero=true
```
