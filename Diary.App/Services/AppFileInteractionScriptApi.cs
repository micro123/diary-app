using System.Diagnostics;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Diary.App.ViewModels.Dialogs;
using Diary.ScriptHost;
using Diary.Script.Runtime;
using Diary.ScriptBase;
using Ursa.Controls;

namespace Diary.App.Services;

public sealed class AppFileInteractionScriptApi(
    App app,
    ScriptExportService exportService,
    ScriptHostCallContext context) : IFileInteractionApi
{
    public async ValueTask<DirectorySelection?> PickDirectoryAsync(
        DirectoryPickerOptions options,
        CancellationToken cancellationToken = default)
    {
        EnsureScope();
        cancellationToken.ThrowIfCancellationRequested();
        var window = (app.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var storageProvider = window?.StorageProvider;
        if (storageProvider is null)
            throw new InvalidOperationException("当前没有可用的 UI 窗口。");

        var folders = await Dispatcher.UIThread.InvokeAsync(() => storageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = options.Title, AllowMultiple = false }));
        cancellationToken.ThrowIfCancellationRequested();
        var folder = folders.FirstOrDefault();
        if (folder is null)
            return null;

        var path = folder.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            throw new InvalidOperationException("所选目录不存在。");
        var selectionId = Guid.NewGuid().ToString("N");
        exportService.RegisterDirectory(selectionId, path, context);
        return new DirectorySelection(selectionId, folder.Name);
    }

    public async ValueTask<OptionDialogResult> SelectOptionAsync(
        OptionDialogRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureScope();
        Validate(request);
        var vm = new ScriptOptionDialogViewModel(request);
        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(vm.Abort));
        var result = await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var value = await OverlayDialog.ShowCustomModal<OptionDialogResult>(vm, options: new OverlayDialogOptions
            {
                Title = request.Title,
                CanDragMove = false,
                CanResize = false,
                CanLightDismiss = request.DismissPolicy == DialogDismissPolicy.AllowCancel,
                IsCloseButtonVisible = request.DismissPolicy == DialogDismissPolicy.AllowCancel,
            });
            return value;
        });
        cancellationToken.ThrowIfCancellationRequested();
        return result ?? new OptionDialogResult(OptionDialogStatus.Cancelled);
    }

    public async ValueTask<OpenExportedFileResult> AskToOpenExportedFileAsync(
        string fileId,
        CancellationToken cancellationToken = default)
    {
        EnsureScope();
        if (!exportService.TryGetFile(fileId, context, out var path, out var fileName))
            return new(OpenExportedFileStatus.Failed,
                new("EXPORTED_FILE_NOT_FOUND", "导出文件不存在或已过期。", ScriptErrorCategory.Validation));

        var result = await SelectOptionAsync(new OptionDialogRequest
        {
            Title = "导出完成",
            Message = $"{fileName}\n\n是否立即打开？",
            DismissPolicy = DialogDismissPolicy.RequireChoice,
            Options =
            [
                new DialogOption("open", "打开"),
                new DialogOption("decline", "不打开"),
            ],
            DefaultOptionId = "open",
        }, cancellationToken);
        if (result.Status != OptionDialogStatus.Selected)
            return new(OpenExportedFileStatus.Failed,
                new("CANCELLED", "打开文件询问已取消。", ScriptErrorCategory.Cancellation));
        if (result.OptionId == "decline")
            return new(OpenExportedFileStatus.UserDeclined);

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return new(OpenExportedFileStatus.Opened);
        }
        catch (Exception exception)
        {
            return new(OpenExportedFileStatus.Failed,
                new("EXPORTED_FILE_OPEN_FAILED", exception.Message, ScriptErrorCategory.Host));
        }
    }


    private void EnsureScope()
    {
        if (!ScriptHostCallScope.AllowsInteractive(context))
            throw new InvalidOperationException("当前脚本执行入口不允许交互式宿主能力。");
    }
    private static void Validate(OptionDialogRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.Options.Count == 0)
            throw new ArgumentException("选项对话框参数无效。");
        if (request.Options.Any(option => string.IsNullOrWhiteSpace(option.Id) || string.IsNullOrWhiteSpace(option.Label))
            || request.Options.Select(option => option.Id).Distinct(StringComparer.Ordinal).Count() != request.Options.Count
            || (request.DefaultOptionId is not null
                && !request.Options.Any(option => string.Equals(option.Id, request.DefaultOptionId, StringComparison.Ordinal))))
            throw new ArgumentException("选项对话框选项无效。");
    }
}

public sealed class ContextualExportScriptApi(
    ScriptExportService service,
    ScriptHostCallContext context) : IExportApi
{
    public ValueTask<IReadOnlyList<ExportFormatDescriptor>> ListFormatsAsync(CancellationToken cancellationToken = default) =>
        service.ListFormatsAsync(cancellationToken);

    public ValueTask<IReadOnlyList<ExportTemplateDescriptor>> ListTemplatesAsync(
        string? formatId = null,
        CancellationToken cancellationToken = default) =>
        service.ListTemplatesAsync(formatId, cancellationToken);

    public ValueTask<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken = default) =>
        service.ExportAsync(request, context, cancellationToken);
}
