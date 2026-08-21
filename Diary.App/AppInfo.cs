namespace Diary.App;

public static class AppInfo
{
    public const string AppName = "Diary Tools NG";

    public static readonly string AppVersionString = VersionInfo.AppVersionString();

    public static readonly string AppVersionDetails = VersionInfo.AppVersionDetail();

    public static readonly long AppSequence = VersionInfo.AppSequence();

    public static readonly string AppBuildChannel = VersionInfo.AppBuildChannel();
}
