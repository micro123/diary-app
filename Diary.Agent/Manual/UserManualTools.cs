using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Diary.Agent.Tools;

namespace Diary.Agent.Manual;

public sealed record UserManualSection(
    string Id,
    string Title,
    int Level,
    string Content);

public sealed record UserManualSearchMatch(
    string SectionId,
    string Title,
    int Level,
    string Snippet);

public sealed class UserManualIndexService
{
    public const string HtmlFileName = "DiaryApp-User-Manual.html";
    private readonly string? _manualPath;
    private readonly Lazy<IReadOnlyList<UserManualSection>> _sections;

    public UserManualIndexService() : this(ResolveDefaultPath())
    {
    }

    internal UserManualIndexService(string? manualPath)
    {
        _manualPath = manualPath;
        _sections = new Lazy<IReadOnlyList<UserManualSection>>(
            LoadSections,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsAvailable => _manualPath is not null && File.Exists(_manualPath);

    public IReadOnlyList<UserManualSearchMatch> Search(string query, int limit)
    {
        EnsureAvailable();
        var normalizedQuery = NormalizeSearch(query);
        var tokens = Tokenize(normalizedQuery);
        return _sections.Value
            .Select(section => new
            {
                Section = section,
                Score = Score(section, normalizedQuery, tokens),
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Section.Level)
            .ThenBy(item => item.Section.Title, StringComparer.Ordinal)
            .Take(limit)
            .Select(item => new UserManualSearchMatch(
                item.Section.Id,
                item.Section.Title,
                item.Section.Level,
                CreateSnippet(item.Section.Content, normalizedQuery, tokens)))
            .ToArray();
    }

    public UserManualSection? ReadSection(string sectionId)
    {
        EnsureAvailable();
        return _sections.Value.FirstOrDefault(section =>
            string.Equals(section.Id, sectionId, StringComparison.Ordinal));
    }

    private IReadOnlyList<UserManualSection> LoadSections()
    {
        EnsureAvailable();
        var html = File.ReadAllText(_manualPath!);
        var document = new HtmlParser().ParseDocument(html);
        var main = document.QuerySelector("main#quarto-document-content")
                   ?? document.QuerySelector("main")
                   ?? throw new InvalidDataException("用户手册缺少正文区域。");
        var sections = new List<UserManualSection>();
        var generatedId = 0;
        foreach (var section in main.QuerySelectorAll("section[id]"))
        {
            var heading = section.Children.FirstOrDefault(IsHeading);
            if (heading is null)
                continue;
            var title = NormalizeText(heading.TextContent);
            if (string.IsNullOrWhiteSpace(title))
                continue;
            var content = NormalizeText(string.Join(' ', section.ChildNodes
                .Where(node => node is not IElement { TagName: "SECTION" })
                .Where(node => !ReferenceEquals(node, heading))
                .Select(node => node.TextContent)));
            var id = section.Id;
            if (string.IsNullOrWhiteSpace(id))
                id = $"section-{++generatedId}";
            sections.Add(new UserManualSection(id, title, ParseLevel(heading.TagName), content));
        }
        return sections;
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable)
            throw new FileNotFoundException("当前安装目录中未找到 DiaryApp 用户手册。", _manualPath);
    }

    private static bool IsHeading(IElement element) =>
        element.TagName is "H1" or "H2" or "H3" or "H4";

    private static int ParseLevel(string tagName) =>
        int.TryParse(tagName.AsSpan(1), out var level) ? level : 1;

    private static int Score(UserManualSection section, string query, IReadOnlyList<string> tokens)
    {
        var title = section.Title.ToLowerInvariant();
        var content = section.Content.ToLowerInvariant();
        var score = 0;
        if (title.Contains(query, StringComparison.Ordinal))
            score += 100;
        if (content.Contains(query, StringComparison.Ordinal))
            score += 30;
        foreach (var token in tokens)
        {
            if (title.Contains(token, StringComparison.Ordinal))
                score += 20;
            if (content.Contains(token, StringComparison.Ordinal))
                score += 5;
        }
        return score;
    }

    private static string CreateSnippet(
        string content,
        string query,
        IReadOnlyList<string> tokens)
    {
        const int maxLength = 120;
        if (content.Length <= maxLength)
            return content;
        var normalized = content.ToLowerInvariant();
        var index = normalized.IndexOf(query, StringComparison.Ordinal);
        if (index < 0)
        {
            index = tokens
                .Select(token => normalized.IndexOf(token, StringComparison.Ordinal))
                .Where(position => position >= 0)
                .DefaultIfEmpty(0)
                .Min();
        }
        var start = Math.Max(0, index - 40);
        var length = Math.Min(maxLength, content.Length - start);
        return $"{(start > 0 ? "…" : string.Empty)}{content.Substring(start, length)}{(start + length < content.Length ? "…" : string.Empty)}";
    }

    private static IReadOnlyList<string> Tokenize(string query) =>
        Regex.Split(query, @"[\p{P}\p{S}\s]+")
            .Where(token => token.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string NormalizeText(string value) =>
        Regex.Replace(value, @"\s+", " ").Trim();

    private static string NormalizeSearch(string value) =>
        NormalizeText(value).ToLowerInvariant();

    private static string? ResolveDefaultPath()
    {
        foreach (var root in CandidateRoots())
        {
            var packaged = Path.Combine(root, "Docs", "UserManual", HtmlFileName);
            if (File.Exists(packaged))
                return packaged;
            var development = Path.Combine(root, "Docs", "UserManual", "_output", HtmlFileName);
            if (File.Exists(development))
                return development;
        }
        return null;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(start);
            for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
            {
                if (seen.Add(directory.FullName))
                    yield return directory.FullName;
            }
        }
    }
}

public sealed class SearchUserManualTool(UserManualIndexService manual) : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{"query":{"type":"string","minLength":1,"maxLength":100},"limit":{"type":"integer","minimum":1,"maximum":5}},"required":["query"],"additionalProperties":false}
        """).RootElement.Clone();

    public AgentToolDescriptor Descriptor { get; } = new(
        "diary.user-manual.search",
        "diary_search_user_manual",
        "搜索用户手册",
        "按简短关键词搜索当前版本 DiaryApp 用户手册，只返回紧凑章节索引。回答功能用法、设置和故障排查问题时先搜索，再只读取最相关的一个章节。",
        Schema,
        AgentToolOrigin.BuiltIn,
        AgentToolRisk.ReadOnly,
        "diary.ai-agent");

    public ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!arguments.TryGetProperty("query", out var queryElement)
            || queryElement.ValueKind != JsonValueKind.String)
        {
            return ValueTask.FromResult(AgentToolResult.Failure("invalid_arguments", "query 必须是字符串。"));
        }
        var query = queryElement.GetString()?.Trim() ?? string.Empty;
        if (query.Length is 0 or > 100)
            return ValueTask.FromResult(AgentToolResult.Failure("invalid_arguments", "query 长度必须为 1–100 个字符。"));
        var limit = arguments.TryGetProperty("limit", out var limitElement) ? limitElement.GetInt32() : 3;
        if (limit is < 1 or > 5)
            return ValueTask.FromResult(AgentToolResult.Failure("invalid_arguments", "limit 必须为 1–5。"));
        try
        {
            var results = manual.Search(query, limit);
            return ValueTask.FromResult(AgentToolResult.Success(JsonSerializer.Serialize(new
            {
                count = results.Count,
                results = results.Select(result => new
                {
                    sectionId = result.SectionId,
                    title = result.Title,
                    level = result.Level,
                    snippet = result.Snippet,
                }),
                guidance = "选择最相关的一个 sectionId 调用 diary_read_user_manual_section。",
            }), "DiaryApp 用户手册"));
        }
        catch (FileNotFoundException exception)
        {
            return ValueTask.FromResult(AgentToolResult.Failure("manual_unavailable", exception.Message));
        }
        catch (InvalidDataException exception)
        {
            return ValueTask.FromResult(AgentToolResult.Failure("manual_invalid", exception.Message));
        }
    }
}

public sealed class ReadUserManualSectionTool(UserManualIndexService manual) : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{"sectionId":{"type":"string","minLength":1,"maxLength":200},"offset":{"type":"integer","minimum":0},"maxCharacters":{"type":"integer","minimum":300,"maximum":4000}},"required":["sectionId"],"additionalProperties":false}
        """).RootElement.Clone();

    public AgentToolDescriptor Descriptor { get; } = new(
        "diary.user-manual.read-section",
        "diary_read_user_manual_section",
        "读取用户手册章节",
        "读取 diary_search_user_manual 返回的一个指定章节。默认只返回 2000 字符；仅在结果截断且确有必要时使用 nextOffset 继续读取，不要一次读取多个章节。",
        Schema,
        AgentToolOrigin.BuiltIn,
        AgentToolRisk.ReadOnly,
        "diary.ai-agent");

    public ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!arguments.TryGetProperty("sectionId", out var sectionElement)
            || sectionElement.ValueKind != JsonValueKind.String)
        {
            return ValueTask.FromResult(AgentToolResult.Failure("invalid_arguments", "sectionId 必须是字符串。"));
        }
        var sectionId = sectionElement.GetString()?.Trim() ?? string.Empty;
        if (sectionId.Length is 0 or > 200)
            return ValueTask.FromResult(AgentToolResult.Failure("invalid_arguments", "sectionId 长度必须为 1–200 个字符。"));
        var offset = arguments.TryGetProperty("offset", out var offsetElement)
            ? offsetElement.GetInt32()
            : 0;
        if (offset < 0)
            return ValueTask.FromResult(AgentToolResult.Failure("invalid_arguments", "offset 不能小于 0。"));
        var maxCharacters = arguments.TryGetProperty("maxCharacters", out var maxElement)
            ? maxElement.GetInt32()
            : 2000;
        if (maxCharacters is < 300 or > 4000)
            return ValueTask.FromResult(AgentToolResult.Failure("invalid_arguments", "maxCharacters 必须为 300–4000。"));
        try
        {
            var section = manual.ReadSection(sectionId);
            if (section is null)
                return ValueTask.FromResult(AgentToolResult.Failure("manual_section_not_found", "指定的用户手册章节不存在，请先重新搜索。"));
            if (offset >= section.Content.Length && (offset > 0 || section.Content.Length > 0))
                return ValueTask.FromResult(AgentToolResult.Failure("manual_offset_out_of_range", "offset 已超出章节正文长度。"));
            var length = Math.Min(maxCharacters, section.Content.Length - offset);
            var content = length > 0 ? section.Content.Substring(offset, length) : string.Empty;
            var nextOffset = offset + length;
            var truncated = nextOffset < section.Content.Length;
            return ValueTask.FromResult(new AgentToolResult(
                true,
                JsonSerializer.Serialize(new
                {
                    sectionId = section.Id,
                    title = section.Title,
                    level = section.Level,
                    offset,
                    totalCharacters = section.Content.Length,
                    content,
                    isTruncated = truncated,
                    nextOffset = truncated ? nextOffset : (int?)null,
                }),
                Source: "DiaryApp 用户手册",
                IsTruncated: truncated));
        }
        catch (FileNotFoundException exception)
        {
            return ValueTask.FromResult(AgentToolResult.Failure("manual_unavailable", exception.Message));
        }
        catch (InvalidDataException exception)
        {
            return ValueTask.FromResult(AgentToolResult.Failure("manual_invalid", exception.Message));
        }
    }
}
