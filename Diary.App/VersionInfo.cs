namespace Diary.App;

internal static partial class VersionInfo
{
    static partial void GetVersionStringImpl(ref string versionString);
    static partial void GetVersionDetailImpl(ref string versionString);
    static partial void GetSequenceImpl(ref long sequence);
    static partial void GetBuildChannelImpl(ref string buildChannel);

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

    public static long AppSequence()
    {
        long result = 0;
        GetSequenceImpl(ref result);
        return result;
    }

    public static string AppBuildChannel()
    {
        string result = "release";
        GetBuildChannelImpl(ref result);
        return result;
    }
}
