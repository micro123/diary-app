# DiaryApp UI 自动化测试

## 1. 目标与边界

DiaryApp 在 Windows Debug 构建中提供基于 Chrome DevTools Protocol（CDP）的本地 UI 自动化入口，用于像真实用户一样读取视觉树、点击、输入、切换页面和截图，并采样常见交互的响应时间。

该入口只用于本地开发和验证：

- `Chrome.DevTools.Avalonia.v11` 只在 Debug 配置引用，Release 包不应包含 CDP 程序集。
- CI 的 Release restore 必须显式传入 `-p:Configuration=Release`；发布包校验会拒绝 `Avalonia.Diagnostics`、`CDP.Integration.*`、`Chrome.DevTools.*` 和 `Xaml.Compiler` 调试组件。
- 只有显式设置 `DIARY_CDP_PORT` 时才启动监听，默认构建和正常启动不会开放端口。
- `DIARY_UI_TEST_ROOT` 将配置、数据库和临时文件映射到独立测试 profile。
- 测试 profile 使用独立单实例 ID，可以与正常 DiaryApp 并行运行。
- CDP 具备输入模拟、截图和运行时检查能力，不得在正式包或不可信网络中开放。

当前使用兼容 Avalonia 11 和 SkiaSharp 2.88.9 的 `Chrome.DevTools.Avalonia.v11 0.1.0-preview.30`。升级 SkiaSharp 3.x 后应重新评估更新版本。

## 2. 使用方法

环境要求：Windows、PowerShell 7、.NET SDK，以及提供全局 `WebSocket` 的 Node.js 22 或更高版本。

`Tools/ui-test.ps1` 提供完整生命周期：

```powershell
.\Tools\ui-test.ps1 start
.\Tools\ui-test.ps1 smoke
.\Tools\ui-test.ps1 status
.\Tools\ui-test.ps1 stop
```

如果 Debug App 已经构建，可跳过重复构建：

```powershell
.\Tools\ui-test.ps1 start -NoBuild
```

需要测试 Jira、Redmine 等 Tracker 插件时显式启用插件模式：

```powershell
.\Tools\ui-test.ps1 start -WithPlugins
```

默认仍使用 `--core-only`，避免普通 smoke 受外部 Tracker 配置或网络影响；`-WithPlugins` 仅用于隔离 profile 下的插件配置、管理页和同步联调。

`start` 会创建 `.build-tmp/ui-test/profiles/<runId>`，以 `--core-only` 启动应用，等待 CDP ready，并将 PID、端口、profile 和冷启动时间写入 `.build-tmp/ui-test/current.json`。`stop` 会校验 PID 对应的可执行文件后再终止进程，避免误杀其他 DiaryApp 实例。

## 3. Smoke 覆盖范围

`Tools/ui-smoke.mjs` 直接使用原始 CDP WebSocket，不依赖浏览器 DOM 或 Playwright 页面模型。当前覆盖：

1. 首次使用引导关闭。
2. “事项查询”“统计工具”“日记记录”主导航切换。
3. 设置菜单打开、程序设置对话框打开和关闭。
4. 主题切换及截图差异确认。
5. 新建事项、标题输入、“使用今天”和主导航切换后的本地保存结果。
6. `新建 -> 修改 -> 新建` 回归：不手动保存时，第一条有内容事项必须自动持久化，第二个编辑器保持空白。
7. 标签设置：通过真实设置对话框创建标签、打开“自动化操作”页签并保存，确认模板编辑器能立即读取新标签。
8. 模板设置：创建模板并配置默认标题、工时和标签；从模板替换未保存草稿时先保存旧草稿，并验证模板字段、标签和导航后的持久化结果。
9. 视觉树读取、`#ViewList` 查询和全窗口截图的预热后性能采样。

脚本会在隔离 SQLite 中创建一条名为“UI自动化响应测试”的事项，这是预期测试数据，不会进入用户正式数据库。

关键控件使用稳定 `Name` 定位；主导航项由可见文字向上定位到 `SelectionListItem`，不依赖每次运行变化的 CDP 节点编号。

## 4. 输出

测试报告写入：

- `.build-tmp/ui-test/reports/*.json`
- `.build-tmp/ui-test/screenshots/*.png`

报告包含：

- 冷启动到 CDP ready 时间。
- 页面切换、设置、主题、新建、聚焦和输入耗时。
- `DOM.getDocument` 30 次、`DOM.querySelector` 100 次、截图 10 次的 min/P50/P95/max/average。
- 功能断言、截图路径和非阻断发现。
- 标签创建、自动化页签打开、模板配置与应用耗时，以及模板替换前草稿保留状态。

性能值用于同一机器、同一构建方式下的趋势比较，不应直接作为跨机器的发布阈值。首次页面创建包含视图构造和数据加载，应与预热后的 CDP 查询耗时分开观察。

## 5. 最近一次本机基线

2026-08-21 在 Windows Debug、`--core-only`、全新隔离 profile 下执行通过：

| 项目 | 结果 |
| --- | ---: |
| 冷启动到 CDP ready | 1554 ms |
| 事项查询首次切换 | 369 ms |
| 统计工具首次切换 | 288 ms |
| 日记记录切换 | 83 ms |
| 程序设置对话框打开 | 190 ms |
| 新建事项表单打开 | 215 ms |
| 标题输入反映到视觉树 | 26 ms |
| 标签创建并打开自动化页签 | 632 ms |
| 模板配置并保存 | 483 ms |
| 模板应用并保留旧草稿 | 142 ms |
| `DOM.getDocument` P50 / P95 | 5.23 / 8.16 ms |
| `DOM.querySelector` P50 / P95 | 0.44 / 0.59 ms |
| 全窗口截图 P50 / P95 | 109 / 134 ms |

本次功能断言全部通过，未产生 smoke finding。自动化结果显示新建日期为 2026-08-21，导航后事项仍存在并显示“本地已保存”；`新建 -> 修改 -> 新建` 的第一条记录保留断言也已固化，最近一次耗时约 91 ms。

同日在 Windows Debug、`-WithPlugins`、隔离 profile 下完成 Redmine 真实联调：配置保存约 255 ms，管理页首次进入约 277 ms，活动同步约 134 ms，Issue 搜索约 387 ms，项目搜索约 483 ms，创建测试 Issue 约 516 ms，工时同步约 950 ms。远程回读确认工时 ID `22` 的 Issue、活动、日期、用户、备注和 `0.25` 小时均正确；重复触发后远程仍只有一条匹配记录。测试还验证了插件配置文件整体加密、日志不记录响应正文或 API Key，以及 Issue 导入后共享列表即时刷新。

## 6. 已知兼容性

CDP 的 `DOM.getDocument`、`DOM.querySelector`、`DOM.getBoxModel`、`Input.dispatchMouseEvent`、`Input.insertText` 和 `Page.captureScreenshot` 已验证可用。Playwright 的 `connectOverCDP()` 可以建立连接，但其高层截图调用在当前预览版 CDP 上可能等待超时，因此项目脚本使用原始 CDP 命令。
