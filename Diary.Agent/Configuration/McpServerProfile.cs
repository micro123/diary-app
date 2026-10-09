using Diary.Agent.Tools;
using System.Text.RegularExpressions;

namespace Diary.Agent.Configuration;

public enum McpTransportKind
{
    Stdio,
    StreamableHttp,
}

public sealed record McpToolPolicy
{
    public required string ToolName { get; init; }

    public bool Enabled { get; init; }

    public AgentToolRisk Risk { get; init; } = AgentToolRisk.ReadOnly;
}

public static class McpToolPolicyGuard
{
    private static readonly HashSet<string> ProhibitedDestructiveTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "delete",
        "destroy",
        "drop",
        "erase",
        "purge",
        "remove",
        "rmdir",
        "truncate",
        "unlink",
    };

    public static bool IsProhibitedDestructiveToolName(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return false;
        var segmented = Regex.Replace(toolName, "(?<=[a-z0-9])(?=[A-Z])", "_");
        return Regex.Split(segmented, "[^A-Za-z0-9]+")
            .Any(ProhibitedDestructiveTokens.Contains);
    }
}

public sealed record McpServerProfile
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public bool Enabled { get; init; }

    public McpTransportKind Transport { get; init; }

    public string Command { get; init; } = string.Empty;

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    public Uri? Endpoint { get; init; }

    public AiAuthenticationConfiguration Authentication { get; init; } = new()
    {
        Kind = AiAuthenticationKind.None,
    };

    public AiProxyConfiguration Proxy { get; init; } = new() { Mode = AiProxyMode.System };

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    public IReadOnlyList<McpToolPolicy> Tools { get; init; } = [];
}

public static class McpServerProfileValidator
{
    public static IReadOnlyList<string> Validate(McpServerProfile profile)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(profile.Id)
            || profile.Id.Length > 100
            || !profile.Id.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
        {
            errors.Add("MCP Server ID 只能包含 ASCII 字母、数字、点、下划线和短横线。");
        }
        if (string.IsNullOrWhiteSpace(profile.DisplayName) || profile.DisplayName.Length > 100)
            errors.Add("MCP Server 显示名称不能为空且不能超过 100 个字符。");
        if (profile.Timeout < TimeSpan.FromSeconds(5) || profile.Timeout > TimeSpan.FromMinutes(10))
            errors.Add("MCP 超时必须在 5 秒到 10 分钟之间。");
        if (profile.Transport == McpTransportKind.Stdio)
        {
            if (string.IsNullOrWhiteSpace(profile.Command)
                || profile.Command.Contains('\r')
                || profile.Command.Contains('\n'))
            {
                errors.Add("stdio MCP 必须配置无换行的启动命令。");
            }
            if (profile.Arguments.Any(argument => argument.Contains('\0')))
                errors.Add("stdio MCP 参数不得包含 NUL。");
        }
        else if (profile.Endpoint is null
                 || !profile.Endpoint.IsAbsoluteUri
                 || (profile.Endpoint.Scheme != Uri.UriSchemeHttp && profile.Endpoint.Scheme != Uri.UriSchemeHttps)
                 || !string.IsNullOrEmpty(profile.Endpoint.UserInfo))
        {
            errors.Add("Streamable HTTP MCP Endpoint 必须是无 userinfo 的绝对 HTTP/HTTPS 地址。");
        }
        var toolNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in profile.Tools)
        {
            if (string.IsNullOrWhiteSpace(tool.ToolName) || !toolNames.Add(tool.ToolName))
                errors.Add("MCP 本地工具策略名称不能为空或重复。");
            if (tool.Risk == AgentToolRisk.Destructive)
                errors.Add("首版内置 MCP Client 不允许启用破坏性工具。");
            if (tool.Enabled && McpToolPolicyGuard.IsProhibitedDestructiveToolName(tool.ToolName))
                errors.Add($"MCP 工具 {tool.ToolName} 具有删除语义，不允许向 Agent 开放。");
        }
        return errors;
    }
}
