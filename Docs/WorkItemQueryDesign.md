# 自定义事项查询设计

## 1. 文档范围

本文记录 Diary.App 自定义事项查询的当前实现、查询语义、数据库边界和后续扩展计划。

当前已经实现查询模型、规范化校验、SQLite/PostgreSQL provider、跨数据库契约测试、独立查询页面、统计详情复用和保存查询。
脚本宿主已经提供受限的只读查询 API，C#、Lua 和 Python 脚本目录发现、构建、管理入口及独立 Worker
路由已经接入；数据库 reader 级流式查询、跨平台运行时打包和更完整的 Tracker 脚本 API 仍在后续计划中。

## 2. 目标

- 支持按日期范围、标签、标题/备注关键字和优先级组合查询。
- SQLite 与 PostgreSQL 使用相同查询语义。
- 多标签查询不重复返回同一事项。
- 查询结果使用日期和事项 ID 稳定排序。
- 查询接口只读取事项、备注和标签，不修改模板或 Tracker 数据。
- 统计页面和脚本 API 复用同一个结构化查询模型。

## 3. 当前模型

查询模型位于 `Diary.Core/Data/Base/WorkItemQuery.cs`：

```csharp
public sealed record WorkItemQuery
{
    public string? StartDate { get; init; }
    public string? EndDate { get; init; }
    public IReadOnlyCollection<int> TagIds { get; init; }
    public WorkItemTagFilter TagFilter { get; init; }
    public string? Text { get; init; }
    public WorkPriorities? Priority { get; init; }
    public int? Limit { get; init; }
    public int Offset { get; init; }
}
```

查询进入 provider 前由 `WorkItemQueryNormalizer` 校验并规范化。日期必须是
`yyyy-MM-dd`，标签 ID 去重且最多 500 个，`Limit` 必须在 1 到 10,000 之间，
偏移必须非负并与上限一起使用；无效枚举、日期范围或标签组合会被拒绝。

数据库统一入口：

```csharp
ICollection<WorkItem> QueryWorkItems(WorkItemQuery query);
```

`DbInterfaceBase` 只定义查询契约，provider 负责 SQL 语法、参数绑定和字段映射。

## 4. 标签匹配语义

当前支持五种模式：

| 模式 | 语义 |
| --- | --- |
| `Ignore` | 不使用标签筛选 |
| `Any` | 事项至少具有一个所选标签 |
| `All` | 事项具有全部所选标签，可以同时具有其他标签 |
| `None` | 事项没有任何标签 |
| `Exact` | 事项标签集合与所选集合完全一致 |

空标签集合的行为：

- `Ignore`：不筛选标签。
- `Any`、`All`：空标签集合会被拒绝，避免空条件被误解为匹配全部。
- `None`：返回无标签事项。
- `Exact`：等价于无标签集合，返回无标签事项。

`TagIds` 在查询前去重，因此重复选择不会改变 `All` 或 `Exact` 的计数结果。

## 5. 其他查询条件

### 5.1 日期

`StartDate` 和 `EndDate` 都是可选条件，边界包含在结果中。

```text
create_date >= StartDate
create_date <= EndDate
```

当前核心日期格式为 `yyyy-MM-dd`，provider 按现有 schema 字符串日期语义查询。

### 5.2 关键字

关键字同时搜索：

- `work_items.comment` 事项标题。
- `work_notes.note` 事项备注。

当前使用大小写不敏感的子串查询：

- SQLite：`instr(lower(...), lower($text))`。
- PostgreSQL：`strpos(lower(...), lower($n))`。

所有用户输入通过 provider 参数绑定，不拼接进 SQL。

### 5.3 优先级

`Priority` 为 `null` 时忽略条件，否则精确匹配 `WorkPriorities` 数值。

### 5.4 分页和排序

结果固定排序：

```text
create_date ASC, id ASC
```

规范化后的 `Limit` 应用分页，稳定排序保证同一查询条件下分页不会因为未定义行顺序而漂移。

## 6. Provider 实现

SQLite 和 PostgreSQL 都使用相关子查询处理标签，不把标签表直接展开为结果行，因此一个事项只返回一次。

```text
WorkItemQuery
  -> provider 生成参数化 SQL
  -> work_items 核心条件
  -> work_notes 关键字 EXISTS
  -> work_item_tags 标签计数/EXISTS
  -> 日期、ID 稳定排序
  -> WorkItem 集合
```

`Any`、`All` 和 `Exact` 使用所选标签计数；`Exact` 额外比较事项的总标签数。
`None` 和空集合 `Exact` 使用 `NOT EXISTS`。

查询流程图见 [事项查询流程](diagrams/work-item-query.svg)，源文件为
[`work-item-query.puml`](diagrams/work-item-query.puml)。

## 7. 查询页面

页面入口：左侧导航“事项查询”。

当前页面支持：

- 默认选择本月日期范围。
- 修改开始日期和结束日期。
- 搜索事项标题或备注。
- 选择全部优先级或具体优先级。
- 多选标签。
- 选择五种标签匹配模式。
- 展示日期、事项、耗时、优先级和标签。
- 进入页面时同步当前未禁用标签。
- 查询失败时保留上一次成功结果并显示错误状态。
- 从结果定位到对应日期和事项。
- 保存、应用、更新、重命名和删除常用查询条件。
- 批量加载结果标签，避免逐事项查询。
- 默认结果上限为 200，超出上限时提示用户调整条件。

当前页面限制：

- 保存查询当前使用独立本地 JSON 文件，尚未提供导入导出。
- 大结果集的标签读取按 provider 支持的批次大小分块。

## 8. 统计复用

统计详情已经迁移到 `QueryWorkItems`：

- 一级标签使用 `Any` 和单个标签 ID。
- 父子标签组合使用 `All` 和两个标签 ID。
- 日期范围继续使用统计页当前开始和结束日期。

旧 `GetWorkItemsByTagAndDate` 暂时保留为兼容入口，但当前统计代码不再依赖其固定 `l1/l2` 参数。

## 9. 测试覆盖

共享 `DbContractTests` 同时运行在 SQLite 和 PostgreSQL：

- 日期范围包含两端。
- `Ignore` 不筛选标签。
- `Any` 返回任意匹配且结果不重复。
- `All` 要求全部所选标签。
- `None` 只返回无标签事项。
- `Exact` 要求完全相同标签集合。
- 空集合 `Exact` 返回无标签事项。
- 重复标签 ID 不影响查询语义。
- 关键字同时搜索标题和备注且忽略大小写。
- 优先级与标签条件组合。
- 分页使用日期和 ID 稳定顺序。
- 空结果返回空集合而不是 `null`。

## 10. 脚本只读宿主

`Diary.ScriptHost` 提供 `IDiaryApi`，将脚本 DTO 转换为核心
`WorkItemQuery`，复用同一规范化和 provider 查询语义。工作项查询只返回事项、备注和
标签的不可变 DTO；输入无效、数据库失败或取消时返回结构化错误。脚本查询已覆盖
错误隔离、敏感字段和 SQLite 结果一致性测试。

后续计划：

- 评估总数查询和真正的分页结果模型。
- 评估按 Tracker 绑定条件查询，但不能让核心模型依赖具体 Tracker 类型。

## 11. 维护约定

- 新查询条件必须在 SQLite/PostgreSQL 中保持相同语义。
- 新查询条件必须增加共享数据库契约测试。
- 用户输入必须使用参数绑定。
- 标签模式的空集合语义不能隐式改变。
- 查询接口只读，不在查询过程中修改工作项、标签、模板或 Tracker 绑定。
- 查询排序变更必须同步评估分页稳定性。
