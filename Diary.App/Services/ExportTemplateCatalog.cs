using System.Text.Json;
using System.Text.RegularExpressions;
using Diary.ScriptHost;
using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.App.Services;

/// <summary>
/// 管理用户导入的模板文件。模板文件不随插件发布，插件只负责按扩展名校验和渲染。
/// </summary>
public sealed class ExportTemplateCatalog : IExportTemplateCatalog
{
    private const string StateFileName = "export-templates.json";
    private const string StorageDirectoryName = "export-templates";
    private static readonly Regex SnakeCasePart = new("^[a-z][a-z0-9]*(?:_[a-z0-9]+)*$", RegexOptions.Compiled);
    private static readonly Regex VersionPart = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);
    private readonly object _gate = new();
    private readonly ILogger<ExportTemplateCatalog> _logger;
    private readonly Dictionary<string, PersistedTemplate> _templates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IExportTemplateHandler> _handlers;
    private readonly string _storageDirectory;
    private readonly string _statePath;

    public ExportTemplateCatalog(
        ILogger<ExportTemplateCatalog> logger,
        IEnumerable<IExportTemplateHandler> handlers,
        string? storageDirectory = null)
    {
        _logger = logger;
        _handlers = new Dictionary<string, IExportTemplateHandler>(StringComparer.OrdinalIgnoreCase);
        foreach (var handler in handlers)
        {
            foreach (var extension in handler.SupportedTemplateExtensions)
            {
                var normalized = NormalizeExtension(extension);
                if (string.IsNullOrWhiteSpace(normalized))
                    throw new InvalidOperationException($"数据模板扩展名不能为空：{handler.PluginId}。");
                if (!_handlers.TryAdd(normalized, handler))
                    throw new InvalidOperationException($"数据模板扩展名冲突：{normalized}。");
            }
        }
        _storageDirectory = storageDirectory ?? Path.Combine(FsTools.GetApplicationConfigDirectory(), StorageDirectoryName);
        _statePath = storageDirectory is null
            ? Path.Combine(FsTools.GetApplicationConfigDirectory(), StateFileName)
            : Path.Combine(storageDirectory, StateFileName);
        Load();
    }

    public IReadOnlyList<ExportTemplateDescriptor> List(string? formatId = null)
    {
        lock (_gate)
        {
            return _templates.Values
                .Where(item => item.Enabled && (formatId is null
                    || string.Equals(item.Descriptor.FormatId, formatId, StringComparison.Ordinal)))
                .Select(item => item.Descriptor)
                .OrderBy(item => item.TemplateId, StringComparer.Ordinal)
                .ThenBy(item => item.TemplateVersion, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public IReadOnlyList<ExportTemplateCatalogEntry> ListAll()
    {
        lock (_gate)
        {
            return _templates.Values
                .Select(item => new ExportTemplateCatalogEntry(item.Descriptor, item.Enabled))
                .OrderBy(item => item.Descriptor.TemplateId, StringComparer.Ordinal)
                .ThenBy(item => item.Descriptor.TemplateVersion, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public bool TryResolve(
        string templateId,
        string templateVersion,
        out ExportTemplateRegistration registration)
    {
        lock (_gate)
        {
            if (!_templates.TryGetValue(Key(templateId, templateVersion), out var item)
                || !item.Enabled
                || !File.Exists(item.TemplateFilePath))
            {
                registration = null!;
                return false;
            }

            if (!_handlers.TryGetValue(item.Descriptor.TemplateFileExtension, out var handler)
                || !string.Equals(handler.PluginId, item.Descriptor.PluginId, StringComparison.Ordinal)
                || !string.Equals(handler.FormatId, item.Descriptor.FormatId, StringComparison.Ordinal))
            {
                registration = null!;
                return false;
            }

            registration = new ExportTemplateRegistration(
                item.Descriptor,
                item.TemplateFilePath,
                handler);
            return true;
        }
    }

    public async ValueTask<ExportTemplateImportResult> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return Failure("TEMPLATE_FILE_NOT_FOUND", "模板文件不存在。");
        if (new FileInfo(sourcePath).Length > OpenXmlTemplateSafety.MaxPackageBytes)
            return Failure("EXPORT_TEMPLATE_TOO_LARGE", "模板文件大小超过 20 MiB 限制。");

        var extension = NormalizeExtension(Path.GetExtension(sourcePath));
        if (!_handlers.TryGetValue(extension, out var handler))
            return Failure("EXPORT_TEMPLATE_HANDLER_UNAVAILABLE", $"没有插件支持模板扩展名“{extension}”。");

        try
        {
            await using var stream = File.OpenRead(sourcePath);
            var validation = await handler.ValidateAsync(
                stream,
                new ExportTemplateValidationContext(extension, Path.GetFileName(sourcePath)),
                cancellationToken);
            if (!validation.IsValid)
                return new(false, Diagnostics: validation.Diagnostics);

            var templateIdResult = BuildTemplateId(handler.PluginId, validation.TemplateName);
            if (!templateIdResult.IsValid)
                return Failure("EXPORT_TEMPLATE_ID_INVALID", templateIdResult.Error!);
            if (!IsValidVersion(validation.TemplateVersion))
                return Failure("EXPORT_TEMPLATE_VERSION_INVALID", "模板校验器必须返回安全的模板版本标识。");

            var descriptor = new ExportTemplateDescriptor(
                templateIdResult.TemplateId!,
                validation.TemplateVersion!,
                handler.PluginId,
                handler.FormatId,
                extension,
                validation.DisplayName ?? Path.GetFileNameWithoutExtension(sourcePath),
                validation.Description,
                validation.Bindings,
                validation.Features);
            var key = Key(descriptor.TemplateId, descriptor.TemplateVersion);
            lock (_gate)
            {
                if (_templates.ContainsKey(key))
                    return Failure("EXPORT_TEMPLATE_ALREADY_EXISTS", "相同模板 ID 和版本已经存在。");
            }

            var relativePath = Path.Combine(
                descriptor.TemplateId,
                descriptor.TemplateVersion,
                Path.GetFileName(sourcePath));
            var targetPath = Path.Combine(_storageDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await using (var target = File.Create(targetPath))
            {
                stream.Position = 0;
                await stream.CopyToAsync(target, cancellationToken);
            }

            lock (_gate)
            {
                _templates[key] = new PersistedTemplate(descriptor, targetPath, true);
                Save();
            }
            return new(true, descriptor, validation.Diagnostics);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "导入数据模板失败：{Path}", sourcePath);
            return Failure("EXPORT_TEMPLATE_IMPORT_FAILED", "模板导入失败。");
        }
    }

    public async ValueTask<ExportTemplateImportResult> RevalidateAsync(
        string templateId,
        string templateVersion,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetPersisted(templateId, templateVersion, out var persisted))
            return Failure("EXPORT_TEMPLATE_UNAVAILABLE", "模板不存在。");
        if (!_handlers.TryGetValue(persisted.Descriptor.TemplateFileExtension, out var handler))
            return Failure("EXPORT_TEMPLATE_HANDLER_UNAVAILABLE", "模板处理插件不可用。");

        try
        {
            await using var stream = File.OpenRead(persisted.TemplateFilePath);
            var validation = await handler.ValidateAsync(
                stream,
                new ExportTemplateValidationContext(
                    persisted.Descriptor.TemplateFileExtension,
                    Path.GetFileName(persisted.TemplateFilePath)),
                cancellationToken);
            if (!validation.IsValid)
            {
                SetEnabled(templateId, templateVersion, false);
                return new(false, Diagnostics: validation.Diagnostics);
            }

            var idResult = BuildTemplateId(handler.PluginId, validation.TemplateName);
            if (!idResult.IsValid
                || !IsValidVersion(validation.TemplateVersion)
                || !string.Equals(idResult.TemplateId, templateId, StringComparison.Ordinal)
                || !string.Equals(validation.TemplateVersion, templateVersion, StringComparison.Ordinal))
            {
                SetEnabled(templateId, templateVersion, false);
                return Failure("EXPORT_TEMPLATE_IDENTITY_CHANGED", "模板重新校验后 ID 或版本发生变化。");
            }

            var descriptor = persisted.Descriptor with
            {
                DisplayName = validation.DisplayName ?? persisted.Descriptor.DisplayName,
                Description = validation.Description,
                Bindings = validation.Bindings,
                Features = validation.Features,
            };
            lock (_gate)
            {
                _templates[Key(templateId, templateVersion)] = persisted with
                {
                    Descriptor = descriptor,
                    Enabled = true,
                };
                Save();
            }
            return new(true, descriptor, validation.Diagnostics);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetEnabled(templateId, templateVersion, false);
            _logger.LogWarning(exception, "重新校验数据模板失败：{TemplateId} {Version}", templateId, templateVersion);
            return Failure("EXPORT_TEMPLATE_REVALIDATION_FAILED", "模板重新校验失败。");
        }
    }

    public bool SetEnabled(string templateId, string templateVersion, bool enabled)
    {
        lock (_gate)
        {
            var key = Key(templateId, templateVersion);
            if (!_templates.TryGetValue(key, out var item))
                return false;
            _templates[key] = item with { Enabled = enabled };
            Save();
            return true;
        }
    }

    public bool Archive(string templateId, string templateVersion) => SetEnabled(templateId, templateVersion, false);

    private bool TryGetPersisted(string templateId, string version, out PersistedTemplate persisted)
    {
        lock (_gate)
            return _templates.TryGetValue(Key(templateId, version), out persisted!);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_statePath))
                return;
            var loaded = JsonSerializer.Deserialize<List<PersistedTemplate>>(File.ReadAllText(_statePath)) ?? [];
            foreach (var item in loaded)
            {
                if (File.Exists(item.TemplateFilePath))
                    _templates[Key(item.Descriptor.TemplateId, item.Descriptor.TemplateVersion)] = item;
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "加载数据模板目录失败，将使用空目录");
            _templates.Clear();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var json = JsonSerializer.Serialize(_templates.Values.ToArray(), new JsonSerializerOptions { WriteIndented = true });
        var temporaryPath = _statePath + ".tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _statePath, true);
    }

    private static string NormalizeExtension(string extension) =>
        string.IsNullOrWhiteSpace(extension)
            ? string.Empty
            : (extension.StartsWith('.') ? extension : "." + extension).ToLowerInvariant();

    private static string Key(string templateId, string version) => templateId + "\n" + version;

    private static (bool IsValid, string? TemplateId, string? Error) BuildTemplateId(
        string pluginId,
        string? templateName)
    {
        if (!IsValidQualifiedName(pluginId) || string.IsNullOrWhiteSpace(templateName) || !SnakeCasePart.IsMatch(templateName))
            return (false, null, "插件 ID 或模板名必须使用全小写 snake_case，并以点号分隔插件命名空间。");
        return (true, pluginId + "." + templateName, null);
    }

    private static bool IsValidQualifiedName(string value) =>
        value.Split('.', StringSplitOptions.RemoveEmptyEntries).Length > 0
        && value.Split('.').All(SnakeCasePart.IsMatch);

    private static bool IsValidVersion(string? value) =>
        !string.IsNullOrWhiteSpace(value) && VersionPart.IsMatch(value);

    private static ExportTemplateImportResult Failure(string code, string message) =>
        new(false, ErrorCode: code, ErrorMessage: message);

    private sealed record PersistedTemplate(
        ExportTemplateDescriptor Descriptor,
        string TemplateFilePath,
        bool Enabled);
}
