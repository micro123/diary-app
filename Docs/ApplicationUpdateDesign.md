# DiaryApp 应用更新设计

## 1. 文档状态与范围

本文定义 DiaryApp 客户端应用包更新的目标架构、清单协议和安全应用流程。当前代码尚未实现应用包增量更新；独立的局域网同步服务将在后续开发，从 GitHub Release 获取源包，在本地生成更新数据并通过抽象更新源向客户端提供服务。

同步服务的详细需求、GitHub Release 源资产契约、存储模型和 API 设计见 [ApplicationUpdateServerDesign.md](ApplicationUpdateServerDesign.md)。

本文只讨论应用安装目录中的程序文件更新，不替代以下现有机制：

- SQLite/PostgreSQL 数据库 schema 迁移；
- 数据库备份与还原；
- Tracker 插件 schema 迁移；
- 脚本共享包导入；
- 普通的程序重启。

## 2. 目标与非目标

### 2.1 目标

- 支持 Windows `win-x64` 和 Linux `linux-x64` 自包含发布包；
- 支持普通包和 Windows Python 捆绑包两种 `flavor`；
- 同步服务从最终发布目录生成规范化逐文件 SHA-256 清单；
- 客户端比较实际安装文件与最新清单，只下载新增、缺失或内容变化的文件；
- 局域网同步服务负责从 GitHub Release 同步源包、生成内容寻址文件和批量包，不影响客户端更新计划逻辑；
- 更新在暂存目录完成下载和验证，主程序退出后再替换安装文件；
- 替换失败可回滚，异常中断后可恢复；
- 未知用户文件、配置、数据库、日志、备份、脚本和外部字体不被删除；
- 清单不可用、跨度不受支持或本地状态异常时可回退完整包更新。

### 2.2 非目标

- 第一阶段不做 DLL 内部二进制差分；
- 第一阶段不做静默强制更新；
- 第一阶段不支持 macOS；
- 不允许服务端直接下发任意命令或更新安装目录之外的文件；
- 不把应用显示版本字符串直接当作可排序版本；
- 不依赖 ZIP 字节完全可复现来判断文件内容是否变化。

## 3. 当前基础与约束

- 应用显示版本为 `<DataVersion>-r<Git CommitCount>`；正式版本仍以发布 Tag 为人类可读标识；
- `DataVersion` 是数据库兼容版本，不等同于应用更新顺序；
- 配置位于 `ApplicationData/Diary.App`，数据库和日志位于 `LocalApplicationData/Diary.App`，正常情况下不在安装目录；
- 当前发布为 Windows/Linux 自包含目录，包含 .NET 运行时、Avalonia、第三方依赖、Tracker 插件、脚本 Worker、字体和文档；
- 当前 `Program.RequestRestart()` 只会在正常退出后重启同一安装，不具备文件替换能力；
- Windows 运行中的 EXE/DLL 不能可靠覆盖，Linux 也不应在主进程仍使用文件时原地修改；
- 当前 ZIP 发布方式只负责传输，ZIP 哈希不能表示逻辑文件集合是否相同。

## 4. 核心设计决策

### 4.1 使用逐文件内容清单

每个受管理文件至少记录：

- 规范化相对路径；
- 未压缩字节数；
- 原始文件 SHA-256；
- Windows/Linux 可执行属性；
- 所属组件，可用于服务器批量传输和 UI 统计。

客户端以文件 SHA-256 判断内容是否变化。文件时间、ZIP 条目时间、压缩结果和 CI Runner 元数据不参与内容相等判断。

### 4.2 区分内容身份与传输完整性

- `file.sha256`：文件解压后的原始内容哈希；
- `manifestContentId`：规范化文件列表的哈希，用于表示整个版本的逻辑内容；
- `fullPackage.sha256`：下载归档的字节哈希，只用于尽早发现传输损坏；
- 同步服务生成的 `manifest`：版本、发布维度和逐文件哈希的本地服务数据；当前部署边界由受控局域网、服务访问控制和源包校验保证，不要求清单数字签名。

即使相同文件被重新压缩成不同 ZIP，`manifestContentId` 仍应保持一致。同步服务重新打包时，服务端 `fullPackage.sha256` 以实际提供给客户端的归档为准，GitHub 源 ZIP 的哈希只作为同步源校验数据保存。

### 4.3 服务端通过抽象更新源接入

客户端逻辑只依赖以下能力：

```csharp
public interface IUpdateSource
{
    Task<UpdateManifestEnvelope?> GetLatestAsync(
        string channel,
        string runtimeIdentifier,
        string packageFlavor,
        CancellationToken cancellationToken);

    Task<Stream> OpenContentAsync(
        string sha256,
        CancellationToken cancellationToken);

    Task<Stream?> OpenFullPackageAsync(
        UpdateManifest manifest,
        CancellationToken cancellationToken);
}
```

`OpenContentAsync()` 的底层以后可以是：

- `blobs/sha256/<hash>` 内容寻址接口；
- 服务端动态生成的批量文件包；
- 对象存储；
- 本地目录测试源。

若后续需要减少 HTTP 请求，可以增加批量下载接口，但不改变更新计划中的逐文件身份。

### 4.4 GitHub Release 同步服务与服务端契约

服务端专项需求见 [ApplicationUpdateServerDesign.md](ApplicationUpdateServerDesign.md)。

服务端实现为独立的局域网同步服务，不把 GitHub API 直接暴露给客户端。同步服务负责把 GitHub Release 源包转换为本地更新源：

1. 按允许的仓库、Tag、channel、RID 和 flavor 规则查询 GitHub Release，不接受任意用户提供的下载地址。
2. 将源 ZIP 下载到隔离暂存区，校验 Release、资产名称、文件大小和源包 SHA-256；源包损坏或不符合发布矩阵时不得进入 latest。
3. 安全解压源 ZIP，检查路径穿越、链接、文件数量、单文件大小和总大小限制，并从最终目录生成规范化逐文件 `manifest`。
4. 在本地按文件 SHA-256 生成内容缓存、`manifestContentId` 和服务端完整包；若需要把 `.update/installed-manifest.json` 放入完整包，必须在本地重新打包后重新计算 `fullPackage.size` 和 `fullPackage.sha256`。
5. 完整包、逐文件内容缓存和清单全部校验通过后，原子更新本地 latest 索引。同步服务保存 GitHub 源 Release、资产名称和源包哈希，客户端只看到服务端最终包的 `fullPackage` 信息。

`IUpdateSource` 是客户端依赖的逻辑契约，不规定同步服务必须使用某种 HTTP 路径、对象存储或数据库。同步服务必须提供以下语义：

- `GetLatestAsync()` 按 `channel`、`rid` 和 `flavor` 精确选择本地 latest；服务端不根据客户端显示版本字符串判断新旧，客户端负责比较 `sequence`；
- 返回 `null` 只表示该发布维度没有已完成同步的清单；网络错误、鉴权失败、限流和服务故障必须作为可区分的不可用错误返回；
- `OpenContentAsync()` 只接受规范化的小写 64 位 SHA-256，返回本地缓存中对应的原始文件字节、正确长度和可重试的读取流；
- `OpenFullPackageAsync()` 返回与指定 `manifest` 的 `channel`、`sequence`、`rid` 和 `flavor` 完全匹配的服务端归档；客户端仍需按 `fullPackage.size` 和 `fullPackage.sha256` 验证归档，并在解压后按清单逐文件复检。

同步服务发布本地 latest 前必须确认：

- 清单引用的每个文件哈希都已缓存并可读取，文件长度与 `size` 一致；
- stable 和允许自动更新的 preview 清单都有可用的服务端完整包；
- 完整包、逐文件内容和清单来自同一个同步快照，不能让客户端看到只生成了一半的数据；
- 同一个 `channel/sequence/rid/flavor` 在服务端一经暴露即不可变；如果源包或生成数据发生变化必须生成新的 `sequence`，仅恢复完全相同的快照可以重建原数据；
- 当前 latest 和其引用的内容至少保留到该版本不再允许增量升级。

服务端和传输适配器应保留以下错误语义：

- GitHub 没有匹配的 Release：返回无可用更新；
- 本地没有对应发布维度：返回 `null`；
- 网络中断、超时、429 或 5xx：返回暂时不可用，客户端保留当前安装并允许稍后重试；
- 源包、内容缓存或服务端完整包缺失、长度不符或哈希不一致：视为同步数据损坏，禁止应用该版本；
- 清单格式、路径、RID、flavor、文件长度或文件哈希不合法：视为本地生成失败，不能暴露为 latest。

当前部署假定同步服务和客户端运行在受控局域网内，不要求清单数字签名。服务仍必须限制监听地址和访问来源，保护本地缓存、同步凭据及管理接口，并禁止通过更新接口下发命令、脚本或安装目录之外的路径。若未来服务暴露到不受控网络，必须重新引入清单签名或等价的端到端信任机制。

## 5. 版本与发布维度

清单必须包含以下维度：

- `versionId`：人类可读版本，例如 `1.0.0-r479`；
- `sequence`：服务端分配或由正式提交计数派生的单调递增整数；
- `dataVersion`：数据库数据版本，仅用于展示和升级风险提示；
- `channel`：例如 `stable`、`preview`；
- `rid`：`win-x64` 或 `linux-x64`；
- `flavor`：`standard` 或 `python313`；
- `manifestFormatVersion`：更新协议版本；
- `minUpdaterVersion`：应用该清单所需的最低更新器协议版本；
- `minIncrementalSequence`：允许直接增量升级的最低本地序号。

客户端使用 `sequence` 判断新旧，不对 `versionId` 做字符串大小比较。相同 `sequence` 的不同 `rid/flavor` 属于同一应用版本的不同发布产物。

同一 `channel/sequence/rid/flavor` 发布维度一经发布必须保持不可变；若任何受管理文件、清单字段或完整包内容发生变化，必须分配新的 `sequence`，不得覆盖原发布内容。

`minUpdaterVersion` 约束的是实际负责解析事务计划和执行替换的更新器版本，不是安装目录中目标 `Diary.Updater` 文件的显示版本。当前更新器版本不足时，不能仅因为选择了完整包就跳过该约束，必须先走 10.1.1 的协议自举交接。

## 6. 清单格式

示例：

```json
{
  "manifest": {
    "manifestFormatVersion": 1,
    "versionId": "1.0.0-r479",
    "sequence": 479,
    "dataVersion": "1.0.0",
    "channel": "stable",
    "rid": "win-x64",
    "flavor": "standard",
    "minUpdaterVersion": 1,
    "minIncrementalSequence": 450,
    "manifestContentId": "sha256:...",
    "files": [
      {
        "path": "Diary.App.dll",
        "size": 1422336,
        "sha256": "...",
        "component": "app",
        "executable": false
      },
      {
        "path": "Fonts/NotoSansMonoCJKsc-Regular.otf",
        "size": 16393784,
        "sha256": "...",
        "component": "font",
        "executable": false
      }
    ]
  },
  "fullPackage": {
    "size": 81516234,
    "sha256": "..."
  }
}
```

### 6.1 规范化规则

生成和解析清单时必须遵守：

- 路径为 UTF-8 相对路径并统一使用 `/`；
- 禁止空路径、绝对路径、盘符、UNC、`.` 和 `..` 段；
- 按路径的 ordinal 顺序排序；
- Windows 清单禁止仅大小写不同的重复路径；
- SHA-256 固定使用 64 位小写十六进制；
- JSON 属性顺序固定，UTF-8 无 BOM，不输出无意义空白；
- `fullPackage` 属于外层传输描述，不写入安装清单，也不参与 `manifestContentId`；归档哈希先检查传输损坏，解压后仍必须按逐文件清单复检；
- `manifestContentId` 由 `rid`、`flavor` 和规范化文件条目计算，不包含发布时间、下载地址或 ZIP 元数据。

## 7. 本地安装状态

每次完整安装或成功更新后，在安装目录保存应用管理的清单副本：

```text
<install>/.update/installed-manifest.json
```

运行事务状态和下载缓存保存在用户数据目录：

```text
<LocalApplicationData>/Diary.App/updates/
├─ state.json
├─ staging/<versionId>/<rid>/<flavor>/
├─ backup/<transactionId>/
└─ downloads/<sha256>.download
```

安装清单用于确定应用拥有过哪些文件；实际更新前仍要对需要复用的本地文件计算 SHA-256，不能只信任上次清单。

安装清单只保存同步服务生成的 `manifest`，自身不进入 `files` 或 `manifestContentId`，避免产生自引用。加载时必须重新验证 RID、flavor、路径和内容身份；验证失败即视为没有可信增量基线，不得依据该文件删除安装内容。

以下内容永远不作为普通应用文件删除：

- 不在已安装清单中的未知文件；
- `ApplicationData/Diary.App` 下的配置和脚本；
- `LocalApplicationData/Diary.App` 下的数据库、日志、更新状态和备份；
- 用户选择的外部字体和外部数据库备份；
- 安装目录外的任何路径。

## 8. 更新检查与计划生成

### 8.1 检查阶段

1. 根据当前操作系统、架构、更新频道和包 flavor 请求最新清单；
2. 校验清单格式版本、RID、flavor、路径安全和清单内容身份；
3. 比较 `sequence`，远端不大于本地时报告无更新；
4. 检查 `minUpdaterVersion` 和 `minIncrementalSequence`；
5. 缺少本地安装清单或不满足增量条件时选择完整包；
6. 满足条件时进入逐文件计划生成。

### 8.2 文件比较

对最新清单中的每个文件：

- 本地不存在：`Download`；
- 大小不同：`Download`；
- SHA-256 不同：`Download`；
- SHA-256 相同：`Keep`。

对旧安装清单中存在、但最新清单中不存在的路径：

- 当前文件仍与旧清单哈希一致：`Delete`；
- 当前文件已被本地修改：默认 `PreserveUnexpectedChange` 并记录警告，不静默删除。

未知文件既不下载也不删除。

计划生成结果至少包含：

- 下载文件列表和总字节数；
- 保留文件列表；
- 安全删除列表；
- 本地修改冲突；
- 是否退回完整包；
- 预计临时空间需求。

## 9. 下载与暂存

- 所有内容先下载到用户数据目录，不直接写安装目录；
- 下载文件名使用期望 SHA-256，避免服务端路径影响本地路径；
- 支持取消和断点续传属于后续增强，第一阶段可以重新下载单个失败文件；
- 每个文件下载完成后立即验证长度和 SHA-256；
- 同一哈希文件只保存一份，可跨版本复用下载缓存；
- 暂存完成后再次验证整个更新计划；
- 磁盘空间不足时在退出主程序前失败，不进入替换阶段；
- 完整包也必须先验证包哈希，再安全解压并验证最终逐文件清单。

## 10. 应用更新事务

### 10.1 外部更新进程

更新替换必须由独立进程执行。建议新增最小化 `Diary.Updater` 项目，并按目标 RID 发布为单文件工具。主程序在应用更新前将更新器复制到用户更新目录，从该目录启动，避免更新器依赖正在被替换的安装文件。

更新器参数使用结构化计划文件，不在命令行传递大段 JSON：

```text
Diary.Updater --apply <transaction.json> --wait-pid <pid>
```

计划文件包含：

- 安装目录的规范化绝对路径；
- 当前和目标版本；
- 主程序 PID；
- 暂存目录与备份目录；
- 新增、替换和删除操作；
- 每个源/目标文件的期望哈希；
- 更新成功后启动的可执行文件及原始参数；
- 随机事务令牌。
- 事务专属引导更新器副本的规范化绝对路径及其 SHA-256；
- 目标版本更新器暂存文件的路径、SHA-256、RID 和协议版本；
- 引导更新器与目标更新器之间的一次性交接令牌。

### 10.1.1 更新器自身更新与协议自举

安装目录中的 `Diary.Updater` 是受管理文件，可以随普通应用文件一起替换；当前正在运行的进程绝不能直接从该路径启动并试图覆盖自身。每个事务必须建立一个独立的、位于用户更新目录的引导副本：

1. `Diary.App` 完成清单验证和暂存后，必须先单独验证目标 `Diary.Updater` 的 RID、协议版本、执行权限、文件长度和 SHA-256。目标更新器来自已验证的暂存内容，不得从服务端提供的任意路径直接启动。
2. 主程序把当前可用的更新器复制到 `<LocalApplicationData>/Diary.App/updates/bootstrap/<transactionId>/`，复制结果再次按哈希验证，然后从该副本启动。引导副本是事务基础设施，不属于安装清单、完整包受管理文件集合或删除列表。
3. 引导副本必须是目标 RID 的自包含单文件工具，不能在运行时依赖安装目录中将要被替换的 DLL、配置或其他文件。它只通过事务计划、更新目录中的日志和暂存文件工作。
4. 主程序退出、单实例锁释放后，引导副本取得事务所有权并检查 `minUpdaterVersion`。当前引导版本足够时，由它直接执行完整计划，其中可以替换安装目录中的旧 `Diary.Updater`。
5. 当前引导版本不足时，引导副本不得执行普通文件替换。它只验证暂存的目标更新器和一次性交接令牌，然后以同一事务计划启动目标更新器；目标更新器重新验证整个计划并原子地领取事务所有权后，才进入 `Applying`。交接期间只能有一个进程执行文件操作。
6. 目标更新器从安装目录之外的暂存路径运行，因此可以替换安装目录中的旧更新器。目标更新器失败时负责按事务日志回滚；交接启动失败或目标更新器未领取事务所有权时，由引导副本负责回滚。任何一方都不得删除事务专属引导副本，直到新应用完成稳定性确认。
7. 更新器在 `Applying` 阶段崩溃时，事务日志必须保留引导副本、目标暂存副本和所有已完成操作。下次启动或用户选择修复时，必须使用仍可用的引导副本执行恢复入口，不能只依赖可能已损坏或已被回滚的安装目录更新器。
8. 新应用完成启动和稳定性确认后，才可清理引导副本、交接令牌和旧更新器备份。清理失败只记录日志，不影响已完成的更新。

建议命令行入口保持结构化且只传递文件路径：

```text
Diary.Updater --apply <transaction.json> --wait-pid <pid>
Diary.Updater --recover <transaction.json>
```

`--recover` 必须验证事务令牌、路径根、文件哈希和当前状态；它不能接受服务端命令、脚本或任意启动目标。完整包和增量包共用这套自举流程，完整包不是绕过更新器协议门槛的特殊通道。

### 10.2 主程序退出

1. 用户确认安装更新；
2. 主程序保存配置并停止 Survey、脚本 Worker、数据库连接和后台任务；
3. 启动外部更新器；
4. 通过现有 Avalonia 正常关闭路径退出，不使用直接 `Environment.Exit()`；
5. 更新器等待指定 PID 退出并设置超时；
6. 单实例锁释放后才开始替换。

### 10.3 文件替换

第一阶段采用受管理文件的事务式原地替换：

1. 将所有待替换和待删除的现有文件复制到事务备份目录；
2. 新文件先复制为目标目录中的同目录临时文件；
3. 验证临时文件哈希；
4. 使用同卷重命名或平台可用的原子替换操作提交单个文件；
5. 新增文件在最后重命名到目标路径；
6. 删除操作最后执行；
7. 写入新的 `installed-manifest.json`；
8. 将事务状态标记为 `Applied`；
9. 启动新版本应用。

目录替换不是跨平台统一原子操作，因此不能声称整个版本一次性原子切换；通过完整备份、逐项日志和逆序回滚保证事务恢复能力。未来若引入稳定 Launcher 和版本目录，可以升级为目录级版本切换。

### 10.4 Linux 权限

Linux 清单必须记录需要执行权限的文件。更新器在替换后显式设置：

- `Diary.App`；
- `Diary.Script.Worker`；
- `Diary.Updater`；
- 其他清单标记为 `executable` 的文件。

不得依赖 ZIP 自动保留 Unix mode。

## 11. 回滚与崩溃恢复

事务状态至少包括：

```text
Created
Downloading
ReadyToApply
WaitingForExit
HandoffPrepared
HandingOff
Applying
Applied
Restarted
RollingBack
RolledBack
Failed
```

每完成一个文件操作都把结果追加到事务日志。发生以下情况时执行逆序回滚：

- 文件复制、替换或权限设置失败；
- 新清单写入失败；
- 操作路径或哈希与计划不一致；
- 更新器崩溃后下次启动发现事务停留在 `Applying`。
- 引导更新器启动目标更新器失败，或目标更新器未能领取事务所有权；
- 目标更新器在替换安装目录中的 `Diary.Updater` 前后崩溃。

安装目录中的旧 `Diary.Updater` 必须和其他受替换文件一样进入事务备份。引导副本和目标更新器暂存文件位于用户更新目录，不能被安装目录的删除操作影响；恢复优先使用事务专属引导副本，避免恢复路径再次依赖安装目录中不确定版本的更新器。

如果新程序已成功启动但随后业务初始化失败，不自动回滚数据库迁移；应用包回滚与数据库降级是不同风险域。需要依赖现有数据库迁移备份和兼容性检查阻止不安全启动。第一阶段只保证文件事务失败时恢复旧程序文件。

备份清理策略：

- 成功启动并完成一次稳定性确认后删除旧事务备份；
- 默认最多保留最近一个成功版本备份；
- 清理失败只记录日志，不影响新版本运行。

## 12. 完整包兜底

以下情况使用完整包：

- 首次启用更新，本地没有可信安装清单；
- 本地 `sequence` 低于 `minIncrementalSequence`；
- 更新器协议版本过低且无法完成 10.1.1 的自举交接；
- 清单格式不支持；
- 本地安装目录存在大量冲突或关键文件无法识别；
- 服务端缺少所需内容哈希；
- 增量下载总量达到完整包大小的配置阈值；
- 用户手动选择完整修复。

完整包更新仍需遵循暂存、文件哈希、受管理路径、自举和回滚规则，不能直接清空安装目录后解压。若只是当前更新器版本低于 `minUpdaterVersion`，完整包不能作为绕过方式；必须先暂存并验证目标更新器，或报告需要升级更新器。

## 13. 客户端模块建议

```text
Diary.App/Updates/
├─ UpdateCheckService
├─ UpdateCoordinator
├─ UpdateSettings
└─ UpdateViewModel

Diary.Update/
├─ Models/
│  ├─ UpdateManifest
│  ├─ UpdateFileEntry
│  ├─ UpdatePlan
│  └─ UpdateTransaction
├─ IUpdateSource
├─ UpdateManifestVerifier
├─ UpdatePlanner
├─ UpdateDownloader
└─ UpdatePathPolicy

Diary.Updater/
├─ Program
├─ UpdateApplier
├─ UpdateHandoffService
├─ UpdateRollbackService
└─ UpdateProcessLauncher
```

职责边界：

- `Diary.Update` 不依赖 Avalonia，供主程序、更新器和测试共享；
- `Diary.App` 负责 UI、检查时机、用户确认和正常退出；
- `Diary.Updater` 只接受已经验证的事务计划，负责等待、替换、回滚和重启；
- 服务器实现通过 `IUpdateSource` 适配，不进入核心更新计划逻辑。

## 14. 打包流程集成

建议新增跨平台 .NET 清单生成工具，由同步服务调用，而不是分别在 PowerShell/Bash 中实现哈希和规范化：

```text
Diary.Update.ManifestTool
```

`Diary.Updater` 必须按目标 RID 发布为自包含单文件，并作为普通应用文件进入 GitHub Release 源 ZIP。事务级引导副本仍由客户端在运行时复制到用户更新目录，CI 不需要为每个事务预生成引导副本。

CI 的 Tag 发布流程只负责生成 GitHub Release 源资产：

1. `dotnet publish` 完成应用、脚本 Worker 和目标 RID 的 `Diary.Updater`；
2. 清理非目标 `runtimes`，并将 PDB 移入独立 `-dbg.zip`；调试符号不属于应用受管理文件，也不进入更新清单；
3. 生成普通完整 ZIP，并计算 GitHub 源 ZIP 的大小和 SHA-256；
4. Windows Python 包基于普通包的独立目录快照加入 Python 3.13，再以 `python313` flavor 生成独立源 ZIP；
5. 校验每个源 ZIP 的文件集合完整、路径安全且不含 PDB；
6. 生成供同步服务核对的源资产元数据，至少包含 Tag、资产名称、RID、flavor、源 ZIP 大小和 SHA-256；
7. 将普通源 ZIP、Windows Python 源 ZIP、独立调试符号包和源资产元数据上传到 GitHub Release。

同步服务从源 ZIP 最终目录生成逐文件 `manifest`、`manifestContentId`、内容缓存和服务端完整包。若服务端完整包携带 `.update/installed-manifest.json`，该文件只包含本地生成的 `manifest`，不包含 `fullPackage`，且自身不列入受管理文件；服务端必须对重新打包后的归档重新计算 `fullPackage.size` 和 `fullPackage.sha256`。

## 15. 安全要求

- 当前部署假定同步服务和客户端位于受控局域网，服务访问控制和网络边界是清单信任基础；
- 同步服务必须通过 HTTPS 从 GitHub 获取源包，并校验 CI 发布的源资产大小和 SHA-256；
- 所有目标路径必须在解析后的安装根目录内；
- 禁止符号链接、重解析点或硬链接把写入引向安装目录外；
- Windows 使用不区分大小写的冲突检查；
- 下载和解压设置单文件大小、总大小和文件数量上限；
- ZIP 或批量包必须防止路径穿越和解压炸弹；
- 同步服务的 GitHub 凭据、缓存目录和管理接口必须限制访问，不能由普通更新请求读取或修改；
- 更新器不接受服务端命令行、脚本或任意进程启动指令；
- 只允许启动清单和本地计划约定的 DiaryApp 入口；
- 日志不得包含访问令牌或完整服务器凭据；
- 正式渠道默认禁止降级，测试渠道降级必须由用户明确确认；
- 如果同步服务未来暴露到不受控网络，必须重新引入清单签名或等价的端到端完整性保护，不能继续只依赖局域网边界。

## 16. 用户体验

第一阶段建议：

- 启动后延迟检查，不阻塞主窗口；
- 设置页提供更新频道、自动检查和手动检查；
- 显示当前版本、目标版本、预计下载量和是否需要完整包；
- 下载可以取消，不在未确认时退出应用；
- 安装更新前提示应用将重启；
- 更新失败显示阶段、简要原因、日志路径和手动下载入口；
- 不在数据库正在备份/还原或脚本导入事务期间启动文件替换。

## 17. 测试与验收

### 17.1 清单和计划测试

- 相同文件生成相同逐文件哈希和 `manifestContentId`；
- ZIP 时间戳或压缩方式变化不影响内容判断；
- 新增、修改、缺失和安全删除计划正确；
- 本地修改文件不被静默删除；
- Windows 大小写冲突、绝对路径和 `..` 被拒绝；
- 文件哈希、长度、RID、flavor 和服务端生成清单的内容身份错误均失败；
- 未知清单版本触发完整包或升级更新器提示。

### 17.2 更新事务测试

- 主程序退出后才能替换锁定文件；
- 新增、替换、删除成功后清单一致；
- 任一步失败可逆序恢复；
- 更新器中途终止后可根据事务日志恢复；
- 当前更新器可以替换安装目录中的旧 `Diary.Updater`，且运行中的引导副本不被锁定或删除；
- `minUpdaterVersion` 高于当前版本时，旧引导副本只能完成验证和交接，目标更新器领取事务后才能替换文件；
- 引导启动失败、交接失败、目标更新器崩溃和替换更新器自身失败均可恢复；
- Windows 和 Linux 可执行权限正确；
- 用户配置、SQLite 数据库、日志、备份和脚本保持不变；
- standard 与 python flavor 不会互相删除组件；
- 完整包兜底可以修复缺失或损坏的安装目录；
- 更新成功后的数据库兼容检查和迁移继续由现有机制处理。

### 17.3 CI 验收

- Windows/Linux 对最终发布目录生成源 ZIP 并验证；同步服务从源 ZIP 生成清单并验证；
- 同步服务生成的清单文件数与服务端完整包受管理文件数一致；
- 随机抽样和全量哈希复检通过；
- 使用上一正式版本产物执行真实更新和回滚；
- Python 捆绑版单独执行 flavor 更新测试；
- 发布流程仍保留完整 ZIP，增量服务不可用不阻断手动更新；
- 同步服务只返回精确 `channel/rid/flavor` 的 latest，发布暴露前所有清单内容和完整包均可读取且哈希一致；
- 同一 `channel/sequence/rid/flavor` 的内容、清单和完整包不可变，生成数据发生变化时必须使用新的 `sequence`；
- 内容哈希不存在、响应长度错误、源包校验失败和服务端清单不一致均被客户端或同步服务拒绝；
- 同步服务缓存、重试、429、5xx 和内容缺失分别映射为可重试不可用、发布损坏或安全验证失败，不被误报为无更新。

## 18. 实施阶段

### 阶段一：可信完整更新基础

- 完成清单模型、规范化、源包校验和路径策略；
- 完成自包含独立更新器、事务日志、引导交接、文件替换和回滚；
- 第一版可以只下载完整包，但必须按最终文件清单验证和安装。

### 阶段二：逐文件增量

- 实现实际文件哈希比较和更新计划；
- 实现 `IUpdateSource.OpenContentAsync()`；
- 只下载变化内容，保留完整包兜底；
- 增加缓存、并发限制和进度统计。

### 阶段三：传输优化

- 服务端按需要增加批量内容包或内容寻址缓存；
- 根据真实发布数据决定是否对少数大文件引入二进制差分；
- 只有当请求数量或存储规模成为瓶颈时再引入分块/CAS。

## 19. 结论

DiaryApp 应采用基于规范化逐文件 SHA-256 清单的客户端更新模型。客户端比较实际安装内容与最新清单，局域网同步服务负责从 GitHub Release 生成精确发布维度的 latest、按内容哈希读取文件、匹配的完整包和不可变本地发布索引。ZIP 仅作为传输容器，不承担内容身份判断。

第一阶段优先建立源包校验、暂存、外部更新进程、更新器自举、事务替换和回滚；在此基础上增加逐文件下载。同步服务尚未开发时，可以先用本地目录测试源验证同一契约；若未来脱离受控局域网，必须重新评估端到端信任和清单签名。
