using Newtonsoft.Json;

namespace Diary.RedMine.Response;

public class TimeInfo
{
    public static string Query() => "time_entries.json";

    [JsonProperty("id")]
    public int Id { get; set; }
    [JsonProperty("project")]
    public CommonInfo Project { get; set; } = new();
    [JsonProperty("issue")]
    public CommonInfo Issue { get; set; } = new();
    [JsonProperty("user")]
    public CommonInfo User { get; set; } = new();
    [JsonProperty("activity")]
    public CommonInfo Activity { get; set; } = new();
    [JsonProperty("hours")]
    public double Hours { get; set; }
    [JsonProperty("comment")]
    public string Comment { get; set; } = string.Empty;
    

    public class QueryResult
    {
        [JsonProperty("time_entries")] public List<TimeInfo> TimeEntries { get; set; } = new();
        [JsonProperty("total_count")] public int Total { get; set; }
    }
}