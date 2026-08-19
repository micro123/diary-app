using System.Text.Json;
using Diary.Script.Runtime;
using Diary.ScriptBase;

namespace Diary.ScriptHost;

public sealed class WorkerExportProxy(
    Func<WorkerHostCallPayload, CancellationToken, ValueTask<WorkerHostResultPayload>> callHost)
    : IExportApi
{
    public async ValueTask<IReadOnlyList<ExportFormatDescriptor>> ListFormatsAsync(CancellationToken cancellationToken = default)
    {
        var result = await callHost(new("exports.formats.list", JsonSerializer.SerializeToElement(new { }, ExportJson.Options)), cancellationToken);
        EnsureSuccess(result, "读取导出格式失败。");
        return result.Result?.Deserialize<ExportFormatDescriptor[]>(ExportJson.Options) ?? [];
    }

    public async ValueTask<IReadOnlyList<ExportTemplateDescriptor>> ListTemplatesAsync(
        string? formatId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await callHost(new(
            "exports.templates.list",
            JsonSerializer.SerializeToElement(new { format_id = formatId }, ExportJson.Options)),
            cancellationToken);
        EnsureSuccess(result, "读取导出模板失败。");
        return result.Result?.Deserialize<ExportTemplateDescriptor[]>(ExportJson.Options) ?? [];
    }

    public async ValueTask<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken = default)
    {
        var result = await callHost(new("exports.export", JsonSerializer.SerializeToElement(request, ExportJson.Options)), cancellationToken);
        EnsureSuccess(result, "导出失败。");
        return result.Result?.Deserialize<ExportResult>(ExportJson.Options)
            ?? new ExportResult(false, request.FormatId, request.Content?.Kind ?? ExportContentKind.Table, null, null, null,
                new("HOSTCALL_UNAVAILABLE", "宿主未返回导出结果。", ScriptErrorCategory.Host));
    }

    private static void EnsureSuccess(WorkerHostResultPayload result, string fallback)
    {
        if (!result.Success)
            throw new InvalidOperationException(result.Error?.Message ?? fallback);
    }
}
