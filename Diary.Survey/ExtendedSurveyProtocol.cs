using System.Text.Json;
using System.Text.Json.Serialization;

namespace Diary.Survey;

public static class ExtendedSurveyProtocol
{
    public const int Version = 2;
    public const string CapabilitiesKind = "capabilities";
    public const string CustomStatisticsKind = "custom_statistics";
    public const string GroupByTag = "tag";
    public const string GroupByDate = "date";
    public const string GroupByPriority = "priority";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string SerializeRequest(ExtendedSurveyRequest request)
        => JsonSerializer.Serialize(request, JsonOptions);

    public static bool TryDeserializeRequest(string content, out ExtendedSurveyRequest? request)
    {
        request = null;
        try
        {
            request = JsonSerializer.Deserialize<ExtendedSurveyRequest>(content, JsonOptions);
            return request is { Version: Version }
                && request.Kind is CapabilitiesKind or CustomStatisticsKind
                && !string.IsNullOrWhiteSpace(request.RequestId);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string SerializeSuccess(string requestId, string dataJson)
    {
        using var document = JsonDocument.Parse(dataJson);
        return JsonSerializer.Serialize(new ExtendedSurveyResponse
        {
            Version = Version,
            RequestId = requestId,
            Ok = true,
            Data = document.RootElement.Clone(),
        }, JsonOptions);
    }

    public static string SerializeError(string requestId, string error)
        => JsonSerializer.Serialize(new ExtendedSurveyResponse
        {
            Version = Version,
            RequestId = requestId,
            Ok = false,
            Error = error,
        }, JsonOptions);

    public static string SerializeCapabilitiesRequest(string requestId)
        => SerializeRequest(new ExtendedSurveyRequest
        {
            RequestId = requestId,
            Kind = CapabilitiesKind,
        });

    public static string SerializeCapabilitiesSuccess(
        string requestId,
        string hostname,
        string username)
        => SerializeSuccess(requestId, JsonSerializer.Serialize(new ExtendedSurveyCapabilities
        {
            Hostname = hostname,
            Username = username,
            Kinds = [CapabilitiesKind, CustomStatisticsKind],
            GroupDimensions = [GroupByTag, GroupByDate, GroupByPriority],
            SupportsDetails = true,
        }, JsonOptions));
}

public sealed class ExtendedSurveyRequest
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = ExtendedSurveyProtocol.Version;

    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = ExtendedSurveyProtocol.CustomStatisticsKind;

    [JsonPropertyName("start_date")]
    public string StartDate { get; set; } = string.Empty;

    [JsonPropertyName("end_date")]
    public string EndDate { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("tag_names")]
    public string[] TagNames { get; set; } = Array.Empty<string>();

    [JsonPropertyName("tag_filter")]
    public string TagFilter { get; set; } = "ignore";

    [JsonPropertyName("priority")]
    public int? Priority { get; set; }

    [JsonPropertyName("group_by")]
    public string GroupBy { get; set; } = ExtendedSurveyProtocol.GroupByTag;

    [JsonPropertyName("include_details")]
    public bool IncludeDetails { get; set; }
}

public sealed class ExtendedSurveyCapabilities
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = ExtendedSurveyProtocol.CapabilitiesKind;

    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("kinds")]
    public string[] Kinds { get; set; } = Array.Empty<string>();

    [JsonPropertyName("group_dimensions")]
    public string[] GroupDimensions { get; set; } = Array.Empty<string>();

    [JsonPropertyName("supports_details")]
    public bool SupportsDetails { get; set; }
}

public sealed class ExtendedSurveyResponse
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
