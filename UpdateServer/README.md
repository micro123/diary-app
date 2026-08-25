# DiaryApp Python 更新服务器

该服务仅使用 Python 3.11+ 标准库，支持两种发布来源：从指定 GitHub Releases 自动同步 `stable`/`preview`，以及由本机工具将已打包并校验的 Windows 包直接发布到 `local`。两种来源共用 ZIP 安全规则、逐文件 Blob 索引、manifest 和原子 latest 快照。

服务启动后立即同步一次，之后按 `pollIntervalSeconds` 定时重新查询 GitHub，默认周期为 6 小时。每个 `channel/rid/flavor` 只保留当前 latest 快照；新版本完整发布后会删除该维度的旧快照，并清理不再被任何 latest 引用的内容 Blob。`stable`、`preview` 和 `local` 相互独立；本机直传不会重置 GitHub 自动检查周期。

## 本机构建并发布到 local

### Windows 原生方式

Windows 10/11 推荐使用 PowerShell 7、Python 3.11+ 和 Quarto CLI 直接运行，不要求安装 Docker、Git Bash、`curl`、`zip` 或 `unzip`：

```powershell
# 第一次：后台启动 Python 服务，构建 standard 包并发布
.\Tools\local-update.ps1 all

# 服务已经运行时：只构建并发布下一个 local 版本
.\Tools\local-update.ps1 publish

# 查看服务和 local latest
.\Tools\local-update.ps1 status

# 停止服务；发布数据仍保留
.\Tools\local-update.ps1 server-stop
```

本机未安装 Quarto、但 `Docs/UserManual/_output/` 已有通过基本格式检查的 HTML/PDF 时，可仅为升级链路测试显式复用现有手册产物：

```powershell
.\Tools\local-update.ps1 publish -ReuseExistingManual
```

该选项会输出警告，生成的包可能不包含最新手册修改，不用于正式发布；默认行为仍要求安装 Quarto 并重新渲染。

PowerShell 工具默认发布 `win-x64/standard`，也可使用 `-Flavor python313` 构建带 Python 3.13 embeddable runtime 的包。两种包都会先用 Quarto 渲染用户手册，将稳定文件名的 HTML/PDF 注入 `Docs/UserManual`，并执行与 CI 相同的手册存在性和格式校验。Python flavor 会首次下载并校验 Python 官方 ZIP，缓存到 `artifacts/cache/python`：

```powershell
.\Tools\local-update.ps1 all -Flavor python313
```

服务配置、PID、日志和数据保存在 `UpdateServer/.local-windows/`，发布 Token 仍保存在 `UpdateServer/publish_token.txt`，这些本机文件都已忽略。服务进程以隐藏窗口启动；若启动失败，可查看 `server.stderr.log`。默认只监听 `127.0.0.1:18080`，用于同一台 Windows 机器上的升级测试。 PowerShell 工具使用 `serve-local` 模式，不启动 GitHub 定时同步，因此本地验证不依赖 GitHub 网络或 API 配额。

### Bash/Docker 方式

Linux、WSL 或已经安装 Docker/Git Bash 的环境可以继续使用原有工具；本机构建环境同样需要 Quarto CLI，以生成发布包内的 HTML/PDF 用户手册：

```bash
# 第一次：构建/启动本机 Docker 服务，打包并发布 python313 包
./Tools/local-update.sh all

# 服务已经运行时：只打包并发布下一个 local 版本
./Tools/local-update.sh publish

# 查看服务和 local latest
./Tools/local-update.sh status
```

两种工具都会生成仅限本机使用的随机发布 Token，在构建时注入单调递增的 UTC 时间序号和 `BuildChannel=local`，使包内 `AppSequence`、显示版本和服务器 manifest 保持一致；上传后还会回读 latest 并核对 sequence 与完整包 SHA-256。local 的 sequence 使用较大的时间值，但客户端记录构建频道；用户主动切回 `stable`/`preview` 时会按目标频道重新比较，不会被 local sequence 锁定。

本地发布只生成 local 通道需要的运行包，不生成 GitHub Release 使用的独立 `-dbg.zip`、release metadata 或版本化手册附件；运行包自身的目录结构、更新器、运行时裁剪、PDB 排除、Python 哈希、稳定路径手册和 ZIP 校验与最新 Tag/手动 CI 保持一致。

在 Windows 应用中测试时，将“更新服务器”设为 `http://127.0.0.1:18080`，“更新频道”设为 `local`，包类型与发布包保持一致（默认 `standard`，也可使用 `Auto`）。先运行一个 sequence 较低的旧包，再执行一次 `publish` 生成更高 sequence 的包，随后在应用中点击“检查更新”。更新准备完成后应用会退出，由 `Diary.Updater.exe` 完成替换并重启；更新确认和失败回滚逻辑与 stable/preview 共用。

上传到另一台局域网服务器时，复制该服务器配置的发布 Token，并指定地址：

```bash
./Tools/local-update.sh publish \
  --server http://192.168.1.40:18080 \
  --token-file /path/to/publish_token.txt
```

Windows 客户端设置为对应服务器地址、频道 `local`、包类型 `Auto` 或 `python313`。旧客户端没有 `local` 选项，第一次需要手工解压并运行一份已经包含本功能的新包；之后即可连续使用 local 在线更新。

## 启动

复制 `config.example.json`，按实际仓库和存储目录修改。公开仓库无需 Token；私有仓库或 GitHub API 限流场景可设置配置指定的环境变量，默认是 `DIARY_GITHUB_TOKEN`。

```bash
cd UpdateServer
python3 -m diary_update_server --config config.json sync
python3 -m diary_update_server --config config.json serve
# 仅提供 local 发布/下载，不启动 GitHub 定时同步
python3 -m diary_update_server --config config.json serve-local
```

同步一次后启动并持续轮询：

```bash
python3 -m diary_update_server --config config.json sync-and-serve
```

服务端不会记录 GitHub Token。同步失败时继续提供上一次完整发布的 latest 快照。

## Docker Compose 部署

默认容器配置位于 `config.docker.json`，仓库为 `micro123/diary-app`，数据写入 Docker Volume `/data`，每 6 小时同步一次。按需修改配置后启动：

```bash
cd UpdateServer
docker compose up -d --build
docker compose ps
```

查看日志：

```bash
docker compose logs -f diary-update-server
```

公开仓库通常不需要 Token。若 GitHub API 限流或仓库改为私有，可在启动前提供环境变量：

```bash
export DIARY_GITHUB_TOKEN="<token>"
docker compose up -d --build
```

建议同时设置立即同步接口使用的 Bearer Token：

```bash
export DIARY_UPDATE_SYNC_TOKEN="<long-random-token>"
docker compose up -d --build
```

本机直传接口必须配置独立发布 Token；Token 为空时接口保持禁用：

```bash
export DIARY_UPDATE_PUBLISH_TOKEN="<long-random-token>"
docker compose up -d --build
```

监听端口默认是 `18080`，可以通过环境变量调整宿主机端口，不改变容器内配置：

```bash
DIARY_UPDATE_PORT=28080 docker compose up -d
```

Compose 使用只读根文件系统、非 root 用户、移除 Linux capabilities，并将更新数据保存在命名卷 `diary-update-data`。删除普通容器不会删除更新缓存；只有显式执行 `docker compose down -v` 才会删除数据卷。

## 下载页面

浏览器访问服务器根地址会跳转到下载页面：

```text
http://服务器地址:18080/downloads
```

页面只列出服务器当前保留的 latest，展示频道、平台、包类型、版本、大小和 SHA-256，并提供完整包下载按钮。完整包响应会带有包含版本、RID 和 flavor 的下载文件名。页面不会显示或调用立即同步接口。

## 立即同步接口

运维脚本可以通过隐藏的 REST 接口立即触发一次 GitHub 同步：

```bash
curl -X POST \
  -H "Authorization: Bearer ${DIARY_UPDATE_SYNC_TOKEN}" \
  http://服务器地址:18080/api/v1/internal/sync
```

成功接收返回 `202 Accepted`；已有同步正在执行时返回 `409 Conflict`。手动同步不会修改自动调度的下次执行时间，例如自动同步原定 18:00 执行，17:00 手动触发后仍会在 18:00 按计划检查。

当 `DIARY_UPDATE_SYNC_TOKEN` 为空时，接口不校验 Token，仅适用于已经通过防火墙或反向代理严格限制访问的可信局域网。隐藏路径本身不构成安全措施；对其他部署应设置长随机 Token，并在 Nginx 上进一步限制来源。

## 客户端接口

- `GET /api/v1/updates/latest?channel=preview&rid=win-x64&flavor=standard`
- `GET /api/v1/updates/content/{sha256}`
- `GET /api/v1/updates/packages/{channel}/{sequence}/{rid}/{flavor}`
- `GET /downloads`
- `POST /api/v1/internal/sync`（不会出现在下载页面）
- `POST /api/v1/internal/publish/local`（运维工具使用，原始 ZIP 请求体）
- `GET /health/live`
- `GET /health/ready`
- `GET /health/status`

`publish/local` 要求 `DIARY_UPDATE_PUBLISH_TOKEN` Bearer Token，限制最大上传体积，并在切换 latest 前完成完整包哈希、ZIP 路径、文件数量、压缩比、运行时布局和 flavor 校验。第一版适用于受控局域网。若经 Nginx 暴露到其他网络，应增加 HTTPS、访问控制、请求体限制和请求限流；当前 manifest 没有数字签名，不能把单纯反向代理视为公网安全方案。
