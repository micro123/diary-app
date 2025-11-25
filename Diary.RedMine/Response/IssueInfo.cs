using Newtonsoft.Json;

namespace Diary.RedMine.Response;

public class IssueInfo
{
    public static string Query() => "issues.json";
    public static string Fetch(int id) => $"issues/{id}.json";

    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("project")] public CommonInfo Project { get; set; } = new();
    [JsonProperty("tracker")] public CommonInfo Tracker { get; set; } = new();
    [JsonProperty("status")] public CommonInfo Status { get; set; } = new();
    [JsonProperty("priority")] public CommonInfo Priority { get; set; } = new();
    [JsonProperty("author")] public CommonInfo Author { get; set; } = new();
    [JsonProperty("assigned_to")] public CommonInfo AssignedTo { get; set; } = new();
    [JsonProperty("subject")] public string Subject { get; set; } = string.Empty;
    [JsonProperty("description")] public string Description { get; set; } = string.Empty;

    public class SearchResult
    {
        [JsonProperty("issues")] public List<IssueInfo> Issues { get; set; } = new();
        [JsonProperty("total_count")] public int Total { get; set; }
    }

    public class FetchResult
    {
        [JsonProperty("issue")] public IssueInfo Issue { get; set; } = new();
    }

    public class PostData
    {
        [JsonProperty("project_id")] public int ProjectId { get; set; }
        [JsonProperty("subject")] public required string Subject { get; set; }

        [JsonProperty("description", NullValueHandling = NullValueHandling.Ignore)]
        public string? Description { get; set; }

        [JsonProperty("assigned_to_id", NullValueHandling = NullValueHandling.Ignore)]
        public string? AssignedToId { get; set; }
    }

    public class PostRes(int projectId, string subject)
    {
        [JsonProperty("issue")] public PostData Data { get; set; } = new() { ProjectId = projectId, Subject = subject };
    }
}