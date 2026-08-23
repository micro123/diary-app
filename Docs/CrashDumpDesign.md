# CrashDump 与崩溃提示设计

## 1. 范围

当前实现覆盖 Windows 和 Linux 的 .NET 托管进程崩溃诊断；macOS 不在当前产品支持、发布和验证范围内。
Dump 和关联滚动日志归档仅保存在本机，不自动上传。Dump 可能包含进程内存，日志可能包含操作路径和业务摘要，用户应在确认后再提供给他人。

## 2. 进程模型

`Diary.App` 使用同一发布可执行文件提供三个相互隔离的运行模式：

1. 正常应用模式：执行常规 Avalonia 初始化、单实例守卫、数据库和插件加载。
2. `--capture-crash-dump <request.json>`：独立捕获进程，不初始化正常应用；通过
   `Microsoft.Diagnostics.NETCore.Client.DiagnosticsClient.WriteDump` 对目标 PID 生成 Triage Dump，并以允许原日志文件继续被写入的共享读取方式，将当前保留的滚动日志压缩到 CrashDump 目录。
3. `--show-crash-report <result.json>`：独立最小 Avalonia 进程，不初始化数据库、插件、脚本或单实例守卫；
   显示异常类型、简要消息、Dump/日志归档状态和路径，并提供“打开 Dump 文件夹”操作。提示窗口固定顶部说明和底部操作区，
   中间详情区域在长异常或长路径下滚动，窗口允许调整大小，Dump 与日志归档路径可选择复制。

正常进程在最早的 `Program.Main` 阶段注册 `AppDomain.CurrentDomain.UnhandledException` 处理器。
发生终止性托管未处理异常后，原进程写入请求文件，启动捕获进程并最多等待 30 秒；捕获进程完成后写入结果文件、
启动崩溃提示进程并退出，原进程随后按原始异常终止。捕获与提示均为 best-effort，任何诊断失败都不得覆盖原始异常。

## 3. 文件和保留策略

目录：

- Windows：`%LOCALAPPDATA%/Diary.App/CrashDumps`
- Linux：`$XDG_DATA_HOME/Diary.App/CrashDumps` 对应的 .NET LocalApplicationData 目录

每次崩溃最多生成：

- `<进程>-<UTC时间>-<PID>.dmp`：Triage Dump；
- `<进程>-<UTC时间>-<PID>.logs.zip`：当时保留的应用滚动日志；
- `<进程>-<UTC时间>-<PID>.json`：捕获结果和简要异常信息；
- 捕获期间短暂存在的 `.request.json`，完成后删除。

正常应用日志只按大小滚动，基础文件名固定为 `Diary.App.log`，单文件上限为 16 MiB，达到上限后使用 `Diary.App_001.log`、`Diary.App_002.log` 等可预测序号继续写入，最多保留最近 4 个文件；文件名不包含日期。日志初始化时会清理旧版 `Diary.AppYYYYMMDD.log` 和 `Diary.AppYYYYMMDD_NNN.log`，避免旧文件绕过保留上限。默认只保留最近 5 个成功 Dump，并同步清理对应结果文件和 `.logs.zip` 日志归档。

## 4. 异常边界

以下情况会触发捕获：

- 到达 `AppDomain.CurrentDomain.UnhandledException` 且 `IsTerminating=true` 的托管异常。

以下情况不会触发崩溃 Dump：

- 已由 Avalonia UI 全局处理器设置 `Handled=true` 的可恢复 UI 异常；
- 已观察并记录的后台 Task 异常；
- 普通脚本编译或执行失败。

以下情况不能保证成功捕获：

- `Environment.FailFast`、StackOverflow、运行时或本机代码严重损坏；
- 诊断 IPC 被禁用；
- 目标进程在捕获进程连接前已经退出；
- 磁盘空间、权限或安全策略阻止写入。

Dump 和日志归档分别执行并记录结果：Dump 失败时仍会尽力收集日志，日志不存在或归档失败也不会改变 Dump 的成功状态，更不会覆盖原始异常。

这类更低层故障如需完整覆盖，后续应补充 .NET Runtime 自动 Dump 或操作系统级 core/WER 策略。

## 5. 测试与发布门禁

`CrashReporterTests` 覆盖：

- 请求/结果 JSON 往返和保留数量清理；
- 启动独立 `Diary.App --capture-crash-dump` 进程，对真实 .NET Worker 生成 Triage Dump，并归档当前与已滚动日志；
- 最小崩溃提示窗口的简要信息、Dump/日志状态、“打开 Dump 文件夹”按钮，以及长内容下操作区保持可见；
- 自启动命令行参数和无 shell 捕获模式。

Windows/Linux CI 都运行上述真实进程测试。正式和手动发布工作流同时检查
`Microsoft.Diagnostics.NETCore.Client.dll` 已进入自包含发布包。
