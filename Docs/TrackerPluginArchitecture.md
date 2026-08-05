# Tracker 插件化架构升级方案

## 1. 文档目的

本文档定义 DiaryApp 后续 tracker 插件化重构的目标架构、宿主契约、版本兼容策略、插件数据库迁移、配置页面、管理页面和工作项编辑器扩展方式。

本文档解决以下问题：

- 日记功能必须在没有任何 tracker 插件时完整运行。
- Redmine 只是一个可选 tracker，不能成为核心数据库的隐含依赖。
- 后续可以增加 Jira、PLM 或其他类似系统。
- 同时启用多个 tracker，甚至同一种 tracker 的多个实例。
- 插件可以拥有自己的数据表、配置项、管理页面和工作项编辑器扩展。
- 主程序和插件之间需要明确的版本兼容和升级协议。
- 插件数据库需要能够独立迁移，迁移失败不能破坏核心日记功能。

## 2. 当前状态

当前项目已经完成 tracker 插件化的基础骨架：

- `Diary.PluginBase` 提供 manifest、兼容性检查、插件入口、实例和迁移契约。
- `Diary.PluginUI` 提供配置、管理页、编辑器和模板贡献契约。
- `PluginHost` 已支持插件注册和迁移结果状态。
- `PluginInstanceRegistry` 已按 `(PluginId, InstanceId)` 管理实例。
- 宿主已遍历兼容插件生成 `PluginInstanceRegistration`，不再在实例注册处硬编码 Redmine。
- 插件 UI 和模板贡献已通过工厂按实例注册。
- Redmine 数据访问已拆到 `Diary.RedMine.SQLite` 和 `Diary.RedMine.PostgreSQL`。
- SQLite 和 PostgreSQL 使用共享的数据库扩展契约测试。
- Redmine schema 已有独立版本表和 0 -> 1 -> 2 迁移链。
- Redmine 表已经使用 `instance_id` 做数据隔离。

当前实现仍然存在以下缺口：

- Redmine manifest 尚未开启 `SupportsMultipleInstances`。
- `App.ConfigureCheck()` 仍保留 Redmine UI 数据初始化，实例生命周期、数据库迁移和 UI 注册尚未完全统一编排。
- `Diary.Database` 仍保留 `IRedMineDb` 扩展路径。
- 数据库扩展发现仍使用 `Diary.RedMine.*.dll` 文件模式。
- 编辑器、模板和上传状态已具备多 tracker 聚合基础，但缺少完整多实例端到端验收。
- 插件配置迁移、诊断页面、重试和卸载流程尚未完整实现。

因此当前状态是“具备可选插件和实例隔离基础”，还不是“任意 tracker 可以无核心代码改动地安装、升级、运行和移除”。

## 3. 核心设计原则

### 3.1 核心域优先

DiaryApp 的核心域只有：

- 工作项。
- 日期。
- 耗时。
- 优先级。
- 备注。
- 标签。
- 统计。

核心域不得引用 Redmine、Jira、PLM 或其他 tracker 的类型。

### 3.2 插件拥有自己的能力

tracker 插件负责：

- 远程 API。
- 远程对象模型。
- 本地 tracker 数据表。
- tracker 数据库迁移。
- tracker 配置。
- tracker 设置页面。
- tracker 管理页面。
- 工作项编辑器扩展区域。
- 上传、同步和远程状态处理。

### 3.3 插件失败不能影响日记

以下情况不能阻止核心日记功能启动：

- tracker 未配置。
- tracker 网络不可用。
- tracker 插件数据库迁移失败。
- tracker 插件程序集缺失。
- tracker 插件版本与主程序不兼容。

出现上述情况时，主程序应禁用对应 tracker，并显示清晰的状态和错误信息。

### 3.4 本地保存和远程上传分离

核心工作项和插件本地绑定属于本地数据库事务。

远程 tracker API 调用不属于本地数据库事务。远程上传失败时，核心日记和本地绑定仍应保留，并允许后续重试。

### 3.5 版本号职责单一

应用版本、插件 API 版本、核心数据库版本和插件数据库版本必须分别管理，不能混用。

## 4. 目标项目结构

建议新增插件契约项目：

```text
Diary.PluginBase
```

该项目只包含主程序和插件之间稳定、精简的契约，不包含 Avalonia 具体控件，也不包含 Redmine 类型。

建议的项目关系：

```text
Diary.Core
  核心数据模型、核心配置和核心常量

Diary.Database
  核心数据库接口和数据库 provider 能力

Diary.PluginBase
  插件 manifest、插件生命周期、数据库迁移、非 UI tracker 契约

Diary.PluginUI
  配置页面、管理页面和工作项编辑器 UI 扩展契约

Diary.App
  主程序、插件宿主、核心 UI 和插件 UI 容器

Diary.Tracker.RedMine
  Redmine API、Redmine 配置、Redmine 数据库、Redmine UI

Diary.Tracker.Jira
  Jira API、Jira 配置、Jira 数据库、Jira UI
```

依赖方向必须保持：

```text
Diary.Tracker.RedMine -> Diary.PluginBase
Diary.Tracker.RedMine -> Diary.Core
Diary.Tracker.RedMine -> Diary.Database
Diary.App             -> Diary.PluginBase
Diary.App             -> Diary.PluginUI
Diary.PluginUI        -> Diary.PluginBase
```

`Diary.Core` 不得反向引用 `Diary.PluginBase` 或任意 tracker 插件。

`Diary.PluginBase` 不得引用 Avalonia、具体 View、具体 ViewModel 或 `Diary.App`。
需要 UI 的插件契约放在 `Diary.PluginUI`，由它依赖 `Diary.GUIBase` 和 `Diary.PluginBase`。

## 5. 插件 Manifest

每个插件必须提供 manifest，用于主程序在注册服务和加载 UI 之前进行兼容性检查。

示例：

```csharp
public sealed record PluginManifest
{
    public required string Id { get; init; }
    public required string Version { get; init; }

    public int ApiVersion { get; init; }
    public uint MinCoreDataVersion { get; init; }
    public uint? MaxCoreDataVersion { get; init; }

    public IReadOnlyList<PluginDependency> Dependencies { get; init; }
        = Array.Empty<PluginDependency>();

    public IReadOnlyList<string> RequiredCapabilities { get; init; }
        = Array.Empty<string>();
}
```

Redmine 插件的 manifest 示例：

```text
Id: tracker.redmine
Version: 1.2.0
ApiVersion: 2
MinCoreDataVersion: 0x00010000
RequiredCapabilities: SqlTransactions, ForeignKeys
```

### 5.1 版本兼容规则

- `ApiVersion` 表示插件 API 契约版本。
- `MinCoreDataVersion` 表示插件需要的核心数据库最低版本。
- `MaxCoreDataVersion` 只在明确存在不兼容时使用。
- 应用版本只用于发布和显示，不作为唯一兼容判断依据。
- 插件程序集目标框架固定为主程序支持的 .NET 版本。
- API 发生破坏性变化时递增 `ApiVersion` 主版本。
- 非破坏性新增能力应通过 capability 检查，不应无故提升 API 主版本。

### 5.2 插件依赖

插件可以声明对其他插件的依赖：

```csharp
public sealed record PluginDependency(
    string PluginId,
    string VersionRange,
    bool Optional = false);
```

依赖处理规则：

- 必选依赖缺失时插件不可启用。
- 可选依赖缺失时插件可以降级运行。
- 依赖关系必须在初始化前检查，包括必选依赖存在性、声明的版本范围和必选依赖环。
- 必选依赖关系形成环时，相关插件全部进入阻塞状态；可选依赖缺失时插件可以降级运行。

## 6. 插件状态和加载生命周期

插件不应该只有加载和未加载两种状态。

建议状态：

```text
Discovered       已发现程序集
Installed        已安装
Compatible       兼容性检查通过
MigrationRequired 需要数据库迁移
Enabled          已启用
Disabled         用户主动禁用
Blocked          版本、依赖或能力不满足
MigrationFailed  数据库迁移失败
```

启动顺序：

```text
发现插件程序集
  -> 读取 manifest
  -> 检查 Plugin API
  -> 检查核心数据库版本
  -> 检查依赖和 provider 能力
  -> 加载插件配置
  -> 创建插件数据库上下文
  -> 执行插件数据库迁移
  -> 注册插件服务
  -> 创建插件实例
  -> 注册导航页、管理页和编辑器扩展
```

如果迁移失败：

- 记录插件 ID、版本、目标数据库版本和异常信息。
- 插件状态设置为 `MigrationFailed`。
- 不注册插件的业务服务和 UI。
- 核心日记仍然正常启动。
- 在设置页面提供重试和导出日志入口。

## 7. 插件数据库架构

### 7.1 核心表和插件表分离

核心数据库只创建：

```text
work_items
work_notes
work_tags
work_item_tags
data_versions
```

Redmine 插件创建：

```text
redmine_projects
redmine_activities
redmine_issues
redmine_time_entries
```

Jira 插件创建：

```text
jira_projects
jira_issues
jira_worklogs
```

不得继续在 `SQLiteDb.Initialized()` 或 `PgDb.Initialized()` 中创建 tracker 表。

### 7.2 工作项关联

每个 tracker 的本地绑定表都使用核心工作项 ID：

```text
redmine_time_entries.work_id -> work_items.id
jira_worklogs.work_id       -> work_items.id
```

一个核心工作项可以同时绑定多个 tracker：

```text
work_items
  -> redmine_time_entries
  -> jira_worklogs
  -> another_tracker_entries
```

插件表可以通过外键和 `ON DELETE CASCADE` 与核心工作项关联。核心删除工作项时，插件本地绑定应自动清理。

### 7.3 多实例支持

插件类型和插件实例必须区分：

```text
插件类型: tracker.redmine

实例:
  redmine.company
  redmine.personal
```

即使初期只支持每种 tracker 一个实例，也应预留 `InstanceId`。

插件表中的关联数据建议包含：

```text
instance_id
work_id
```

这样同一种 tracker 未来可以配置多个服务器或账号。

## 8. 插件数据库迁移

### 8.1 版本表

建议使用统一的插件版本表：

```sql
CREATE TABLE plugin_data_versions (
    plugin_id CHAR(128) PRIMARY KEY,
    schema_version INTEGER NOT NULL
);
```

插件拥有迁移 SQL 和迁移实现，主程序只负责调度和记录结果。

### 8.2 迁移接口

建议接口：

```csharp
public interface IPluginMigration
{
    string PluginId { get; }
    uint FromVersion { get; }
    uint ToVersion { get; }

    bool Up(IPluginMigrationContext context);
}
```

迁移上下文不应暴露具体的 `SQLiteConnection` 或 `NpgsqlConnection`，而应提供 provider 无关能力：

```csharp
public interface IPluginMigrationContext
{
    string ProviderName { get; }
    uint CoreDataVersion { get; }

    bool ExecRaw(string sql);
    List<T> Query<T>(string sql, Func<DbDataReader, T> map, params object[] args);
}
```

### 8.3 迁移规则

- 插件迁移版本单调递增。
- 每个迁移有明确的起始版本和目标版本。
- 迁移成功后才更新 `plugin_data_versions`。
- 失败时回滚当前迁移。
- 不自动执行降级迁移。
- 迁移脚本必须支持重复检查。
- 破坏性迁移前应提供备份或导出提示。
- 缺少迁移路径时插件进入 `MigrationFailed`。
- 插件缺失时保留原有插件数据，不自动删除。

### 8.4 事务边界

核心数据库迁移和插件数据库迁移可以按以下方式处理：

```text
核心迁移完成
  -> 每个插件单独执行迁移事务
  -> 成功后记录插件版本
  -> 失败只禁用当前插件
```

不建议把所有插件迁移放进一个超大的全局事务。一个插件失败不应该回滚核心数据库或其他正常插件。

## 9. 插件接口

建议在 `Diary.PluginBase` 中定义不依赖 UI 的主接口：

```csharp
public interface ITrackerPlugin
{
    PluginManifest Manifest { get; }

    void RegisterServices(IServiceCollection services);

    object CreateConfiguration();
    IEnumerable<IPluginMigration> GetMigrations();
    ITrackerInstance CreateInstance(string instanceId, object configuration);
}
```

配置页面、管理页面和编辑器扩展属于 `Diary.PluginUI`：

```csharp
public interface ITrackerUiContribution
{
    string PluginId { get; }

    ViewModelBase? CreateSettingsPage(object configuration);
    ViewModelBase? CreateManagementPage(string instanceId);
    ITrackerEditorExtension CreateEditorExtension(string instanceId);
}
```

插件实例接口：

```csharp
public interface ITrackerInstance
{
    string PluginId { get; }
    string InstanceId { get; }
    string DisplayName { get; }
    bool IsConfigured { get; }

    IDictionary<int, object?>? LoadBindingsByDate(string date);
}
```

主程序不应直接引用 `RedMineConfig`、`RedMineIssue` 或 `RedMineActivity`。

## 10. 工作项编辑器扩展

当前 `WorkEditorViewModel` 只有一个 `_tracker`，需要改为多个扩展：

```csharp
private IReadOnlyList<ITrackerEditorExtension> _trackerExtensions;
```

建议接口：

```csharp
public interface ITrackerEditorExtension
{
    string InstanceId { get; }
    ViewModelBase View { get; }

    void Load(WorkItem item, object? binding);
    void Save(WorkItem item);
    void CloneTo(ITrackerEditorExtension target);

    bool IsLocked { get; }
    bool CanDelete { get; }

    Task<TrackerOperationResult> UploadAsync(WorkItem item);
}
```

核心编辑器负责：

- 创建所有已启用 tracker 扩展。
- 按顺序加载所有 tracker 的绑定。
- 保存核心工作项和所有本地 tracker 绑定。
- 克隆所有 tracker 扩展状态。
- 聚合锁定状态和删除权限。
- 展示每个 tracker 的独立上传状态。

核心编辑器不得出现：

```csharp
SetRedMineActivity(...)
SetRedMineIssue(...)
RedMineDb!.CreateWorkTimeEntry(...)
```

### 10.1 锁定和删除策略

建议规则：

- 任意 tracker 要求锁定时，核心工作项进入锁定状态。
- 所有 tracker 都允许删除时，核心工作项才允许删除。
- 每个 tracker 区域显示自己的锁定原因。
- 未配置或不可用的 tracker 不应阻止核心工作项编辑。

### 10.2 上传策略

上传应支持：

- 单独上传当前 tracker。
- 上传所有可上传 tracker。
- 显示每个 tracker 的成功、失败和未配置状态。
- 失败后单独重试。

远程上传结果建议统一为：

```csharp
public sealed record TrackerOperationResult(
    bool Success,
    string? Error,
    string? RemoteId = null);
```

## 11. 模板系统

### 11.1 当前问题

当前 `Diary.Core.Data.App.Template` 包含：

```csharp
public int DefaultActivity { get; set; }
public int DefaultIssue { get; set; }
```

这会导致核心模板模型直接携带 Redmine 语义。模板应用流程也直接调用：

```csharp
SelectedWork.SetRedMineActivity(...);
SelectedWork.SetRedMineIssues(...);
```

模板编辑器则直接依赖 `DbShareData.RedMineActivities` 和 `DbShareData.RedMineIssues`。

这种设计无法自然支持：

- Jira 的项目、Issue、工时类型默认值。
- 同一个工作项同时配置多个 tracker 的默认值。
- Redmine 插件未安装时仍然保留模板数据。
- 插件卸载后再次安装时恢复原有模板扩展。

### 11.2 模板数据分层

核心模板只保存日记本身的预设：

```csharp
public sealed record Template
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DefaultTitle { get; set; } = string.Empty;
    public double DefaultTime { get; set; }
    public ICollection<int> DefaultWorkTags { get; set; } = Array.Empty<int>();
    public ICollection<TemplateExtensionData> Extensions { get; set; }
        = Array.Empty<TemplateExtensionData>();
}
```

tracker 扩展数据使用稳定的插件 ID 和实例 ID：

```csharp
public sealed record TemplateExtensionData
{
    public required string PluginId { get; init; }
    public required string InstanceId { get; init; }
    public int SchemaVersion { get; init; }
    public required string PayloadJson { get; init; }
}
```

示例：

```json
{
  "id": "template.daily-development",
  "name": "日常开发",
  "defaultTitle": "开发任务",
  "defaultTime": 1.0,
  "defaultWorkTags": [1, 8],
  "extensions": [
    {
      "pluginId": "tracker.redmine",
      "instanceId": "redmine.company",
      "schemaVersion": 1,
      "payloadJson": "{\"issueId\":123,\"activityId\":9}"
    },
    {
      "pluginId": "tracker.jira",
      "instanceId": "jira.team",
      "schemaVersion": 2,
      "payloadJson": "{\"projectKey\":\"APP\",\"issueKey\":\"APP-42\"}"
    }
  ]
}
```

核心程序只负责保存和保留 `PayloadJson`，不解析 tracker 专属字段。

payload 必须保存稳定的业务标识，而不是 UI 状态：

- Redmine 保存 `issueId`、`activityId`。
- Jira 保存 `issueKey` 或远程唯一 ID。
- 不保存 ComboBox 的 `SelectedIndex`。
- 不依赖管理列表当前的排序和分页结果。

UI 索引只能作为编辑器运行时状态，不能进入模板持久化格式。

### 11.3 未安装插件时的数据处理

模板文件中的未知扩展数据必须保留：

- 插件未安装时，核心模板仍可加载。
- 模板编辑器不显示未知扩展的编辑控件。
- 保存模板时不能删除未知扩展数据。
- 插件重新安装并启用后，可以恢复对应默认值。
- 用户明确删除模板或扩展数据时，才删除对应 payload。

这要求模板序列化层使用显式的 `Extensions` 集合，不能在反序列化时丢弃未知插件字段。

### 11.4 模板插件接口

模板扩展接口属于 `Diary.PluginUI`，因为模板编辑器可能需要插件提供 UI：

```csharp
public interface ITrackerTemplateContributor
{
    string PluginId { get; }
    string InstanceId { get; }
    int CurrentSchemaVersion { get; }

    object CreateDefaultData();

    ViewModelBase CreateEditor(
        object? data,
        TemplateEditorContext context);

    string Serialize(object data);
    object? Deserialize(string payloadJson, int schemaVersion);

    void ApplyTo(
        object data,
        ITrackerEditorExtension target);
}
```

主程序的模板协调器负责：

- 加载核心模板字段。
- 按 `PluginId` 和 `InstanceId` 查找已启用插件。
- 将 payload 交给插件反序列化。
- 创建插件模板编辑区域。
- 应用模板时调用插件的 `ApplyTo()`。
- 保存模板时重新序列化插件 payload。
- 保留找不到插件的原始 payload。

### 11.5 模板应用流程

应用模板时应拆成两个阶段：

```text
应用核心模板字段
  -> 标题
  -> 默认耗时
  -> 默认标签

应用 tracker 模板扩展
  -> 查找 pluginId + instanceId
  -> 反序列化 payload
  -> 调用 tracker 扩展 ApplyTo
  -> 更新对应编辑器区域
```

核心 `DiaryEditorViewModel` 不应再调用：

```csharp
SetRedMineActivity(...)
SetRedMineIssues(...)
```

而应调用通用协调器：

```csharp
templateCoordinator.Apply(template, selectedWork);
```

### 11.6 模板编辑流程

模板编辑器应由核心字段区和插件字段区组成：

```text
TemplateEditor
  ├── 核心字段：名称、标题、耗时、标签
  ├── Redmine 公司实例扩展区
  ├── Redmine 个人实例扩展区
  └── Jira 团队实例扩展区
```

插件未配置时：

- 插件模板区可以隐藏或显示为不可用。
- 原有 payload 必须继续保留。
- 不应因为插件不可用而阻止核心模板保存。

插件配置发生变化后，模板编辑器可以重新解析和刷新对应扩展区。

### 11.7 模板版本和迁移

模板文件需要独立的文件 schema 版本：

```text
Template file schema version: 2
Redmine template payload version: 1
Jira template payload version: 2
```

旧版本模板中的 Redmine 字段：

```json
{
  "defaultActivity": 9,
  "defaultIssue": 123
}
```

应迁移为：

```json
{
  "extensions": [
    {
      "pluginId": "tracker.redmine",
      "instanceId": "redmine.default",
      "schemaVersion": 1,
      "payloadJson": "{\"activityId\":9,\"issueId\":123}"
    }
  ]
}
```

迁移时不能要求 Redmine 插件一定已经安装。建议流程：

- 核心模板迁移器识别旧字段。
- 生成带有 `tracker.redmine` 标识的透明 payload。
- 如果无法确定旧配置对应的实例，使用 `redmine.default`。
- Redmine 插件加载后负责识别和升级该 payload。
- 迁移完成后保留旧字段一段兼容周期，确认升级成功后再删除。

### 11.8 模板扩展的失败策略

单个 tracker 模板 payload 损坏时：

- 核心模板字段仍然可用。
- 该 tracker 扩展显示错误状态。
- 原始 payload 不应被覆盖。
- 日志记录插件 ID、实例 ID和 payload schema 版本。
- 用户可以删除该扩展数据，但不能影响其他扩展。

### 11.9 模板测试要求

- 没有任何 tracker 时可以创建、编辑和应用模板。
- Redmine 插件存在时可以读写 Redmine 模板扩展。
- Redmine 和 Jira 扩展可以同时存在于一个模板中。
- 插件缺失时模板核心字段仍可用。
- 插件缺失时未知 payload 保存后不丢失。
- 插件重新安装后未知 payload 可以恢复。
- 旧版 `DefaultActivity` 和 `DefaultIssue` 可以迁移。
- payload schema 升级失败时不会覆盖原始数据。

## 12. 配置系统

当前 Redmine 配置直接挂在 `AllConfig.RedMineSettings` 上，后续应改成插件贡献配置。

插件通过 `Diary.PluginUI` 提供配置页面：

```csharp
public interface ITrackerConfigurationProvider
{
    string PluginId { get; }

    object CreateDefaultConfiguration();
    bool Validate(object configuration, out string? error);
    ViewModelBase CreateSettingsPage(object configuration);
}
```

主程序负责：

- 保存和加载插件配置。
- 识别配置文件版本。
- 显示插件启用状态。
- 调用插件配置校验。

插件负责：

- 配置字段和默认值。
- 配置校验。
- 密钥或 API Key 的敏感数据处理。
- 配置版本迁移。

配置版本和数据库版本分开管理。例如：

```text
Redmine configuration version: 3
Redmine database schema version: 7
```

## 13. 管理页面

插件管理页面不应由主程序硬编码导航项。

插件提供：

- 顶级管理页面。
- 可选的子页面。
- 远程连接测试。
- 项目、问题、活动等缓存管理。
- 手动刷新和同步入口。

主程序只负责将插件页面挂载到导航容器中。

多个实例的页面标题建议包含实例名：

```text
Redmine - 公司服务器
Redmine - 个人服务器
Jira - 项目组
```

## 14. 数据保存协调器

建议将当前 `WorkEditorViewModel.Save()` 中的持久化流程抽成协调器：

```csharp
public interface IWorkItemPersistenceCoordinator
{
    SaveWorkItemResult Save(
        WorkItemDraft draft,
        IReadOnlyList<ITrackerEditorExtension> extensions);
}
```

保存流程：

```text
开始本地事务
  -> 创建或更新核心 work_item
  -> 保存备注
  -> 保存标签
  -> 保存所有 tracker 本地绑定
提交本地事务
```

如果某个插件本地绑定保存失败，应回滚整个本地事务，避免核心工作项和插件绑定状态不一致。

远程上传在保存事务之外执行。

## 15. 数据库能力声明

插件 manifest 可以声明需要的数据库能力：

```text
SqlTransactions
ForeignKeys
ReturningClause
MultipleStatementExecution
```

如果某个插件只支持 PostgreSQL，应明确声明：

```text
SupportedProviders: PostgreSQL
```

不要让插件在运行时执行不兼容 SQL 后才失败。

## 16. 安装、升级和卸载

### 15.1 安装

安装流程：

```text
复制插件程序集
  -> 读取 manifest
  -> 检查主程序和 API 兼容性
  -> 注册插件信息
  -> 创建默认配置
  -> 执行插件数据库迁移
  -> 用户启用插件
```

### 15.2 升级

升级流程：

```text
发现新插件版本
  -> 检查 API 和核心数据兼容性
  -> 读取旧插件配置
  -> 执行配置迁移
  -> 执行插件数据库迁移
  -> 替换插件程序集
  -> 重新加载插件
```

插件程序集版本升级不一定等于数据库版本升级。只有表结构或数据格式变化时才提升插件 schema version。

### 15.3 卸载

卸载插件时默认：

- 禁用插件。
- 保留插件配置。
- 保留插件数据表。
- 核心日记继续运行。

只有用户明确选择“删除插件数据”时，才执行清理迁移或数据删除操作。

## 17. 当前改造路线图

以下路线以当前代码为起点。已完成项不再作为待办，后续实现必须按依赖顺序推进。

### 阶段 1：通用实例生命周期

当前基础：插件 manifest、`PluginHost`、`PluginInstanceRegistry` 和 Redmine 实例配置已经存在。

- 已将插件实例配置生成和 `App.RegisterTrackerInstances()` 改为遍历所有插件。
- 通用实例配置存储和实例状态接口仍待定义。
- 统一实例创建、数据库初始化、迁移和 UI 注册顺序。
- 接入 `SupportsMultipleInstances`，贯通导航、管理页和编辑器上下文。

验收标准：新增测试 tracker 不需要修改 `Diary.App` 的 tracker 专用分支即可创建两个实例。

### 阶段 2：多 tracker 编辑器和保存协调

- 已将编辑器状态改为扩展集合，并完成加载、保存、克隆、锁定、删除权限和上传状态聚合。
- 已将核心工作项与本地 tracker 绑定放入同一个本地事务。
- 已将远程上传移出事务，并支持按实例返回结果。

验收标准：Redmine 和测试 tracker 可以同时绑定一个工作项，一个 tracker 上传失败不影响另一个。

### 阶段 3：模板扩展落地

- 已完成透明 `Extensions` payload、旧 Redmine 字段迁移、未知 payload 保留和多实例 contributor 注册。
- 仍需补充模板损坏 payload、创建/编辑/应用和插件缺失测试。

验收标准：缺少 tracker 插件时模板核心字段仍可用，插件恢复后原 payload 可以重新编辑。

### 阶段 4：核心边界收紧

- 将 `IRedMineDb` 和 Redmine 模型收敛到 Redmine 插件内部。
- 移除 `Diary.App` 对 Redmine 配置和实例类型的直接引用。
- 将 `Diary.RedMine.*.dll` 扫描改为通用插件数据库扩展能力。

验收标准：移除 Redmine 程序集后核心日记、核心数据库、模板和编辑器仍可运行。

### 阶段 5：配置、诊断和卸载

- 通用配置持久化、配置 schema 迁移和敏感字段处理。
- 插件诊断页面、迁移重试和错误导出。
- 禁用/卸载时保留配置和数据，删除数据必须显式确认。

验收标准：单个插件可以独立禁用、重试和移除，不影响核心数据或其他插件。

### 阶段 6：完整测试和发布门槛

- 覆盖插件缺失、版本不兼容、依赖缺失、迁移失败和恢复。
- 覆盖 SQLite/PostgreSQL schema 迁移幂等和历史坏版本号。
- 覆盖多实例隔离、多 tracker 保存和远程失败重试。
- 将外部 Redmine API 测试与本地契约测试分离。

验收标准：核心测试不依赖远程 Redmine 服务，插件失败不会阻止核心日记启动。

## 18. 测试要求

### 核心数据库测试

- 核心数据库不加载任何 tracker 插件时可以初始化。
- 核心工作项、标签、备注和统计功能正常。
- 核心数据库迁移不创建 tracker 表。
- 无 tracker 时模板创建、编辑和应用正常。

### 插件迁移测试

- 从每个历史插件 schema 版本升级到最新版本。
- 迁移失败时版本号不前进。
- 迁移重复执行不会破坏数据。
- SQLite 和 PostgreSQL 都执行插件迁移测试。
- 模板文件和插件数据库迁移可以独立执行。

### 多 tracker 测试

- 一个工作项同时绑定 Redmine 和 Jira。
- 两个 tracker 的绑定互不覆盖。
- 一个 tracker 上传失败不影响另一个 tracker。
- 删除核心工作项时两个插件绑定都被清理。
- 一个模板可以同时保存和应用多个 tracker 的默认值。
- 缺失 tracker 插件时模板未知 payload 不丢失。

### 兼容性测试

- API 版本过低的插件进入 `Blocked`。
- 核心数据库版本不足的插件进入 `Blocked`。
- 缺少必选依赖的插件不能启用。
- 插件迁移失败时核心应用仍可启动。

## 19. 最终验收标准

完成本方案后，应满足：

- 不安装任何 tracker 时，DiaryApp 可以完整记录和查询工作日记。
- 核心数据库不包含 Redmine 或 Jira 专用表。
- Redmine 作为独立插件安装、升级和卸载。
- 可以同时启用多个不同 tracker。
- 可以同时启用同一种 tracker 的多个实例。
- 每个插件拥有独立的配置、管理页面和数据库迁移。
- 插件数据库迁移失败不会阻止核心日记启动。
- 核心工作项可以同时关联多个 tracker。
- 模板可以同时包含多个 tracker 的扩展数据。
- tracker 缺失时模板核心字段仍然可用，扩展 payload 可恢复。
- 本地保存和远程上传相互独立。
- 插件 API、核心数据版本和插件数据版本互不混淆。
- 主程序不再出现 `RedMineDb!`、`SetRedMineActivity()` 等 tracker 专用调用。

## 20. 第一批实施任务

第一批只处理实例生命周期，不同时改动模板和远程 API：

1. 定义通用实例配置枚举和实例状态模型。
2. 把 `RegisterTrackerInstances()` 的 Redmine 分支改成插件遍历。
3. 将数据库扩展初始化和插件实例创建放到同一生命周期协调器。
4. 增加一个内存测试 tracker，验证两个实例可以被宿主创建。
5. 验证插件迁移失败时核心 UI 仍可启动。
6. 再进入多 tracker 编辑器改造。

对应的执行清单维护在 `Docs/TODOS.md`；当前实现基线维护在 `Docs/CurrentArchitecture.md`；多 tracker 编辑器的实施设计见 `Docs/MultiTrackerEditorDesign.md`。
