# TODO

## Tracker 插件化

Tracker 插件当前路线、阶段 TODO 和验收标准统一维护在 [`Docs/TODOS.md`](Docs/TODOS.md)；目标设计见 [`Docs/TrackerPluginArchitecture.md`](Docs/TrackerPluginArchitecture.md)。

## 严重 Bug

- [x] `PgDb.cs:366` SQL 拼写错误 `DELTE FROM work_items`，应为 `DELETE`（已修复）
- [x] `SQLiteDb.cs:293` `DeleteWorkTag` 使用 `item.Id` 但参数名为 `tag`（已修复）
- [x] `SQLiteDb.cs:625` `WorkItemWasUploaded()` 对 SELECT 误用 `ExecuteNonQuery()`（已修复）
- [x] `SqliteMigrator.cs:61` 迁移读取器 `reader.GetInt32(4)` 列索引越界 — 已不存在（已修复）
- [x] `PostgreSQLFactory.cs:17` `GetMigration()` 抛 `NotImplementedException`（已修复）

## 安全隐患

- [x] `EasySaveLoad.cs` AES 固定 IV（已修复）
- [x] `EasySaveLoad.cs` 密钥派生未使用 PBKDF2（已修复）

## 线程安全 / 异步

- [ ] `MainWindowViewModel.cs:97` 多处 `Dispatcher.UIThread.Post(async void ...)` — 异常无法捕获
- [ ] `AppRespondent.cs:68` / `AppSurveyor.cs:57` `Task.Run` fire-and-forget 吞异常
- [ ] `WorkEditorViewModel.cs:367-382` `Upload()` 在任意线程更新 UI 绑定属性
- [ ] `EventDispatcher.cs:41-44` `AsyncMsg()` 使用 `Task.Run`，处理函数不在 UI 线程
- [ ] `DbMigrationViewModel.cs:65` 同步阻塞 `Dispatcher.UIThread.Invoke`，可能死锁
- [ ] `AppRespondent.cs:51` / `AppSurveyor.cs:42` `Aio.Wait()` 同步阻塞，`Shutdown()` 中阻塞 UI 线程（原误标为 NngManager.cs）

## 资源泄漏

- [x] 所有 ViewModel `Messenger.Register` 注册未取消（共 13 处，涉及 SurveyViewModel/StatisticsViewModel/RedMineManageViewModel/MainWindowViewModel/DiaryEditorViewModel）
- [x] `Logging.cs` `ILoggerFactory` / Serilog 未 Dispose，退出时可能丢日志
- [x] `SingletonApp.cs:30` `ListenPipe` Task 未跟踪

## 代码重复

- [x] `RedMineProjectViewModel` + `RedMineIssueManageViewModel` 分页逻辑完全重复 → 提取至 `PaginatedSearchViewModel<T>` 基类
- [x] `DiaryEditorViewModel.cs:417-579` 上下文菜单构建代码三份重复（FillDayMenus/FillMonthMenus/FillYearMenus） → 统一辅助方法
- [x] `StatisticsView.axaml` + `SurveyView.axaml` 快速日期按钮网格重复 → 提取至 `QuickDateSelectButton` 用户控件
- [x] `SqliteMigrator.cs` + `PgMigrator.cs` ~90% 代码相同（15 处 Ok() 调用一一对应） → 提取至 `BaseMigrator` 基类
- [x] `RedMineApis.cs` `SearchIssueByKeywords` + `SearchIssueByIds` 95% 相同 → 合并至 `SearchIssuesInternal` 私有方法

## 死代码 / 未实现

- [ ] `SQLiteMigration.cs:19` `Up()` 迁移逻辑被注释
- [ ] `PgMigration.cs:12` `Up()` 迁移未执行（`_stmts` TODO 未完成）
- [ ] `RedMineApis.cs:208` `CloseIssue()` 空壳
- [ ] `ProcUtils.cs:88` `Restart()` 抛 `NotImplementedException`
- [ ] `DbRecords.cs` 两个实现均无迁移记录

## 其他

- [x] `DiaryEditorViewModel.cs:321-415` ViewModel 中直接遍历 Avalonia 视觉树，应移至 View 层 → 已移至 `DiaryEditorView.axaml.cs`
- [x] `SingletonBase<T>` 构造失败后 Lazy 永久缓存异常，无法恢复 → `LazyThreadSafetyMode.PublicationOnly`
- [x] `IoUtils` >8MB 文件静默丢弃，无日志 → 添加 `LogWarning`
- [x] `WorkEditorViewModel.cs` `CollectionChanged` 事件处理器内联写数据库，职责混乱 → DB 写入移至 `AddTag`/`DelTag` 命令
