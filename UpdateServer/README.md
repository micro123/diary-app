# DiaryApp Python 更新服务器

该服务仅使用 Python 3.11+ 标准库，从指定 GitHub Releases 同步 DiaryApp 发布包，校验 release metadata、ZIP 安全规则、包大小和 SHA-256，然后原子发布局域网更新快照。

服务启动后立即同步一次，之后按 `pollIntervalSeconds` 定时重新查询 GitHub，默认周期为 6 小时。每个 `channel/rid/flavor` 只保留当前 latest 快照；新版本完整发布后会删除该维度的旧快照，并清理不再被任何 latest 引用的内容 Blob。`stable` 和 `preview` 是独立频道，因此可以各自保留一个最新版本。

## 启动

复制 `config.example.json`，按实际仓库和存储目录修改。公开仓库无需 Token；私有仓库或 GitHub API 限流场景可设置配置指定的环境变量，默认是 `DIARY_GITHUB_TOKEN`。

```bash
cd UpdateServer
python3 -m diary_update_server --config config.json sync
python3 -m diary_update_server --config config.json serve
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
- `GET /health/live`
- `GET /health/ready`
- `GET /health/status`

第一版适用于受控局域网。若经 Nginx 暴露到其他网络，应增加 HTTPS、访问控制和请求限流；当前 manifest 没有数字签名，不能把单纯反向代理视为公网安全方案。
