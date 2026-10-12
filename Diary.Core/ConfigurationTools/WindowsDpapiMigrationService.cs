using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Diary.Core.ConfigurationTools;

public sealed class WindowsDpapiMigrationService
{
    public IReadOnlyList<WindowsDpapiSidCandidate> DiscoverPreviousSidCandidates()
    {
        if (!OperatingSystem.IsWindows())
            return [];

        var currentSid = GetCurrentSid();
        var protectRoot = GetProtectRoot();
        if (!Directory.Exists(protectRoot))
            return [];

        return Directory.EnumerateDirectories(protectRoot)
            .Select(path => CreateCandidate(path, currentSid))
            .Where(candidate => candidate is not null)
            .OrderByDescending(candidate => candidate!.HasPreferred)
            .ThenByDescending(candidate => candidate!.LastWriteTime)
            .Cast<WindowsDpapiSidCandidate>()
            .ToArray();
    }

    public WindowsDpapiMigrationResult Migrate(
        ConfigurationDocumentStore store,
        IEnumerable<ConfigurationFileDescriptor> descriptors,
        string oldSid,
        string oldPassword)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldSid);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldPassword);
        if (oldSid.Contains('\0') || oldPassword.Contains('\0'))
            throw new ArgumentException("SID 和密码不能包含空字符。");
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI 身份迁移仅支持 Windows。");
        if (store.DiagnoseMasterKey().Status != ConfigurationMasterKeyStatus.Unreadable)
            throw new InvalidOperationException("仅当 Diary 配置主密钥无法由当前身份解密时才允许迁移。");

        var normalizedOldSid = oldSid.Trim();
        nint sidPointer = 0;
        try
        {
            if (!ConvertStringSidToSid(normalizedOldSid, out sidPointer))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "旧 SID 格式无效。");

            var currentSid = GetCurrentSid();
            if (string.Equals(normalizedOldSid, currentSid, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("旧 SID 不能与当前用户 SID 相同。", nameof(oldSid));
            var oldSidDirectory = Path.Combine(GetProtectRoot(), normalizedOldSid);
            if (!Directory.Exists(oldSidDirectory))
                throw new DirectoryNotFoundException($"未找到旧 SID 的 DPAPI 主密钥目录：{oldSidDirectory}");

            var snapshot = store.CreateSnapshot(descriptors);
            var dpapiBackup = Path.Combine(snapshot, "WindowsDpapi");
            CopyDirectory(oldSidDirectory, Path.Combine(dpapiBackup, normalizedOldSid));
            var currentSidDirectory = Path.Combine(GetProtectRoot(), currentSid);
            if (Directory.Exists(currentSidDirectory))
                CopyDirectory(currentSidDirectory, Path.Combine(dpapiBackup, currentSid));

            if (!CryptUpdateProtectedState(
                    sidPointer,
                    oldPassword,
                    0,
                    out var successCount,
                    out var failureCount))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows DPAPI 主密钥迁移失败。");
            }

            var diagnostic = store.DiagnoseMasterKey();
            return new WindowsDpapiMigrationResult(
                normalizedOldSid,
                currentSid,
                successCount,
                failureCount,
                diagnostic.Status == ConfigurationMasterKeyStatus.Readable,
                diagnostic.Message,
                snapshot);
        }
        finally
        {
            if (sidPointer != 0)
                LocalFree(sidPointer);
        }
    }

    private static string GetProtectRoot()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "Protect");

    private static WindowsDpapiSidCandidate? CreateCandidate(string path, string currentSid)
    {
        var sid = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(sid)
            || !sid.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sid, currentSid, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var files = Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly).ToArray();
        return new WindowsDpapiSidCandidate(
            sid,
            files.Length,
            files.Any(file => string.Equals(Path.GetFileName(file), "Preferred", StringComparison.OrdinalIgnoreCase)),
            Directory.GetLastWriteTime(path));
    }

    [SupportedOSPlatform("windows")]
    private static string GetCurrentSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value
               ?? throw new InvalidOperationException("无法读取当前 Windows 用户 SID。");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    [DllImport(
        "advapi32.dll",
        EntryPoint = "ConvertStringSidToSidW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSidToSid(string stringSid, out nint sid);

    [DllImport(
        "crypt32.dll",
        EntryPoint = "CryptUpdateProtectedState",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUpdateProtectedState(
        nint oldSid,
        [MarshalAs(UnmanagedType.LPWStr)] string oldPassword,
        uint flags,
        out uint successCount,
        out uint failureCount);

    [DllImport("kernel32.dll", EntryPoint = "LocalFree", ExactSpelling = true)]
    private static extern nint LocalFree(nint memory);
}

public sealed record WindowsDpapiMigrationResult(
    string OldSid,
    string CurrentSid,
    uint SuccessCount,
    uint FailureCount,
    bool DiaryMasterKeyReadable,
    string VerificationMessage,
    string SnapshotDirectory);

public sealed record WindowsDpapiSidCandidate(
    string Sid,
    int FileCount,
    bool HasPreferred,
    DateTime LastWriteTime)
{
    public string DisplayName
        => $"{Sid}　文件 {FileCount}　Preferred：{(HasPreferred ? "有" : "无")}　更新：{LastWriteTime:yyyy-MM-dd HH:mm}";

    public override string ToString() => DisplayName;
}
