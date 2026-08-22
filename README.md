# DiaryApp

工作日记桌面应用，基于 .NET 10.0 + Avalonia UI 构建，支持 Linux 与 Windows。

## 功能概览

- **工作日记**：按天记录工作项（耗时、优先级、备注、标签），支持复制前一天记录、快捷工时与自然时间输入（如 `1h30m`）
- **Tracker 插件**：RedMine 与 Jira 工时同步，支持多实例、追加式提交、上传状态与结果确认
- **自定义事项查询**：日期/标签/文本/优先级过滤（五种标签模式）、快捷日期、结果导出 CSV/Markdown
- **标签自动化规则**：按 Tracker 实例配置「添加标签 → 补默认字段」规则
- **统计页**：工时分布、标签明细
- **界面字体**：默认使用发布包中的 Noto Sans Mono CJK SC，也可选择“跟随系统”、已安装的系统字体或外部 `.ttf`/`.otf` 字体文件；保存后立即切换，不可用时安全回退
- **跨设备工时调查**：在局域网中汇总多台 DiaryApp/DiaryToolpp 的工时统计，兼容旧版日期查询，并支持新版关键词、标签、优先级和明细查询
- **脚本系统**：C#、Lua、Python 三种语言的脚本，支持应用脚本（手动执行）、编辑器脚本（日历右键按日/周/月/季/年/事项执行）、自动化脚本（`daily HH:mm` 定时 + 启动补跑）与查询脚本（只读统计汇总）。API 参考见 [`Docs/ScriptApi`](Docs/ScriptApi/CSharp.md)

## 运行要求

- .NET 10 SDK
- 可选：Python 3.10+（运行 Python 脚本时需要）、PostgreSQL（不使用 SQLite 时）、RedMine/Jira 服务（使用对应 Tracker 插件时）
- 发布包在 `Fonts/` 中附带 Noto Sans Mono CJK SC 及其 SIL Open Font License 1.1 授权文本，作为中英文 2:1 等宽的应用默认字体及 CJK 后备字体，但不附带 Emoji 字体；Linux 环境仍需安装可用的 Emoji 字体，也可在程序设置中选择其他字体来源

## 数据库

- 默认使用 SQLite，本地即可运行；也支持 PostgreSQL
- 核心表与 Tracker 插件表共享同一物理数据库，插件 schema 独立迁移（当前 schema 见 [`Docs/diagrams/database-schema.puml`](Docs/diagrams/database-schema.puml)）
- 当前核心数据版本仍为 `1.0.0`，SQLite/PostgreSQL 暂无待执行核心迁移；提升数据版本时，契约测试要求两个 provider 同步登记迁移。SQLite 支持手动创建、校验和还原完整物理数据库，包含核心表与 Tracker 扩展表；还原任务会在下次启动时执行，失败时自动恢复还原前数据库。SQLite 迁移前也会将一致性快照写入数据库同目录的 `Backups`。PostgreSQL 已接入 custom-format `pg_dump`/`pg_restore`、工具版本检查、最小权限预检和独立目标库还原；具备 `CREATEDB` 时自动创建目标，否则使用设置中配置的已有空数据库。目标不得与当前库相同，启动原貌复检通过后才持久化配置切换。数据库设置已提供 PostgreSQL Client `bin` 目录配置，Windows 必须配置，Linux 未配置时会探测 `PATH`，缺少 `pg_dump`/`pg_restore` 时视为不支持。完整设计见 [`Docs/DatabaseBackupRestoreDesign.md`](Docs/DatabaseBackupRestoreDesign.md)

## 快速开始

```bash
dotnet build DiaryApp.sln
dotnet run --project Diary.App
```

运行全部测试：

```bash
dotnet test --solution DiaryApp.sln --configuration Release
```

> RedMine 外部 API 测试默认跳过，设置 `DIARY_RUN_REDMINE_EXTERNAL_TESTS=1` 后运行。PostgreSQL 契约测试依赖 Docker；CI 的 Linux 门禁设置 `DIARY_REQUIRE_POSTGRES_TESTS=1`，容器不可用时直接失败，本地未安装 Docker 时显示为 Inconclusive。

## 文档

- 最终用户手册（提交 Quarto 源文件与截图；CI 生成 HTML/PDF 并附加到 Release）：[`Docs/UserManual`](Docs/UserManual/index.qmd)
- 脚本 API 参考：[C#](Docs/ScriptApi/CSharp.md)、[Lua](Docs/ScriptApi/Lua.md)、[Python](Docs/ScriptApi/Python.md)
- 架构：[当前架构](Docs/CurrentArchitecture.md)、[数据库备份还原设计](Docs/DatabaseBackupRestoreDesign.md)、[插件目标架构](Docs/TrackerPluginArchitecture.md)、[脚本系统设计](Docs/ScriptSystemDesign.md)
- 调查功能：[使用指南](Docs/SurveyUserGuide.md)、[协议设计](Docs/SurveyProtocolDesign.md)
- 发布说明：[CHANGELOG](Docs/CHANGELOG.md)；Agent 发布操作：[新 Tag 发布指南](Docs/AgentReleaseTagGuide.md)

## 版本

- 版本号为 `1.0.0-r{CommitCount}`：`1.0.0` 是数据格式版本（`Diary.Core/DataVersion.cs`），`rN` 是发布时的 Git 提交计数，由 Windows/Linux 版本生成脚本自动生成
- 正式发布：推送 `v*` 标签触发 CI（`.github/workflows/release-on-tags.yml`）；Windows Runner 构建 `win-x64`，Ubuntu Runner 构建 `linux-x64`，全量测试和 Quarto 手册渲染通过后，发布应用包、调试符号、更新 metadata 及用户手册 PDF/HTML

## 配置文件加密

包含数据库密码、API Key 或 Token 的配置文件由程序自动使用 AES-256-GCM 加密并校验完整性。实际文件密钥由安装时随机生成的主密钥派生，代码中的 `StorageFileAttribute` 字符串仅作为用途标识，不是解密密码。

- Windows：主密钥使用当前登录用户的 DPAPI 保护。
- Linux：主密钥文件权限限制为当前用户读写。
- 旧版 `Salted__` AES-CBC/PBKDF2 配置仍可读取，下一次保存会自动迁移为新格式。

主密钥保存在应用配置目录的 `.diary-master-key`。备份或迁移加密配置时必须同时保留该文件；Windows 下主密钥还绑定原用户，不能只复制密文到其他用户或机器后直接解密。新格式不再支持仅凭硬编码口令使用 OpenSSL 手工解密。
