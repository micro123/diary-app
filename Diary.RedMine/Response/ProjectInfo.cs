using Newtonsoft.Json;

namespace Diary.RedMine.Response;

public class ProjectInfo
{
    public static string Search() => "search.json";
    public static string All() => "projects.json";
    public static string Fetch(int id) => $"projects/{id}.json";
    
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("title")] public string Title { get; set; } = string.Empty;
    [JsonProperty("description")] public string Description { get; set; } = string.Empty;

    private string? _name;
    [JsonProperty("name")]
    public string Name
    {
        get => _name ?? Title;
        set => _name = value;
    }
    
    public class SearchResult
    {
        [JsonProperty("results")] public List<ProjectInfo> Results { get; set; } = new();

        private List<ProjectInfo>? _projects;

        [JsonProperty("projects")]
        public List<ProjectInfo> Projects
        {
            get => _projects ?? Results;
            set => _projects = value;
        }
        
        [JsonProperty("total_count")] public int Total { get; set; }
    }

    public class FetchResult
    {
        [JsonProperty("project")] public ProjectInfo Project { get; set; } = new();
    }
}