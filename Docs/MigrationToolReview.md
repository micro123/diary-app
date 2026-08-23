# DiaryToolpp 数据迁移工具审阅

## 审阅范围

本次审阅以旧 C++ 项目 E:/Workspace/DiaryToolpp 为数据源，覆盖：

- SQLite 驱动：src/sql/sqlite3_driver.cpp
- PostgreSQL 驱动：extensions/sqldrivers/postgresql/src/pg_driver.cpp
- 当前迁移实现：Diary.MigrationTool
- 当前 SQLite/PostgreSQL 核心数据库 provider

旧项目迁移源版本为 5.0.0，数据库版本值为 0x50000。SQLite 和 PostgreSQL 驱动维护的核心数据包括标签、工作项、备注和工作项标签关联；旧 schema 中还存在 Redmine 活动、问题及上传状态字段。

## 当前迁移契约

迁移的目的是把旧记录导入当前程序用于统计，不是恢复旧的远程 Tracker 工作流。因此迁移只处理以下核心数据：

- 日期、标题、耗时和优先级；
- 本地备注；
- 标签及标签颜色、层级、禁用状态；
- 工作项与标签的关联。

以下旧字段明确忽略：

- act_id、issue_id 和 is_uploaded；
- redmine_activities、redmine_issues 中的内容；
- 任何远程项目、问题、活动或时间条目。

迁移过程不调用 Redmine API，不初始化、不清理、不写入目标 Tracker 扩展数据库。目标库中已有的 Tracker 配置和独立缓存数据不会被迁移器主动改写；核心数据替换时，数据库外键可能按既有 schema 级联移除旧工作项绑定。导入工作项不会产生任何 Tracker 绑定。

所有导入工作项在核心字段、备注和标签完成后标记为 is_read_only=true。只读状态是核心数据的一部分，而不是仅由 UI 模拟的状态：

- 编辑器显示“迁移记录（只读）”，并禁用日期、标题、耗时、优先级、标签和备注编辑；由于旧软件没有附加字段功能，迁移记录不显示附加信息入口；
- 保存流程拒绝修改只读工作项；
- SQLite/PostgreSQL 的工作项、备注、标签关联和工作项 ID 更新 SQL 都带有只读保护；
- 删除仍允许执行，但会明确提示这只是删除本地统计记录，不涉及任何远程 Tracker 数据。

只读记录仍然参与日期、标签和耗时统计。迁移记录没有 Tracker 绑定，且 `CanUpload()` 对 `IsImportedReadOnly` 返回 false，因此不计入日记页的“提交工时”，不把迁移记录伪装成已上传数据。

## 已发现并修正的问题

### 1. 迁移事务未可靠回滚

迁移入口统一使用核心数据库事务编排：

- 无法开始事务时立即失败并报告进度；
- 任意迁移步骤返回失败时回滚；
- 异常时回滚并将异常消息传递给进度回调；
- 提交失败时不保留清理或部分导入结果。

### 2. DropData 与迁移外层事务冲突

SQLite provider 的命令会绑定当前事务，DropData 只有在没有外层事务时才创建自己的事务。PostgreSQL provider 的 DropData 使用当前连接和当前事务；没有外层事务时仍可独立执行。这样清空核心数据与导入过程保持在同一事务边界内。

两端清理核心数据时均按外键依赖从子表到父表执行：先删除工作项附加字段值、标签关联和备注，再删除附加字段定义、工作项和标签。这样即使工作项已经保存附加字段值，`DropData` 也不会因字段定义仍被引用而中断。

### 3. 工作项字段和 ID 映射

迁移无论新生成 ID 是否与旧 ID 相同，都会写入耗时和优先级，并检查工作项更新、ID 重映射和只读标记的结果。旧工作项 ID 和标签 ID 会尽量保留，发生冲突时按当前 provider 的结果进行重映射。

### 4. 可空备注读取

旧 schema 中 work_items.note 可以为 NULL。迁移读取时区分 NULL 和文本值，只有非空备注才创建当前库的备注记录。

### 5. 标签颜色通道

旧项目使用 ImGui 的 IM_COL32，数据库中保存为 AABBGGRR；当前核心模型使用 0xRRGGBB。迁移时统一转换红蓝通道。SQLite 和 PostgreSQL 源驱动都读取原始整数，PostgreSQL 还兼容旧驱动写入的 integer 类型值。

### 6. 迁移记录的不可编辑约束

只读标记在核心 schema 和 WorkItem 模型中持久化，并由 provider 写入条件共同保护。迁移先导入工作项、备注和标签关联，最后批量标记只读，避免导入阶段被保护条件阻断。

## 测试覆盖

Diary.DbTests/MigrationToolTests.cs 使用与 DiaryToolpp 5.0.0 一致的临时 SQLite 源库；DbContractTests.cs 还对 SQLite/PostgreSQL 共用的只读写保护契约进行验证，覆盖：

- 工作项 ID 相同和需要重映射两种情况；
- 耗时、优先级、备注、日期和标题导入；
- NULL 备注；
- 标签 ID 重映射、禁用状态、主次级别和 ImGui 颜色转换；
- 工作项与标签关联导入；
- 迁移工作项全部标记为只读；
- 迁移器不主动创建或清理目标 Redmine 数据；导入工作项没有 Tracker 绑定，独立的活动/项目缓存不受迁移器主动改写；
- 只读工作项不能更新核心字段、ID、备注或标签；
- 不支持的源版本拒绝且保留目标数据；
- 导入中途失败时核心数据回滚，迁移器没有主动写入目标 Tracker 数据；
- 迁移失败后事务可以重新开始。

PostgreSQL 源驱动的字段类型差异已根据旧项目源码完成对照。Diary.DbTests/MigrationToolTests.cs 只有 3 个 SQLite 迁移测试，没有 PostgreSQL 迁移测试；当前环境没有可用 Docker，因此 PgContractTests 按项目既有规则（Assert.Inconclusive）跳过；SQLite provider 与迁移回归测试已执行通过。
