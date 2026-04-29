# TODO

## 严重 Bug

- [ ] `PgDb.cs:366` SQL 拼写错误 `DELTE FROM work_items`，应为 `DELETE`
- [ ] `SQLiteDb.cs:293` `DeleteWorkTag` 使用 `item.Id` 但参数名为 `tag`（已修复）
- [x] `SQLiteDb.cs:625` + `PgDb.cs:633` `WorkItemWasUploaded()` 对 SELECT 误用 `ExecuteNonQuery()`（已修复）
- [x] `PgDb.cs:228` `KeepAlive()` 对 SELECT 误用 `ExecuteNonQuery()` 且 Stopwatch 未 Start（已修复）
- [ ] `SqliteMigrator.cs:61` 迁移读取器 `reader.GetInt32(4)` 列索引越界
- [ ] `PostgreSQLFactory.cs:17` `GetMigration()` 抛 `NotImplementedException`

## 安全隐患

- [x] `EasySaveLoad.cs` AES 固定 IV（已修复）
- [x] `EasySaveLoad.cs` 密钥派生未使用 PBKDF2（已修复）

## 线程安全 / 异步

- [ ] `MainWindowViewModel.cs` 多处 `Dispatcher.UIThread.Post(async void ...)` — 异常无法捕获
- [ ] `SurveyViewModel.cs` `_respondDatas.Clear()` 未持锁，与 `StoreData` 存在竞态条件
- [ ] `SurveyViewModel.cs` / `AppRespondent.cs` / `AppSurveyor.cs` `Task.Run` fire-and-forget 吞异常
- [ ] `WorkEditorViewModel.cs` `Upload()` 在任意线程更新 UI 绑定属性
- [ ] `EventDispatcher.cs:41-44` `AsyncMsg()` 使用 `Task.Run`，处理函数不在 UI 线程
- [ ] `DbMigrationViewModel.cs:65` 同步阻塞 `Dispatcher.UIThread.Invoke`，可能死锁
- [ ] `NngManager.cs` `Aio.Wait()` 同步阻塞，`Shutdown()` 中会阻塞 UI 线程

## 资源泄漏

- [ ] 所有 ViewModel `Messenger.Register` 注册未取消
- [ ] `MainWindowViewModel.cs:291` `window.PropertyChanged` 事件未取消订阅
- [ ] `PgDb.cs:397,631` `NpgsqlCommand` 未用 `using` 包裹
- [ ] `SQLiteDb.cs:393,622` `SQLiteCommand` 未用 `using` 包裹
- [ ] `Logging.cs` `ILoggerFactory` / Serilog 未 Dispose，退出时可能丢日志
- [ ] `SingletonApp.cs:30` `ListenPipe` Task 未跟踪

## 代码重复

- [ ] `RedMineProjectViewModel` + `RedMineIssueManageViewModel` 分页逻辑完全重复
- [ ] `DiaryEditorViewModel.cs:417-579` 上下文菜单构建代码三份重复
- [ ] `StatisticsView.axaml` + `SurveyView.axaml` 快速日期按钮网格重复
- [ ] `SqliteMigrator.cs` + `PgMigrator.cs` ~90% 代码相同
- [ ] `RedMineApis.cs` `SearchIssueByKeywords` + `SearchIssueByIds` 95% 相同

## 死代码 / 未实现

- [ ] `SQLiteMigration.cs:19` `Up()` 迁移逻辑被注释
- [ ] `PgMigration.cs:12` `Up()` 迁移未执行（`_stmts` TODO 未完成）
- [ ] `RedMineApis.cs:208` `CloseIssue()` 空壳
- [ ] `ProcUtils.cs:88` `Restart()` 抛 `NotImplementedException`
- [ ] `DbRecords.cs` 两个实现均无迁移记录

## 其他

- [ ] `DiaryEditorViewModel.cs:321-415` ViewModel 中直接遍历 Avalonia 视觉树，应移至 View 层
- [ ] `AllConfig` 无脏标记/自动保存机制
- [ ] `SingletonBase<T>` 构造失败后 Lazy 永久缓存异常，无法恢复
- [ ] `IoUtils` >8MB 文件静默丢弃，无日志
- [ ] `WorkEditorViewModel.cs` `CollectionChanged` 事件处理器内联写数据库，职责混乱
