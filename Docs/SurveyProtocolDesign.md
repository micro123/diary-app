# Survey 协议扩展设计

## 目标

Survey 必须继续兼容 DiaryToolpp 的旧调查协议，同时允许新版节点执行带筛选条件的自定义统计查询。

调查者仍然同时承担受访者职责：调查者监听本机服务，并通过 `127.0.0.1` 连接自身接收调查结果。

## v1 旧协议

v1 固定使用 TCP `9721`，请求和响应均为 UTF-8 文本。

请求格式保持不变：

```text
yyyy-MM-dd:yyyy-MM-dd
```

响应保持旧版 `RespondData` JSON 字段：`hostname`、`username`、`date_start`、`date_end`、`hours` 和 `tags`。

因此旧版和新版节点可以继续互通：

- 旧版调查者可以查询新版受访者。
- 新版调查者可以使用 v1 查询旧版受访者。
- 默认日期查询仍广播到所有连接在 `9721` 的节点。

## v2 扩展协议

v2 固定使用 TCP `9722`，仅新版节点连接，不改变 `9721` 的请求解析。

请求为 JSON：

```json
{
  "version": 2,
  "request_id": "唯一请求 ID",
  "kind": "custom_statistics",
  "start_date": "2026-08-01",
  "end_date": "2026-08-31",
  "text": "关键词",
  "tag_names": ["项目A"],
  "tag_filter": "Any",
  "priority": 2,
  "group_by": "date",
  "include_details": true
}
```

受访者在本地将标签名解析为标签 ID，再通过核心 `WorkItemQuery` 查询数据库；不接受远程 SQL。v2 当前支持日期、文本、标签名、标签筛选模式和优先级。

响应使用带请求 ID 的 JSON 包装，统计数据放在 `data` 中：

```json
{
  "version": 2,
  "request_id": "唯一请求 ID",
  "ok": true,
  "data": {
    "hostname": "host",
    "username": "user",
    "hours": 2.5,
    "record_count": 3,
    "group_by": "date",
    "groups": [
      { "name": "2026-08-11", "hours": 2.5, "record_count": 3 }
    ],
    "details": [
      {
        "date": "2026-08-11",
        "comment": "整理调查协议",
        "hours": 1.5,
        "priority": "P1",
        "tags": ["项目A"]
      }
    ],
    "details_truncated": false,
    "tags": []
  },
  "error": null
}
```

### 能力发现

调查者可以在 `9722` 发送 `kind = "capabilities"` 的 v2 请求，受访者返回自身支持的能力：

```json
{
  "kind": "capabilities",
  "hostname": "host",
  "username": "user",
  "kinds": ["capabilities", "custom_statistics"],
  "group_dimensions": ["tag", "date", "priority"],
  "supports_details": true
}
```

能力发现不访问数据库，也不会改变旧版 `9721` 协议。当前新版节点声明支持自定义统计、标签/日期/优先级分组和受限结果明细。结果明细最多返回 500 条，超过上限时通过 `details_truncated` 标记。

## 兼容边界

扩展查询只发送到 `9722`。不能把 v2 JSON 发送到 `9721`，因为旧版受访者会按第一个冒号拆分为日期范围。

当调查者没有填写扩展条件时，界面仍发送 v1 日期请求；填写关键词、标签或优先级后切换到 v2，并提示结果只来自新版节点。

## 生命周期

- 调查者：监听 `9721` 和 `9722`，同时连接 `127.0.0.1` 的两个受访者端。
- 新版受访者：连接调查者的 `9721` 和 `9722`。
- 旧版受访者：只连接 `9721`，因此不会收到 v2 请求。
- 配置界面不暴露新端口，两个端口均为协议常量。
- 调查页可以主动探测新版节点能力，并显示节点支持的查询类型、分组维度和明细能力；扩展查询可以选择分组维度并请求结果明细。
