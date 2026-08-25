using System.Globalization;
using System.Text.Json;
using Diary.Utils;

namespace Diary.App.Services;

[DiAutoRegister(singleton: true)]
public sealed class McpSetupService(AiContextSnapshotService snapshotService)
{
    public string ExecutablePath => Path.Combine(
        AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Diary.Mcp.exe" : "Diary.Mcp");

    public string SnapshotPath => snapshotService.DefaultMcpSnapshotPath;

    public string GuidePath => Path.Combine(AppContext.BaseDirectory, "Docs", "AiScriptContextGuide.md");

    public bool SnapshotExists => File.Exists(SnapshotPath);

    public string SnapshotStatus
    {
        get
        {
            if (!SnapshotExists)
                return "未生成 · 请先确认披露范围";
            try
            {
                var updatedAt = File.GetLastWriteTime(SnapshotPath)
                    .ToString("yyyy-MM-dd\u00a0HH:mm:ss", CultureInfo.InvariantCulture);
                return $"已生成 · {updatedAt}";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return $"已生成 · 更新时间不可用（{exception.Message}）";
            }
        }
    }

    public string CreateGenericConfiguration() =>
        McpSetupContentBuilder.CreateGenericConfiguration(ExecutablePath, SnapshotPath);

    public string CreateAiInstructions() =>
        McpSetupContentBuilder.CreateAiInstructions(ExecutablePath, SnapshotPath);
}

public static class McpSetupContentBuilder
{
    private static readonly string[] ToolNames =
    [
        "diary_list_tags",
        "diary_list_extra_fields",
        "diary_list_templates",
        "diary_list_tracker_instances",
        "diary_query_work_items",
        "diary_summarize_work_items",
        "diary_validate_script",
    ];

    public static string CreateGenericConfiguration(string executablePath, string snapshotPath)
    {
        ValidatePath(executablePath, nameof(executablePath));
        ValidatePath(snapshotPath, nameof(snapshotPath));
        var configuration = new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["diary"] = new
                {
                    command = executablePath,
                    args = new[] { "--snapshot", snapshotPath },
                },
            },
        };
        return JsonSerializer.Serialize(configuration, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string CreateAiInstructions(string executablePath, string snapshotPath)
    {
        var configuration = CreateGenericConfiguration(executablePath, snapshotPath);
        return $"""
            # 配置 DiaryApp MCP

            请在当前 MCP 客户端中注册一个名为 `diary` 的 stdio MCP Server。

            - 传输方式：`stdio`
            - 启动程序：`{executablePath}`
            - 快照参数：`--snapshot`
            - 只读快照：`{snapshotPath}`

            通用配置：

            ```json
            {configuration}
            ```

            配置要求：

            1. 直接启动上述程序，不要使用 shell 包装命令。
            2. 不要注入数据库密码、连接字符串、Tracker Token 或云服务密钥。
            3. 这是 stdio MCP，由 MCP 客户端按需启动，不是 HTTP 服务或常驻端口。
            4. 配置完成后，列出并确认以下只读工具可用：{string.Join("、", ToolNames.Select(name => $"`{name}`"))}。
            5. MCP 只读取指定快照，不会查询数据库；DiaryApp 数据变化后需要由用户重新刷新快照。
            6. 如果客户端使用不同配置格式，请保持 command、args 和 stdio 语义不变再转换。
            7. `diary_validate_script` 只接受请求中的 C#、Lua 或 Python 源码并返回编译/解析诊断；不要传本地文件路径，它不会执行脚本。
            """;
    }

    private static void ValidatePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("MCP 配置必须使用绝对路径。", parameterName);
    }
}
