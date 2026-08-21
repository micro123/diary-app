# DiaryApp UI 自动化测试

## 1. 目标与边界

DiaryApp 在 Windows Debug 构建中提供基于 Chrome DevTools Protocol（CDP）的本地 UI 自动化入口，用于读取 Avalonia 视觉树、聚焦和触发控件、输入文本、发送快捷键、切换页面、截图并采样交互响应时间。

该入口只用于本地开发和验证：

- `Chrome.DevTools.Avalonia.v11` 只在 Debug 配置引用，Release 包不应包含 CDP 程序集。
- 只有显式设置 `DIARY_CDP_PORT` 时才启动监听，默认构建和正常启动不会开放端口。
- `DIARY_UI_TEST_ROOT` 将配置、数据库、日志和临时文件映射到独立测试 profile。
- 测试 profile 使用独立单实例 ID，可以与正常 DiaryApp 并行运行。
- `DIARY_UI_TEST_SCENARIO` 只在隔离 profile 中预置测试场景，不修改用户配置。
- CDP 具备输入模拟、截图和运行时检查能力，不得在正式包或不可信网络中开放。
- Release restore 必须显式传入 `-p:Configuration=Release`；发布包校验会拒绝 `Avalonia.Diagnostics`、`CDP.Integration.*`、`Chrome.DevTools.*` 和 `Xaml.Compiler` 调试组件。

当前使用兼容 Avalonia 11 和 SkiaSharp 2.88.9 的 `Chrome.DevTools.Avalonia.v11 0.1.0-preview.30`。升级 SkiaSharp 3.x 后应重新评估更新版本。

完整功能级状态见 [`UiAutomationCoverage.md`](UiAutomationCoverage.md)，UI 功能入口见 [`UiFeatureInventory.md`](UiFeatureInventory.md)。

## 2. 生命周期和测试场景

`Tools/ui-test.ps1` 管理单个隔离 App 生命周期：

```powershell
.\Tools\ui-test.ps1 start
.\Tools\ui-test.ps1 status
.\Tools\ui-test.ps1 smoke
.\Tools\ui-test.ps1 stop
```

如果 Debug App 已构建，可跳过重复构建：

```powershell
.\Tools\ui-test.ps1 start -NoBuild
```

脚本支持以下场景：

| 场景 | 启动参数 | 用途 |
| --- | --- | --- |
| `default` | 默认 | 核心、设置、标签和模板 |
| `extended` | `-Scenario extended` | 开启开发者功能，显示脚本管理 |
| `survey` | `-Scenario survey` | 开启调查者和本机受访节点 |
| `database-error` | `-Scenario database-error` | 注入不存在的数据库驱动，验证恢复 UI |
| `plugins` | `-Scenario plugins -WithPlugins` | 加载 Tracker 插件和动态管理页 |

`start` 会创建 `.build-tmp/ui-test/profiles/<runId>`，等待 CDP ready，并将 PID、端口、profile、场景和冷启动时间写入 `.build-tmp/ui-test/current.json`。`stop` 会校验 PID 对应的可执行文件后再终止进程，避免误杀其他 DiaryApp 实例。

需要复用外部 Tracker 测试配置时，使用已有加密 profile 作为 seed：

```powershell
.\Tools\ui-test.ps1 start -NoBuild -Scenario plugins -WithPlugins -SeedProfile '<encrypted-profile>'
```

seed 只复制加密配置文件，不应提交到 Git，也不得写入报告和文档。未提供 Redmine seed 时，全量编排将该套件标记为 `blocked-external`，而不是伪造通过。

## 3. 全量编排

一次运行全部可重复套件：

```powershell
.\Tools\ui-full-test.ps1 -NoBuild -RedmineSeedProfile '<encrypted-profile>'
```

不需要外部服务时可以省略 `-RedmineSeedProfile`；其余套件仍执行，Redmine 结果记录为 `blocked-external`。编排器按场景创建隔离 profile，并在套件组结束后停止 App。

当前 8 个套件如下：

| 套件 | 结构化步骤 | 主要覆盖 |
| --- | ---: | --- |
| `ui-settings-full` | 9 | 首次引导、设置分组、保存/丢弃、导航动态重建、数据库/迁移对话框、运行日志导出、更新检查、设置性能 |
| `ui-smoke` | 单独断言集 | 标签、模板、主题、新建草稿、`新建 -> 修改 -> 新建`、模板替换前草稿保留、视觉树和截图性能 |
| `ui-core-full` | 14 | 主外壳、应用菜单、`Alt+数字`、关于、复制入口、日记快捷键、查询、保存查询、统计、核心性能 |
| `ui-extended-full` | 9 | C#/Lua/Python 脚本创建、筛选、重新加载、预览运行、执行历史、日志、API Reference、删除、性能 |
| `ui-script-editor` | 4 | 独立脚本编辑器、命令区、编译检查和安全关闭 |
| `ui-database-error` | 8 | 日记/查询/统计数据库异常状态、重试、设置入口、诊断导出和异常状态性能 |
| `ui-survey-full` | 8 | v1 查询、v2 能力发现、详情、筛选、三种分组、明细开关、校验错误和性能 |
| `ui-redmine-full` | 12 | 多 Tracker 设置、Redmine 管理、项目/Issue、标签规则、工时同步、防重复、删除边界、安全和性能 |

7 个带结构化摘要的套件合计 64 个步骤；`ui-smoke` 另含标签、模板、主题、草稿、本地持久化和性能断言。

## 4. 单套件调试

启动匹配场景后可直接运行 Node 脚本：

```powershell
.\Tools\ui-test.ps1 start -NoBuild -Scenario default
node .\Tools\ui-core-full.mjs
.\Tools\ui-test.ps1 stop
```

脚本管理和编辑器共享同一个 `extended` profile：

```powershell
.\Tools\ui-test.ps1 start -NoBuild -Scenario extended
node .\Tools\ui-extended-full.mjs
node .\Tools\ui-script-editor.mjs
.\Tools\ui-test.ps1 stop
```

测试脚本使用 `Tools/ui-cdp.mjs` 的原始 WebSocket 客户端和稳定 `Name`/控件类型/可见文字定位。关键操作优先使用 `DOM.focus` 配合键盘触发，减少坐标点击在动画、弹层和布局变化时丢失。导航完成条件是目标 View 已可见，不以点击命令返回作为完成信号。

## 5. 报告和判定

输出目录：

- `.build-tmp/ui-test/reports/*.json`
- `.build-tmp/ui-test/screenshots/*.png`
- `.build-tmp/ui-test/profiles/*`

单套件报告包含场景、profile、冷启动时间、步骤耗时、断言结果、性能样本和 finding。全量报告汇总每个套件的 `passed`、`failed` 或 `blocked-external` 状态。

判定规则：

- 功能断言失败、对话框未关闭、目标页面未出现或状态未持久化均为 `failed`。
- 外部服务配置未提供时为 `blocked-external`，不计作功能通过。
- 性能值用于同一机器、同一构建方式下的趋势比较，不作为跨机器固定发布阈值。
- 首次页面创建包含视图构造和数据加载，应与预热后的视觉树/动作耗时分开观察。
- 原生文件选择器、目录选择器、系统托盘和真实备份/还原不由 Avalonia CDP 控制，保留 Windows 原生人工或专用驱动验证。

## 6. 2026-08-21 本机基线

最终全量报告：`.build-tmp/ui-test/reports/ui-full-test-2026-08-21T17-34-10-152Z.json`。

| 项目 | 结果 |
| --- | ---: |
| 全量套件 | 8 / 8 passed |
| 结构化步骤 | 64 / 64 passed |
| smoke | passed，含 1 个日期环境 warning |
| 全量耗时 | 64,825 ms |
| Debug 冷启动到 CDP ready | 1,419–1,660 ms |
| core 视觉树 P50 / P95 | 4.50 / 9.40 ms |
| core 全窗口截图 P50 / P95 | 109.89 / 135.08 ms |
| smoke `DOM.getDocument` P50 / P95 | 5.83 / 16.17 ms |
| smoke `DOM.querySelector` P50 / P95 | 0.53 / 0.73 ms |
| smoke 截图 P50 / P95 | 110.42 / 138.53 ms |
| 设置对话框三次打开 P95 | 200.13 ms |
| 脚本页刷新最大值 | 45.28 ms |
| Survey 视觉树 P95 / 动作 P95 | 16.08 / 4.26 ms |
| Redmine 工时同步 | 232.35 ms |
| Redmine 管理页导航 P95 | 105.00 ms |

稳定性收口还使用两个全新 profile 连续执行 `ui-core-full`，两次均为 14/14 passed，用于确认删除取消对话框和保存查询删除取消不会遮挡后续页面。

测试机系统时钟错误显示为 2026-08-22，导致 profile 目录名包含 `20260822`，且 App 中“今天”显示为 2026-08-22；本项目基准日期按 2026-08-21 记录，测试没有修改系统时钟。smoke 的日期 warning 仅反映该环境差异，不影响“使用今天”、草稿保留和本地保存断言。

Redmine 套件使用隔离测试服务并产生可清理测试数据；报告只记录耗时、数量和远程 ID 摘要，不包含服务器地址、账号或凭据。

## 7. 当前覆盖边界

- Jira 真实服务、权限矩阵和自托管版本差异为 `Blocked-External`。
- 原生文件/目录选择器、托盘、真实备份与还原为 `Manual-Native`。
- 查询/统计计算、附加字段转换、Tracker 数据契约等低层语义继续由单元和集成测试承担。
- 后台任务进度预览 UI 尚未实现，状态为 `Not-Implemented`。
- Release 不运行 CDP；“Release 包不含 CDP”由构建和发布包集成校验负责。
- 脚本交互式 XLSX/CSV/DOCX/Mustache 导出仍缺少真实 UI 端到端门禁。

## 8. CDP 兼容性

已验证 `DOM.getDocument`、`DOM.querySelector`、`DOM.getBoxModel`、`DOM.focus`、`Input.dispatchMouseEvent`、`Input.dispatchKeyEvent`、`Input.insertText` 和 `Page.captureScreenshot`。Playwright 的 `connectOverCDP()` 可以建立连接，但高层截图调用在当前预览版 CDP 上可能等待超时，因此项目脚本使用原始 CDP 命令。
