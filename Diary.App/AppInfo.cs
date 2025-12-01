using Diary.Core;

namespace Diary.App;

public static class AppInfo
{
    public const string AppName = "Diary Tools NG";

    public static readonly string AppVersionString = VersionInfo.AppVersionString();

    public static readonly string AppVersionDetails = VersionInfo.AppVersionDetail();
}