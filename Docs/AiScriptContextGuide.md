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

正式发布版的 `Diary.Mcp.exe` 或 `Diary.Mcp` 是独立进程入口，但它会复用同一应用目录中的 .NET Runtime、Roslyn 和脚本引擎文件。请保留完整的 DiaryApp 解压目录，不要只复制 MCP 可执行文件；移动安装位置后，应重新从页面复制包含新绝对路径的配置。

不要为 MCP 进程额外注入数据库密码、Tracker Token 或云服务密钥等环境变量。

## 从程序设置复制配置

刷新快照后，也可以重新打开“程序设置”，使用设置列表末尾的“AI 与 MCP”区域。该区域采用与其他程序设置一致的标准设置行，每项操作都有独立标签、按钮和帮助提示：

- “打开 AI 上下文”：保存当前设置、显示脚本管理页并直接切换到披露页面；它不会自动生成快照。
- “复制 AI 说明”：复制一段 Markdown，包含 stdio 语义、绝对路径、通用 JSON、工具列表和安全要求，可直接粘贴给 AI，让它转换为当前客户端需要的格式。
- “复制 MCP JSON”：只复制 `mcpServers.diary.command/args` 配置，适合支持该通用形状的客户端。
- “打开使用文档”：打开本指南。

两种复制内容都只引用可执行文件和快照路径，不读取或嵌入快照正文，也不会自动修改 Claude、Codex、编辑器或其他 Agent 的配置文件。快照不存在时复制按钮保持禁用，应先进入“AI 上下文”确认披露范围并刷新。

## 可用工具

- `diary_list_tags`：标签目录，不含 metadata。
- `diary_list_extra_fields`：附加字段定义，不含事项字段值。
- `diary_list_templates`：模板及默认标签。
- `diary_list_tracker_instances`：Tracker 实例安全摘要。
- `diary_query_work_items`：在快照内按日期、标签、文本和优先级筛选。
- `diary_summarize_work_items`：汇总快照内事项数量、工时和标签分组。
- `diary_validate_script`：只编译或解析请求中提供的 C#、Lua、Python 源码并返回行列诊断，不读取脚本文件，也不执行脚本。

事项查询和汇总只有在生成快照时显式包含事项才有数据；它们不会回查数据库，也不支持 SQL、路径或写操作。若当前快照未包含事项，这两个工具会返回 `available: false` 和 `work_items_not_disclosed`，提示回到“AI 上下文”显式勾选事项并刷新 MCP 快照；该结果表示未授权，不等于查询结果为空。

校验脚本时传入 `language`（`csharp`、`lua` 或 `python`）和完整 `source`。返回值中的 `succeeded` 表示相应编译/解析阶段是否通过，`diagnostics` 包含 code、message、severity、category、line 和 column。C# 会执行 Roslyn 编译与宿主安全策略但不加载程序集；Lua 只编译代码块；Python 只做语法树和安全策略检查。因此成功结果不代表脚本入口元数据完整，也不保证实际运行成功。源码上限为 256 KiB，服务不会读取调用方提供的本地路径、安装额外依赖或写入编译缓存。

## 更新与撤销

- 更新：重新选择范围并点击“刷新 MCP 快照”。
- 缩小范围：取消敏感节或事项内容后重新刷新；旧的手工导出文件不会自动删除。
- 撤销 MCP：关闭使用它的 Agent，并删除页面显示的 `mcp-snapshot.json`。
- 排错：快照不存在、超过 2 MiB、schema 不受支持或 JSON 损坏时，MCP 会在 stderr 给出错误并退出，不会尝试连接数据库。

实现和安全边界详见 [AI 脚本上下文与只读 MCP 设计](AiScriptContextDesign.md)。
