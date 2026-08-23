# AI 脚本上下文与本地 MCP 使用指南

DiaryApp 可以把你明确选择的本地结构信息导出给外部 AI，帮助 AI 根据真实标签、附加字段和模板编写脚本。它不会把数据库连接或写入能力交给 AI。

## 生成上下文包

1. 在“程序设置”中开启“显示开发者功能”。
2. 打开“脚本管理”，切换到“AI 上下文”。
3. 选择要披露的标签、附加字段定义、模板、Tracker 摘要、保存查询和只读 Host 能力。
4. 默认不要勾选事项内容。确有需要时，显式勾选事项，并限制开始日期、结束日期和最多条数。
5. 点击“生成预览”，检查页面中的 Markdown。
6. 点击“导出 Markdown”直接提供给对话式 AI，或点击“导出 JSON”交给自动化工具。

标签 metadata、数据库位置、连接字符串、Tracker URL/Token/API Key、加密配置和写 API 永远不会进入上下文包。事项标题、备注和附加字段值会标记为不可信数据，仍应在分享前人工检查。

## 启用本地 stdio MCP

先在“AI 上下文”页点击“刷新 MCP 快照”。页面会显示完整的 MCP 启动命令和快照路径。MCP 每次启动只读取这份快照；本地数据发生变化后，需要回到页面主动刷新。

支持 MCP 的客户端通常接受类似配置：

```json
{
  "mcpServers": {
    "diary": {
      "command": "/absolute/path/to/Diary.Mcp",
      "args": [
        "--snapshot",
        "/absolute/path/to/mcp-snapshot.json"
      ]
    }
  }
}
```

请使用页面实际显示的绝对路径。源码开发环境也可以运行：

```bash
dotnet run --project Diary.Mcp -- --snapshot "/absolute/path/to/mcp-snapshot.json"
```

不要为 MCP 进程额外注入数据库密码、Tracker Token 或云服务密钥等环境变量。

## 可用工具

- `diary_list_tags`：标签目录，不含 metadata。
- `diary_list_extra_fields`：附加字段定义，不含事项字段值。
- `diary_list_templates`：模板及默认标签。
- `diary_list_tracker_instances`：Tracker 实例安全摘要。
- `diary_query_work_items`：在快照内按日期、标签、文本和优先级筛选。
- `diary_summarize_work_items`：汇总快照内事项数量、工时和标签分组。

后两个工具只有在生成快照时显式包含事项才有数据。它们不会回查数据库，也不支持 SQL、路径、脚本或写操作。

## 更新与撤销

- 更新：重新选择范围并点击“刷新 MCP 快照”。
- 缩小范围：取消敏感节或事项内容后重新刷新；旧的手工导出文件不会自动删除。
- 撤销 MCP：关闭使用它的 Agent，并删除页面显示的 `mcp-snapshot.json`。
- 排错：快照不存在、超过 2 MiB、schema 不受支持或 JSON 损坏时，MCP 会在 stderr 给出错误并退出，不会尝试连接数据库。

实现和安全边界详见 [AI 脚本上下文与只读 MCP 设计](AiScriptContextDesign.md)。
