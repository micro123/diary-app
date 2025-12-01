namespace Diary.App;

internal static partial class VersionInfo
{
    static partial void GetVersionStringImpl(ref string versionString);
    static partial void GetVersionDetailImpl(ref string versionString);

    public static string AppVersionString()
    {
        string result = string.Empty;
        GetVersionStringImpl(ref result);
        return result;
    }

    public static string AppVersionDetail()
    {
        string result = string.Empty;
        GetVersionDetailImpl(ref result);
        return result;
    }
}