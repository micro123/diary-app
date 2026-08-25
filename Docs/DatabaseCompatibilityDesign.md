# 数据库兼容性检查与迁移设计

## 1. 背景

数据库版本号只能表示应用认为数据库应该处于哪个迁移节点，不能证明实际结构和数据已经达到该状态。
例如，迁移脚本可能在写入版本号前后被中断，用户也可能手动删除字段、索引或约束；而
`CREATE TABLE IF NOT EXISTS` 只判断表是否存在，不会修复已有表的缺列和结构漂移。

因此数据库兼容性采用多层证据，而不是单独依赖 `data_versions`：

```text
连接/provider 信息
        + provider 能力
        + data_versions 声明版本
        + migration metadata / history
        + 规范化 schema fingerprint
        + 数据完整性检查
        = DbCompatibilityReport
```

## 2. 兼容性状态

`Diary.Database` 暴露 `DbCompatibilityReport`，状态包括：

- `Uninitialized`：未发现核心表；
- `Compatible`：版本、provider、结构和数据检查均通过，可以写入；
- `NeedsMigration`：版本较旧，且存在连续、可执行的迁移链；
- `NewerThanApplication`：数据库版本高于当前程序，禁止降级写入；
- `MigrationUnavailable`：版本较旧但没有完整迁移链；
- `SchemaDrift`：缺少表、字段、索引或外键，或实际结构指纹与登记值不一致；
- `MigrationIncomplete`：元数据仍处于 `Running` 或 `Failed`；
- `DataIntegrityError`：发现外键冲突、重复稳定键等阻塞性数据问题；
- `ProviderMismatch`：数据库记录的 provider 与当前驱动不一致；
- `CapabilityMissing`：数据库不具备事务、外键或唯一索引等必需能力；
- `Unavailable`：连接、系统目录或检查命令失败。

应用启动只有 `Compatible` 才会把数据库交给业务层。旧库先执行迁移，迁移结束必须重新生成报告并通过复检。

## 3. Schema Contract 和指纹

核心 schema contract 位于 `Diary.Database/Compatibility/CoreSchemaContract.cs`，使用 provider 无关的逻辑类型描述：

- 表和字段；
- 逻辑类型、可空性、主键；
- 必需索引和唯一索引；
- 外键目标和删除动作。

SQLite 和 PostgreSQL 分别从自己的系统目录读取实际结构，转换成 `DbSchemaSnapshot`，再按照固定排序规则生成
SHA-256 指纹。不能直接 hash provider 的原始 SQL，因为两个 provider 的物理类型、默认值表达式和索引 SQL 不同。
provider 快照只纳入 `CoreSchemaContract` 声明的核心表、字段、索引和外键；provider 自动生成的约束索引以及额外
字段/索引不会改变核心逻辑指纹，但缺少或错误的必需对象仍然会在契约比较中阻塞。

比较规则是：

- 缺少表、字段、主键、索引或外键：阻塞；
- 类型或可空性不匹配：阻塞；
- 额外字段暂不阻塞，但必须通过正式迁移登记；
- 指纹和已登记元数据不一致：视为未登记结构变化，阻塞写入。

## 4. 迁移元数据

核心初始化会创建两张元数据表：

```text
diary_schema_metadata
- schema_version
- provider_id
- schema_fingerprint
- migration_state: Stable / Running / Failed
- last_migration_id
- last_error
- updated_at

diary_schema_migrations
- migration_id
- version_from / version_to
- checksum
- applied_at
- success
- error
```

`data_versions` 仍然保留，并继续作为迁移链的版本游标；但它不再是兼容性唯一依据。
每个 `Migration` 自动拥有稳定 ID 和校验值，provider 自定义迁移可以覆盖 checksum 以绑定实际 SQL 定义。

迁移流程：

```text
检查兼容性
  → 创建 provider 可提供的备份
  → metadata = Running
  → 开启事务
  → 执行单步迁移
  → 校验 data_versions 已到达 VersionTo
  → 写入 migration history
  → 提交事务
  → 将已提交版本写回 Running 元数据
  → 所有步骤完成后重新读取 schema / data
  → metadata = Stable
```

迁移失败时事务回滚，并在事务外留下 `Failed` 状态和错误信息。下一次启动不会把失败库当作正常数据库，
而是提示恢复备份或重新执行迁移。

## 5. 数据库备份与还原

完整 provider 行为、PostgreSQL 工具调用、最小权限预检、安全还原目标和验收标准见
[`DatabaseBackupRestoreDesign.md`](DatabaseBackupRestoreDesign.md)。本节只记录与兼容性和迁移直接相关的摘要。

数据库维护能力通过 `IDbMaintenanceProvider` 作为 provider 的可选能力暴露。创建备份要求当前连接仍然有效，
还原和还原回滚要求 provider 尚未建立连接；应用层的 `DatabaseRestoreCoordinator` 负责维护任务暂存、启动时应用、
启动复检和失败回滚。

当前 SQLite 备份使用 SQLite Online Backup API 生成完整物理数据库副本，覆盖核心表和同一物理数据库中的 Tracker 扩展表。
备份创建后执行 `PRAGMA quick_check`，用户还原前再次执行完整性和核心兼容性检查。还原不会在业务进程运行期间直接替换打开的文件，
而是先将备份复制到应用数据目录的待还原区域，下一次启动时执行以下流程：

```text
选择备份
  → 当前连接中校验备份
  → 暂存还原任务
  → 下次启动前关闭数据库连接
  → 生成还原前安全副本
  → 替换 SQLite 主文件及 WAL/SHM 伴随文件
  → 连接、初始化、迁移和兼容性复检
  → 成功则清理暂存任务；失败则恢复安全副本
```

还原前安全副本保留在 SQLite 数据库同目录的 `Backups` 中，便于用户在应用复检成功后仍保留一个恢复点。
当前 SQLite 备份文件是可直接打开的 SQLite 文件，尚未增加密码加密和自动保留策略。

PostgreSQL 的工具目录配置已加入 provider 设置。Windows 必须显式配置包含 `pg_dump` 和 `pg_restore` 的 `bin` 目录；
Linux 在未配置目录时搜索 `PATH`。任一工具缺失时，provider 应退化为不支持本地备份和还原；PostgreSQL 原生 dump/restore
的实际调用和恢复到目标数据库的流程另行实现，不通过业务表逐行导出。

## 6. Provider 契约

每个 provider 必须实现：

```csharp
DbProviderInfo GetProviderInfo();
DbSchemaSnapshot InspectSchema();
DbSchemaMetadata? ReadSchemaMetadata();
bool WriteSchemaMetadata(DbSchemaMetadata metadata);
bool RecordMigrationHistory(DbMigrationHistoryEntry entry);
```

同时应报告真实 server/library version 和 capabilities。当前核心必需能力为事务、外键和唯一索引；SQLite 连接时显式开启
foreign keys，PostgreSQL 从系统目录读取实际结构。

provider 的 `Initialized()` 仍可用于空库和向后兼容的幂等基础设施创建，但不能再被视为兼容性证明。
初始化完成后必须调用 `CheckCompatibility()`。

## 7. 数据完整性检查

结构检查通过后，provider 执行轻量的数据检查：

- SQLite 执行 `PRAGMA foreign_key_check`；
- SQLite/PostgreSQL 检查标签附加字段 `field_key` 的不区分大小写重复；
- 后续可以加入日期格式、JSON、孤儿绑定和业务范围检查。

数据问题和结构问题使用不同错误码，UI 可以分别提示“修复数据”和“恢复/迁移结构”。

## 8. 核心库与插件库的边界

核心 schema 报告不吞并 tracker 插件的业务表。插件目前使用独立的 `plugin_data_versions` 和
`IPluginMigration` 链，由 `TrackerPluginLifecycleCoordinator` 按实例隔离执行；某个插件迁移失败时，只应将该实例标为
`MigrationFailed`，不能回滚或删除核心工作项数据库。

后续插件可以实现同样的 schema contributor 契约，提供：

- `(PluginId, InstanceId)` 身份；
- 插件声明版本和迁移历史；
- 插件表/字段/索引/外键的规范化快照；
- 插件数据完整性检查。

宿主最终可以把这些结果聚合成“核心数据库兼容，但 Redmine 实例迁移失败”的分层诊断，而不是把插件问题误报为核心
数据库不可用。插件扩展表不应直接加入核心 `CoreSchemaContract`，避免安装或禁用 tracker 改变核心库兼容性。

## 9. 启动行为

`Diary.App.App.TryConnectDatabase()` 不再直接比较 `GetDataVersion()`：

1. 连接 provider；
2. 执行基础初始化；
3. 获取 `DbCompatibilityReport`；
4. 只对 `NeedsMigration` 执行迁移；
5. 对其他非兼容状态拒绝进入可写业务界面；
6. 兼容或迁移复检通过后保存最新 fingerprint。

这样可以明确区分“数据库来自更新版本”“结构被手动修改”“迁移链缺失”和“数据损坏”，避免所有问题都被归类为
“版本不一致”。

### 9.1 当前核心迁移

当前核心数据版本为 `1.0.1`（`0x00010001`）。SQLite 与 PostgreSQL 均登记
`0x00010000 -> 0x00010001` 迁移，为 `tag_extra_field_definitions` 增加
`default_value TEXT NOT NULL DEFAULT ''`，并写入版本码 `65537`。旧版初始化 SQL保持 `1.0.0` 原貌，确保迁移测试真实覆盖旧结构；迁移不修改工作项历史值。

## 10. 测试要求

数据库契约测试至少覆盖：

- 初始化库通过兼容性检查并能保存、复读 fingerprint；
- 空库识别为 `Uninitialized`，关闭连接识别为 `Unavailable`；
- 缺少表/字段、缺少或错误索引被识别为 `SchemaDrift`，额外索引不改变核心 fingerprint；
- 更高数据版本被识别为 `NewerThanApplication`；
- provider、元数据版本或 fingerprint 不匹配时拒绝迁移和写入；
- `Running`/`Failed` 元数据在重启检查时识别为 `MigrationIncomplete`；
- 连续迁移逐步提交，并逐条校验成功/失败迁移历史、checksum 和最终 `Stable` 元数据；
- SQLite/PostgreSQL 的正式 `1.0.0 -> 1.0.1` 迁移新增默认值列、保留旧字段定义并写入空默认值；
- 第二步迁移失败时保留第一步提交，失败步骤回滚并写入 `Failed` 元数据；
- 迁移返回失败、抛异常或不推进版本时回滚并留下失败历史；
- SQLite 和 PostgreSQL 的逻辑 schema fingerprint 在各自 provider 内稳定；
- SQLite 外键脏数据和 PostgreSQL 不区分大小写的重复字段键被识别为 `DataIntegrityError`；
- `ValidateDataAfterMigration` 开关分别覆盖阻止迁移完成和跳过最终数据校验的行为；
- provider 不匹配、结构漂移和数据完整性错误不会打开可写业务连接。
