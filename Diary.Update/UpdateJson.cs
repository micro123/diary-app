using System.Text.Json;
using System.Text.Json.Serialization;

namespace Diary.Update;

public static class UpdateJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();
    public static JsonSerializerOptions CompactOptions { get; } = CreateOptions(writeIndented: false);
    public static UpdateJsonContext Context { get; } = new(Options);
    public static UpdateJsonContext CompactContext { get; } = new(CompactOptions);

    private static JsonSerializerOptions CreateOptions(bool writeIndented = true)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = writeIndented,
        };
        options.Converters.Add(new JsonStringEnumConverter<UpdateFileOperationKind>(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new JsonStringEnumConverter<UpdateTransactionState>(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new JsonStringEnumConverter<UpdateJournalPhase>(JsonNamingPolicy.CamelCase));
        return options;
    }
}
