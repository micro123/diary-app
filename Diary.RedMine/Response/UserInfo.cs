using Diary.Core.Data.AppConfig;
using Newtonsoft.Json;

namespace Diary.RedMine.Response;

public class UserInfo
{
    public static string Query() => "users/current.json";

    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("firstname")] public string? FirstName { get; set; }
    [JsonProperty("lastname")] public string? LastName { get; set; }

    public class Res
    {
        [JsonProperty("user")] public UserInfo? User { get; set; }
    }
}