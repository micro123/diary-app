using System.Collections.Concurrent;
using Diary.ScriptHost;
using Diary.Script.Runtime;
using Diary.ScriptBase;
using Microsoft.Extensions.Logging;

namespace Diary.App.Services;

public sealed class ScriptExportService : IContextualExportApi
{
    private readonly ILogger<ScriptExportService> _logger;
    private readonly IExportTemplateCatalog _templateCatalog;
    private readonly IReadOnlyDictionary<string, IExportHandler> _exportHandlers;
    private readonly ConcurrentDictionary<string, DirectorySelectionEntry> _directories = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ExportFileEntry> _files = new(StringComparer.Ordinal);
    private readonly TimeSpan _lifetime = TimeSpan.FromMinutes(10);

    public ScriptExportService(
        ILogger<ScriptExportService> logger,
        IExportTemplateCatalog templateCatalog,
        IEnumerable<IExportHandler> exportHandlers)
    {
        _logger = logger;
        _templateCatalog = templateCatalog;
        var handlers = new Dictionary<string, IExportHandler>(StringComparer.Ordinal);
        foreach (var handler in exportHandlers)
        {
            if (!handlers.TryAdd(handler.Descriptor.FormatId, handler))
                throw new InvalidOperationException($"导出格式 ID 冲突：{handler.Descriptor.FormatId}。");
        }
        _exportHandlers = handlers;
    }

    public void RegisterDirectory(string selectionId, string path, ScriptHostCallContext context)
    {
        _directories[selectionId] = new DirectorySelectionEntry(
            selectionId,
            context.ExecutionId,
            context.WorkerId,
            path,
            DateTimeOffset.UtcNow.Add(_lifetime));
    }

    public bool TryGetDirectory(string selectionId, ScriptHostCallContext context, out string path)
    {
        path = string.Empty;
        if (!_directories.TryGetValue(selectionId, out var entry)
            || entry.ExpiresAt <= DateTimeOffset.UtcNow
            || !string.Equals(entry.ExecutionId, context.ExecutionId, StringComparison.Ordinal)
            || !string.Equals(entry.WorkerId, context.WorkerId, StringComparison.Ordinal))
            return false;
        path = entry.Path;
        return true;
    }

    public bool TryGetFile(string fileId, ScriptHostCallContext context, out string path, out string fileName)
    {
        path = string.Empty;
        fileName = string.Empty;
        if (!_files.TryGetValue(fileId, out var entry)
            || entry.ExpiresAt <= DateTimeOffset.UtcNow
            || !string.Equals(entry.ExecutionId, context.ExecutionId, StringComparison.Ordinal)
            || !string.Equals(entry.WorkerId, context.WorkerId, StringComparison.Ordinal))
            return false;
        path = entry.Path;
        fileName = entry.FileName;
        return true;
    }

    public ValueTask<IReadOnlyList<ExportTemplateDescriptor>> ListTemplatesAsync(
        string? formatId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_templateCatalog.List(formatId));
    }

    public ValueTask<IReadOnlyList<ExportFormatDescriptor>> ListFormatsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<ExportFormatDescriptor>>(
            _exportHandlers.Values
                .Select(handler => handler.Descriptor)
                .OrderBy(descriptor => descriptor.FormatId, StringComparer.Ordinal)
                .ToArray());
    }

    public ValueTask<ExportResult> ExportAsync(
        ExportRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Failure(
            request,
            "DIRECTORY_SELECTION_INVALID",
            "导出请求缺少执行上下文。",
            ScriptErrorCategory.Permission));

    public async ValueTask<ExportResult> ExportAsync(
        ExportRequest request,
        ScriptHostCallContext context,
        CancellationToken cancellationToken = default)
    {
        if (!ScriptHostCallScope.AllowsInteractive(context))
            return Failure(request, "HOSTCALL_SCOPE_NOT_SUPPORTED", "当前脚本执行入口不允许导出。", ScriptErrorCategory.Permission);

        if (!_exportHandlers.TryGetValue(request.FormatId, out var handler))
            return Failure(request, "EXPORT_INVALID_REQUEST", "不支持的导出格式。", ScriptErrorCategory.Validation);

        var validation = ExportRequestValidator.Validate(request, handler.Descriptor);
        if (validation is not null)
            return new(false, request.FormatId, ExportRequestValidator.GetContentKind(request), null, null, null, validation);
        if (!TryGetDirectory(request.DirectorySelectionId, context, out var directory))
            return Failure(request, "DIRECTORY_SELECTION_INVALID", "目录选择令牌无效或已过期。", ScriptErrorCategory.Permission);

        if (request.Template is not null)
            return await ExportTemplateAsync(request, request.Template, handler.Descriptor, directory, context, cancellationToken);

        var finalName = EnsureExtension(request.FileName, handler.Descriptor.DefaultExtension);
        var outputPath = GetUniquePath(directory, finalName);
        try
        {
            Directory.CreateDirectory(directory);
            var executionContext = new ExportExecutionContext(
                outputPath,
                _ => throw new InvalidOperationException("通用导出没有模板流。"),
                cancellationToken,
                message => _logger.LogDebug("导出插件：{Message}", message));
            var renderResult = await handler.RenderAsync(request, executionContext, cancellationToken);
            var fileId = RegisterFile(context, outputPath);
            return new(true, request.FormatId, ExportRequestValidator.GetContentKind(request), fileId,
                Path.GetFileName(outputPath), renderResult.ItemCount, null);
        }
        catch (OperationCanceledException)
        {
            TryDelete(outputPath);
            throw;
        }
        catch (ExportHandlerException exception)
        {
            TryDelete(outputPath);
            return Failure(request, exception.Code, exception.Message, ScriptErrorCategory.Validation, exception.Retryable);
        }
        catch (Exception exception)
        {
            TryDelete(outputPath);
            _logger.LogWarning(exception, "脚本导出失败：{Path}", outputPath);
            return Failure(request, "EXPORT_FAILED", "导出文件生成或保存失败。", ScriptErrorCategory.Host, true);
        }
    }

    private async ValueTask<ExportResult> ExportTemplateAsync(
        ExportRequest request,
        ExportTemplateSource source,
        ExportFormatDescriptor format,
        string directory,
        ScriptHostCallContext context,
        CancellationToken cancellationToken)
    {
        if (!_templateCatalog.TryResolve(source.TemplateId, source.TemplateVersion, out var registration))
            return Failure(request, "EXPORT_TEMPLATE_UNAVAILABLE", "模板不存在、已禁用或对应插件不可用。", ScriptErrorCategory.Validation);
        if (!string.Equals(registration.Descriptor.FormatId, format.FormatId, StringComparison.Ordinal))
            return Failure(request, "EXPORT_TEMPLATE_FORMAT_MISMATCH", "模板与导出格式不匹配。", ScriptErrorCategory.Validation);
        if (!ExportTemplateBindingValidator.TryApplyDefaults(
                source,
                registration.Descriptor,
                out var normalized,
                out var diagnostics))
            return Failure(
                request,
                "EXPORT_TEMPLATE_BINDING_INVALID",
                string.Join(" ", diagnostics.Select(item => item.Message)),
                ScriptErrorCategory.Validation);

        var finalName = EnsureExtension(request.FileName, registration.Descriptor.TemplateFileExtension);
        var outputPath = GetUniquePath(directory, finalName);
        try
        {
            Directory.CreateDirectory(directory);
            var executionContext = new ExportExecutionContext(
                outputPath,
                _ => ValueTask.FromResult<Stream>(File.OpenRead(registration.TemplateFilePath)),
                cancellationToken,
                message => _logger.LogDebug("模板导出：{Message}", message));
            var renderRequest = request with { Template = normalized };
            var renderResult = await registration.Handler.RenderAsync(renderRequest, executionContext, cancellationToken);
            var fileId = RegisterFile(context, outputPath);
            return new(true, request.FormatId, ExportRequestValidator.GetContentKind(request), fileId,
                Path.GetFileName(outputPath), renderResult.ItemCount, null);
        }
        catch (OperationCanceledException)
        {
            TryDelete(outputPath);
            throw;
        }
        catch (ExportHandlerException exception)
        {
            TryDelete(outputPath);
            return Failure(request, exception.Code, exception.Message, ScriptErrorCategory.Validation, exception.Retryable);
        }
        catch (Exception exception)
        {
            TryDelete(outputPath);
            _logger.LogWarning(exception, "模板导出失败：{Path}", outputPath);
            return Failure(request, "EXPORT_TEMPLATE_FAILED", "模板导出失败。", ScriptErrorCategory.Host, true);
        }
    }

    private string RegisterFile(ScriptHostCallContext context, string outputPath)
    {
        var fileId = Guid.NewGuid().ToString("N");
        _files[fileId] = new ExportFileEntry(
            fileId,
            context.ExecutionId,
            context.WorkerId,
            outputPath,
            Path.GetFileName(outputPath),
            DateTimeOffset.UtcNow.Add(_lifetime));
        return fileId;
    }

    private static string EnsureExtension(string fileName, string extension) =>
        Path.HasExtension(fileName) ? fileName : fileName + extension;

    private static string GetUniquePath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate))
            return candidate;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 1; ; index++)
        {
            candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    private static ExportResult Failure(
        ExportRequest request,
        string code,
        string message,
        ScriptErrorCategory category,
        bool retryable = false) =>
        new(false, request.FormatId, ExportRequestValidator.GetContentKind(request), null, null, null,
            new ScriptApiError(code, message, category, retryable));

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 失败清理不覆盖原始导出错误。
        }
    }

    private sealed record DirectorySelectionEntry(
        string SelectionId,
        string ExecutionId,
        string WorkerId,
        string Path,
        DateTimeOffset ExpiresAt);

    private sealed record ExportFileEntry(
        string FileId,
        string ExecutionId,
        string WorkerId,
        string Path,
        string FileName,
        DateTimeOffset ExpiresAt);
}
