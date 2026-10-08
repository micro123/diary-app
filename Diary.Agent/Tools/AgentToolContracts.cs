using System.Text.Json;
using System.Text.RegularExpressions;

namespace Diary.Agent.Tools;

public enum AgentToolOrigin
{
    BuiltIn,
    Module,
    ExternalWeb,
    Mcp,
}

public enum AgentToolRisk
{
    ReadOnly,
    Write,
    Destructive,
}

public sealed record AgentToolDescriptor(
    string Id,
    string ModelName,
    string DisplayName,
    string Description,
    JsonElement InputSchema,
    AgentToolOrigin Origin,
    AgentToolRisk Risk,
    string OwnerId);

public sealed record AgentToolInvocationContext(
    Guid RunId,
    Guid InvocationId,
    IServiceProvider Services);

public sealed record AgentToolResult(
    bool Succeeded,
    string Content,
    string? ErrorCode = null,
    string? Source = null,
    bool IsExternalContent = false,
    bool IsTruncated = false,
    string? EffectSummary = null)
{
    public static AgentToolResult Success(string content, string? source = null) =>
        new(true, content, Source: source);

    public static AgentToolResult Failure(string code, string message) =>
        new(false, message, code);
}

public interface IAgentTool
{
    AgentToolDescriptor Descriptor { get; }

    ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default);
}

public sealed partial class AgentToolRegistry
{
    private readonly Dictionary<string, IAgentTool> _tools = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public IReadOnlyList<string> RegistrationErrors
    {
        get
        {
            lock (_sync)
                return _errors.ToArray();
        }
    }

    private readonly List<string> _errors = [];

    public bool TryRegister(IAgentTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        lock (_sync)
        {
            var descriptor = tool.Descriptor;
            var error = Validate(descriptor);
            if (error is not null)
            {
                _errors.Add($"{descriptor.Id}: {error}");
                return false;
            }
            if (!_tools.TryAdd(descriptor.ModelName, tool))
            {
                _errors.Add($"{descriptor.Id}: 模型工具名重复：{descriptor.ModelName}。");
                return false;
            }
            return true;
        }
    }

    public bool ReplaceOwnerTools(string ownerId, IEnumerable<IAgentTool> tools)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentNullException.ThrowIfNull(tools);
        var replacements = tools.ToArray();
        lock (_sync)
        {
            var replacementNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tool in replacements)
            {
                if (!string.Equals(tool.Descriptor.OwnerId, ownerId, StringComparison.Ordinal))
                {
                    _errors.Add($"{tool.Descriptor.Id}: OwnerId 与替换范围不一致。");
                    return false;
                }
                var error = Validate(tool.Descriptor);
                if (error is not null || !replacementNames.Add(tool.Descriptor.ModelName))
                {
                    _errors.Add($"{tool.Descriptor.Id}: {error ?? "替换集合中的模型工具名重复。"}");
                    return false;
                }
                if (_tools.TryGetValue(tool.Descriptor.ModelName, out var existing)
                    && !string.Equals(existing.Descriptor.OwnerId, ownerId, StringComparison.Ordinal))
                {
                    _errors.Add($"{tool.Descriptor.Id}: 模型工具名与其他 Owner 冲突：{tool.Descriptor.ModelName}。");
                    return false;
                }
            }
            foreach (var existing in _tools
                         .Where(pair => string.Equals(pair.Value.Descriptor.OwnerId, ownerId, StringComparison.Ordinal))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _tools.Remove(existing);
            }
            foreach (var tool in replacements)
                _tools.Add(tool.Descriptor.ModelName, tool);
            return true;
        }
    }

    public AgentToolSnapshot CreateSnapshot(Func<AgentToolDescriptor, bool>? predicate = null)
    {
        predicate ??= static _ => true;
        lock (_sync)
        {
            var tools = _tools.Values
                .Where(tool => predicate(tool.Descriptor))
                .OrderBy(tool => tool.Descriptor.ModelName, StringComparer.Ordinal)
                .ToDictionary(tool => tool.Descriptor.ModelName, StringComparer.Ordinal);
            return new AgentToolSnapshot(tools);
        }
    }

    private static string? Validate(AgentToolDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor.Id) || string.IsNullOrWhiteSpace(descriptor.OwnerId))
            return "ID 和 OwnerId 不能为空。";
        if (!ModelNameRegex().IsMatch(descriptor.ModelName))
            return "模型工具名只能包含 ASCII 字母、数字和下划线。";
        if (descriptor.InputSchema.ValueKind != JsonValueKind.Object)
            return "输入 schema 必须是 JSON 对象。";
        if (!descriptor.InputSchema.TryGetProperty("type", out var type)
            || type.GetString() != "object")
        {
            return "输入 schema 根类型必须是 object。";
        }
        return null;
    }

    [GeneratedRegex("^[A-Za-z0-9_]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelNameRegex();
}

public sealed class AgentToolSnapshot
{
    private readonly IReadOnlyDictionary<string, IAgentTool> _tools;

    internal AgentToolSnapshot(IReadOnlyDictionary<string, IAgentTool> tools)
    {
        _tools = tools;
    }

    public IReadOnlyList<AgentToolDescriptor> Descriptors =>
        _tools.Values.Select(tool => tool.Descriptor).ToArray();

    public bool TryGet(string modelName, out IAgentTool? tool) => _tools.TryGetValue(modelName, out tool);
}

public sealed class AgentToolExecutor(
    int maxInputBytes = 64 * 1024,
    int maxResultBytes = 128 * 1024)
{
    public async ValueTask<AgentToolResult> InvokeAsync(
        AgentToolSnapshot snapshot,
        string modelName,
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        if (!snapshot.TryGet(modelName, out var tool) || tool is null)
            return AgentToolResult.Failure("tool_not_found", "请求的工具未在本次运行快照中启用。");
        if (System.Text.Encoding.UTF8.GetByteCount(arguments.GetRawText()) > maxInputBytes)
            return AgentToolResult.Failure("tool_input_too_large", "工具参数超过大小限制。");
        try
        {
            var result = await tool.InvokeAsync(arguments, context, cancellationToken);
            return Truncate(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return AgentToolResult.Failure("tool_cancelled", "工具调用已取消。");
        }
        catch (Exception)
        {
            return AgentToolResult.Failure("tool_failed", "工具调用失败；详细信息仅记录在本地诊断中。");
        }
    }

    private AgentToolResult Truncate(AgentToolResult result)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(result.Content);
        if (bytes.Length <= maxResultBytes)
            return result;
        var length = maxResultBytes;
        while (length > 0 && (bytes[length] & 0xC0) == 0x80)
            length--;
        var content = System.Text.Encoding.UTF8.GetString(bytes, 0, length) + "\n[结果已截断]";
        return result with { Content = content, IsTruncated = true };
    }
}
