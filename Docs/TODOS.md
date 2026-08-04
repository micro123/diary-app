# DiaryApp TODO

本文只维护当前工作项。已完成内容保留在“已完成”中，规划内容必须有明确的前置依赖和验收标准。

## 当前基线

- [x] `Diary.PluginBase` 插件契约、manifest、兼容性检查
- [x] 插件程序集发现和 `PluginHost` 注册
- [x] 插件实例注册表和 `(PluginId, InstanceId)` 身份校验
- [x] `Diary.PluginUI` 配置、管理页、编辑器扩展契约
- [x] SQLite/PostgreSQL Redmine 数据库扩展
- [x] 插件数据库版本表和 schema 0 -> 1 -> 2 迁移
- [x] Redmine 数据表使用 `instance_id` 隔离
- [x] Redmine 配置实例列表和启用状态
- [x] 当前架构文档与组件、生命周期、数据库扩展图

## 本轮已完成

- [x] 旧 SQLite schema 缺少 `instance_id` 但版本号为 2 的恢复测试
- [x] Redmine 初始化幂等测试
- [x] Redmine 插件 ID 和默认实例 ID 常量化
- [x] 实例注册协调器和成功/失败日志
- [x] 内存 tracker 实例注册测试
- [x] `TrackerKey` 统一扩展身份、批量绑定和模板匹配
- [x] 按 `TrackerKey` 执行扩展克隆，避免依赖集合顺序
- [x] 在编辑器保留按实例的上传结果状态
- [x] UI 贡献工厂和实例贡献注册表
- [x] Redmine API、数据库扩展和编辑器扩展绑定具体实例
- [x] Redmine 管理页及子页面使用当前实例的 API、缓存和数据库扩展
- [x] 模板 contributor 工厂和按实例注册
- [x] 宿主遍历插件生成实例配置，移除 Redmine 实例注册硬编码
- [x] 插件 UI 程序集改为通用扫描，缺失 UI 不阻断核心启动
- [x] 编辑器扩展集合和多 tracker 状态聚合基础
- [x] 工作项本地保存事务和远程上传协调

## 阶段 1：通用实例生命周期

目标：主程序不再硬编码只创建 Redmine 实例。

- [ ] 定义通用实例配置存储接口，返回所有已启用插件实例
- [x] 将 `App.RegisterTrackerInstances()` 改为遍历插件和配置实例
- [ ] 将实例创建、数据库初始化、迁移和 UI 注册纳入统一生命周期
- [ ] 明确实例状态：未配置、已启用、已禁用、迁移失败、连接失败
- [ ] 迁移失败时只禁用当前插件/实例，不影响核心日记
- [ ] 将 `SupportsMultipleInstances` 接入实际配置、导航和编辑器流程

验收：新增一个测试 tracker 后，主程序无需增加 tracker 专用分支即可创建和显示其实例。

## 阶段 2：核心编辑器多 tracker

目标：一个工作项可以同时拥有多个 tracker 扩展。

设计文档：[`MultiTrackerEditorDesign.md`](MultiTrackerEditorDesign.md)

- [x] 将编辑器中的单一 tracker 状态改为扩展集合
- [x] 聚合所有扩展的加载、保存、克隆、锁定和删除权限
- [x] 为每个实例显示独立的本地保存和远程上传状态
- [x] 本地工作项与所有 tracker 绑定使用同一个本地事务
- [x] 远程上传移出本地事务，支持单实例失败和重试
- [x] 删除所有 `FirstOrDefault()` 单 tracker 选择逻辑

验收：Redmine 公司实例和测试 tracker 可以同时编辑、保存、克隆和上传。

## 阶段 3：模板扩展完整落地

- [x] 完成核心模板透明 `Extensions` payload 的读写
- [x] 迁移旧 `DefaultActivity`/`DefaultIssue` 字段
- [x] 插件缺失时保留未知 payload
- [x] 支持同一 tracker 多实例模板编辑区
- [ ] payload schema 迁移失败时保留原始 JSON
- [ ] 增加模板创建、编辑、应用和插件缺失测试

验收：卸载 tracker 后模板核心字段仍可用，重新安装后原扩展数据可恢复。

## 阶段 4：移除 Redmine 核心耦合

- [ ] 将 `IRedMineDb` 和 Redmine 数据模型收敛到 Redmine 插件边界
- [ ] 移除 `Diary.App` 对 `RedMineConfigurationStore` 等具体类型的直接依赖
- [ ] 将数据库扩展扫描从 `Diary.RedMine.*.dll` 改为通用插件能力发现
- [ ] 核心 UI 不引用 Redmine ViewModel、配置或远程模型
- [ ] 插件缺失时核心数据库、编辑器、模板和主窗口均可运行

验收：移除 Redmine 程序集后，核心日记可以完整启动和使用。

## 阶段 5：配置、诊断和卸载

- [ ] 通用插件配置持久化和配置 schema 迁移
- [ ] API Key 等敏感字段的存储、遮罩和更新策略
- [ ] 插件管理/诊断页面
- [ ] 迁移失败重试、日志详情和导出
- [ ] 禁用插件时保留配置和数据
- [ ] 只有用户明确确认时才删除插件数据

验收：用户可以查看插件状态、重试失败迁移，并在不删除核心数据的情况下禁用或移除插件。

## 阶段 6：测试与质量门槛

- [ ] 插件缺失、版本不兼容、依赖缺失和能力缺失测试
- [ ] SQLite/PostgreSQL 插件迁移幂等测试
- [ ] 错误 schema 版本号但缺少列的恢复测试
- [ ] 多实例数据隔离测试
- [ ] 多 tracker 本地事务和远程失败测试
- [ ] 模板未知 payload 保留测试
- [ ] 外部 Redmine API 测试与本地契约测试分离

## 非 tracker TODO

- [ ] 修复 `MainWindowViewModel` 等 fire-and-forget UI 异常处理
- [ ] 完成 `RedMineApis.CloseIssue()`
- [ ] 完成 `ProcUtils.Restart()`
- [ ] 完成 SQLite/PostgreSQL 其他未实现迁移
- [ ] 增量升级和 CrashDump
