using System.Text.RegularExpressions;

namespace Diary.App;

public sealed record ScriptCompletionItem(string Text, string Description);

public static partial class ScriptCompletionProvider
{
    private static readonly IReadOnlyDictionary<string, string[]> Keywords =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [".cs"] = ["class", "public", "private", "sealed", "return", "async", "await", "new", "null", "true", "false", "var", "if", "else", "foreach", "using"],
            [".lua"] = ["function", "local", "return", "if", "then", "else", "end", "for", "in", "do", "nil", "true", "false"],
            [".py"] = ["def", "return", "if", "else", "elif", "for", "in", "while", "None", "True", "False", "class", "try", "except", "with"],
        };

    private static readonly IReadOnlyDictionary<string, string[]> Members =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["context"] = ["request", "arguments", "target", "source", "diary"],
            ["diary"] = ["workItems"],
            ["workItems"] = ["query"],
        };

    public static IReadOnlyList<ScriptCompletionItem> GetCompletions(
        string sourcePath,
        string text,
        int caretOffset)
    {
        caretOffset = Math.Clamp(caretOffset, 0, text.Length);
        var prefix = ReadIdentifierBackward(text, caretOffset);
        var memberOwner = ReadMemberOwner(text, caretOffset - prefix.Length);
        var values = memberOwner is not null && Members.TryGetValue(memberOwner, out var members)
            ? members.Select(item => new ScriptCompletionItem(item, $"{memberOwner} 成员"))
            : GetGeneralCompletions(sourcePath, text);
        return values
            .Where(item => item.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(item => item.Text, StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.Text, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<ScriptCompletionItem> GetGeneralCompletions(string sourcePath, string text)
    {
        if (Keywords.TryGetValue(Path.GetExtension(sourcePath), out var keywords))
        {
            foreach (var keyword in keywords)
                yield return new ScriptCompletionItem(keyword, "语言关键字");
        }
        foreach (Match match in IdentifierRegex().Matches(text))
        {
            var identifier = match.Value;
            if (identifier.Length > 1)
                yield return new ScriptCompletionItem(identifier, "当前文件符号");
        }
        foreach (var hostName in Members.Keys)
            yield return new ScriptCompletionItem(hostName, "脚本宿主对象");
    }

    private static string ReadIdentifierBackward(string text, int offset)
    {
        var start = offset;
        while (start > 0 && IsIdentifierCharacter(text[start - 1]))
            start--;
        return text[start..offset];
    }

    private static string? ReadMemberOwner(string text, int offset)
    {
        if (offset <= 0 || text[offset - 1] != '.')
            return null;
        return ReadIdentifierBackward(text, offset - 1) is { Length: > 0 } owner ? owner : null;
    }

    private static bool IsIdentifierCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';

    [GeneratedRegex(@"\b[A-Za-z_][A-Za-z0-9_]*\b", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();
}
