# DiaryApp

工作日记桌面应用，基于 .NET 10.0 + Avalonia UI 构建，支持 Linux 与 Windows。

## 功能概览

- **工作日记**：按天记录工作项（耗时、优先级、备注、标签），支持复制前一天记录、快捷工时与自然时间输入（如 `1h30m`）
- **Tracker 插件**：RedMine 与 Jira 工时同步，支持多实例、追加式提交、上传状态与结果确认
- **自定义事项查询**：日期/标签/文本/优先级过滤（五种标签模式）、快捷日期、结果导出 CSV/Markdown
- **标签自动化规则**：按 Tracker 实例配置「添加标签 → 补默认字段」规则
- **统计页**：工时分布、标签明细
- **Survey**：调查协议 v1/v2（端口 9721/9722），支持能力发现与自定义统计
- **脚本系统**：C#、Lua、Python 三种语言的脚本，支持应用脚本（手动执行）、编辑器脚本（日历右键按日/周/月/季/年/事项执行）、自动化脚本（`daily HH:mm` 定时 + 启动补跑）与查询脚本（只读统计汇总）。API 参考见 [`Docs/ScriptApi`](Docs/ScriptApi/CSharp.md)

## 运行要求

- .NET 10 SDK
- 可选：Python 3.10+（运行 Python 脚本时需要）、PostgreSQL（不使用 SQLite 时）、RedMine/Jira 服务（使用对应 Tracker 插件时）

## 数据库

- 默认使用 SQLite，本地即可运行；也支持 PostgreSQL
- 核心表与 Tracker 插件表共享同一物理数据库，插件 schema 独立迁移（当前 schema 见 [`Docs/diagrams/database-schema.puml`](Docs/diagrams/database-schema.puml)）

## 快速开始

```bash
dotnet build DiaryApp.sln
dotnet run --project Diary.App
```

运行测试（各测试项目均为可执行程序）：

```bash
for t in Diary.AppTests Diary.DbTests Diary.JiraTests Diary.RedMineTests Diary.ScriptTests Diary.SurveyTests Diary.UtilTests; do
    dotnet run --project $t
done
```

> RedMine 外部 API 测试默认跳过，设置 `DIARY_RUN_REDMINE_EXTERNAL_TESTS=1` 后运行。

## 文档

- 脚本 API 参考：[C#](Docs/ScriptApi/CSharp.md)、[Lua](Docs/ScriptApi/Lua.md)、[Python](Docs/ScriptApi/Python.md)
- 架构：[当前架构](Docs/CurrentArchitecture.md)、[插件目标架构](Docs/TrackerPluginArchitecture.md)、[脚本系统设计](Docs/ScriptSystemDesign.md)
- 发布说明：[CHANGELOG](Docs/CHANGELOG.md)

## 版本

- 版本号为 `1.0.0-r{CommitCount}`：`1.0.0` 是数据格式版本（`Diary.Core/DataVersion.cs`），`rN` 是发布时的 Git 提交计数，由构建脚本自动生成（`Diary.App/Scripts/gen_version.sh`）
- 正式发布：推送 `v*` 标签触发 CI（`.github/workflows/release-on-tags.yml`），自动构建 win-x64/linux-x64 自包含包并从 CHANGELOG 提取发布说明

## 配置文件加密

加密配置文件（如 `DiaryApp.config`）可以无需程序、仅通过 OpenSSL 命令行解密：

```bash
# 解密
openssl enc -aes-256-cbc -md sha256 -pbkdf2 -iter 100000 -d \
    -in DiaryApp.config -pass pass:你的密码

# 加密（如需手动生成）
openssl enc -aes-256-cbc -md sha256 -pbkdf2 -iter 100000 \
    -in 明文.json -out DiaryApp.config -pass pass:你的密码
```

**参数说明**

| 参数 | 含义 |
|------|------|
| `enc` | 对称加密子命令 |
| `-aes-256-cbc` | 算法 AES-256，CBC 模式 |
| `-md sha256` | 指定与程序一致的 PBKDF2 摘要算法 |
| `-pbkdf2` | 使用 PBKDF2 派生密钥 |
| `-iter 100000` | PBKDF2 迭代次数（与程序一致） |
| `-d` | 解密模式，不加为加密模式 |
| `-in <文件>` | 输入文件 |
| `-pass pass:<密码>` | 指定密码（也可用 `-pass file:<路径>` 从文件读取） |

> 迭代次数 100,000 遵循 OWASP/NIST 建议。可在程序中调整，但需与 `-iter` 保持一致。
