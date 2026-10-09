namespace Diary.ScriptHost;

public enum AgentReportPeriod
{
    Day,
    Week,
}

public sealed record AgentReportExportCommand(
    AgentReportPeriod Period,
    string Date,
    string FormatId,
    string FileName,
    string IdempotencyKey,
    string? PreviewVersion = null);

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
    string? ErrorMessage = null);

public interface IAgentReportExportApi
{
    ValueTask<AgentReportExportPreview> PreviewAsync(
        AgentReportExportCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<AgentReportExportResult> ExportAsync(
        AgentReportExportCommand command,
        CancellationToken cancellationToken = default);
}
