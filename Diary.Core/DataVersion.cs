namespace Diary.Core;

public static class DataVersion
{
    public const uint Major = 0;
    public const uint Minor = 0;
    public const uint Patch = 0;

    public static string VersionString => $"{Major}.{Minor}.{Patch}";
    public static uint VersionCode => Major * 0x10000u + Minor * 0x100u + Patch;
}
