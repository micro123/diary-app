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
    [JsonProperty("spent_on")]
    public string SpentOn { get; set; } = string.Empty;
    

    public class QueryResult
    {
        [JsonProperty("time_entries")] public List<TimeInfo> TimeEntries { get; set; } = new();
        [JsonProperty("total_count")] public int Total { get; set; }
    }


    public class PostData
    {
        [JsonProperty("activity_id")]
        public int ActivityId { get; set; }
        [JsonProperty("issue_id")]
        public int IssueId { get; set; }
        [JsonProperty("spent_on")]
        public required string SpentOn { get; set; }
        [JsonProperty("hours")]
        public double Hours { get; set; }
        [JsonProperty("comments")]
        public required string Comments { get; set; }
    }

    public class PostRes(int issue, int activity, string date, string comment, double hours)
    {
        [JsonProperty("time_entry")]
        public PostData TimeEntry { get; set; } = new()
        {
            IssueId = issue,
            ActivityId = activity,
            SpentOn = date,
            Comments = comment,
            Hours = hours
        };
    }

    public class PostResult
    {
        [JsonProperty("time_entry")]
        public TimeInfo TimeEntry { get; set; } = new();
    }
}