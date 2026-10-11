namespace Diary.ScriptHost;

public enum AgentReportPeriod
{
    Day,
    Week,
    Month,
    Quarter,
    Year,
    Custom,
}

public sealed record AgentReportExportCommand(
    AgentReportPeriod Period,
    string Date,
    string FormatId,
    string FileName,
    string IdempotencyKey,
    string? PreviewVersion = null,
    string? StartDate = null,
    string? EndDate = null,
    string? SavedQueryId = null,
    string? TemplateId = null,
    bool IncludeSummary = false,
    bool IncludeTagStatistics = false,
    bool IncludeDailyHours = false);

public sealed record AgentReportExportPreview(
    bool Succeeded,
    AgentReportExportCommand? Command,
    string? StartDate,
    string? EndDate,
    int ItemCount,
    double TotalHours,
    string? PreviewVersion,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record AgentReportExportResult(
    bool Succeeded,
    string? FileName,
    int? ItemCount,
    bool Duplicate,
    string? PreviewVersion,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    string? ArtifactId = null);

public sealed record AgentReportArtifact(
    string ArtifactId,
    string FileName,
    string FormatId,
    DateTimeOffset CreatedAt,
    long Size,
    bool Exists);

public interface IAgentReportExportApi
{
    ValueTask<AgentReportExportPreview> PreviewAsync(
        AgentReportExportCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<AgentReportExportResult> ExportAsync(
        AgentReportExportCommand command,
        CancellationToken cancellationToken = default);

    IReadOnlyList<AgentReportArtifact> ListArtifacts(int limit = 20) => [];

    AgentReportExportResult LocateArtifact(string artifactId) =>
        new(false, null, null, false, null, "artifact_not_supported", "当前宿主不支持报告定位。");
}
