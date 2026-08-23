using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Diary.Utils;

namespace Diary.App.Diagnostics;

internal sealed record CrashReportRequest(
    int ProcessId,
    string ProcessName,
    string ApplicationVersion,
    DateTimeOffset OccurredAtUtc,
    string ExceptionType,
    string ExceptionMessage,
    string DumpDirectory,
    string DumpPath,
    string ResultPath,
    bool ShowDialog,
    string LogDirectory,
    string LogArchivePath);

internal sealed record CrashReportResult(
    CrashReportRequest Request,
    bool DumpSucceeded,
    long? DumpSizeBytes,
    string? ErrorMessage,
    bool LogArchiveSucceeded,
    long? LogArchiveSizeBytes,
    string? LogArchiveErrorMessage);

internal static class CrashReportStore
{
    private const int MaxExceptionMessageLength = 500;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string GetDumpDirectory() =>
        Path.Combine(FsTools.GetApplicationDataDirectory(), "CrashDumps");

    public static (CrashReportRequest Request, string RequestPath) CreateRequest(
        Exception exception,
        string? dumpDirectory = null,
        int? processId = null,
        string? processName = null,
        DateTimeOffset? occurredAtUtc = null,
        bool showDialog = true,
        string? logDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var directory = Path.GetFullPath(dumpDirectory ?? GetDumpDirectory());
        Directory.CreateDirectory(directory);
        var pid = processId ?? Environment.ProcessId;
        var name = SanitizeFileName(processName ?? Process.GetCurrentProcess().ProcessName);
        var occurredAt = occurredAtUtc ?? DateTimeOffset.UtcNow;
        var baseName = $"{name}-{occurredAt:yyyyMMdd-HHmmssfff}-{pid}";
        var dumpPath = Path.Combine(directory, baseName + ".dmp");
        var resultPath = Path.Combine(directory, baseName + ".json");
        var requestPath = Path.Combine(directory, baseName + ".request.json");
        var sourceLogDirectory = Path.GetFullPath(logDirectory ?? FsTools.GetApplicationDataDirectory());
        var logArchivePath = Path.Combine(directory, baseName + ".logs.zip");
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
        var message = exception.Message;
        if (message.Length > MaxExceptionMessageLength)
            message = message[..MaxExceptionMessageLength] + "…";
        var request = new CrashReportRequest(
            pid,
            name,
            version,
            occurredAt,
            exception.GetType().FullName ?? exception.GetType().Name,
            message,
            directory,
            dumpPath,
            resultPath,
            showDialog,
            sourceLogDirectory,
            logArchivePath);
        WriteJson(requestPath, request);
        return (request, requestPath);
    }

    public static CrashReportRequest ReadRequest(string path) =>
        ReadJson<CrashReportRequest>(path);

    public static void WriteResult(CrashReportResult result) =>
        WriteJson(result.Request.ResultPath, result);

    public static CrashReportResult ReadResult(string path) =>
        ReadJson<CrashReportResult>(path);

    public static void DeleteRequest(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static void Prune(string directory, int maxDumpCount = 5)
    {
        if (maxDumpCount < 1)
            throw new ArgumentOutOfRangeException(nameof(maxDumpCount));
        if (!Directory.Exists(directory))
            return;
        var obsoleteDumps = new DirectoryInfo(directory)
            .EnumerateFiles("*.dmp", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Skip(maxDumpCount)
            .ToArray();
        foreach (var dump in obsoleteDumps)
        {
            DeleteBestEffort(dump.FullName);
            DeleteBestEffort(Path.ChangeExtension(dump.FullName, ".json"));
            DeleteBestEffort(Path.ChangeExtension(dump.FullName, ".request.json"));
            DeleteBestEffort(Path.ChangeExtension(dump.FullName, ".logs.zip"));
        }
    }

    private static T ReadJson<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"崩溃报告文件内容无效：{path}");

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            DeleteBestEffort(temporaryPath);
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = new string(value
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "DiaryApp" : sanitized;
    }

    private static void DeleteBestEffort(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
