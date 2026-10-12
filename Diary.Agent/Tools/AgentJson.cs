using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Diary.Agent.Tools;

public static class AgentJson
{
    private static readonly JavaScriptEncoder UnicodeEncoder = JavaScriptEncoder.Create(UnicodeRanges.All);
    private static readonly JsonSerializerOptions GeneralCompact = CreateOptions(JsonSerializerDefaults.General, false);
    private static readonly JsonSerializerOptions GeneralIndented = CreateOptions(JsonSerializerDefaults.General, true);
    private static readonly JsonSerializerOptions WebCompact = CreateOptions(JsonSerializerDefaults.Web, false);
    private static readonly JsonSerializerOptions WebIndented = CreateOptions(JsonSerializerDefaults.Web, true);

    public static string Serialize<T>(
        T value,
        JsonSerializerDefaults defaults = JsonSerializerDefaults.General,
        bool writeIndented = false) =>
        JsonSerializer.Serialize(value, GetOptions(defaults, writeIndented));

    public static string Format(JsonElement value, bool writeIndented = true) =>
        JsonSerializer.Serialize(value, GetOptions(JsonSerializerDefaults.General, writeIndented));

    public static string Normalize(string content, bool writeIndented = false)
    {
        if (string.IsNullOrEmpty(content))
            return content;
        try
        {
            using var document = JsonDocument.Parse(content);
            return Format(document.RootElement, writeIndented);
        }
        catch (JsonException)
        {
            return content;
        }
    }

    private static JsonSerializerOptions GetOptions(JsonSerializerDefaults defaults, bool writeIndented) =>
        (defaults, writeIndented) switch
        {
            (JsonSerializerDefaults.General, false) => GeneralCompact,
            (JsonSerializerDefaults.General, true) => GeneralIndented,
            (JsonSerializerDefaults.Web, false) => WebCompact,
            (JsonSerializerDefaults.Web, true) => WebIndented,
            _ => throw new ArgumentOutOfRangeException(nameof(defaults)),
        };

    private static JsonSerializerOptions CreateOptions(JsonSerializerDefaults defaults, bool writeIndented) =>
        new(defaults)
        {
            Encoder = UnicodeEncoder,
            WriteIndented = writeIndented,
        };
}
