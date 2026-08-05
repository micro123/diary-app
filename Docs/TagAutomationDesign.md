# 标签自动化规则设计

## 1. 文档范围

本文设计“标签添加时为 Tracker 编辑器字段应用默认值”的能力。

典型场景：用户添加“加班”标签后，Redmine 编辑器自动将活动字段设置为“加班”；
用户之后仍然可以手动改成其他活动。应用模板时，如果模板添加了标签，也必须触发相同规则。

本文是目标设计，当前代码尚未实现完整的标签自动化协调器、规则编辑 UI 和 Tracker 规则存储。

## 2. 核心原则

- 标签是核心领域对象，不包含 Redmine、GitHub、Linear 等具体字段。
- 规则属于 Tracker 插件实例配置，不是全局的 `TagId -> 某个字段` 映射。
- 一个标签可以关联多个 Tracker 实例。
- 同一个 Tracker 实例可以配置多条规则。
- 标签规则只应用默认值，不建立不可变的强绑定。
- 当前字段已有值时，默认不覆盖用户值。
- 删除标签不会反向清除或恢复 Tracker 字段。
- 脚本不能操作模板，也不能绕过标签自动化和 Tracker 权限边界。
- 模板由宿主/编辑器负责选择和应用，但模板添加标签时必须触发标签添加规则。

## 3. 触发语义

标签自动化的触发条件是：

```text
标签从“不存在”变为“存在”
```

不是“模板中包含标签”，也不是“工作项加载后发现标签”。

### 3.1 必须触发的场景

- 用户手动添加标签。
- 应用模板添加标签。
- 批量操作添加标签。
- 新建工作项时，宿主明确执行添加标签操作。

### 3.2 不应触发的场景

- 打开已有工作项并从数据库加载标签。
- 保存后重新加载工作项。
- 切换当前工作项。
- 删除标签。
- Tracker 初始化或重新注册。
- 模板读取已存在标签但没有产生新增。

如果工作项已经有“开发”标签，应用包含“开发、加班”的模板时，只对“加班”触发规则。
已有标签不会因为再次出现在模板中而重复触发。

## 4. 添加事件和顺序

核心编辑器应把手动添加、模板添加和批量添加统一为标签添加事件。

```csharp
public enum TagAddSource
{
    User,
    Template,
    Batch,
}

public sealed record TagAddedEvent(
    WorkTag Tag,
    TagAddSource Source,
    int Sequence);
```

批量添加时必须保留实际添加顺序：

```csharp
public sealed record TagAddBatch(
    IReadOnlyList<WorkTag> Tags,
    TagAddSource Source);
```

事件流程：

```text
准备添加标签
  -> 判断标签是否已经存在
  -> 不存在则写入当前草稿/本地绑定
  -> 发布 TagAddedEvent
  -> Tracker 实例按规则应用默认值
  -> 继续处理下一个标签
```

示例：模板标签顺序为：

```text
开发 -> 加班 -> 紧急
```

则规则按这个顺序处理。不能先把标签合并成无序集合，再一次性应用规则。

标签添加事件只描述事实，不包含模板对象。模板仍然是宿主内部的输入来源，脚本不能通过事件取得模板并操作模板。

## 5. 规则归属和配置结构

规则按以下层级归属：

```text
PluginId
  -> InstanceId
      -> RuleId
          -> TagId
          -> Match
          -> Action
```

例如：

```text
tracker.redmine
  redmine.company
    rule-1: TagId=12 -> ActivityId=9
    rule-2: TagId=15 -> ActivityId=10

  redmine.personal
    rule-3: TagId=12 -> ActivityId=3
```

同一个核心标签可以在不同 Tracker 实例中映射为不同字段值。

Redmine 配置建议：

```csharp
public sealed class RedMineInstanceSettings : RedMineConfig
{
    public string InstanceId { get; set; } = RedMinePluginConstants.DefaultInstanceId;
    public string DisplayName { get; set; } = "RedMine工具";
    public bool Enabled { get; set; }

    public IList<RedMineTagRule> TagRules { get; set; } = new List<RedMineTagRule>();
}

public sealed class RedMineTagRule
{
    public string RuleId { get; set; } = Guid.NewGuid().ToString("N");
    public int TagId { get; set; }
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
    public RedMineTagRuleMatch Match { get; set; } = new();
    public RedMineTagRuleAction Action { get; set; } = new();
}

public sealed class RedMineTagRuleMatch
{
    public TagRuleTrigger Trigger { get; set; } = TagRuleTrigger.TagAdded;
    public bool OnlyApplyUnsetFields { get; set; } = true;
}

public sealed class RedMineTagRuleAction
{
    public int? ActivityId { get; set; }
    public int? IssueId { get; set; }
}
```

上面的 Redmine 类型只属于 Redmine 插件。核心代码不得解析 `ActivityId`、`IssueId` 或其他 Redmine 字段。

## 6. 通用扩展点

核心编辑器通过通用协调器通知已注册的 Tracker 实例，Tracker 插件自行解释自己的规则和字段。

```csharp
public sealed record TagAutomationContext(
    bool UserInitiated,
    bool OnlyApplyUnsetFields,
    TagAddSource Source,
    int Sequence,
    IReadOnlyCollection<int> AddedTagIds,
    IReadOnlyCollection<int> RemovedTagIds);

public sealed record TagAutomationResult(
    TrackerKey Key,
    bool Applied,
    IReadOnlyCollection<string> ChangedFields,
    IReadOnlyCollection<string> Conflicts,
    string? Error = null);
```

建议的 Tracker 能力接口：

```csharp
public interface ITrackerTagRuleProvider
{
    string PluginId { get; }

    ViewModelBase? CreateRuleEditor(
        string instanceId,
        WorkTag? tag);

    TagAutomationResult ApplyRules(
        string instanceId,
        WorkItem? item,
        IReadOnlyCollection<WorkTag> before,
        IReadOnlyCollection<WorkTag> after,
        TagAutomationContext context);
}
```

核心也可以使用协调器封装多个 Provider：

```csharp
public interface ITagAutomationCoordinator
{
    IReadOnlyList<TagAutomationResult> Apply(
        WorkItem? item,
        IReadOnlyCollection<WorkTag> before,
        IReadOnlyCollection<WorkTag> after,
        TagAutomationContext context);
}
```

核心协调器负责顺序、异常隔离和结果汇总；Tracker Provider 负责读取实例配置、匹配规则和修改自己的编辑器扩展。

## 7. 默认应用和用户覆盖

默认策略为：

```text
OnlyIfUnset
```

例如：

```text
Activity 为空
添加“加班”
-> Activity = 加班
```

```text
Activity 已经是“开发”
添加“加班”
-> 保持 Activity = 开发
```

用户在标签添加前已经设置的字段也不得被覆盖。

后续可以支持以下模式，但不作为第一阶段默认行为：

```csharp
public enum TagAutomationMode
{
    OnlyIfUnset,
    AskBeforeReplace,
    AlwaysReplace,
}
```

删除标签不触发反向操作：

```text
添加“加班” -> Activity 自动设置为“加班”
用户手动修改为“开发”
删除“加班” -> Activity 仍然是“开发”
```

这样标签只是默认值来源，不成为字段的强绑定来源。

## 8. 多规则和冲突处理

同一个标签可以通过多条规则修改不同字段：

```text
加班 -> Activity = 加班
加班 -> Issue = 项目管理
加班 -> Priority = High
```

如果多个规则修改同一个字段：

1. 只处理本次实际新增的标签。
2. 忽略禁用规则。
3. 按 `Priority` 从高到低排序。
4. 同一个字段只接受第一条有效规则。
5. 当前字段已有值时，默认不覆盖。
6. 冲突写入 `TagAutomationResult.Conflicts` 并记录诊断。
7. 冲突不应阻止用户添加标签。

多个标签按添加顺序依次处理，每一步都基于前一步更新后的编辑器状态。

## 9. 规则编辑入口

规则编辑需要支持“按 Tracker 实例管理”和“按标签查看”两种视角，但不能在两个页面各自实现一套保存逻辑。

### 9.1 Tracker 实例设置页

推荐入口：

```text
设置
  -> Redmine
      -> 公司 Redmine
          -> 标签自动规则
```

页面管理当前实例的全部规则，支持：

- 新增规则。
- 编辑规则。
- 删除规则。
- 启用/禁用规则。
- 调整优先级。
- 选择核心标签。
- 选择 Redmine 活动或 Issue。
- 校验目标活动/Issue 是否仍然存在。
- 显示规则冲突和无效目标。

### 9.2 核心标签编辑器中的 Tracker 扩展

编辑“加班”标签时，可以查看各 Tracker 实例关联的规则：

```text
标签：加班

Redmine 公司实例
  活动：加班
  应用方式：字段为空时
  优先级：100

Redmine 个人实例
  活动：Extra Work
  应用方式：字段为空时
  优先级：100
```

核心标签编辑器只挂载插件贡献，不解析 Tracker 规则：

```csharp
public interface ITagRuleEditorContribution
{
    string PluginId { get; }

    ViewModelBase? CreateEditor(
        WorkTag tag,
        IReadOnlyList<TrackerInstanceDescriptor> instances);
}
```

也可以使用实例级接口：

```csharp
public interface ITrackerTagRuleEditor
{
    string PluginId { get; }
    ViewModelBase? CreateEditor(string instanceId, WorkTag tag);
}
```

### 9.3 共享编辑服务

两个入口必须共享同一个插件规则编辑 ViewModel 或规则编辑服务，避免一个页面保存后被另一个页面覆盖。

推荐做法：

- `RedMineConfigurationViewModel` 管理完整 Redmine 配置。
- `RedMineTagRuleEditorViewModel` 管理规则编辑状态。
- Tracker 设置页和标签编辑器都创建同一种规则编辑 ViewModel。
- 保存由 Redmine 配置对象统一持久化。
- 保存完成后发送配置更新事件，刷新已打开的规则页面。

## 10. 编辑器集成

当前 `WorkEditorViewModel.AddTag()` 已经是手动添加标签入口。目标流程为：

```text
读取当前标签集合
  -> 添加一个新标签
  -> 持久化草稿/本地绑定
  -> 生成 TagAddedEvent
  -> TagAutomationCoordinator 通知所有 Tracker 实例
  -> 各实例按规则应用默认字段
  -> 刷新 Tracker 扩展 UI
```

应用模板时必须复用同一个标签添加服务，而不是直接操作 `WorkTags`：

```text
应用模板
  -> 读取模板核心字段和默认标签
  -> 按模板顺序调用 AddTag
  -> 每个新增标签分别发布 TagAddedEvent
  -> 规则按顺序应用
```

这样可以保证手动添加和模板添加拥有完全一致的规则语义。

标签加载和数据库同步必须使用静默路径，不发布添加事件：

```csharp
SyncTags();       // 只同步状态，不触发规则
AddTag(tag);      // 事实上的新增，触发规则
```

## 11. 持久化和事务

标签自动化修改的是当前编辑器中的 Tracker 扩展草稿，最终字段随现有工作项保存流程持久化。

本地保存流程仍然是：

```text
核心工作项 + 标签 + Tracker 本地绑定
       -> 一个本地事务
       -> 成功提交
```

标签规则应用本身不能调用远程 Tracker API。远程上传仍在本地事务提交之后执行。

规则配置保存属于 Tracker 插件配置保存流程，不应和工作项保存事务混在一起。

## 12. 错误处理

- 规则引用的标签已删除：规则标记为无效并提示用户，不阻止核心标签使用。
- Redmine 活动或 Issue 已不存在：规则保留原始 ID，编辑器显示无效目标，不覆盖当前字段。
- Tracker 实例未启用：不执行该实例规则。
- Tracker 扩展未创建：记录诊断，不阻止其他 Tracker 规则。
- 一个规则执行失败：保留已应用的其他默认值，记录该实例错误。
- 配置迁移失败：保留原始规则 JSON，不覆盖核心标签和工作项。

## 13. 测试计划

核心行为：

- 手动添加标签触发规则。
- 应用模板添加标签触发规则。
- 批量添加按标签添加顺序触发规则。
- 已存在标签不会重复触发规则。
- 加载已有工作项不会触发规则。
- 删除标签不会反向清除字段。
- 当前字段已有值时默认不覆盖。
- 用户手动覆盖默认值后，重新保存和加载不会被规则恢复。

实例和规则：

- 一个标签可以关联多个 Tracker 实例。
- 同一 Tracker 实例可以配置多条规则。
- 不同实例对同一标签可以应用不同字段值。
- 多规则按优先级处理同字段冲突。
- 多标签按添加顺序处理，并使用前一步的字段状态。
- 禁用规则不会执行。
- 无效标签、无效活动和无效 Issue 不阻止核心编辑器使用。

编辑和持久化：

- Tracker 设置页可以新增、编辑、删除和启用/禁用规则。
- 标签编辑器中的 Tracker 扩展可以查看和编辑同一份规则。
- 两个编辑入口不会互相覆盖未提交修改。
- 规则配置保存和加载保持实例、规则 ID、优先级和未知字段。
- 配置迁移失败保留原始规则数据。
- 日志和诊断不输出 Token、密码或其他敏感配置。

## 14. 实施顺序

1. 定义标签添加服务和 `TagAddedEvent`，区分加载路径与新增路径。
2. 定义 `TagAutomationContext`、`TagAutomationResult` 和协调器。
3. 为 Redmine 实例配置增加规则集合及配置 schema 版本。
4. 实现 Redmine 标签规则匹配和 `OnlyIfUnset` 默认应用。
5. 将手动添加标签和模板添加标签统一接入添加服务。
6. 增加 Tracker 实例设置页的规则编辑器。
7. 增加标签编辑器的插件规则贡献入口，并复用规则编辑服务。
8. 增加多实例、多规则、顺序和用户覆盖测试。
9. 为 GitHub、Linear 等后续 Tracker 复用通用规则边界。

## 15. 维护约定

- 核心代码不得出现 `ActivityId`、`IssueId` 等 Redmine 专用规则字段。
- 新 Tracker 通过实例配置和规则 Provider 接入，不修改核心标签模型语义。
- 新规则必须声明触发时机、默认覆盖策略、冲突策略和失败行为。
- 规则应用必须是可诊断的，但日志不得包含敏感配置。
- 任何规则设计变更都必须保持“标签默认值”和“用户最终值”之间的边界清晰。
