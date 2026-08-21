# DiaryApp 升级同步服务需求设计

## 1. 文档状态与关联设计

本文定义 DiaryApp 局域网升级同步服务的产品需求、数据模型、接口契约、同步流程、存储结构、安全边界、运维要求和验收标准。

本文是 [`ApplicationUpdateDesign.md`](ApplicationUpdateDesign.md) 的服务端专项设计。总设计定义客户端 `IUpdateSource`、逐文件清单、事务式更新器和 `Diary.Updater` 自举流程；本文定义负责实现 `IUpdateSource` 的独立服务程序。

当前代码尚未实现该服务。本文假定服务部署在受控局域网中，从 GitHub Release 同步源包，在服务本地生成清单和内容缓存，再向 DiaryApp 客户端提供升级数据。

## 2. 目标与范围

### 2.1 服务目标

- 从指定 GitHub 仓库的 Release 发现并同步受支持的 DiaryApp 发布包；
- 将 GitHub Release 源资产转换为稳定、可重复读取的本地发布快照；
- 为每个 `channel/rid/flavor` 维护独立的 latest 指针；
- 从最终应用目录生成规范化逐文件 SHA-256 清单；
- 以文件哈希为键保存内容缓存，支持多个版本和多个 flavor 复用相同文件；
- 为客户端提供最新清单、逐文件内容和完整包三类能力；
- 同步失败时继续提供上一次完整可用的 latest，不暴露半成品；
- 服务重启、网络中断、磁盘不足和进程崩溃后可以识别、清理或恢复同步事务；
- 通过局域网边界、服务访问控制、源包哈希和严格路径策略保护更新链路；
- 不向客户端下发命令、脚本、任意 URL 或安装目录之外的写入目标。

### 2.2 服务非目标

- 不替代 GitHub Release 的构建和发布流程；
- 不在服务端执行 DiaryApp 程序、更新器或包内脚本；
- 不修改应用数据库、配置、日志、备份或用户脚本；
- 第一阶段不实现 DLL 二进制差分、分块差分或服务端智能合并；
- 不把 GitHub API 暴露给客户端；
- 不允许客户端指定任意 GitHub 仓库、Tag、下载地址或本地文件路径；
- 不负责更新自身的部署程序、操作系统服务注册或宿主机操作系统；
- 不依赖 GitHub Release 说明文本承载机器可解析的核心协议。

### 2.3 信任假设

当前部署信任边界如下：

- GitHub 仓库、Release 发布权限和 CI 发布流程由项目维护者控制；
- 同步服务与 DiaryApp 客户端运行在受控局域网；
- 同步服务主机的本地文件系统、服务配置和管理凭据由部署方控制；
- 当前不要求 manifest 数字签名，源包 SHA-256 用于发现下载损坏、资产错配和本地缓存损坏，不替代不受控网络中的端到端信任；
- 如果服务以后暴露到不受控网络、跨租户网络或公网，必须重新设计清单签名、客户端公钥信任和服务身份验证，不能只增加一个公网端口。

## 3. 发布矩阵与版本语义

### 3.1 支持的发布维度

服务必须支持以下发布矩阵：

| RID | Flavor | 说明 |
| --- | --- | --- |
| `win-x64` | `standard` | Windows 普通自包含包 |
| `win-x64` | `python313` | Windows 普通包加 Python 3.13 embedded runtime |
| `linux-x64` | `standard` | Linux 普通自包含包 |

`python313` 不是 Linux 支持的 flavor。服务发现到不支持的 `rid/flavor` 组合时必须拒绝该资产，不得将其写入 latest。

### 3.2 发布维度字段

同步服务生成的每个客户端清单必须包含：

- `manifestFormatVersion`：清单格式版本；
- `versionId`：人类可读版本，例如 `1.0.0-r438`；
- `sequence`：单调递增的发布序号，用于客户端判断新旧；
- `dataVersion`：数据库兼容版本，只用于展示和风险提示；
- `channel`：`stable`、`preview` 或部署配置允许的其他频道；
- `rid`：目标运行时标识；
- `flavor`：包 flavor；
- `minUpdaterVersion`：应用该清单所需的最低更新器协议版本；
- `minIncrementalSequence`：允许从旧安装基线直接执行增量更新的最低序号；
- `manifestContentId`：规范化文件集合的逻辑内容哈希；
- `files`：逐文件清单；
- `fullPackage`：服务实际提供的完整包大小和字节 SHA-256。

客户端只使用 `sequence` 判断版本先后，不比较 `versionId` 字符串。服务不得根据 GitHub Release 的发布时间、资产上传时间或字符串排序自动生成版本顺序。

### 3.3 sequence 规则

- 同一个发布版本在不同 `rid/flavor` 上可以使用相同 `sequence`；
- 同一个 `channel` 中，服务不得把更小的 `sequence` 自动暴露为最新版本；
- 同一个 `channel/sequence/rid/flavor` 一旦成功发布，其源包、清单、内容对象和完整包都不可变；
- 如果源包内容、清单字段、文件集合或服务端完整包发生变化，必须生成新的 `sequence`；
- 服务重新下载完全相同的源资产时，可以恢复原有快照，不得因此生成无意义的新版本；
- 正式 Tag 中的 `-rN` 可以作为 `sequence` 的候选来源，但必须通过源元数据或严格格式校验确认；
- 不带 `rN` 的 alpha、beta、rc 或手动构建必须在机器可读源元数据中显式提供 `sequence`；
- 服务无法可靠确定 `sequence` 时，必须拒绝自动同步，不得按字典序或当前时间猜测。

## 4. 总体架构

### 4.1 逻辑组件

| 组件 | 主要职责 |
| --- | --- |
| `GitHubReleaseClient` | 通过 GitHub API 查询 Release 和资产，负责超时、重试和限流处理 |
| `ReleaseSelector` | 根据仓库、Tag、频道、发布状态和版本策略选择候选 Release |
| `SourceMetadataValidator` | 校验 CI 生成的机器可读源元数据与实际 Release 资产 |
| `SyncCoordinator` | 创建同步事务、串行化同一发布维度、驱动状态机 |
| `QuarantineStore` | 保存下载中的源 ZIP 和临时元数据，隔离未验证内容 |
| `ArchiveInspector` | 安全检查 ZIP、路径、链接、文件数量、大小和运行时目录 |
| `ManifestBuilder` | 遍历最终目录、计算逐文件哈希、分类组件并生成清单 |
| `ContentStore` | 按 SHA-256 保存、读取、校验和复用文件内容 |
| `PackageStore` | 保存服务实际提供的完整包及其大小、哈希和来源 |
| `SnapshotRepository` | 持久化源发布、快照、文件、Blob 引用和 latest 指针 |
| `LatestPublisher` | 在所有校验完成后原子提交本地发布快照 |
| `UpdateApi` | 实现客户端 `IUpdateSource` 的 HTTP 或其他局域网传输适配 |
| `AdminApi` | 提供受保护的同步、重试、状态、固定版本和垃圾回收操作 |
| `GarbageCollector` | 按引用关系清理不再需要的源包、完整包和内容 Blob |
| `HealthAndMetrics` | 提供存活、就绪、同步状态、日志和指标 |

### 4.2 数据流

一次成功同步必须按以下顺序执行：

1. 服务发现符合策略的 GitHub Release；
2. 服务读取源元数据，确定版本、序号、频道和资产矩阵；
3. 服务将所有必需运行包下载到隔离暂存区；
4. 服务逐个校验资产名称、大小、SHA-256 和 Release 归属；
5. 服务安全解压到不可直接暴露的临时目录；
6. 服务检查最终目录并生成逐文件清单；
7. 服务写入或复用所有内容 Blob；
8. 服务保存服务端完整包，第一阶段默认直接复用经过校验的源 ZIP；
9. 服务验证清单、Blob、完整包和发布维度之间的一致性；
10. 服务将新快照写入持久化仓库，并原子更新对应 latest 指针；
11. 服务清理本次事务的临时文件，但不立即删除仍被其他快照引用的数据。

任何步骤失败都只能将本次事务标记为 `Rejected` 或 `Failed`，不能替换已有 latest。

### 4.3 发布原子性

服务必须将一个 Release 的发布视为一个同步事务。默认要求 Release 中配置的所有必需矩阵资产均验证通过后才发布该 Release。若部署明确允许部分矩阵发布，也必须满足以下条件：

- 只为已完成的精确 `rid/flavor` 创建快照；
- 未完成的维度继续返回旧 latest 或无可用更新；
- 不得用另一个 RID 或 flavor 的包填补缺失维度；
- 当源元数据声明所有矩阵必须齐全时，任一资产缺失则整次发布不进入 latest。

客户端永远不能读取 `staging`、`quarantine` 或尚未完成的快照目录作为更新源。

## 5. GitHub Release 源资产契约

### 5.1 现有运行包命名

正式发布工作流当前生成以下资产：

```text
DiaryAppNG-<TAG>-win-x64.zip
DiaryAppNG-<TAG>-win-x64-dbg.zip
DiaryAppNG-<TAG>-win-x64-python313.zip
DiaryAppNG-<TAG>-linux-x64.zip
DiaryAppNG-<TAG>-linux-x64-dbg.zip
```

同步服务只同步运行包，不把 `-dbg.zip` 放入客户端内容缓存和应用清单。调试包可以被服务记录为 Release 附件，但不通过客户端更新接口提供。

运行包要求：

- Windows 和 Linux 应用包均为自包含包；
- 每个应用包都包含目标 RID 的 `Diary.Updater` 自包含单文件；
- 普通运行包不包含 PDB；
- Windows Python 包只用于 `win-x64/python313`；
- 包内不能包含指向外部路径的符号链接、重解析点或硬链接；
- 包的根目录必须是应用安装目录内容，不允许再套一层不可预期的版本目录，除非源元数据明确声明并由服务剥离该包装层；
- 包中的 `runtimes` 只保留目标 RID 和允许的 RID 无关目录，不包含其他平台目录。

### 5.2 机器可读源元数据

Release 说明文本不能作为同步协议。CI 必须额外生成一个机器可读的源元数据资产，建议命名为：

```text
DiaryAppNG-<TAG>-release-metadata.json
```

该文件不需要数字签名，但必须由服务按 JSON 格式和字段约束验证。最小结构如下：

```json
{
  "schemaVersion": 1,
  "repository": "owner/DiaryApp",
  "tag": "v1.0.0-r438",
  "commit": "0123456789abcdef0123456789abcdef01234567",
  "versionId": "1.0.0-r438",
  "sequence": 438,
  "dataVersion": "1.0.0",
  "channel": "stable",
  "manifestFormatVersion": 1,
  "minUpdaterVersion": 1,
  "minIncrementalSequence": 0,
  "assets": [
    {
      "rid": "win-x64",
      "flavor": "standard",
      "kind": "package",
      "name": "DiaryAppNG-v1.0.0-r438-win-x64.zip",
      "size": 81516234,
      "sha256": "..."
    },
    {
      "rid": "win-x64",
      "flavor": "python313",
      "kind": "package",
      "name": "DiaryAppNG-v1.0.0-r438-win-x64-python313.zip",
      "size": 122516234,
      "sha256": "..."
    },
    {
      "rid": "linux-x64",
      "flavor": "standard",
      "kind": "package",
      "name": "DiaryAppNG-v1.0.0-r438-linux-x64.zip",
      "size": 79516234,
      "sha256": "..."
    }
  ],
  "debugAssets": [
    {
      "rid": "win-x64",
      "name": "DiaryAppNG-v1.0.0-r438-win-x64-dbg.zip"
    },
    {
      "rid": "linux-x64",
      "name": "DiaryAppNG-v1.0.0-r438-linux-x64-dbg.zip"
    }
  ]
}
```

服务必须验证：

- `repository` 与配置的唯一允许仓库一致；
- `tag`、`commit`、GitHub Release 和资产归属一致；
- `versionId`、`sequence`、`dataVersion` 和 `channel` 非空且格式合法；
- `sequence` 为非负整数，且符合当前频道的单调规则；
- `manifestFormatVersion` 和 `minUpdaterVersion` 在服务支持范围内；
- `assets` 中每个 `rid/flavor` 组合最多出现一个运行包；
- 运行包名称、资产名称和实际 GitHub 资产一一对应；
- `size` 为非负整数，`sha256` 为 64 位小写十六进制；
- `python313` 只能与 `win-x64` 组合；
- 资产元数据没有遗漏配置要求的矩阵维度，也没有混入未知矩阵；
- 元数据中的资产大小和哈希与服务重新下载后计算出的值一致。

如果 Release 缺少源元数据，服务默认拒绝自动同步。仅允许管理员在受保护的本地管理入口显式导入并填写完整元数据，不允许客户端触发该旁路。

### 5.3 Release 选择规则

- 只查询配置中允许的仓库；
- 默认只处理已发布且非 Draft 的 Release；
- `stable` 和 `preview` 的映射以源元数据及服务配置为准，不能只依据 GitHub 的 `prerelease` 字段；
- 当前 Tag 规则中 `v1.0.0-r438` 含有连字符，GitHub 可能将其标记为 prerelease，因此服务不能把 `prerelease=true` 自动等同于 `preview`；
- 服务必须排除被管理员暂停、撤回或标记为不兼容的 Tag；
- 服务不得把 GitHub Release 发布时间作为 `sequence`；
- 同一 Tag 重新出现时必须比较 commit、资产名称、大小和哈希；有差异时拒绝覆盖原快照；
- 发现多个候选 Release 时按 `sequence` 选择，而不是按 Tag 字符串排序；
- 正式频道默认禁止自动降级；管理员可以在本地管理接口显式固定测试版本。

## 6. 同步流程与状态机

### 6.1 触发方式

服务至少支持：

- 启动时恢复上次未完成事务并执行一次非阻塞同步检查；
- 按配置周期自动轮询 GitHub Release；
- 管理员从受保护管理入口手动触发同步；
- 测试环境使用指定 Tag 或本地目录源进行离线同步。

客户端请求不应直接触发 GitHub 下载。客户端只能读取已经提交到本地 latest 的数据。

### 6.2 同步状态

每个同步事务至少包含以下状态：

```text
Discovered
Downloading
Downloaded
Inspecting
Extracting
Indexing
Packaging
ReadyToPublish
Published
Rejected
RetryWaiting
Failed
Cancelled
```

状态规则：

- `Discovered` 只表示找到候选 Release，不表示可以向客户端提供；
- `Downloading` 和 `Extracting` 期间产生的文件只能位于临时目录；
- `Inspecting` 必须完成源资产、归档和路径检查；
- `Indexing` 必须生成完整清单并写入内容 Blob；
- `Packaging` 必须确认完整包字节、大小和哈希；
- 只有 `ReadyToPublish` 的事务才能进入 latest 提交流程；
- `Published` 只能在快照、内容和 latest 指针都持久化成功后写入；
- `Rejected` 表示输入不符合发布要求，不应无限自动重试；
- `RetryWaiting` 表示网络、GitHub 限流或临时 IO 错误，按退避策略重试；
- `Failed` 必须保留可诊断原因，并在服务重启时重新判断是否可以恢复；
- `Cancelled` 不得留下可被客户端读取的 latest 或半成品数据。

### 6.3 并发与幂等

- 同一 `repository/tag/commit` 只能有一个活动同步事务；
- 同一 `channel/rid/flavor` 同时只能有一个发布事务持有写锁；
- 不同 RID 的下载可以并行，但 latest 提交必须经过同一个发布事务协调；
- 同一源资产哈希已存在时可以复用源缓存，不得重复生成不同的内容身份；
- 同一文件 SHA-256 已存在时直接复用 Blob；写入前后都必须校验哈希；
- 服务重启后依据持久化状态恢复，不依赖内存中的任务队列；
- 重试不得创建新的 `sequence`，除非发现源快照内容与原记录不一致；
- 同一发布的重复同步必须得到相同 `manifestContentId` 和逐文件哈希。

### 6.4 网络重试

- GitHub API 查询、资产下载和元数据读取分别配置超时；
- 连接失败、DNS 临时错误、5xx 和 429 可重试；
- 429 应遵守 `Retry-After`，并叠加最大退避上限；
- 4xx 中除 408、409 和 429 外默认不可重试，避免错误凭据造成请求风暴；
- 下载使用临时文件，只有完整长度和 SHA-256 校验成功后才改名为正式源缓存；
- 第一阶段可以不支持断点续传，但必须支持失败后删除临时文件并重新下载；
- GitHub 不可用时不得清空旧 latest。

## 7. 源包检查与清单生成

### 7.1 ZIP 安全检查

服务必须在解压前检查：

- 条目路径使用 `/` 或可安全转换为 `/`；
- 禁止绝对路径、盘符、UNC、空路径、`.`、`..` 和路径规范化后逃逸根目录；
- 禁止符号链接、硬链接、重解析点和其他可将写入指向根目录外的条目；
- 单个文件大小不超过配置上限；
- 解压后总字节数不超过配置上限；
- 条目数量和目录深度不超过配置上限；
- 压缩比异常或解压后远大于压缩包的归档进入人工检查或直接拒绝；
- 不允许重复路径、仅大小写不同的 Windows 冲突路径和规范化后相同的路径；
- 不允许 ZIP 中包含第二个未知应用包、嵌套 Release 包或服务配置文件。

服务不得直接把 ZIP 条目的 Unix 权限、时间戳或外部属性当作可信业务数据。Linux 可执行属性由最终解压文件和服务规则共同确定，Windows 使用目标文件类型和已知入口规则确定。

### 7.2 最终目录检查

安全解压后，服务必须确认：

- 应用入口和 `Diary.Updater` 存在；
- 目标 RID 的运行时目录存在；
- 不存在其他目标平台运行时目录；
- `Diary.Script.Worker` 及其运行时依赖存在；
- 普通应用包不包含 PDB；
- Windows `python313` 包存在可执行的 Python 3.13 embedded runtime；
- `standard` 包不会意外包含 Python 捆绑目录；
- 包内所有受管理文件都位于最终安装根目录；
- 应用包中没有 `.update/installed-manifest.json` 或其他自引用清单，除非服务明确采用重新打包模式；
- 所有必需文件的路径、大小和类型符合服务配置。

检查规则必须可配置但不能通过客户端请求修改。缺少入口、Worker、更新器或目标运行时的包必须拒绝。

### 7.3 文件分类

服务为每个文件写入 `component`，用于服务统计、诊断和未来批量传输。分类不是客户端删除权限的依据。初始分类建议：

| Component | 示例 |
| --- | --- |
| `app` | DiaryApp 主程序、核心程序集和应用配置模板 |
| `updater` | `Diary.Updater` 及其独立运行所需文件 |
| `worker` | `Diary.Script.Worker` 和 Worker 依赖 |
| `plugin` | Tracker 插件程序集和插件 UI |
| `runtime` | `runtimes/<rid>` 或 RID 无关运行库 |
| `font` | `Fonts/` 下的字体和授权文本 |
| `python` | Windows `python313` 包中的 Python runtime |
| `documentation` | 随应用发布的用户文档 |
| `other` | 通过验证但未纳入专门分类的受管理文件 |

分类规则必须固定、可测试，并在服务升级时保持向后兼容。当前规范化文件条目包含 `path`、`size`、`sha256`、`component` 和 `executable`，因此这些字段的序列化或取值变化都可能改变 `manifestContentId`；服务不得自行省略字段或更换算法，若要调整必须与客户端同步升级 `manifestFormatVersion`。

### 7.4 逐文件清单

每个受管理文件至少写入：

- `/` 作为分隔符的规范化相对路径；
- 原始未压缩字节数；
- 原始文件 SHA-256，小写 64 位十六进制；
- `component`；
- `executable`。

以下内容不进入普通受管理文件清单：

- PDB 和独立调试包内容；
- 源 ZIP 本身；
- 服务本地索引、日志、缓存数据库和同步事务文件；
- 客户端用户配置、数据库、日志、备份和脚本；
- 服务重新打包模式下的 `.update/installed-manifest.json` 自身。

清单生成必须按规范化路径的 ordinal 顺序排序。不同 ZIP 时间戳、压缩级别、创建工具和 CI Runner 不得改变逐文件哈希或 `manifestContentId`。

### 7.5 manifestContentId

服务必须复用总设计定义的规范化算法。实现上应将以下字段按固定属性顺序、无 BOM、无无意义空白序列化为 UTF-8：

```json
{
  "rid": "win-x64",
  "flavor": "standard",
  "files": [
    {
      "path": "Diary.App.dll",
      "size": 1422336,
      "sha256": "...",
      "component": "app",
      "executable": false
    }
  ]
}
```

`manifestContentId` 为上述规范化内容的 SHA-256。它不包含发布时间、GitHub 下载 URL、服务地址、ZIP 时间戳、ZIP 压缩结果或 `fullPackage` 哈希。

如果未来修改规范化字段、排序、哈希算法或 manifest 结构，必须提升 `manifestFormatVersion`，并在服务和客户端部署兼容版本后再发布新清单。

## 8. 内容缓存与完整包

### 8.1 Content Store

内容缓存以文件原始字节 SHA-256 作为唯一身份：

```text
blobs/sha256/ab/abcdef0123456789...
```

要求：

- Blob 写入使用临时文件，写完后校验长度和 SHA-256，再原子改名；
- 已存在的 Blob 必须在复用前检查文件存在、长度和哈希；
- Blob 路径由服务计算，不能直接使用客户端提供的路径；
- 同一哈希不能对应多个内容；
- Blob 不因某个 Release 删除而立即删除，必须由引用追踪和垃圾回收决定；
- API 读取 Blob 时可以流式传输，不能把大文件全部读入服务内存；
- Blob 损坏时，服务应从仍保留的源 ZIP 或其他可验证快照重建，不能继续对外提供损坏数据。

### 8.2 完整包策略

第一阶段默认直接提供经过校验的 GitHub 源 ZIP：

- 服务端 `fullPackage.size` 和 `fullPackage.sha256` 等于该源 ZIP 的实际值；
- 服务不需要重压缩或修改源 ZIP；
- 客户端成功应用事务后，根据服务返回的 manifest 写入本地 `.update/installed-manifest.json`；
- 源 ZIP 的字节哈希与逐文件 `manifestContentId` 互相独立。

如果未来服务需要把 `.update/installed-manifest.json` 或其他服务生成元数据放入完整包，必须：

- 将重新打包后的归档作为新的服务端包保存；
- 重新计算 `fullPackage.size` 和 `fullPackage.sha256`；
- 保留源 ZIP 的来源引用和源包哈希；
- 确保服务端包的文件集合仍等于 manifest 受管理文件加允许的元数据；
- 不把 `installed-manifest.json` 自身加入 manifest 的 `files` 或 `manifestContentId`；
- 在该快照发布前完成完整包解压复检。

同一个快照不能同时对同一 API 版本隐式提供两个不同的完整包。若源包和重新打包包并存，必须在快照记录中明确 `packageKind` 和唯一的 `fullPackage` 引用。

### 8.3 批量内容包

批量内容包是可选优化，不得改变逐文件清单身份：

- 批量包必须有独立的大小和 SHA-256；
- 批量包内每个文件仍按 manifest 的路径、大小和 SHA-256 验证；
- 批量包不能携带服务端命令或解压到安装目录之外的路径；
- 客户端不能因为批量包失败而跳过逐文件哈希验证；
- 第一阶段可以完全不提供批量包，客户端逐 Blob 下载仍必须可用。

## 9. 本地持久化模型

### 9.1 推荐目录结构

服务数据根目录由部署配置指定，不得默认写入 DiaryApp 客户端用户数据目录。推荐结构：

```text
<service-data>/
├─ config/
├─ source/
│  └─ github/<repository>/<tag>/
│     ├─ release-metadata.json
│     └─ assets/<asset-name>.zip
├─ snapshots/
│  └─ <channel>/<sequence>/<rid>/<flavor>/
│     ├─ manifest.json
│     ├─ package.zip
│     ├─ source.json
│     └─ status.json
├─ blobs/
│  └─ sha256/<prefix>/<sha256>
├─ indexes/
│  └─ latest/<channel>/<rid>/<flavor>.json
├─ transactions/<sync-id>/
├─ logs/
└─ locks/
```

`source`、`snapshots`、`blobs` 和 `indexes` 必须使用服务账户可写、普通客户端不可写的权限。客户端 API 进程只能通过服务代码读取已提交数据，不能直接暴露数据根目录。

### 9.2 元数据数据库

实现可以使用 SQLite、PostgreSQL 或其他事务数据库，但必须提供以下逻辑实体：

| 实体 | 关键字段 |
| --- | --- |
| `source_releases` | repository、tag、commit、version_id、sequence、channel、metadata_hash、status、discovered_at |
| `source_assets` | release_id、rid、flavor、kind、name、size、sha256、local_path、status |
| `snapshots` | snapshot_id、release_id、rid、flavor、manifest_format_version、manifest_content_id、package_size、package_sha256、status |
| `snapshot_files` | snapshot_id、path、size、sha256、component、executable |
| `blobs` | sha256、size、local_path、first_seen_at、last_verified_at、ref_count 或可重建引用信息 |
| `latest_pointers` | channel、rid、flavor、snapshot_id、sequence、updated_at |
| `sync_runs` | sync_id、trigger、state、error_code、retry_count、started_at、finished_at |
| `pins` | channel、rid、flavor、pinned_sequence、reason、expires_at |

所有路径、哈希、序号和发布维度字段都必须有唯一性和格式约束。数据库记录和文件对象的提交顺序必须保证：数据库不会指向不存在的文件，latest 不会指向未完成快照。

### 9.3 快照不变性

`Snapshot` 是服务向客户端暴露的不可变逻辑对象。快照进入 `Published` 后：

- manifest 文件不原地修改；
- package 文件不原地覆盖；
- snapshot_files 不更新已有路径；
- Blob 内容不原地替换；
- latest 只能从一个已完成快照切换到另一个已完成快照；
- 修复必须创建新的快照或恢复完全相同的快照文件。

## 10. 客户端 API 需求

### 10.1 通用要求

客户端 API 是局域网读取接口，与管理 API 分离。客户端不需要知道 GitHub、源快照路径、服务本地目录或同步事务 ID。

建议使用版本化 HTTP API：

```text
/api/v1/updates/...
```

服务必须：

- 支持请求取消和连接断开后的流资源释放；
- 返回正确的 `Content-Length`，如果支持 Range，必须遵守 Range 语义；
- 不对文件内容做会改变客户端可见字节的转换；
- 不在响应中返回访问令牌、GitHub 凭据、服务本地路径或管理错误堆栈；
- 对不合法的 `channel/rid/flavor/sequence/hash` 快速拒绝；
- 对客户端请求设置并发、连接、单请求字节和请求频率限制。

### 10.2 最新清单

```http
GET /api/v1/updates/latest?channel=stable&rid=win-x64&flavor=standard
```

成功响应：

- `200 OK`：返回已发布的 `UpdateManifestEnvelope`；
- `404 Not Found`：精确发布维度没有本地 latest，客户端映射为 `null`；
- `400 Bad Request`：参数格式或组合不支持；
- `429 Too Many Requests`：请求过于频繁；
- `503 Service Unavailable`：服务未就绪、数据库不可用或同步服务暂时无法提供读取；
- `500`：服务内部错误，不得伪装成无更新。

响应示例：

```json
{
  "manifest": {
    "manifestFormatVersion": 1,
    "versionId": "1.0.0-r438",
    "sequence": 438,
    "dataVersion": "1.0.0",
    "channel": "stable",
    "rid": "win-x64",
    "flavor": "standard",
    "minUpdaterVersion": 1,
    "minIncrementalSequence": 0,
    "manifestContentId": "sha256:...",
    "files": []
  },
  "fullPackage": {
    "size": 81516234,
    "sha256": "..."
  }
}
```

服务必须保证返回的 manifest、fullPackage 和 snapshot 是同一快照。latest 指针切换后，旧快照在保留期内仍可以通过其完整包和内容哈希读取。

### 10.3 内容 Blob

```http
GET /api/v1/updates/content/{sha256}
```

要求：

- `{sha256}` 必须是小写 64 位十六进制；
- `200 OK` 返回原始文件字节；
- `Content-Length` 必须等于 manifest 中的 `size`；
- 读取前或后台完整性检查发现 Blob 损坏时返回 `503` 或 `500`，不能返回损坏字节；
- 不存在的 Blob 返回 `404`，并记录快照引用关系以便服务修复；
- 允许使用 `ETag`、`Last-Modified` 和缓存控制，但缓存标识不能让不同哈希共享响应；
- 第一阶段可以不支持 Range；若支持 Range，服务必须验证范围、响应长度和最终客户端哈希。

### 10.4 完整包

```http
GET /api/v1/updates/packages/{channel}/{sequence}/{rid}/{flavor}
```

要求：

- 只返回已发布且仍在保留期内的快照；
- 路径中的发布维度必须与快照 manifest 完全匹配；
- 只允许 `GET`，不允许客户端上传、替换或请求服务重打包；
- `200 OK` 返回服务端 `fullPackage` 对应的原始归档字节；
- `Content-Length` 必须等于 `fullPackage.size`；
- 如果快照已被清理，返回 `410 Gone` 并记录可供管理员修复的错误；
- 如果服务内存在快照但包损坏或不可读，返回 `503`，不能静默返回其他版本；
- 包返回后客户端仍必须验证 `fullPackage.sha256` 和逐文件清单。

### 10.5 错误响应

除成功响应外，API 应返回统一错误对象：

```json
{
  "error": {
    "code": "UPSTREAM_UNAVAILABLE",
    "message": "update source is temporarily unavailable",
    "retryable": true,
    "requestId": "..."
  }
}
```

`message` 不应包含 GitHub Token、完整本地路径、SQL、堆栈或内部凭据。初始错误码至少包括：

- `INVALID_DIMENSION`；
- `NO_LOCAL_SNAPSHOT`；
- `SERVICE_NOT_READY`；
- `UPSTREAM_UNAVAILABLE`；
- `RATE_LIMITED`；
- `SNAPSHOT_CORRUPT`；
- `BLOB_NOT_FOUND`；
- `PACKAGE_NOT_FOUND`；
- `INTERNAL_ERROR`。

### 10.6 健康接口

客户端和部署系统可以读取独立的健康接口：

```http
GET /health/live
GET /health/ready
GET /health/status
```

- `live` 只表示进程仍能响应；
- `ready` 还必须确认数据库、索引和至少一个可配置存储路径可用；
- `status` 可以返回最近同步时间、当前同步状态、latest 数量和存储使用率，但不得返回 GitHub Token 或管理凭据。

## 11. 管理 API 与配置

### 11.1 管理接口边界

管理 API 必须与客户端 API 使用不同端口、不同监听地址或至少不同访问控制策略。推荐只监听 `localhost`，由管理员通过本机 CLI 或受保护的管理 UI 调用。

允许的管理操作：

- 查询同步状态和最近失败原因；
- 触发配置范围内的 Release 发现和同步；
- 重试 `RetryWaiting` 或临时失败事务；
- 暂停或恢复某个 channel；
- 将某个已存在快照固定为测试频道 latest；
- 清理满足引用和保留策略的缓存；
- 导出诊断摘要；
- 重新校验指定快照和 Blob。

管理接口不得接受任意 URL、任意脚本、任意命令行或任意目标进程。固定版本操作只能引用服务已经验证并持久化的 `snapshot_id`，不能引用客户端提交的文件路径。

### 11.2 必需配置

服务配置至少包括：

```yaml
github:
  repository: owner/DiaryApp
  apiBaseUrl: https://api.github.com
  token: ${DIARY_GITHUB_TOKEN}
  pollIntervalMinutes: 15
  requestTimeoutSeconds: 60

releases:
  requiredVariants:
    - rid: win-x64
      flavor: standard
    - rid: win-x64
      flavor: python313
    - rid: linux-x64
      flavor: standard
  channels:
    stable:
      allowPrerelease: false
    preview:
      allowPrerelease: true

storage:
  root: /var/lib/diaryapp-updates
  maxBytes: 107374182400
  maxSourceArchiveBytes: 1073741824
  maxExtractedBytes: 5368709120
  maxFileCount: 100000
  retainPublishedSequences: 2

server:
  clientListenAddress: 192.168.1.10
  clientPort: 8090
  adminListenAddress: 127.0.0.1
  adminPort: 8091
```

实际配置格式可以是 JSON、YAML 或环境变量，但必须将机密与普通配置分离。GitHub Token 不得写入普通 Git 仓库、Release 资产、日志或错误响应。

### 11.3 配置校验

启动时必须校验：

- 仓库标识、GitHub API 地址和 Token 格式；
- channel 名称和 prerelease 策略；
- requiredVariants 没有重复，且组合在支持矩阵内；
- 存储根目录存在或可创建，权限正确，剩余空间达到最低启动门槛；
- 单文件、总解压、文件数量和保留策略为有效正数；
- 客户端和管理端监听地址不冲突；
- 配置中的最大值不会超过实现安全上限。

配置变更不应自动改变已发布快照和历史序号。变更 requiredVariants 后，服务必须在下一次同步前重新验证矩阵，不得用旧配置假装新 Release 完整。

## 12. 安全要求

### 12.1 上游访问

- 只允许连接配置中的 GitHub API 域名和资产下载域名；
- 不接受从客户端转发的 URL，防止 SSRF；
- 使用 HTTPS 验证 GitHub 证书；
- GitHub Token 只申请读取 Release 所需的最小权限；
- 下载请求必须设置连接、响应头和整体读取超时；
- 服务必须校验 Release 所属仓库、Tag 和 commit；
- 源资产元数据中的哈希只能在服务重新计算并一致后通过；
- GitHub API 返回的外部重定向只能跟随允许的官方域名；
- 资产下载失败不得将残留临时文件作为可用源包。

### 12.2 局域网服务

- 客户端端口只监听配置的局域网接口，不默认监听所有接口；
- 管理端口默认只监听 localhost；
- 可以配置 IP allowlist、反向代理认证或局域网身份认证；
- 普通客户端请求不能访问管理 API、源缓存、数据库和日志目录；
- 服务端不应将 GitHub Token 或同步管理凭据放在客户端可读取的响应头中；
- 同步服务进程使用独立低权限账户运行；
- 数据根目录和配置目录设置最小文件权限；
- 普通请求不能触发任意同步目标、任意 Tag 或任意清理路径。

### 12.3 文件与归档

- 所有路径写入前必须基于规范化绝对路径检查仍位于指定根目录内；
- 拒绝符号链接、硬链接、重解析点和路径穿越；
- 服务不执行包内文件；
- 解压、重打包和 Blob 读取均设置大小、数量和超时限制；
- 临时目录和正式快照目录不能跨越服务数据根目录；
- 失败事务的临时文件必须可识别、可清理且不能被 API 枚举；
- 文件内容和索引写入采用临时文件加原子改名；
- Windows 检查大小写不敏感冲突，Linux 保留执行权限时只允许规则确认过的文件。

### 12.4 降级与撤回

- stable 默认禁止自动降级；
- preview 可以按配置允许降级，但必须由管理员显式固定；
- 被撤回的 Release 不得成为新的 latest；
- 撤回不应立即删除已有客户端正在下载的快照，先标记不可继续发布并等待事务完成或过期；
- 破坏性回滚必须记录操作人、时间、目标 snapshot 和理由。

## 13. 失败处理与恢复

### 13.1 GitHub 不可用

- 保留现有 latest 和所有仍在保留期内的 Blob；
- health 状态显示 `upstream_degraded`，但客户端仍可以读取已发布快照；
- 按指数退避重试，不重复创建发布快照；
- 管理状态中记录最近成功同步和最近失败原因；
- 不把“暂时无法检查”返回为“没有更新”。

### 13.2 源包校验失败

- 将资产移入隔离失败目录或删除，不进入 source 正式缓存；
- 标记 Release 为 `Rejected`；
- 不修改对应频道的 latest；
- 记录资产名、预期大小/哈希、实际大小/哈希和 requestId；
- 同一不变源资产不应无限重试，除非管理员手动重试或 GitHub 资产发生变化。

### 13.3 进程在同步中崩溃

服务重启时必须：

1. 加载 `sync_runs` 和文件事务状态；
2. 找出停留在 `Downloading`、`Extracting`、`Indexing` 或 `Packaging` 的事务；
3. 验证临时文件、数据库记录和源资产是否仍然匹配；
4. 可以安全恢复的继续执行，无法证明一致性的标记失败并清理；
5. 确认 latest 指针仍指向上一个完整快照；
6. 不依据目录名称猜测某个事务已经发布。

### 13.4 磁盘不足

- 在下载或解压前估算源包、解压目录、Blob、完整包和临时文件空间；
- 低于安全余量时拒绝新同步，不删除 latest；
- 写入中遇到磁盘不足时保留错误状态，清理本次临时文件；
- 垃圾回收失败不影响已经发布的快照；
- API 在已发布数据仍可读时继续服务；
- 管理状态报告已用空间、可用空间和预计下一事务空间。

### 13.5 本地数据损坏

- latest 索引损坏时从数据库和已完成快照重建；
- manifest 损坏时从 source ZIP 或可验证快照重新生成；
- Blob 损坏时从保留源包重建，不能用损坏 Blob 重新计算自身哈希；
- package 损坏时从源 ZIP 或最终目录重新生成并重新计算哈希；
- 无法恢复时将该快照标为不可用，并继续提供其他 latest；
- 修复过程不原地改写已发布快照，应生成新的可验证快照或恢复相同快照。

### 13.6 服务自身重启

- 服务启动先恢复元数据数据库和锁状态，再开放 `ready`；
- `live` 可以早于数据恢复成功，但 `ready` 必须等待索引检查完成；
- 启动扫描不得阻塞健康接口超过配置超时；
- 未完成同步事务恢复完成前，自动同步可以延迟，但不能覆盖 latest；
- 服务升级或重启期间，已发布完整包和 Blob 应继续可读，除非存储维护明确停机。

## 14. 保留策略与垃圾回收

### 14.1 保留对象

以下对象默认不可回收：

- 每个频道、RID、flavor 的当前 latest；
- 配置要求保留的最近若干正式版本；
- 管理员固定的 snapshot；
- 正在被客户端下载或服务事务引用的 package 和 Blob；
- 诊断或审计策略要求保留的源资产元数据。

### 14.2 回收算法

采用标记再清理：

1. 从 latest、pins、保留序号和活动事务开始标记 snapshot；
2. 从标记 snapshot 的 `snapshot_files` 标记所有 Blob；
3. 标记对应的完整包和源资产；
4. 再次确认没有活动读取或写入租约；
5. 删除未标记对象的文件；
6. 删除成功后再删除数据库记录；
7. 任一步失败保留对象并记录日志，下次继续。

垃圾回收不能通过路径模式直接删除未知文件，必须以数据库引用和受控目录为依据。服务数据根目录中的未知文件默认保留并报警，防止误删运维文件或外部备份。

## 15. 可观测性与审计

### 15.1 结构化日志

每次同步和 API 请求至少记录：

- `requestId` 或 `syncId`；
- repository、tag、commit、channel、rid、flavor；
- snapshot_id 和 sequence（如果已确定）；
- 阶段、状态、耗时和重试次数；
- 源资产名称、大小和哈希摘要；
- 错误码、retryable 标志和最终处理结果。

日志不得记录：

- GitHub Token；
- 管理认证凭据；
- 完整 Authorization 请求头；
- 客户端数据库、配置和用户文件内容；
- 不必要的完整本地绝对路径。

### 15.2 指标

至少提供以下指标：

- `update_sync_runs_total{result}`；
- `update_sync_duration_seconds`；
- `update_sync_bytes_downloaded_total`；
- `update_sync_failures_total{code}`；
- `update_latest_snapshots{channel,rid,flavor}`；
- `update_blob_count` 和 `update_blob_bytes`；
- `update_storage_bytes{kind}`；
- `update_api_requests_total{endpoint,status}`；
- `update_api_bytes_served_total{kind}`；
- `update_upstream_rate_limited_total`；
- `update_gc_deleted_bytes_total`。

### 15.3 审计事件

以下事件必须可追踪：

- Release 被发现、接受或拒绝；
- 源包下载、哈希验证和解压失败；
- snapshot 发布、撤回、固定和恢复；
- latest 切换；
- 管理员触发同步、回滚或垃圾回收；
- 配置变更和服务启动恢复。

## 16. 性能与容量要求

第一阶段不规定特定硬件，但实现必须满足：

- 下载和 Blob 服务使用流式 IO，不将完整 ZIP 或大文件一次性读入内存；
- 同一内容跨版本、跨 flavor 复用 Blob；
- 同一 Release 的不同资产可以有限并行下载；
- 单个 channel/rid/flavor 的 latest 查询不需要扫描所有文件；
- 内容请求不应触发重新计算哈希，完整性检查应在写入和后台校验阶段完成；
- 客户端取消下载后，服务应尽快释放文件句柄和读取租约；
- GitHub API 轮询有全局和仓库级限流保护；
- 存储达到配置阈值时停止新增同步并报告，不自动删除未确认的数据。

建议容量估算至少包括：

```text
源 ZIP 保留空间
+ 服务完整包保留空间
+ 去重后的 Blob 空间
+ 当前同步的下载临时空间
+ 解压临时空间
+ 数据库、日志和安全余量
```

服务启动和每次同步都应使用相同估算规则，避免只检查 ZIP 大小而忽略解压和 Blob 峰值。

## 17. 测试与验收

### 17.1 源同步测试

- GitHub 正式 Release 的五类资产能够正确映射到三个受支持的运行维度；
- debug ZIP 不进入客户端 manifest 和 Blob 内容；
- 缺少运行资产、资产重名、RID/flavor 错配和未知资产会拒绝发布；
- 源元数据与实际资产大小或哈希不一致会拒绝发布；
- Tag、commit、repository 不匹配会拒绝发布；
- stable、preview 和 GitHub prerelease 状态按照配置和源元数据处理；
- 不带可靠 sequence 的 Release 不会按字符串或时间自动发布；
- 相同源 Release 重复同步是幂等的；
- GitHub 429、5xx、网络断开和超时按退避规则重试；
- GitHub 不可用时已有 latest 仍可读取。

### 17.2 归档与清单测试

- ZIP 路径穿越、绝对路径、盘符、UNC、重复路径和大小写冲突均被拒绝；
- 符号链接、硬链接、重解析点和嵌套应用包均被拒绝；
- 单文件、总大小、文件数量、目录深度和压缩炸弹限制有效；
- PDB 只出现在 debug ZIP，普通包和 manifest 不包含 PDB；
- 目标 RID runtime 存在，非目标 runtime 被拒绝；
- `standard` 和 `python313` 文件集合符合 flavor 约束；
- `Diary.Updater`、Worker 和应用入口缺失时拒绝发布；
- 相同文件生成相同 SHA-256 和 `manifestContentId`；
- ZIP 时间戳、压缩级别和 CI Runner 改变不影响 manifestContentId；
- 服务重新打包时 fullPackage 哈希重新计算且与实际字节一致。

### 17.3 API 契约测试

- 精确 `channel/rid/flavor` 返回对应 latest；
- 无 latest 返回 404 并映射为客户端 `null`；
- GitHub 不可用、服务未就绪和内部错误不会误报为无更新；
- 内容 Blob 的响应长度和实际哈希正确；
- 内容 Blob 缺失、损坏和 package 缺失返回稳定错误码；
- 完整包路径与 manifest 维度不匹配时拒绝；
- API 不接受任意 URL、路径、命令或脚本；
- 客户端 API 无法访问管理接口、源目录和数据库；
- 错误响应不泄露 Token、凭据、堆栈和本地敏感路径；
- 并发读取、取消、重复请求和限流行为符合配置。

### 17.4 崩溃与恢复测试

- 下载、解压、哈希、Blob 写入、package 写入和 latest 提交的每个阶段中断后可恢复或清理；
- latest 提交前进程崩溃不会产生客户端可见半成品；
- latest 提交后进程崩溃不会回退到不存在的 snapshot；
- 数据库和文件对象不一致时服务能报告并重建；
- 磁盘不足不会删除已有 latest；
- 垃圾回收中断后不会删除被 latest、pin 或活动事务引用的内容；
- 服务重启后 `live`、`ready` 和同步恢复状态正确。

### 17.5 端到端测试

- 使用现有发布工作流生成的上一版本资产同步到测试服务；
- Windows standard、Windows python313 和 Linux standard 均能完成完整包更新；
- 使用上一版本安装清单执行新增、修改、删除和保留文件计划；
- 从服务下载的每个文件与服务 manifest 一致；
- 完整包和增量内容均能触发客户端 `Diary.Updater` 的回滚测试；
- 同步服务不可用时客户端仍保留当前版本并显示可重试错误；
- 旧 snapshot 在保留期内可继续完成更新；
- 测试服务和正式服务的源数据根目录、GitHub 仓库和频道配置不会交叉使用。

## 18. 部署与运维要求

### 18.1 运行形态

服务可以实现为 .NET Worker、Windows Service、Linux systemd 服务或容器，但必须满足：

- 进程退出时释放数据库、文件和 GitHub HTTP 资源；
- 支持优雅停止，正在进行的同步事务进入可恢复状态；
- 健康检查、日志和数据目录与程序二进制分离；
- 配置和 Token 可以在不提交代码的情况下注入；
- 服务自身升级不会使用 DiaryApp 客户端的安装目录更新事务；
- 运行账户不是管理员或 root，除非宿主平台确实需要并有额外隔离。

### 18.2 备份

至少备份：

- 发布元数据数据库；
- latest 指针和管理员 pin；
- 服务配置中不含机密的版本策略；
- 仍在保留期内的 manifest 和 source 元数据。

Blob 和完整包可以按容量策略从源 ZIP 重建，但正式环境应保留当前和上一正式版本的可直接服务内容，减少 GitHub 不可用时的恢复时间。

恢复后必须执行：

- 数据库完整性检查；
- latest 指向的 snapshot、manifest、package 和 Blob 引用检查；
- 随机或全量文件哈希检查；
- 健康状态恢复后再开放 `ready`。

### 18.3 版本兼容

- 服务 API 版本通过 `/api/v1` 管理；
- `manifestFormatVersion` 与服务程序版本独立；
- 服务可以同时读取多个仍在支持范围内的 manifest 格式；
- 不支持的 manifest 格式必须拒绝发布，不得降级解释；
- 数据库迁移必须先备份元数据并支持失败恢复；
- 服务升级期间不能原地修改已发布 snapshot 文件。

## 19. 实施阶段

### 阶段一：同步与完整包服务

- 实现 GitHub Release 查询、源元数据校验和三个发布维度映射；
- 实现源 ZIP 隔离下载、安全解压和最终目录检查；
- 实现 manifest、manifestContentId、完整包和 latest 索引；
- 实现 `GET latest` 和完整包 API；
- 实现同步状态、重试、失败保留旧 latest 和基础健康检查；
- 客户端先使用完整包更新，逐文件内容接口可以暂时返回未启用。

### 阶段二：逐文件内容服务

- 实现 Content Store 和 Blob 引用关系；
- 实现逐文件内容 API；
- 实现客户端增量更新所需的完整快照验证；
- 增加并发下载、缓存、限流和进度统计；
- 增加内容损坏重建和旧快照保留。

### 阶段三：运维与传输优化

- 实现管理 API、pin、撤回、诊断和垃圾回收；
- 增加批量内容包和可选 Range 下载；
- 增加高可用或只读副本时，保持 latest 和 snapshot 不变性；
- 根据真实容量决定是否引入分块或差分，不提前引入复杂协议。

## 20. 待确认决策

以下项目不阻塞第一阶段设计，但在服务实现前必须确定：

- 服务使用 SQLite 还是 PostgreSQL 保存元数据；
- 客户端 API 是否只使用明文局域网 HTTP，还是使用内部 HTTPS；
- 局域网客户端是否需要 IP allowlist、反向代理认证或应用级 Token；
- Release 元数据由 CI 作为额外资产上传，还是由服务从受控 Tag 规则生成；推荐前者；
- 服务端完整包第一阶段是否始终复用 GitHub 源 ZIP；推荐复用，避免不必要的重新打包；
- stable/preview 是否要求全矩阵资产齐全后才整体发布；推荐要求齐全；
- 服务自身是否需要独立的发布和回滚机制。

## 21. 结论

DiaryApp 升级同步服务应是一个受控局域网内的 Release 镜像和内容索引服务，而不是 GitHub API 代理或远程命令执行器。CI 负责生成可验证的 GitHub Release 源资产，服务负责下载、隔离、校验、生成清单、缓存内容、维护不可变快照并提供客户端 API。

第一阶段优先保证完整包同步、原子 latest、失败保留旧版本和安全归档处理；第二阶段再启用逐文件 Blob 服务。只要同步服务没有把未完成数据暴露给客户端，即使 GitHub 暂时不可用、服务进程重启或某次发布损坏，也不会破坏已有安装和已发布更新。
