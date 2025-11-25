using Newtonsoft.Json;

namespace Diary.RedMine.Response;

public class CommonInfo
{
    [JsonProperty("id")]
    public int Id { get; set; }
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;
}