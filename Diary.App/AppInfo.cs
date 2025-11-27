using Diary.Core;

namespace Diary.App;

public static class AppInfo
{
    public const string AppName = "Diary Tools NG";

    public static readonly string AppVersionString =
        $"{DataVersion.VersionString}.{VersionInfo.CommitCount}-{VersionInfo.GitVersionShort}";

    public static readonly string AppVersionDetails = $"""
                                                       数据版本：{DataVersion.VersionString} (0x{DataVersion.VersionCode:X8})
                                                       编译增量：{VersionInfo.CommitCount}
                                                       Git分支：{VersionInfo.Branch}
                                                       Git提交：{VersionInfo.GitVersionShort}
                                                       提交消息：{VersionInfo.LastCommitMessage}
                                                       提交时间：{VersionInfo.LastCommitDate}
                                                       编译时间：{VersionInfo.BuildTime}
                                                       编译主机：{VersionInfo.HostName}
                                                       """;
}