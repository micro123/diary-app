namespace Diary.Core;

public static class DataVersion
{
    private const uint Major = 1;
    private const uint Minor = 0;
    private const uint Patch = 0;

    public static string VersionString => $"{Major}.{Minor}.{Patch}";
    public static uint VersionCode => Major * 0x10000u + Minor * 0x100u + Patch;
}
