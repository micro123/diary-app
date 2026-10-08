using System.Text.Json;
using System.Text.Json.Nodes;

namespace Diary.Agent.Runtime;

public static class AgentDataRedactor
{
    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "note",
        "password",
        "secret",
        "token",
        "apiKey",
        "authorization",
        "credential",
        "credentialReference",
    };

    public static string CreateArgumentsSummary(JsonElement arguments, int maxCharacters = 500)
    {
        var node = JsonNode.Parse(arguments.GetRawText());
        Redact(node);
        var text = node?.ToJsonString() ?? "{}";
        return text.Length <= maxCharacters ? text : text[..maxCharacters] + "…";
    }

    private static void Redact(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(pair => pair.Key).ToArray())
            {
                if (SensitiveNames.Any(name => key.Contains(name, StringComparison.OrdinalIgnoreCase)))
                    obj[key] = "[已隐藏]";
                else
                    Redact(obj[key]);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
                Redact(item);
        }
    }
}
