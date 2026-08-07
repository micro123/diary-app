# Python 脚本 API 参考

## 脚本入口

Python 脚本必须定义同步函数 `main(context)` 或 `execute(context)`。不支持 `async def` 或返回其他 awaitable 对象。

```python
def main(context):
    result = context.diary.workItems.query(
        startDate="2026-08-01",
        endDate="2026-08-31",
        limit=100,
    )
    if not result["succeeded"]:
        raise RuntimeError(result["error"]["message"])
```

## 执行上下文

`context` 支持属性访问和等价的字典式访问：

| 属性 | 含义 |
| --- | --- |
| `request` | 完整的、可转换为 JSON 的执行请求字典。 |
| `arguments` | 执行参数字典。 |
| `target` | 目标字典；编辑器日期范围位于 `target["editor"]`。 |
| `source` | 执行来源名称。 |
| `diary` | 宿主 API 根对象。 |

请求和结果字典使用 camelCase 字段名，例如 `startDate`、`endDate` 和 `normalizedQuery`。

## 查询工作项

`context.diary.workItems.query(params=None, **kwargs)` 默认可用，不需要单独申请权限。参数可以通过一个字典、关键字参数或两者一起传入。

| 字段 | 含义 |
| --- | --- |
| `startDate`、`endDate` | 包含边界的 ISO 日期范围，格式为 `yyyy-MM-dd`。 |
| `tagIds` | 数字标签 ID 列表。 |
| `tagFilter` | `Ignore`、`Any`、`All`、`None` 或 `Exact`。 |
| `text` | 文本筛选条件。 |
| `priority` | 数字优先级筛选。 |
| `limit`、`offset` | 分页参数。 |

返回字典包含 `succeeded`、`items`、`normalizedQuery` 和 `error`。工作项包含 `id`、`date`、`comment`、`hours`、`priority`、`note` 和 `tags`。

宿主调用失败会抛出 `HostCallError`。其 `code` 属性可用于识别 `PermissionDenied`、`InvalidInput` 和 `DatabaseUnavailable` 等错误。

## 沙箱限制

- 禁止导入模块。
- 禁止文件访问、动态代码执行、运行时自省、输入以及双下划线运行时属性。
- 只能使用 Worker 暴露的安全内置函数，包括常用集合、数值、迭代、异常和 `print` 函数。
- `print` 会重定向到 Worker 协议流之外，并受到 Worker 输出大小限制。
