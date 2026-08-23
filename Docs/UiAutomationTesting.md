# DiaryApp UI 自动化测试

## 1. 目标与边界

DiaryApp 在 Windows 和 Linux Debug 构建中提供基于 Chrome DevTools Protocol（CDP）的本地 UI 自动化入口，用于读取 Avalonia 视觉树、聚焦和触发控件、输入文本、发送快捷键、切换页面、截图并采样交互响应时间。

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

Windows 使用 `Tools/ui-test.ps1` 管理单个隔离 App 生命周期：

```powershell
.\Tools\ui-test.ps1 start
.\Tools\ui-test.ps1 status
.\Tools\ui-test.ps1 smoke
.\Tools\ui-test.ps1 stop
```

Linux 使用 `Tools/ui-test.sh`；需要 Bash、Python 3、Node.js 22.5+ 和可用的 X11 显示：

```bash
./Tools/ui-test.sh start --port 9333
./Tools/ui-test.sh status
./Tools/ui-test.sh smoke
./Tools/ui-test.sh run ui-core-full
./Tools/ui-test.sh stop
```

如果已经设置 `DISPLAY`，Linux 工具直接复用当前 X11 会话；未设置时会自动查找并启动 `Xvfb`。可以使用 `--display :0` 指定显示，或使用 `--xvfb` 强制创建 1920×1080、24 位色深的独立虚拟显示。工具把 App 和 Xvfb PID 一并写入状态文件，`stop` 会校验可执行文件身份后分别清理。

如果 Debug App 已构建，可跳过重复构建：

```powershell
.\Tools\ui-test.ps1 start -NoBuild
```

```bash
./Tools/ui-test.sh start --no-build --display :0
```

Linux 默认构建流程先按 Debug 配置执行 restore，再使用 `--no-restore` 构建，避免条件引用的 CDP 包因沿用 Release 资产文件而缺失。

脚本支持以下场景：

| 场景 | Windows / Linux 启动参数 | 用途 |
| --- | --- | --- |
| `default` | 默认 | 核心、设置、标签和模板 |
| `extended` | `-Scenario extended` / `--scenario extended` | 开启开发者功能，显示脚本管理 |
| `survey` | `-Scenario survey` / `--scenario survey` | 开启调查者和本机受访节点 |
| `database-error` | `-Scenario database-error` / `--scenario database-error` | 注入不存在的数据库驱动，验证恢复 UI |
| `extra-fields` | `-Scenario extra-fields` / `--scenario extra-fields` | 预置迁移只读事项，验证标签附加字段定义和类型化编辑 |
| `plugins` | `-Scenario plugins -WithPlugins` / `--scenario plugins --with-plugins` | 加载 Tracker 插件和动态管理页 |

两种工具的 `start` 都会创建 `.build-tmp/ui-test/profiles/<runId>`，等待 CDP ready，并将 PID、端口、profile、场景和冷启动时间写入 `.build-tmp/ui-test/current.json`。Linux 额外记录 `platform`、`display`、Xvfb PID 和应用日志路径。`stop` 会校验 PID 对应的可执行文件后再终止进程，避免误杀其他 DiaryApp 或 Xvfb 实例。

需要复用外部 Tracker 测试配置时，使用已有加密 profile 作为 seed：

```powershell
.\Tools\ui-test.ps1 start -NoBuild -Scenario plugins -WithPlugins -SeedProfile '<encrypted-profile>'
```

```bash
./Tools/ui-test.sh start --no-build --scenario plugins --with-plugins \
  --seed-profile '<encrypted-profile>'
```

seed 只复制加密配置文件，不应提交到 Git，也不得写入报告和文档。未提供 Redmine seed 时，全量编排将该套件标记为 `blocked-external`，而不是伪造通过。

## 3. 全量编排

一次运行全部可重复套件：

```powershell
.\Tools\ui-full-test.ps1 -NoBuild -RedmineSeedProfile '<encrypted-profile>'
```

不需要外部服务时可以省略 `-RedmineSeedProfile`；其余套件仍执行，Redmine 结果记录为 `blocked-external`。编排器按场景创建隔离 profile，并在套件组结束后停止 App。

当前全量多场景编排器仍为 PowerShell。Linux 可以通过 `ui-test.sh start` 和 `ui-test.sh run <suite>` 运行任意单套件；无桌面的 Xvfb 全量编排和定期 CI 门禁仍是后续工作，不能仅凭已有桌面会话结果视为 headless 门禁完成。

当前 9 个套件如下：

| 套件 | 结构化步骤 | 主要覆盖 |
| --- | ---: | --- |
| `ui-settings-full` | 9 | 首次引导、设置分组、保存/丢弃、导航动态重建、数据库/迁移对话框、运行日志导出、更新检查、设置性能 |
| `ui-smoke` | 单独断言集 | 标签、模板、主题、新建草稿、`新建 -> 修改 -> 新建`、模板替换前草稿保留、视觉树和截图性能 |
| `ui-core-full` | 14 | 主外壳、应用菜单、`Alt+数字`、关于、复制入口、日记快捷键、查询、保存查询、统计、核心性能 |
| `ui-extended-full` | 11 | AI 上下文默认/显式授权、预览、MCP 快照和手册截图，程序设置标准分组布局、配置生成/复制和跳转，以及 C#/Lua/Python 脚本创建、筛选、重新加载、预览运行、执行历史、日志、API Reference、删除、性能 |
| `ui-script-editor` | 4 | 独立脚本编辑器、命令区、编译检查和安全关闭 |
| `ui-database-error` | 8 | 日记/查询/统计数据库异常状态、重试、设置入口、诊断导出和异常状态性能 |
| `ui-survey-full` | 8 | v1 查询、v2 能力发现、详情、筛选、三种分组、明细开关、校验错误和性能 |
| `ui-extra-fields-full` | 8 | 9 类字段定义、类型化编辑、清空、持久化、停用历史值和迁移只读事项 |
| `ui-redmine-full` | 12 | 多 Tracker 设置、Redmine 管理、项目/Issue、标签规则、工时同步、防重复、删除边界、安全和性能 |

8 个带结构化摘要的套件当前合计 74 个步骤；`ui-smoke` 另含标签、模板、主题、草稿、本地持久化和性能断言。

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

附加字段套件使用独立场景：

```powershell
.\Tools\ui-test.ps1 start -NoBuild -Scenario extra-fields
node .\Tools\ui-extra-fields-full.mjs
.\Tools\ui-test.ps1 stop
```

Linux 的 `run` 会自动传入当前状态文件，不需要手动拼接 `--state`：

```bash
./Tools/ui-test.sh start --no-build --scenario extended --display :0
./Tools/ui-test.sh run ui-extended-full
./Tools/ui-test.sh run ui-script-editor
./Tools/ui-test.sh stop
```

测试脚本使用 `Tools/ui-cdp.mjs` 的原始 WebSocket 客户端和稳定 `Name`/控件类型/可见文字定位。关键操作根据控件行为使用鼠标或 `DOM.focus` 配合键盘触发；菜单、列表选择和异步命令会等待可观察状态，并在输入偶发丢失时执行有限重试。导航完成条件是目标 View 已可见，不以点击命令返回作为完成信号。

## 5. 报告和判定

输出目录：

- `.build-tmp/ui-test/reports/*.json`
- `.build-tmp/ui-test/screenshots/*.png`
- `.build-tmp/ui-test/profiles/*`
- `.build-tmp/ui-test/logs/*.log`

单套件报告包含场景、profile、冷启动时间、步骤耗时、断言结果、性能样本和 finding。全量报告汇总每个套件的 `passed`、`failed` 或 `blocked-external` 状态。

判定规则：

- 功能断言失败、对话框未关闭、目标页面未出现或状态未持久化均为 `failed`。
- 外部服务配置未提供时为 `blocked-external`，不计作功能通过。
- 性能值用于同一机器、同一构建方式下的趋势比较，不作为跨机器固定发布阈值。
- 首次页面创建包含视图构造和数据加载，应与预热后的视觉树/动作耗时分开观察。
- 原生文件选择器、目录选择器、系统托盘和真实备份/还原不由 Avalonia CDP 控制，保留 Windows 原生人工或专用驱动验证。

## 6. 2026-08-22 本机基线

最终全量报告：`.build-tmp/ui-test/reports/ui-full-test-2026-08-22T06-09-21-453Z.json`。

| 项目 | 结果 |
| --- | ---: |
| 全量套件 | 9 / 9 passed |
| 结构化步骤 | 72 / 72 passed |
| smoke | passed，含 1 个默认日期与当天不同的 warning |
| 全量耗时 | 91,288 ms |
| Debug 冷启动到 CDP ready | 1,516–2,510 ms |
| core 视觉树 P50 / P95 | 4.82 / 8.40 ms |
| core 全窗口截图 P50 / P95 | 120.05 / 138.42 ms |
| smoke `DOM.getDocument` P50 / P95 | 7.40 / 20.67 ms |
| smoke `DOM.querySelector` P50 / P95 | 0.58 / 0.76 ms |
| smoke 截图 P50 / P95 | 122.92 / 135.29 ms |
| 设置对话框三次打开 P95 | 138.59 ms |
| 脚本页刷新最大值 | 49.28 ms |
| Survey 视觉树 P95 / 动作 P95 | 7.86 / 21.05 ms |
| 附加字段完整套件 | 15,769 ms |
| Redmine 工时同步 | 235.94 ms |
| Redmine 管理页导航 P95 | 107.69 ms |

稳定性收口还使用两个全新 profile 连续执行 `ui-core-full`，两次均为 14/14 passed，用于确认删除取消对话框和保存查询删除取消不会遮挡后续页面。

本轮运行日期和系统日期均为 2026-08-22，隔离 profile 目录名中的 `20260822` 与实际日期一致。smoke 的日期 warning 表示新建事项继承的当前编辑日期为 2026-08-06；执行“使用今天”后正确切换为 2026-08-22，不影响草稿保留和本地保存断言。

Redmine 套件使用隔离测试服务并产生可清理测试数据；报告只记录耗时、数量和远程 ID 摘要，不包含服务器地址、账号或凭据。

该 Windows 全量报告生成后，`ui-extended-full` 新增 AI 上下文和程序设置 MCP 配置两个步骤，因此当前套件定义合计为 74 步；历史报告中的 72/72 数字保持不变，避免把未执行的新步骤写入旧报告。

富数据日期切换专项使用 10 个日期、每日 14 条事项、250 个 Redmine Issue，并为每条事项设置 Tracker 绑定、2 个标签和 9 类附加字段。相同 CDP 脚本连续切换 12 次日期：优化前中位数约 149 ms、P95/最大约 218 ms；批量预取附加字段并共享 Tracker 选项后，首轮中位数约 127 ms、P95 约 194 ms，热身后的连续切换中位数约 80 ms、P95 约 135 ms。该结果用于本机趋势比较，不作为跨机器固定阈值。

同一富数据 profile 还验证了“复制最近”：源事项包含 Redmine 绑定、2 个标签和 9 类附加字段。修复前复制事务回滚；修复后目标事项成功落在 2026-08-22，两个标签和 9 个字段值与源事项一致，Redmine/Jira 绑定数为 0，符合确认框“不复制远程 Tracker 绑定”的语义。测试新增记录随后已删除。

### 6.1 2026-08-23 Linux X11 验证

Linux 原生 Debug App 在 `DISPLAY=:0`、1280×800 桌面会话中完成验证：生命周期工具可在命令退出后保持独立 App 会话，并由后续 `status/run/stop` 命令接管；`ui-core-full` 为 14/14 passed，`ui-smoke` passed，`ui-extended-full` 为 11/11 passed。Debug 冷启动到 CDP ready 为 1,438–1,960 ms，core 截图 P50/P95 为 53.45/71.82 ms，smoke `DOM.getDocument` P50/P95 为 8.77/11.01 ms；最新扩展套件耗时 9,023 ms。

扩展套件使用隔离 profile 预置一个标签、一项附加字段定义和一条示例事项，验证事项披露默认关闭、日期控件仅在显式授权后显示、预览包含版本化 schema 和不可信数据标记，以及刷新后的 MCP 快照只包含授权范围。随后打开程序设置，验证“AI 与 MCP”使用标准设置分组、五个设置行完整可见、复制内容中的可执行文件/快照路径、AI 可读说明和通用 JSON，并通过“打开 AI 上下文”返回正确页签。截图前会将最后一个操作行滚动到可视区域；用户手册版本已裁掉状态栏和 MCP 命令中的本机路径。

smoke 在较矮窗口中只渲染前三个列表项，第 4 个模板事项会被 ListBox 虚拟化。最终持久化断言因此读取隔离 SQLite profile，确认标题、1.5 小时和标签关联已提交；UI 侧仍验证模板应用、保存状态和页面导航。当前机器未安装 Xvfb，因此本轮没有宣称 headless CI 已通过。

## 7. 当前覆盖边界

- Jira 真实服务、权限矩阵和自托管版本差异为 `Blocked-External`。
- 原生文件/目录选择器、托盘、真实备份与还原为 `Manual-Native`。
- 查询/统计计算、附加字段值转换和 Tracker 数据契约等低层语义继续由单元和集成测试承担；附加字段的真实控件交互和持久化已由 CDP 套件覆盖。
- 后台任务进度预览 UI 尚未实现，状态为 `Not-Implemented`。
- Release 不运行 CDP；“Release 包不含 CDP”由构建和发布包集成校验负责。
- 脚本交互式 XLSX/CSV/DOCX/Mustache 导出仍缺少真实 UI 端到端门禁。

## 8. CDP 兼容性

已在 Windows 和 Linux 验证 `DOM.getDocument`、`DOM.querySelector`、`DOM.getBoxModel`、`DOM.focus`、`Input.dispatchMouseEvent`、`Input.dispatchKeyEvent`、`Input.insertText` 和 `Page.captureScreenshot`。Playwright 的 `connectOverCDP()` 可以建立连接，但高层截图调用在当前预览版 CDP 上可能等待超时，因此项目脚本使用原始 CDP 命令。

Linux 下部分 Avalonia 控件收到第一组坐标点击时只获取焦点，公共点击辅助函数会先执行 `DOM.focus` 再发送鼠标事件；带 Ctrl/Alt/Meta 的快捷键不再额外发送 `char` 事件，避免 `Ctrl+S` 保存后字符 `s` 进入文本框。状态栏日期断言按年月日数值匹配，不依赖 `yyyy/M/d` 或 `MM/dd/yyyy` 等区域格式。
