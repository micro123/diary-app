# 独立配置管理工具设计

## 1. 背景与目标

Diary.App 的敏感配置使用应用主密钥加密；Windows 上主密钥再由当前用户范围的 DPAPI 保护。域迁移、SID 变化或用户配置损坏时，主程序可能无法读取配置。若配置内容本身有错误，也可能需要在不启动业务模块、数据库、Agent 和网络服务的情况下进行修复。

`Diary.ConfigTool` 是随应用发布的独立 Avalonia 程序，用于离线检查、编辑、备份和迁移 Diary.App 配置。它不改变工作事项、工时、标签或数据库结构。

## 2. 功能范围

第一版支持：

- 选择任意 Diary.App 配置目录，默认定位到当前用户的漫游配置目录；
- 显式识别程序、数据库、Tracker、AI 助手和可选模块配置；
- 显示文件是否存在、加密格式、修改时间、`.bak` 状态及读取错误；
- Windows 下显示当前账号和 SID，并将 DPAPI 解密失败解释为用户身份或凭据变化；
- Windows 下自动扫描并以下拉选项展示旧 SID 目录，显示文件数量、`Preferred` 状态和更新时间；选择旧 SID 并输入旧登录密码后，通过系统 `CryptUpdateProtectedState` 迁移旧用户 DPAPI 主密钥，扫描不到时仍可手动输入；
- 主密钥不可读时可在完整快照后重新初始化加密身份；旧主密钥保留为带时间戳的文件，随后通过明文配置包重建当前身份可读的配置；
- 以属性表编辑 JSON 标量，以 JSON 文本编辑完整对象和数组；
- 默认遮蔽名称中包含 password、secret、token、apiKey、credential 等字样的属性；
- 保存前查看原始内容和当前内容，保存时校验 JSON 根节点；
- 基于原始文件 SHA-256 指纹阻止覆盖外部修改；
- 原子保存，并在替换前写入 `.bak`；
- 从 `.bak` 恢复，恢复前将当前文件另存为带时间戳的副本；
- 导入、导出单个明文 JSON；
- 按清单导入、导出全部明文配置，整包导入前创建 `ConfigBackups/<时间戳>` 快照。

第一版不提供：

- 绕过 Windows DPAPI 或破解旧身份配置；
- 启动插件以执行插件自定义迁移；
- 修改数据库、工作事项或其他业务数据；
- 自动保存或在主程序运行时强制写入；
- 将 Agent 对话、审计日志、通知历史等运行状态当作普通配置编辑。

## 3. 配置目录与显式目录表

工具只展示显式注册的配置描述符，不递归把所有 JSON 文件识别为配置。描述符包含稳定 ID、显示名称、类别、相对路径、加密 purpose 和说明。

首批目录包括：

- `app_settings.json`；
- `sqlite_config.dat`、`pgsql_config.dat`；
- `redmine_settings.json`、`jira_settings.json`；
- `ai-agent-credentials.dat`、`ai-agent/settings.json`；
- `module-states.json`、`remote-mcp/settings.json`；
- `statistics.json`、`templates.json`、`work_item_queries.json`。

新增配置时需要显式加入目录表，避免误编辑会话、审计和缓存文件。

## 4. 加密与兼容性

`ConfigurationDocumentStore` 按指定目录独立工作，不依赖 `EasySaveLoad` 的进程级主密钥缓存。加密格式与主程序保持一致：

- 当前格式：`DiaryGCM` v1，使用配置 purpose 派生文件密钥并通过 AES-GCM 认证加密；
- 旧格式：识别 `Salted__` 并按既有 PBKDF2/AES-CBC 规则只读兼容；
- Windows：`.diary-master-key` 使用 `DataProtectionScope.CurrentUser`；
- Linux/macOS：主密钥为 32 字节随机数据，非 Windows 平台限制为当前用户读写。

工具不能绕过 DPAPI。当前身份无法解密 `.diary-master-key` 时，配置状态为不可读取，并禁止保存覆盖。用户仍可切换到正确身份导出明文，随后在新身份目录导入并重新加密。

若用户已确认旧身份无法恢复，但此前已经导出明文配置，可执行“重新初始化加密身份”。该操作仅在现有主密钥确实不可读取时开放：先创建配置与主密钥快照，再把旧主密钥移动为 `.diary-master-key.unreadable-<时间戳>`，而不是删除；首次导入加密配置时生成绑定当前身份的新主密钥。未重新导入的旧加密配置仍然不可读取。

若旧 SID 目录仍位于 `%APPDATA%\Microsoft\Protect` 且用户知道切换前的登录密码，可优先执行“使用旧 SID 和密码迁移”。工具调用 Windows `CryptUpdateProtectedState`，由系统使用旧密码解开旧 SID 目录内的用户 DPAPI 主密钥，再以当前账号凭据写入当前 SID 目录。该 API 必须从迁入后的当前账号运行，并会删除已成功迁移的旧主密钥。因此工具在调用前必须完整备份 Diary 配置、旧 SID 目录和当前 SID 目录，显示成功与失败数量，并在调用后立即验证 `.diary-master-key`。

旧密码仅保留在迁移对话框和同步 Windows API 调用期间，不写入配置、日志、通知或诊断导出。迁移失败不能自动回退 Windows DPAPI 目录，用户应使用调用前快照交由管理员恢复。

## 5. 写入保护

加载配置时记录原始字节 SHA-256。保存前重新计算磁盘文件指纹：

- 原文件被修改时拒绝保存；
- 原文件被删除时拒绝保存；
- 加载时不存在但后来被创建时拒绝保存；
- 加载失败的配置禁止保存。

通过校验后先写同目录临时文件并刷新磁盘，再复制原文件为 `.bak`，最后原子替换主文件。恢复 `.bak` 前另存当前文件，防止恢复操作本身造成不可逆覆盖。

## 6. 明文传输格式

整包导出目录包含 `manifest.json` 与按类别组织的 JSON 文件。清单记录格式版本、导出时间、源平台、稳定配置 ID、导出路径、源相对路径和是否包含敏感信息。

导入时只接受当前版本显式识别的配置 ID，所有清单路径都必须位于导入根目录内。导入前对现有配置和主密钥创建快照。无法读取的目标配置不会被空内容覆盖，用户必须先处理或移走原文件。

明文导出不提供保密性，UI 在单项和整包导出前明确提示其中可能包含密码、令牌、API Key 和代理凭据。

## 7. 发布与运行

`Diary.ConfigTool` 是独立的 `WinExe`/桌面可执行项目，加入解决方案的 `Tools` 分类。Diary.App 的 RID 发布过程会单独以 self-contained 方式发布工具，再将产物合并到应用目录，因此 Windows 包包含 `Diary.ConfigTool.exe`，Linux 包包含 `Diary.ConfigTool`。

工具可在 Diary.App 无法启动时直接运行。为了避免并发覆盖，UI 保存和整包导入前要求用户确认主程序已经退出；底层指纹校验作为第二层保护。

## 8. 测试与验收

核心测试覆盖：

- 加密配置往返、敏感内容不落明文和 `.bak` 创建；
- 外部修改后的保存拒绝；
- `.bak` 恢复及恢复前副本保留；
- 明文整包导出与重新加密导入；
- ConfigTool Debug/Release 构建；
- Windows/Linux 发布包中包含工具入口、DLL、deps 和 runtimeconfig。
