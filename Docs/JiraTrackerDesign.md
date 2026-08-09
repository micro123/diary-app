# Jira 工时 Tracker 设计

## 1. 实现范围

Jira 插件只把 Jira 作为项目事项和工时后端，不把 Jira 的 Workflow、Sprint、自定义字段或 Issue 写入模型引入 DiaryApp 核心。当前实现覆盖：

- Jira Cloud v3 风格 REST API 的项目搜索、Issue 查询和 Worklog 追加。
- Jira Cloud 的账号/邮箱 + API Token Basic 认证。
- 自托管 Jira 的 Bearer Token 配置路径。
- `(PluginId, InstanceId)` 多实例配置和启用/禁用。
- SQLite/PostgreSQL 本地项目、Issue 和工作项绑定扩展。
- 工作项编辑器中的 Issue 选择、连接测试、工时追加和远程 ID 保存。

## 2. 追加式工时语义

DiaryApp 的核心工作记录是追加式的。Jira 插件只允许将当前工作项耗时追加到选中的 Issue，不提供删除 Jira Worklog、修改已提交工时或远程 Issue 更新入口。

远程追加成功后，本地保存 Jira Worklog ID，并将编辑器扩展锁定，避免同一个本地工作项重复上传。网络失败、权限拒绝或 Jira 返回错误时，核心工作项和本地绑定保留，用户可以修复配置后重试。

## 3. REST 边界

| 能力 | 当前接口 | 说明 |
| --- | --- | --- |
| 项目查询 | `GET /rest/api/3/project/search` | 只缓存项目键、名称、描述和归档状态 |
| Issue 查询 | `GET /rest/api/3/search/jql` | 只读取 key、summary、project、status |
| Issue 详情 | `GET /rest/api/3/issue/{key}` | 用于后续绑定校验和补全 |
| 追加工时 | `POST /rest/api/3/issue/{key}/worklog` | 以秒提交耗时，日期转换为 Jira started 字段 |

请求失败统一返回 HTTP 状态码和受限错误文本；Token 不写入日志、导出诊断或远程请求正文。

## 4. 当前限制

- 尚未覆盖真实 Jira Cloud、Jira Server/Data Center 的在线契约测试。
- 未实现 Jira 管理页、Issue 创建/更新、Workflow、Sprint、字段映射和全局工时报告。
- 自托管 Jira 的具体 API 版本、Bearer Token 策略和权限差异需要使用目标环境验证。
- 远程 API 不参与本地核心保存事务；本地数据库扩展只保存绑定和远程 Worklog ID。

## 5. 后续验收

1. 使用测试 Jira Cloud 实例验证项目搜索、Issue 查询、权限失败和追加 Worklog。
2. 使用一个自托管 Jira 环境验证 Bearer Token、分页和时间格式。
3. 验证同一 Issue 的多个本地工作项可以分别追加工时，而同一个本地工作项不会重复追加。
4. 验证 Jira 不可用时 DiaryApp 仍能保存和查询核心工作记录。
