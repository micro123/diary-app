using System.Text.Json;
using Diary.Script.Runtime;
using Diary.ScriptBase;

namespace Diary.ScriptHost;

public sealed class WorkerFileInteractionProxy(
    Func<WorkerHostCallPayload, CancellationToken, ValueTask<WorkerHostResultPayload>> callHost)
    : IFileInteractionApi
{
    public async ValueTask<OptionDialogResult> SelectOptionAsync(
        OptionDialogRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await callHost(new(
            "ui.options.select",
            JsonSerializer.SerializeToElement(request, ExportJson.Options)), cancellationToken);
        EnsureSuccess(result, "显示选项对话框失败。");
        return result.Result?.Deserialize<OptionDialogResult>(ExportJson.Options)
            ?? new OptionDialogResult(OptionDialogStatus.Cancelled);
    }

    public async ValueTask<DirectorySelection?> PickDirectoryAsync(
        DirectoryPickerOptions options,
        CancellationToken cancellationToken = default)
    {
        var result = await callHost(new(
            "ui.directory.pick",
            JsonSerializer.SerializeToElement(options, ExportJson.Options)), cancellationToken);
        EnsureSuccess(result, "选择目录失败。");
        return result.Result is null
            ? null
            : result.Result.Value.Deserialize<DirectorySelection>(ExportJson.Options);
    }

    public async ValueTask<OpenExportedFileResult> AskToOpenExportedFileAsync(
        string fileId,
        CancellationToken cancellationToken = default)
    {
        var result = await callHost(new(
            "ui.exported_file.open",
            JsonSerializer.SerializeToElement(new { file_id = fileId }, ExportJson.Options)), cancellationToken);
        EnsureSuccess(result, "打开导出文件失败。");
        return result.Result?.Deserialize<OpenExportedFileResult>(ExportJson.Options)
            ?? new OpenExportedFileResult(OpenExportedFileStatus.Failed,
                new("HOSTCALL_UNAVAILABLE", "宿主未返回打开结果。", ScriptErrorCategory.Host));
    }

    private static void EnsureSuccess(WorkerHostResultPayload result, string fallback)
    {
        if (!result.Success)
            throw new InvalidOperationException(result.Error?.Message ?? fallback);
    }
}
