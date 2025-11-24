using Diary.Core.Data.AppConfig;

namespace Diary.RedMine.Response;

public class UserInfo
{
    public static string Query()
    {
        return $"users/current.json";
    }
}