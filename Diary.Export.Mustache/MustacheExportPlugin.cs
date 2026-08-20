using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Diary.ScriptHost;

namespace Diary.Export.Mustache;

public sealed class MustacheExportPlugin : IExportPlugin
{
    private readonly MustacheExportHandler _exportHandler = new();
    private readonly MustacheTemplateHandler _templateHandler = new();

    public ExportPluginManifest Manifest { get; } = new("mustache", "1.0.0");

    public IEnumerable<IExportHandler> GetExportHandlers() => [_exportHandler];

    public IEnumerable<IExportTemplateHandler> GetTemplateHandlers() => [_templateHandler];
}

internal sealed class MustacheExportHandler : IExportHandler
{
    public ExportFormatDescriptor Descriptor { get; } = new(
        "mustache",
        "Mustache 文本",
        ".txt",
        [".txt", ".md", ".html", ".csv"],
        [],
        SupportsTemplates: true,
        FormatOptions: []);

    public ValueTask<ExportRenderResult> RenderAsync(
        ExportRequest request,
        ExportExecutionContext context,
        CancellationToken cancellationToken = default) =>
        throw new ExportHandlerException(
            "EXPORT_INVALID_REQUEST",
            "Mustache 格式只能通过模板导出。",
            retryable: false);
}

internal sealed class MustacheTemplateHandler : IExportTemplateHandler
{
    public string PluginId => "mustache";
    public string FormatId => "mustache";
    public IReadOnlyList<string> SupportedTemplateExtensions => [".mustache"];

    public async ValueTask<ExportTemplateValidationResult> ValidateAsync(
        Stream templateStream,
        ExportTemplateValidationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(context.FileExtension, ".mustache", StringComparison.OrdinalIgnoreCase))
            return Invalid("EXPORT_TEMPLATE_EXTENSION_INVALID", "Mustache 模板扩展名必须为 .mustache。");
        try
        {
            using var reader = new StreamReader(
                templateStream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                leaveOpen: true);
            var source = await reader.ReadToEndAsync(cancellationToken);
            var template = MustacheTemplate.Parse(source);
            var bindings = template.InferBindings();
            if (bindings.Count == 0)
                return Invalid(
                    "EXPORT_TEMPLATE_MARKER_MISSING",
                    "Mustache 模板至少需要包含一个变量或区块标记。",
                    ExportTemplateMarkers.CreateTemplateName(context.FileName),
                    Path.GetFileNameWithoutExtension(context.FileName));

            return new ExportTemplateValidationResult(
                true,
                ExportTemplateMarkers.CreateTemplateName(context.FileName),
                Path.GetFileNameWithoutExtension(context.FileName),
                "使用标准 Mustache 语法的纯文本模板。",
                "1.0.0",
                bindings,
                [ExportFeature.UnicodeText],
                []);
        }
        catch (MustacheTemplateException exception)
        {
            return Invalid(
                exception.Code,
                exception.Message,
                ExportTemplateMarkers.CreateTemplateName(context.FileName),
                Path.GetFileNameWithoutExtension(context.FileName));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Invalid(
                "EXPORT_TEMPLATE_STRUCTURE_INVALID",
                $"无法读取 Mustache 模板：{exception.Message}");
        }
    }

    public async ValueTask<ExportRenderResult> RenderAsync(
        ExportRequest request,
        ExportExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Template is null)
            throw new InvalidOperationException("Mustache 模板导出请求缺少 template。");

        await using var stream = await context.OpenTemplateAsync(cancellationToken);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        var source = await reader.ReadToEndAsync(cancellationToken);
        MustacheTemplate template;
        try
        {
            template = MustacheTemplate.Parse(source);
        }
        catch (MustacheTemplateException exception)
        {
            throw new ExportHandlerException(exception.Code, exception.Message, retryable: false, innerException: exception);
        }

        var root = MustacheDataContext.Create(request.Template);
        var rendered = template.Render(root, cancellationToken);
        await File.WriteAllTextAsync(
            context.OutputPath,
            rendered,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
        return new ExportRenderResult(root.ItemCount);
    }

    private static ExportTemplateValidationResult Invalid(
        string code,
        string message,
        string? templateName = null,
        string? displayName = null) =>
        new(
            false,
            templateName,
            displayName,
            "使用标准 Mustache 语法的纯文本模板。",
            "1.0.0",
            [],
            [],
            [new ExportDiagnostic(code, message)]);
}

internal sealed class MustacheTemplate
{
    private static readonly Regex StandaloneLine = new(
        "(?m)^[ \\t]*(?<tag>\\{\\{[!#\\^/][^}\\r\\n]*\\}\\})[ \\t]*(?:\\r?\\n|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private MustacheTemplate(IReadOnlyList<MustacheNode> nodes)
    {
        Nodes = nodes;
    }

    private IReadOnlyList<MustacheNode> Nodes { get; }

    public static MustacheTemplate Parse(string source)
    {
        source = StandaloneLine.Replace(source, match => match.Groups["tag"].Value);
        var root = new List<MustacheNode>();
        var stack = new Stack<(string Name, bool Inverted, List<MustacheNode> Nodes)>();
        var current = root;
        var position = 0;
        while (position < source.Length)
        {
            var start = source.IndexOf("{{", position, StringComparison.Ordinal);
            if (start < 0)
            {
                current.Add(new MustacheTextNode(source[position..]));
                break;
            }
            if (start > position)
                current.Add(new MustacheTextNode(source[position..start]));

            var triple = start + 2 < source.Length && source[start + 2] == '{';
            var closeToken = triple ? "}}}" : "}}";
            var end = source.IndexOf(closeToken, start + (triple ? 3 : 2), StringComparison.Ordinal);
            if (end < 0)
                throw new MustacheTemplateException(
                    "EXPORT_TEMPLATE_STRUCTURE_INVALID",
                    "Mustache 模板包含未闭合的标记。");

            var tokenStart = start + (triple ? 3 : 2);
            var token = source[tokenStart..end].Trim();
            if (token.Length == 0)
                throw new MustacheTemplateException(
                    "EXPORT_TEMPLATE_STRUCTURE_INVALID",
                    "Mustache 模板包含空标记。");
            switch (token[0])
            {
                case '!':
                    break;
                case '#':
                case '^':
                    {
                        var name = NormalizeName(token[1..]);
                        var children = new List<MustacheNode>();
                        current.Add(new MustacheSectionNode(name, token[0] == '^', children));
                        stack.Push((name, token[0] == '^', current));
                        current = children;
                        break;
                    }
                case '/':
                    {
                        var name = NormalizeName(token[1..]);
                        if (stack.Count == 0 || !string.Equals(stack.Peek().Name, name, StringComparison.Ordinal))
                            throw new MustacheTemplateException(
                                "EXPORT_TEMPLATE_STRUCTURE_INVALID",
                                $"Mustache 模板区块没有正确闭合：{name}。");
                        current = stack.Pop().Nodes;
                        break;
                    }
                case '&':
                    current.Add(new MustacheVariableNode(NormalizeName(token[1..]), escape: false));
                    break;
                case '>':
                    throw new MustacheTemplateException(
                        "EXPORT_TEMPLATE_UNSUPPORTED",
                        "Mustache 模板暂不支持局部模板（{{> partial}}）。");
                case '=':
                    throw new MustacheTemplateException(
                        "EXPORT_TEMPLATE_UNSUPPORTED",
                        "Mustache 模板暂不支持自定义分隔符。");
                default:
                    current.Add(new MustacheVariableNode(
                        NormalizeName(token),
                        escape: !triple));
                    break;
            }
            position = end + closeToken.Length;
        }

        if (stack.Count > 0)
            throw new MustacheTemplateException(
                "EXPORT_TEMPLATE_STRUCTURE_INVALID",
                $"Mustache 模板区块没有闭合：{stack.Peek().Name}。");
        return new MustacheTemplate(root);
    }

    public IReadOnlyList<ExportBindingDescriptor> InferBindings()
    {
        var sections = new Dictionary<string, bool>(StringComparer.Ordinal);
        var variables = new HashSet<string>(StringComparer.Ordinal);
        CollectBindings(Nodes, sections, variables, insideSection: false);
        var result = sections
            .Select(item => new ExportBindingDescriptor(
                item.Key,
                ExportBindingKind.Context,
                Required: false,
                Description: "Mustache 区块数据"))
            .Concat(variables
                .Where(key => !sections.ContainsKey(key))
                .Select(key => key == "item_count"
                    ? new ExportBindingDescriptor(
                        key,
                        ExportBindingKind.Scalar,
                        ExportScalarType.Integer,
                        Required: false,
                        HasDefaultValue: true,
                        DefaultValue: 0,
                        Description: "Mustache 自动统计的表格总行数")
                    : new ExportBindingDescriptor(
                        key,
                        ExportBindingKind.Context,
                        Required: false,
                        Description: "Mustache 变量")))
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();
        return result;
    }

    public string Render(MustacheContext context, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        foreach (var node in Nodes)
            node.Render(builder, context, cancellationToken);
        return builder.ToString();
    }

    private static void CollectBindings(
        IEnumerable<MustacheNode> nodes,
        IDictionary<string, bool> sections,
        ISet<string> variables,
        bool insideSection)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case MustacheVariableNode variable:
                    if (variable.Name != ".")
                    {
                        var root = variable.Name.Split('.', 2)[0];
                        if (!insideSection)
                            variables.Add(root);
                    }
                    break;
                case MustacheSectionNode section:
                    if (!insideSection)
                    {
                        var key = section.Name.Split('.', 2)[0];
                        sections[key] = sections.TryGetValue(key, out var required)
                            ? required || !section.Inverted
                            : !section.Inverted;
                    }
                    CollectBindings(section.Children, sections, variables, insideSection: true);
                    break;
            }
        }
    }

    private static string NormalizeName(string value)
    {
        var name = value.Trim();
        if (name.Length == 0)
            throw new MustacheTemplateException(
                "EXPORT_TEMPLATE_STRUCTURE_INVALID",
                "Mustache 标记名称不能为空。");
        return name;
    }
}

internal abstract class MustacheNode
{
    public abstract void Render(StringBuilder builder, MustacheContext context, CancellationToken cancellationToken);
}

internal sealed class MustacheTextNode(string text) : MustacheNode
{
    public override void Render(StringBuilder builder, MustacheContext context, CancellationToken cancellationToken) =>
        builder.Append(text);
}

internal sealed class MustacheVariableNode(string name, bool escape) : MustacheNode
{
    public string Name { get; } = name;

    public override void Render(StringBuilder builder, MustacheContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = context.Resolve(Name);
        var text = MustacheDataContext.Format(value);
        builder.Append(escape ? MustacheDataContext.HtmlEscape(text) : text);
    }
}

internal sealed class MustacheSectionNode(
    string name,
    bool inverted,
    IReadOnlyList<MustacheNode> children) : MustacheNode
{
    public string Name { get; } = name;
    public bool Inverted { get; } = inverted;
    public IReadOnlyList<MustacheNode> Children { get; } = children;

    public override void Render(StringBuilder builder, MustacheContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = context.Resolve(Name);
        var truthy = MustacheDataContext.IsTruthy(value);
        if (Inverted)
        {
            if (truthy)
                return;
            RenderChildren(builder, context, cancellationToken);
            return;
        }

        if (!truthy)
            return;
        if (value is IEnumerable enumerable and not string and not IDictionary)
        {
            foreach (var item in enumerable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RenderChildren(builder, context.Push(item), cancellationToken);
            }
            return;
        }
        RenderChildren(builder, context.Push(value), cancellationToken);
    }

    private void RenderChildren(StringBuilder builder, MustacheContext context, CancellationToken cancellationToken)
    {
        foreach (var child in Children)
            child.Render(builder, context, cancellationToken);
    }
}

internal sealed class MustacheContext
{
    private readonly object? _value;
    private readonly MustacheContext? _parent;

    public MustacheContext(object? value, MustacheContext? parent = null, int? itemCount = null)
    {
        _value = value;
        _parent = parent;
        ItemCount = itemCount ?? parent?.ItemCount;
    }

    public int? ItemCount { get; }

    public object? Resolve(string name)
    {
        if (name == ".")
            return _value;
        var current = this;
        while (current is not null)
        {
            if (TryResolve(current._value, name, out var result))
                return result;
            current = current._parent;
        }
        return null;
    }

    public MustacheContext Push(object? child) => new(child, this);

    private static bool TryResolve(object? source, string name, out object? result)
    {
        result = source;
        foreach (var part in name.Split('.'))
        {
            if (part == ".")
                continue;
            if (result is IReadOnlyDictionary<string, object?> dictionary
                && dictionary.TryGetValue(part, out result))
                continue;
            if (result is IDictionary genericDictionary && genericDictionary.Contains(part))
            {
                result = genericDictionary[part];
                continue;
            }
            result = null;
            return false;
        }
        return true;
    }
}

internal static class MustacheDataContext
{
    public static MustacheContext Create(ExportTemplateSource source)
    {
        var root = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var item in source.Values)
            root[item.Key] = ConvertValue(item.Value);

        foreach (var (key, table) in source.Tables)
        {
            var validation = ExportRequestValidator.ValidateTableContent(
                table,
                new ExportContentCapabilities(ExportContentKind.Table, [ExportFeature.UnicodeText, ExportFeature.TypedValues]));
            if (validation is not null)
                throw new ExportHandlerException(
                    validation.Code,
                    $"Mustache 模板表格绑定“{key}”无效：{validation.Message}",
                    retryable: false,
                    details: new Dictionary<string, object?> { ["binding_key"] = key });

            var rows = new List<object?>();
            foreach (var row in table.Rows)
            {
                var item = new Dictionary<string, object?>(StringComparer.Ordinal);
                var cells = new List<object?>();
                for (var index = 0; index < table.Columns.Count; index++)
                {
                    var column = table.Columns[index];
                    var normalized = ExportRequestValidator.NormalizeValue(
                        row[index],
                        column.Type,
                        column.Name,
                        rows.Count + 1);
                    item[column.Name] = normalized;
                    cells.Add(normalized);
                }
                item["cells"] = cells;
                rows.Add(item);
            }
            root[key] = rows;
            root[$"{key}_count"] = rows.Count;
            root[$"{key}_columns"] = table.Columns
                .Select((column, index) => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = column.Name,
                    ["index"] = index,
                })
                .ToArray();
        }
        var itemCount = source.Tables.Values.Sum(table => table.Rows.Count);
        root["item_count"] = itemCount;
        return new MustacheContext(root, itemCount: itemCount);
    }

    public static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool boolean => boolean,
        string text => text.Length > 0,
        ICollection collection => collection.Count > 0,
        IEnumerable enumerable => enumerable.Cast<object?>().Any(),
        _ => true,
    };

    public static string Format(object? value)
    {
        if (value is null)
            return string.Empty;
        if (value is JsonElement element)
            return Format(ConvertValue(element));
        return value switch
        {
            DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly time => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            TimeSpan duration => duration.ToString("c", CultureInfo.InvariantCulture),
            DateTimeOffset dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            bool boolean => boolean ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty,
        };
    }

    public static string HtmlEscape(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&#39;", StringComparison.Ordinal)
            .Replace("`", "&#x60;", StringComparison.Ordinal)
            .Replace("=", "&#x3D;", StringComparison.Ordinal);

    private static object? ConvertValue(object? value)
    {
        if (value is not JsonElement element)
            return value;
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(item => item.Name, item => ConvertValue(item.Value), StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(item => ConvertValue(item)).ToArray(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when element.TryGetDecimal(out var decimalValue) => decimalValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }
}

internal sealed class MustacheTemplateException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
