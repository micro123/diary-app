using Diary.Utils;

namespace Diary.Core;

[VersionSource]
public static partial class DataVersion
{
    private const uint Major = 1;
    private const uint Minor = 0;
    private const uint Patch = 0;
    public const string VersionString = "1.0.0";
    public const uint VersionCode = Major * 0x10000 + Minor * 0x100 + Patch;
}
