using System.Text.Json.Serialization;

namespace Diary.App.Models;

public class RespondTag
{
    [JsonIgnore]
    public const string AnonymousName = "**未分类**";

    [JsonPropertyName("name")]
    public string TagName { get; set; } = AnonymousName;
    [JsonPropertyName("total")]
    public double TagTime { get; set; }

    [JsonIgnore]
    public double Percent { get; set; }

    [JsonPropertyName("children")]
    public List<RespondTag> SubTags { get; set; } = new();

    [JsonIgnore] public bool IsValid => TagTime > 0;
    [JsonIgnore] public bool IsAnno => string.Compare(TagName, AnonymousName, StringComparison.Ordinal) == 0;

    public static RespondTag Null { get; } = new() { TagName = "没有数据！", TagTime = 0 };
}

public class RespondGroup
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = RespondTag.AnonymousName;

    [JsonPropertyName("hours")]
    public double TotalTime { get; set; }

    [JsonPropertyName("record_count")]
    public int RecordCount { get; set; }

    [JsonIgnore]
    public double Percent { get; set; }
}

public class RespondDetail
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    [JsonPropertyName("comment")]
    public string Comment { get; set; } = string.Empty;

    [JsonPropertyName("hours")]
    public double Time { get; set; }

    [JsonPropertyName("priority")]
    public string Priority { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    public string[] Tags { get; set; } = Array.Empty<string>();

    [JsonIgnore]
    public string TagsText => string.Join("、", Tags);
}

public class RespondData
{
    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = string.Empty;
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;
    [JsonPropertyName("date_start")]
    public string DateStart { get; set; } = string.Empty;
    [JsonPropertyName("date_end")]
    public string DateEnd { get; set; } = string.Empty;
    [JsonPropertyName("hours")]
    public double TotalTime { get; set; }
    [JsonPropertyName("record_count")]
    public int RecordCount { get; set; }

    [JsonPropertyName("group_by")]
    public string GroupBy { get; set; } = "tag";

    [JsonPropertyName("tags")]
    public List<RespondTag> Tags { get; set; } = new();

    [JsonPropertyName("groups")]
    public List<RespondGroup> Groups { get; set; } = new();

    [JsonPropertyName("details")]
    public List<RespondDetail> Details { get; set; } = new();

    [JsonPropertyName("details_truncated")]
    public bool DetailsTruncated { get; set; }

    [JsonIgnore] public string Key => $"{Username}@{Hostname}";
}
