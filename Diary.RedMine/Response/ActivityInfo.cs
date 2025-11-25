using Newtonsoft.Json;

namespace Diary.RedMine.Response;

public class ActivityInfo
{
    public static string Query() => "enumerations/time_entry_activities.json";

    public class Res
    {
        [JsonProperty("time_entry_activities")]
        public List<ActivityInfo> TimeEntryActivities { get; set; } = new();
    }
    
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("name")] public string Name { get; set; } = string.Empty;
}