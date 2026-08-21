using System.Runtime.InteropServices;

namespace Diary.Update;

public static class UpdateProtocol
{
    public const int PlanSchemaVersion = 1;
    public const int UpdaterProtocolVersion = 1;

    public static string CurrentRid => (RuntimeInformation.IsOSPlatform(OSPlatform.Windows), RuntimeInformation.ProcessArchitecture) switch
    {
        (true, Architecture.X64) => "win-x64",
        (false, Architecture.X64) when RuntimeInformation.IsOSPlatform(OSPlatform.Linux) => "linux-x64",
        _ => $"{RuntimeInformation.OSDescription}-{RuntimeInformation.ProcessArchitecture}",
    };
}
