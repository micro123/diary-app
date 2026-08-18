# 数据库备份与还原最终设计

- 状态：已确认设计，SQLite 已实现，PostgreSQL 待按本文分阶段实现
- 更新日期：2026-08-18

## 1. 目标

DiaryApp 为数据库 provider 提供统一、可诊断且失败可恢复的备份与还原能力，同时保留 SQLite 与 PostgreSQL
在存储模型、权限和运维方式上的差异。

设计目标：

- 备份覆盖同一物理数据库中的核心表和 Tracker 扩展表；
- 备份结果必须经过 provider 级校验，不能只依据文件存在或进程退出码；
- 还原不能直接破坏当前可用数据库；
- 还原结果必须重新通过核心兼容性、数据完整性和 Tracker 初始化检查；
- 失败时保留当前数据库，或恢复还原前安全副本；
- PostgreSQL 权限预检只查询当前操作需要的信息，不实现通用权限浏览器；
- 密码、连接字符串和数据库内容不得写入命令行、普通日志或诊断导出；
- Windows 和 Linux 行为明确，工具缺失时按能力降级，不影响数据库的普通读写连接。

## 2. 非目标

当前设计不包含：

- SQLite 与 PostgreSQL 之间的数据转换；
- PostgreSQL 集群级角色、表空间和全局对象备份；
- PostgreSQL 当前数据库的原地覆盖还原；
- 完整数据库权限、角色继承或 ACL 管理界面；
- 云端自动上传、远程备份仓库和跨设备同步；
- 第一阶段的备份加密、自动调度和自动保留策略；
- 在工具不可用时使用业务接口逐表导出 PostgreSQL 数据。

跨 provider 转换仍属于数据迁移功能，不属于备份还原。

## 3. 当前状态

| 能力 | SQLite | PostgreSQL |
|---|---|---|
| 手动创建备份 | 已实现 | 待实现 |
| 备份完整性检查 | `PRAGMA quick_check` 已实现 | 计划使用 `pg_restore --list` |
| 兼容性检查 | 已实现 | 复用 `CheckCompatibility()` |
| 还原 | 下次启动替换文件，已实现 | 待实现为恢复到新目标数据库 |
| 失败回退 | 恢复还原前安全副本，已实现 | 保留当前数据库并删除失败目标库 |
| 工具探测 | 不需要外部工具 | Windows 配置目录、Linux 配置目录或 `PATH`，已实现 |
| 权限预检 | 本地文件权限由实际操作验证 | 待实现最小操作能力检查 |

## 4. 核心原则

### 4.1 备份整个 DiaryApp 数据库

SQLite 和 PostgreSQL 都以当前配置指向的整个物理数据库为备份范围，因此自然包含：

- DiaryApp 核心工作项、标签、备注和附加字段；
- `diary_schema_metadata` 和迁移历史；
- Jira、Redmine 等 Tracker 扩展表；
- 插件数据版本和上传状态。

PostgreSQL 数据库应作为 DiaryApp 专用数据库使用。如果同一数据库还包含其他应用对象，完整 `pg_dump` 会将其纳入备份，
并可能因无关对象权限不足而失败；应用不会为了绕过权限而静默遗漏这些对象。

### 4.2 还原到安全目标

- SQLite：当前数据库是单文件存储，采用“暂存备份、下次启动替换、失败恢复安全副本”。
- PostgreSQL：当前数据库是服务端共享资源，采用“恢复到新数据库、验证、再切换配置”。
- PostgreSQL 不默认执行 `--clean` 覆盖当前数据库，也不在当前数据库内做破坏性恢复。

### 4.3 工具结果是最终结论

权限和环境预检只用于提前提供清晰错误。最终是否成功仍以以下结果为准：

- SQLite Online Backup API、文件替换和数据库复检结果；
- `pg_dump` / `pg_restore` 退出码、标准错误和恢复后数据库复检结果。

## 5. 公共能力模型

当前 `Diary.Database` 已提供：

```csharp
[Flags]
public enum DbMaintenanceCapabilities
{
    None = 0,
    Backup = 1,
    Restore = 2,
}

public interface IDbMaintenanceProvider
{
    DbMaintenanceSupport GetMaintenanceSupport();
    DbBackupResult CreateBackup(string destinationPath);
    DbBackupValidationResult ValidateBackup(string backupPath, uint expectedVersion);
    DbRestoreResult RestoreBackup(string backupPath, uint expectedVersion);
    bool RollbackRestore(DbRestoreResult restore, out string? error);
}
```

该接口已满足 SQLite 当前实现。接入 PostgreSQL 前应将请求参数扩展为显式模型，避免把 PostgreSQL 目标数据库信息塞入路径字符串：

```csharp
public sealed record DbBackupRequest(
    string DestinationPath);

public enum DbRestoreTargetMode
{
    CurrentDatabase,
    ExistingEmptyDatabase,
    CreateNewDatabase,
}

public sealed record DbRestoreTarget(
    DbRestoreTargetMode Mode,
    string? DatabaseName = null);

public sealed record DbRestoreRequest(
    string BackupPath,
    uint ExpectedVersion,
    DbRestoreTarget Target);

public sealed record DbMaintenanceProgress(
    string Stage,
    double? Percent,
    string Message);

public sealed record DbRollbackResult(
    bool Success,
    string? Error);
```

同时将长时间维护操作演进为异步接口，SQLite 当前同步实现作为内部原语复用：

```csharp
public interface IDbMaintenanceProvider
{
    DbMaintenanceSupport GetMaintenanceSupport();

    Task<DbBackupResult> CreateBackupAsync(
        DbBackupRequest request,
        IProgress<DbMaintenanceProgress>? progress,
        CancellationToken cancellationToken);

    Task<DbBackupValidationResult> ValidateBackupAsync(
        string backupPath,
        uint expectedVersion,
        CancellationToken cancellationToken);

    Task<DbRestoreResult> RestoreBackupAsync(
        DbRestoreRequest request,
        IProgress<DbMaintenanceProgress>? progress,
        CancellationToken cancellationToken);

    Task<DbRollbackResult> RollbackRestoreAsync(
        DbRestoreResult restore,
        CancellationToken cancellationToken);
}
```

进度只报告阶段、百分比和可公开说明，不包含密码、SQL 内容或数据库正文。取消必须终止 PostgreSQL 子进程并清理临时文件；
SQLite 文件替换进入不可中断临界区后只记录取消请求，完成替换或回滚后再返回。

目标接口保持 provider 无关，但 provider 只接受自己支持的模式：

- SQLite：`CurrentDatabase`，由应用暂存并在下次启动执行；
- PostgreSQL：`CreateNewDatabase` 或 `ExistingEmptyDatabase`；
- PostgreSQL 拒绝 `CurrentDatabase` 原地覆盖。

应用层维护协调器负责 UI、任务状态、数据库生命周期和最终配置切换，provider 只处理数据库差异。

## 6. 应用层编排

统一编排目标如下：

```text
用户操作
  → 获取当前 provider 维护能力
  → provider 前置检查
  → 创建或选择备份文件
  → provider 执行备份/还原
  → provider 校验结果
  → 应用执行核心兼容性和 Tracker 检查
  → 成功后完成任务或切换数据库配置
  → 失败时执行 provider 回退
```

应用层不得在还原未完成时把目标数据库暴露给业务页面、脚本或 Tracker 上传任务。

### 6.1 维护状态

后续统一协调器应暴露以下状态：

```text
Idle
Validating
BackingUp
RestoreStaged
Restoring
Verifying
Switching
Completed
Failed
RollingBack
```

SQLite 当前通过 `DatabaseRestoreCoordinator` 持久化 `Pending` / `Applied`，用于处理应用在替换文件后、启动复检前退出的场景。

## 7. SQLite 设计

### 7.1 备份

当前流程：

```text
用户选择目标文件
  → 创建同目录临时文件
  → SQLite Online Backup API 复制 main 数据库
  → 关闭目标连接
  → PRAGMA quick_check
  → 临时文件原子移动到最终路径
```

约束：

- 内存数据库 `:memory:` 不支持备份和还原；
- 备份目标不能与当前数据库路径相同；
- 迁移前自动备份复用同一实现；
- 备份文件是可直接打开的 SQLite 数据库文件；
- 当前不额外创建容器格式或 manifest。

### 7.2 还原

当前流程：

```text
选择备份文件
  → PRAGMA quick_check
  → 读取 provider、数据版本和兼容性状态
  → 复制到应用数据目录的待还原区域
  → 提示用户退出并重启

下次启动：
  → 在 Connect() 前读取待还原任务
  → 再次校验暂存文件
  → 复制为目标目录临时文件
  → 当前数据库移动到 Backups 安全副本
  → WAL/SHM 伴随文件一并移动
  → 临时文件替换当前数据库
  → Connect / Initialized / CheckCompatibility / MigrateTo
  → 写入兼容性元数据并注册 Tracker
  → 成功后清理待还原任务
```

如果启动连接、初始化、迁移或兼容性复检失败：

```text
关闭失败连接
  → 删除已还原数据库及新 WAL/SHM
  → 将安全副本和伴随文件移回原路径
  → 清理待还原任务
  → 应用保持数据库不可用状态并报告原因
```

启动复检成功后，安全副本继续保留在 `Backups`，不自动删除。

## 8. PostgreSQL 工具发现

需要同时找到：

- `pg_dump`；
- `pg_restore`。

平台规则：

### 8.1 Windows

- 数据库设置必须配置 PostgreSQL Client `bin` 目录；
- 不搜索系统 `PATH`；
- 目录中必须同时存在 `pg_dump.exe` 和 `pg_restore.exe`；
- 配置缺失或任一文件缺失时，备份和还原能力退化为不支持；
- 普通 PostgreSQL 连接、查询和写入不受影响。

### 8.2 Linux

- 已配置工具目录时优先使用配置目录；
- 配置目录无效时继续搜索 `PATH`；
- `PATH` 中必须同时找到 `pg_dump` 和 `pg_restore`；
- 未找到时备份和还原能力退化为不支持；
- 普通 PostgreSQL 连接、查询和写入不受影响。

### 8.3 工具版本检查

文件存在只表示候选工具可用。实际操作前还必须：

1. 执行 `pg_dump --version` 和 `pg_restore --version`；
2. 检查退出码和可解析版本；
3. 从当前连接读取 `server_version_num`；
4. 阻止不支持当前服务端版本的工具组合；
5. 在诊断中记录工具路径和版本，但不记录密码或完整连接字符串。

## 9. PostgreSQL 最小信息与权限预检

设计原则是“检查操作能力，不浏览权限”。不查询：

- 全量 `pg_roles`；
- 全量 `pg_auth_members`；
- 所有角色继承关系；
- 所有数据库、schema、表或 ACL；
- 与备份还原无关的复制、创建角色等权限。

### 9.1 基础信息

只读取：

```sql
SELECT
    current_user AS effective_user,
    current_database() AS database_name,
    current_setting('server_version_num') AS server_version_num;
```

这些值用于命令构建、工具版本检查和错误提示。

### 9.2 自动创建还原数据库

只读取当前有效角色对应的一行：

```sql
SELECT
    current_user AS effective_user,
    rolsuper,
    rolcreatedb,
    rolsuper OR rolcreatedb AS can_create_database
FROM pg_catalog.pg_roles
WHERE rolname = current_user;
```

`has_database_privilege(..., 'CREATE')` 不能代替该检查；它表示当前数据库内创建对象的能力，不表示可以执行
`CREATE DATABASE`。

`CanCreateDatabase = false` 不会禁用全部还原能力，而是退化为“恢复到用户提供的已有空数据库”。

### 9.3 备份能力

备份前只检查 DiaryApp 已知对象：

- 当前数据库连接仍然可用；
- DiaryApp 使用的 schema 具备 `USAGE`；
- 核心表和已注册 Tracker 表具备 `SELECT`；
- provider 已知并实际存在的序列具备导出所需读取权限。

对象来源必须是核心 schema contract 和当前已注册插件的已知表清单，不扫描无关对象。完整数据库中存在额外对象时，
实际 `pg_dump` 仍可能因这些对象的权限失败，此时以工具错误为准，不静默跳过对象。

### 9.4 恢复到已有数据库

只检查指定目标数据库：

- 能够连接目标数据库；
- 目标 schema 具备 `USAGE`；
- 目标 schema 具备 `CREATE`；
- 目标数据库没有非系统业务对象。

空库检查只返回布尔结果和对象数量，不枚举或展示额外对象详情。非空目标数据库拒绝还原，不自动清理。

### 9.5 预检结果

建议模型：

```csharp
public sealed record PgMaintenancePreflightResult(
    bool CanBackup,
    bool CanRestoreToExistingDatabase,
    bool CanCreateRestoreDatabase,
    string EffectiveUser,
    string Database,
    string ServerVersion,
    IReadOnlyList<PgMaintenanceIssue> Issues);
```

`Issues` 只包含当前操作需要的缺失能力，例如：

```text
缺少 public.redmine_projects 的 SELECT 权限
目标数据库 public schema 缺少 CREATE 权限
当前角色没有 CREATEDB，只能恢复到已有空数据库
```

## 10. PostgreSQL 备份流程

推荐使用 custom format：

```text
权限与版本预检
  → 创建同目录临时文件
  → pg_dump --format=custom
  → 检查退出码和 stderr
  → pg_restore --list 校验归档目录
  → 临时文件移动到最终路径
```

命令参数原则：

```text
pg_dump
  --format=custom
  --no-owner
  --no-privileges
  --no-password
  --file=<temporary-path>
  --host=<host>
  --port=<port>
  --username=<user>
  --dbname=<database>
```

- `--no-owner` 避免恢复时要求原 owner；
- `--no-privileges` 避免恢复原 GRANT/REVOKE；
- `--no-password` 禁止子进程等待交互输入；
- 输出先写临时文件，成功校验后再替换最终文件；
- 不把密码放入命令行参数。

因此 PostgreSQL 备份覆盖数据库结构和数据，但恢复时不重放原 owner、GRANT 和 REVOKE；恢复后的对象归目标连接用户所有。

## 11. PostgreSQL 还原流程

### 11.1 首选：自动创建新数据库

当 `CanCreateRestoreDatabase = true`：

第一版只在当前 PostgreSQL 配置指向的同一服务器、端口和账号下创建目标数据库，目标参数只允许改变数据库名。
跨服务器恢复需要单独的目标连接配置和凭据管理，留到后续阶段。

```text
校验 dump
  → 生成唯一数据库名 diary_restore_yyyyMMdd_HHmmss_<suffix>
  → CREATE DATABASE
  → pg_restore 到新数据库
  → Npgsql 连接新数据库
  → CheckCompatibility
  → 必要时执行核心迁移
  → 注册并检查 Tracker 插件
  → 用户确认切换
  → 保存新数据库配置并重新连接
```

失败时：

```text
关闭目标数据库连接
  → 删除新建数据库
  → 保持原数据库连接和配置不变
```

应用显式创建目标数据库，不使用 dump 中的原数据库名执行 `pg_restore --create`，避免名称冲突或误覆盖。

### 11.2 降级：用户提供已有空数据库

当当前角色没有 `CREATEDB`：

```text
用户填写目标数据库名
  → 连接目标数据库
  → 最小权限和空库检查
  → pg_restore
  → 兼容性与 Tracker 检查
  → 用户确认切换
```

目标数据库非空、权限不足或无法连接时拒绝执行，不调用 `--clean`。

### 11.3 禁止原地覆盖

不提供以下默认流程：

```text
pg_restore --clean --if-exists --dbname=<current-database>
```

原因：

- 当前数据库可能仍有 DiaryApp 或其他客户端连接；
- 清理完成后恢复失败会留下部分数据库；
- PostgreSQL 无法像 SQLite 一样通过文件原子替换回滚；
- 用户权限可能足以删除部分对象但不足以完整恢复；
- 额外对象和插件表可能进入不可预测状态。

## 12. PostgreSQL 子进程与密码安全

新增独立工具执行器，职责仅包括：

```text
工具路径
参数列表
环境变量
取消令牌
超时
stdout / stderr 捕获
退出码
执行时长
```

要求：

- 使用参数列表 API，避免手工拼接 shell 命令；
- 不通过 shell 执行，不依赖引号转义；
- 密码不进入参数、日志和异常文本；
- 优先使用临时 `PGPASSFILE`；
- 临时密码文件仅包含当前连接，操作结束立即删除；
- Linux 收紧文件权限；Windows 使用当前用户可访问的临时目录并限制继承权限；
- stderr 进入脱敏后的诊断结果；
- UI 取消操作时终止子进程并清理临时 dump；
- 超时、取消和非零退出码均视为失败。

日志可以记录：

- provider；
- 工具路径和版本；
- 源或目标数据库名；
- 操作阶段；
- 退出码；
- 脱敏错误摘要。

日志不得记录：

- 密码；
- `PGPASSFILE` 内容；
- 完整连接字符串；
- 日记正文或导出数据内容。

## 13. 校验和配置切换

无论 provider，成功还原都必须通过：

```text
连接测试
  → provider 信息
  → 核心数据版本
  → schema fingerprint
  → migration metadata / history
  → 数据完整性检查
  → 必要的核心迁移
  → Tracker 插件初始化和迁移
```

PostgreSQL 只有在目标数据库全部检查通过后才允许保存目标数据库名并切换当前连接。检查失败时原配置保持不变。

SQLite 在替换文件后执行同一套启动检查；失败时恢复安全副本。

## 14. 错误与降级语义

建议错误分类：

```text
UnsupportedPlatform
ToolsNotConfigured
ToolsMissing
ToolVersionMismatch
PermissionDenied
BackupInvalid
BackupFailed
RestoreTargetNotEmpty
RestoreFailed
CompatibilityFailed
TrackerValidationFailed
RollbackFailed
Cancelled
TimedOut
```

能力降级规则：

| 条件 | 结果 |
|---|---|
| PostgreSQL 普通连接成功，但工具缺失 | 数据库正常使用，备份/还原不支持 |
| 可以备份但没有 `CREATEDB` | 允许备份；还原只支持已有空数据库 |
| 目标库缺少 schema `CREATE` | 拒绝该目标，不影响当前数据库 |
| SQLite 是 `:memory:` | 备份/还原不支持 |
| 备份校验失败 | 不进入还原阶段 |
| 还原后兼容性失败 | SQLite 回滚文件；PostgreSQL 删除新目标并保持原配置 |

## 15. 用户界面

数据库设置保留统一入口：

- 创建备份；
- 选择备份并还原；
- provider 配置。

SQLite：

- 创建备份后显示文件路径；
- 还原确认显示备份数据版本；
- 明确提示“下次启动执行”；
- 还原完成或自动回滚后提供诊断信息。

PostgreSQL：

- 显示工具可用性、工具版本和不可用原因；
- Windows 显示必须配置工具目录；
- Linux 显示当前来自配置目录还是 `PATH`；
- 备份前只显示与当前操作有关的缺失权限；
- 还原时优先提供“自动创建新数据库”；
- 没有 `CREATEDB` 时切换为“选择已有空数据库”；
- 不提供“覆盖当前数据库”选项。

## 16. 测试与验收

### 16.1 SQLite 已有门禁

- 在线备份文件可以重新打开；
- 手动备份通过兼容性校验；
- 无效 SQLite 文件被拒绝；
- 还原后读取到备份数据；
- 回滚后恢复原数据库数据；
- 待还原任务在启动时应用；
- 启动失败路径可以恢复原数据库；
- 迁移前备份继续复用通用实现。

### 16.2 PostgreSQL 工具发现门禁

- Windows 未配置目录时，即使 `PATH` 中存在工具也不启用；
- Windows 配置有效目录时找到 `.exe` 工具；
- Linux 未配置时可以从 `PATH` 找到工具；
- 任一工具缺失时判定不支持；
- 不支持的平台不启用维护能力。

### 16.3 PostgreSQL 后续实现门禁

- 工具版本解析和服务端版本不兼容；
- `pg_dump` 成功、失败、超时和取消；
- 临时 dump 和密码文件清理；
- stderr 脱敏；
- 当前角色有/无 `CREATEDB`；
- 已有目标库为空、非空、不可连接和缺少 schema `CREATE`；
- 自动创建新数据库、还原成功、兼容性检查和配置切换；
- 还原失败时删除新目标并保持原配置；
- PostgreSQL 核心表和 Tracker 表数据均保留；
- 工具缺失不影响普通数据库连接。

PostgreSQL 集成测试继续使用 Testcontainers；工具执行测试应覆盖 Linux CI，Windows 至少覆盖路径、进程参数和密码文件策略。

## 17. 实施阶段

### 阶段 A：SQLite

已完成：

- 公共维护契约；
- 手动备份和 `quick_check`；
- 备份兼容性校验；
- 下次启动还原；
- 安全副本和自动回滚；
- UI、测试和文档。

### 阶段 B：PostgreSQL 备份

- 工具版本探测；
- 最小备份权限预检；
- 安全子进程执行器；
- `pg_dump` custom format；
- `pg_restore --list` 校验；
- 临时文件和密码文件清理；
- UI 和测试。

### 阶段 C：PostgreSQL 安全还原

- `CREATEDB` 最小检查；
- 自动创建唯一目标数据库；
- 恢复到新目标；
- 目标兼容性和 Tracker 检查；
- 配置切换；
- 无 `CREATEDB` 时恢复到已有空数据库；
- 失败目标清理和诊断。

### 阶段 D：增强项

- 备份加密；
- 自动备份调度；
- 保留数量和磁盘配额；
- 备份内容摘要和校验和 manifest；
- PostgreSQL 大对象、扩展和更多 server 版本覆盖。

## 18. 最终决策摘要

1. SQLite 使用在线物理备份和下次启动文件替换，当前实现保持不变。
2. PostgreSQL 使用 `pg_dump` / `pg_restore`，不使用业务接口逐表复制。
3. Windows 必须配置 PostgreSQL 工具目录；Linux 可以从配置目录或 `PATH` 探测。
4. 工具缺失只禁用 PostgreSQL 备份还原，不影响普通数据库功能。
5. PostgreSQL 只查询当前操作所需的信息和权限，不实现权限浏览器。
6. 检查当前有效角色的 `rolcreatedb` / `rolsuper`，用于决定是否可以自动创建安全还原目标。
7. 没有 `CREATEDB` 时降级为恢复到用户提供的已有空数据库。
8. PostgreSQL 不支持原地覆盖当前数据库，始终恢复到安全目标后再切换。
9. 所有还原结果必须通过核心兼容性、数据完整性和 Tracker 检查。
10. 密码不进入命令行和日志，临时凭据与失败产物必须清理。
