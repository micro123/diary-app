using Diary.Core.Data.AppConfig;
using Newtonsoft.Json;

namespace Diary.RedMine.Response;

public class UserInfo
{
    public static string Query() => "users/current.json";

    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("login")] public string Login { get; set; } = string.Empty;
    [JsonProperty("firstname")] public string FirstName { get; set; } = string.Empty;
    [JsonProperty("lastname")] public string LastName { get; set; } = string.Empty;

    public class Res
    {
        [JsonProperty("user")] public UserInfo User { get; set; } = new();
    }
}