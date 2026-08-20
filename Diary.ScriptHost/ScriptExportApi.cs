using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using Diary.ScriptBase;
using Diary.Script.Runtime;

namespace Diary.ScriptHost;

public enum DialogDismissPolicy
{
    AllowCancel,
    RequireChoice,
}

public enum OptionDialogStatus
{
    Selected,
    Cancelled,
}

public sealed record DialogOption(
    string Id,
    string Label,
    string? Description = null,
    bool IsDestructive = false);

public sealed record OptionDialogRequest
{
    public string Title { get; init; } = "请选择";
    public string? Message { get; init; }
    public required IReadOnlyList<DialogOption> Options { get; init; }
    public DialogDismissPolicy DismissPolicy { get; init; } = DialogDismissPolicy.AllowCancel;
    public string? DefaultOptionId { get; init; }
}

public sealed record OptionDialogResult(
    OptionDialogStatus Status,
    string? OptionId = null);

public sealed record DirectoryPickerOptions
{
    public string Title { get; init; } = "选择目录";
    public string? SuggestedDirectory { get; init; }
}

public sealed record DirectorySelection(string SelectionId, string DisplayName);

public enum OpenExportedFileStatus
{
    Opened,
    UserDeclined,
    Failed,
}

public sealed record OpenExportedFileResult(
    OpenExportedFileStatus Status,
    ScriptApiError? Error = null);

public enum ExportContentKind
{
    Table,
    Document,
}

public enum ExportColumnType
{
    Text,
    Integer,
    Decimal,
    Date,
    Time,
    Duration,
    DateTime,
    Boolean,
}

public enum ExportAggregation
{
    Sum,
}

public enum ExportTableStyle
{
    Default,
    Compact,
    Report,
}

public enum ExportFeature
{
    UnicodeText,
    TypedValues,
    NumberFormat,
    BackgroundColor,
    MergeCells,
    GeneratedAggregate,
    BasicStyle,
    Paragraphs,
    DocumentTables,
}

public sealed record ExportColumn(
    string Name,
    ExportColumnType Type = ExportColumnType.Text,
    string? NumberFormat = null);

public sealed record ExportAggregateColumn(
    string ColumnName,
    ExportAggregation Aggregation = ExportAggregation.Sum,
    string? Label = null);

public sealed record TableCellMerge(
    int StartRow,
    int StartColumn,
    int RowSpan,
    int ColumnSpan);

public sealed record ExportFormatOptions(
    string FormatId,
    IReadOnlyDictionary<string, object?> Values);

public abstract record ExportContent
{
    public abstract ExportContentKind Kind { get; }
}

public sealed record ExportTableContent : ExportContent
{
    public override ExportContentKind Kind => ExportContentKind.Table;
    public string? Title { get; init; }
    public required IReadOnlyList<ExportColumn> Columns { get; init; }
    public required IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; }
    public IReadOnlyList<ExportAggregateColumn> Aggregates { get; init; } = [];
    public IReadOnlyList<TableCellMerge> Merges { get; init; } = [];
    public ExportTableStyle Style { get; init; } = ExportTableStyle.Default;
}

public sealed record ExportDocumentContent : ExportContent
{
    public override ExportContentKind Kind => ExportContentKind.Document;
    public string? Title { get; init; }
    public required IReadOnlyList<ExportDocumentBlock> Blocks { get; init; }
    public ExportTableStyle Style { get; init; } = ExportTableStyle.Default;
}

public abstract record ExportDocumentBlock;

public sealed record ExportHeadingBlock(string Text, int Level = 1) : ExportDocumentBlock;

public sealed record ExportParagraphBlock(string Text) : ExportDocumentBlock;

public sealed record ExportTableBlock(ExportTableContent Table) : ExportDocumentBlock;

public sealed record ExportTemplateSource
{
    public required string TemplateId { get; init; }
    public required string TemplateVersion { get; init; }
    public IReadOnlyDictionary<string, object?> Values { get; init; } = new Dictionary<string, object?>();
    public IReadOnlyDictionary<string, ExportTableContent> Tables { get; init; } = new Dictionary<string, ExportTableContent>();
    public IReadOnlyDictionary<string, ExportContent> Documents { get; init; } = new Dictionary<string, ExportContent>();
}

public sealed record ExportRequest
{
    public required string FormatId { get; init; }
    public required string DirectorySelectionId { get; init; }
    public required string FileName { get; init; }
    public ExportContent? Content { get; init; }
    public ExportTemplateSource? Template { get; init; }
    public ExportFormatOptions? FormatOptions { get; init; }
    public bool ValidateOnly { get; init; }
}

public sealed record ExportContentCapabilities(
    ExportContentKind ContentKind,
    IReadOnlyList<ExportFeature> Features);

public sealed record ExportFormatOptionDescriptor(
    string Key,
    ExportScalarType Type,
    bool Required = false,
    object? DefaultValue = null,
    string? Description = null);

public sealed record ExportBindingDescriptor(
    string Key,
    ExportBindingKind Kind,
    ExportScalarType? ScalarType = null,
    bool Required = true,
    bool HasDefaultValue = false,
    object? DefaultValue = null,
    string? Description = null);

public sealed record ExportTemplateDescriptor(
    string TemplateId,
    string TemplateVersion,
    string PluginId,
    string FormatId,
    string TemplateFileExtension,
    string DisplayName,
    string? Description,
    IReadOnlyList<ExportBindingDescriptor> Bindings,
    IReadOnlyList<ExportFeature> Features);

public sealed record ExportTemplateValidationContext(
    string FileExtension,
    string FileName);

public sealed record ExportDiagnostic(
    string Code,
    string Message,
    string? BindingKey = null);

public sealed record ExportTemplateValidationResult(
    bool IsValid,
    string? TemplateName,
    string? DisplayName,
    string? Description,
    string? TemplateVersion,
    IReadOnlyList<ExportBindingDescriptor> Bindings,
    IReadOnlyList<ExportFeature> Features,
    IReadOnlyList<ExportDiagnostic> Diagnostics);

public sealed record ExportFormatDescriptor(
    string FormatId,
    string DisplayName,
    string DefaultExtension,
    IReadOnlyList<string> AllowedExtensions,
    IReadOnlyList<ExportContentCapabilities> ContentCapabilities,
    bool SupportsTemplates = false,
    IReadOnlyList<ExportFormatOptionDescriptor>? FormatOptions = null);

public enum ExportBindingKind
{
    Scalar,
    Table,
    Document,
    Context,
}

public enum ExportScalarType
{
    Text,
    Integer,
    Decimal,
    Date,
    Time,
    Duration,
    DateTime,
    Boolean,
}

public sealed record ExportResult(
    bool Succeeded,
    string FormatId,
    ExportContentKind ContentKind,
    string? FileId,
    string? FileName,
    int? ItemCount,
    ScriptApiError? Error,
    bool ValidatedOnly = false);

public interface IExportApi
{
    ValueTask<ExportResult> ExportAsync(
        ExportRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ExportFormatDescriptor>> ListFormatsAsync(
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ExportTemplateDescriptor>> ListTemplatesAsync(
        string? formatId = null,
        CancellationToken cancellationToken = default);
}

public sealed record ExportExecutionContext(
    string OutputPath,
    Func<CancellationToken, ValueTask<Stream>> OpenTemplateAsync,
    CancellationToken CancellationToken,
    Action<string>? Log = null);

public sealed record ExportRenderResult(int? ItemCount);

public sealed record ExportPluginManifest(
    string Id,
    string Version,
    int ApiVersion = 1);

public interface IExportPlugin
{
    ExportPluginManifest Manifest { get; }
    IEnumerable<IExportHandler> GetExportHandlers();
    IEnumerable<IExportTemplateHandler> GetTemplateHandlers();
}

public interface IExportHandler
{
    ExportFormatDescriptor Descriptor { get; }

    ValueTask<ExportRenderResult> RenderAsync(
        ExportRequest request,
        ExportExecutionContext context,
        CancellationToken cancellationToken = default);
}

public interface IExportTemplateHandler
{
    string PluginId { get; }
    string FormatId { get; }
    IReadOnlyList<string> SupportedTemplateExtensions { get; }

    ValueTask<ExportTemplateValidationResult> ValidateAsync(
        Stream templateStream,
        ExportTemplateValidationContext context,
        CancellationToken cancellationToken = default);

    ValueTask<ExportRenderResult> RenderAsync(
        ExportRequest request,
        ExportExecutionContext context,
        CancellationToken cancellationToken = default);
}

public sealed record ExportTemplateRegistration(
    ExportTemplateDescriptor Descriptor,
    string TemplateFilePath,
    IExportTemplateHandler Handler);

public sealed record ExportTemplateCatalogEntry(
    ExportTemplateDescriptor Descriptor,
    bool Enabled);

public sealed record ExportTemplateImportResult(
    bool Succeeded,
    ExportTemplateDescriptor? Descriptor = null,
    IReadOnlyList<ExportDiagnostic>? Diagnostics = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public static class ExportTemplateBindingValidator
{
    public static bool TryApplyDefaults(
        ExportTemplateSource source,
        ExportTemplateDescriptor descriptor,
        out ExportTemplateSource normalized,
        out IReadOnlyList<ExportDiagnostic> diagnostics)
    {
        var errors = new List<ExportDiagnostic>();
        var bindings = descriptor.Bindings.ToDictionary(binding => binding.Key, StringComparer.Ordinal);
        var values = new Dictionary<string, object?>(source.Values, StringComparer.Ordinal);
        var tables = new Dictionary<string, ExportTableContent>(source.Tables, StringComparer.Ordinal);
        var documents = new Dictionary<string, ExportContent>(source.Documents, StringComparer.Ordinal);

        foreach (var key in values.Keys)
            if (!bindings.TryGetValue(key, out var binding)
                || binding.Kind is not (ExportBindingKind.Scalar or ExportBindingKind.Context))
                errors.Add(new("EXPORT_TEMPLATE_UNKNOWN_BINDING", $"未知或类型不匹配的标量绑定：{key}。", key));
        foreach (var key in tables.Keys)
            if (!bindings.TryGetValue(key, out var binding)
                || binding.Kind is not (ExportBindingKind.Table or ExportBindingKind.Context))
                errors.Add(new("EXPORT_TEMPLATE_UNKNOWN_BINDING", $"未知或类型不匹配的表格绑定：{key}。", key));
        foreach (var key in documents.Keys)
            if (!bindings.TryGetValue(key, out var binding) || binding.Kind != ExportBindingKind.Document)
                errors.Add(new("EXPORT_TEMPLATE_UNKNOWN_BINDING", $"未知或类型不匹配的文档绑定：{key}。", key));

        foreach (var binding in descriptor.Bindings)
        {
            var exists = binding.Kind switch
            {
                ExportBindingKind.Scalar => values.ContainsKey(binding.Key),
                ExportBindingKind.Table => tables.ContainsKey(binding.Key),
                ExportBindingKind.Document => documents.ContainsKey(binding.Key),
                ExportBindingKind.Context => values.ContainsKey(binding.Key) || tables.ContainsKey(binding.Key),
                _ => false,
            };
            if (binding.Kind == ExportBindingKind.Context
                && values.ContainsKey(binding.Key)
                && tables.ContainsKey(binding.Key))
                errors.Add(new(
                    "EXPORT_TEMPLATE_BINDING_DUPLICATE",
                    "上下文绑定不能同时提供标量值和表格值。",
                    binding.Key));
            if (!exists && binding.HasDefaultValue)
            {
                if (binding.Kind != ExportBindingKind.Scalar)
                    errors.Add(new("EXPORT_TEMPLATE_DEFAULT_INVALID", "只有标量绑定支持默认值。", binding.Key));
                else
                    values[binding.Key] = binding.DefaultValue;
                exists = true;
            }

            if (!exists && binding.Required)
                errors.Add(new("EXPORT_TEMPLATE_REQUIRED_BINDING_MISSING", "缺少必填模板数据。", binding.Key));

            if (exists && binding.Kind == ExportBindingKind.Scalar
                && !IsScalarValueValid(values[binding.Key], binding.ScalarType))
                errors.Add(new("EXPORT_TEMPLATE_BINDING_TYPE_INVALID", "模板绑定数据类型不匹配。", binding.Key));
        }

        diagnostics = errors;
        normalized = source with
        {
            Values = values,
            Tables = tables,
            Documents = documents,
        };
        return errors.Count == 0;
    }

    private static bool IsScalarValueValid(object? value, ExportScalarType? type)
    {
        if (value is null || type is null)
            return true;
        if (value is JsonElement element)
        {
            return type switch
            {
                ExportScalarType.Text => element.ValueKind == JsonValueKind.String,
                ExportScalarType.Boolean => element.ValueKind is JsonValueKind.True or JsonValueKind.False,
                ExportScalarType.Integer or ExportScalarType.Decimal => element.ValueKind == JsonValueKind.Number,
                _ => element.ValueKind == JsonValueKind.String,
            };
        }
        return type switch
        {
            ExportScalarType.Text => value is string,
            ExportScalarType.Integer => value is int or long or short or byte,
            ExportScalarType.Decimal => value is decimal or double or float or int or long,
            ExportScalarType.Date => value is DateOnly or string,
            ExportScalarType.Time => value is TimeOnly or string,
            ExportScalarType.Duration => value is TimeSpan or string,
            ExportScalarType.DateTime => value is DateTimeOffset or DateTime or string,
            ExportScalarType.Boolean => value is bool,
            _ => true,
        };
    }
}

public interface IExportTemplateCatalog
{
    IReadOnlyList<ExportTemplateDescriptor> List(string? formatId = null);

    IReadOnlyList<ExportTemplateCatalogEntry> ListAll();

    bool TryResolve(
        string templateId,
        string templateVersion,
        out ExportTemplateRegistration registration);

    ValueTask<ExportTemplateImportResult> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default);

    ValueTask<ExportTemplateImportResult> RevalidateAsync(
        string templateId,
        string templateVersion,
        CancellationToken cancellationToken = default);

    bool SetEnabled(string templateId, string templateVersion, bool enabled);

    bool Archive(string templateId, string templateVersion);
}

public interface IFileInteractionApi : IOptionDialogApi
{
    ValueTask<DirectorySelection?> PickDirectoryAsync(
        DirectoryPickerOptions options,
        CancellationToken cancellationToken = default);

    ValueTask<OpenExportedFileResult> AskToOpenExportedFileAsync(
        string fileId,
        CancellationToken cancellationToken = default);
}

public sealed class ExportRequestValidator
{
    public const int MaxRows = 100_000;
    public const int MaxColumns = 256;
    public const int MaxFileNameLength = 240;

    public static ScriptApiError? Validate(ExportRequest request, ExportFormatDescriptor descriptor)
    {
        if (!string.Equals(request.FormatId, descriptor.FormatId, StringComparison.Ordinal))
            return Error("导出格式与格式目录不一致。");
        if (!request.ValidateOnly && string.IsNullOrWhiteSpace(request.DirectorySelectionId))
            return Error("目录选择令牌不能为空。");
        if (string.IsNullOrWhiteSpace(request.FileName))
            return Error("文件名不能为空。");
        if (request.FileName.Length > MaxFileNameLength
            || request.FileName is "." or ".."
            || request.FileName.Any(char.IsControl)
            || request.FileName.Contains('/')
            || request.FileName.Contains('\\'))
            return Error("文件名包含非法字符或路径分隔符。");

        var extension = Path.GetExtension(request.FileName);
        if (string.IsNullOrEmpty(extension))
            extension = descriptor.DefaultExtension;
        if (!descriptor.AllowedExtensions.Any(x => string.Equals(x, extension, StringComparison.OrdinalIgnoreCase)))
            return Error("文件扩展名与导出格式不匹配。");
        if ((request.Content is null) == (request.Template is null))
            return Error("通用导出和模板导出必须且只能选择一种内容来源。");
        if (request.FormatOptions is not null
            && !string.Equals(request.FormatOptions.FormatId, descriptor.FormatId, StringComparison.Ordinal))
            return Error("格式选项的格式 ID 与导出格式不一致。");
        if (request.Template is not null && request.FormatOptions is not null)
            return Error("EXPORT_UNSUPPORTED_FEATURE", "模板导出不支持通用格式选项。");
        var formatOptionsError = ValidateFormatOptions(request.FormatOptions, descriptor);
        if (formatOptionsError is not null)
            return formatOptionsError;
        if (request.Template is not null)
        {
            if (!descriptor.SupportsTemplates)
                return Error($"格式 {descriptor.FormatId} 不支持模板导出。");
            if (string.IsNullOrWhiteSpace(request.Template.TemplateId)
                || string.IsNullOrWhiteSpace(request.Template.TemplateVersion))
                return Error("模板 ID 和版本不能为空。");
            return null;
        }
        var content = request.Content!;
        var capability = descriptor.ContentCapabilities.FirstOrDefault(item => item.ContentKind == content.Kind);
        if (capability is null)
            return Error($"格式 {descriptor.FormatId} 不支持 {content.Kind.ToString().ToLowerInvariant()} 内容。");

        return content switch
        {
            ExportTableContent table => ValidateTableContent(table, capability),
            ExportDocumentContent document => ValidateDocument(document, capability),
            _ => Error("未知的导出内容类型。"),
        };
    }

    private static ScriptApiError? ValidateFormatOptions(
        ExportFormatOptions? formatOptions,
        ExportFormatDescriptor descriptor)
    {
        var optionDescriptors = descriptor.FormatOptions ?? [];
        if (formatOptions is null)
        {
            var required = optionDescriptors.FirstOrDefault(option => option.Required && option.DefaultValue is null);
            return required is null
                ? null
                : Error(
                    "EXPORT_FORMAT_OPTION_REQUIRED",
                    $"缺少必填格式选项：{required.Key}。",
                    new Dictionary<string, object?> { ["option"] = required.Key });
        }
        if (optionDescriptors.Count == 0)
            return Error("EXPORT_UNSUPPORTED_FEATURE", $"格式 {descriptor.FormatId} 不支持格式选项。");

        var optionsByKey = optionDescriptors.ToDictionary(option => option.Key, StringComparer.Ordinal);
        foreach (var (key, value) in formatOptions.Values)
        {
            if (!optionsByKey.TryGetValue(key, out var option))
                return Error(
                    "EXPORT_FORMAT_OPTION_UNKNOWN",
                    $"未知格式选项：{key}。",
                    new Dictionary<string, object?> { ["option"] = key });
            if (!IsFormatOptionValueValid(value, option.Type))
                return Error(
                    "EXPORT_FORMAT_OPTION_TYPE_INVALID",
                    $"格式选项“{key}”必须是 {option.Type}。",
                    new Dictionary<string, object?>
                    {
                        ["option"] = key,
                        ["expected_type"] = JsonNamingPolicy.SnakeCaseLower.ConvertName(option.Type.ToString()),
                    });
        }

        var missing = optionDescriptors.FirstOrDefault(option =>
            option.Required
            && option.DefaultValue is null
            && !formatOptions.Values.ContainsKey(option.Key));
        return missing is null
            ? null
            : Error(
                "EXPORT_FORMAT_OPTION_REQUIRED",
                $"缺少必填格式选项：{missing.Key}。",
                new Dictionary<string, object?> { ["option"] = missing.Key });
    }

    private static bool IsFormatOptionValueValid(object? value, ExportScalarType type)
    {
        if (value is null)
            return false;
        if (value is JsonElement element)
        {
            return type switch
            {
                ExportScalarType.Text => element.ValueKind == JsonValueKind.String,
                ExportScalarType.Integer => element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out _),
                ExportScalarType.Decimal => element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out _),
                ExportScalarType.Boolean => element.ValueKind is JsonValueKind.True or JsonValueKind.False,
                _ => element.ValueKind == JsonValueKind.String,
            };
        }

        return type switch
        {
            ExportScalarType.Text => value is string,
            ExportScalarType.Integer => value is sbyte or byte or short or ushort or int or uint or long or ulong,
            ExportScalarType.Decimal => value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal,
            ExportScalarType.Boolean => value is bool,
            ExportScalarType.Date or ExportScalarType.Time or ExportScalarType.Duration or ExportScalarType.DateTime => value is string,
            _ => false,
        };
    }

    private static ScriptApiError? ValidateDocument(
        ExportDocumentContent document,
        ExportContentCapabilities capability)
    {
        if (document.Blocks.Count > MaxRows)
            return Error("文档块数量超出限制。");
        if (document.Style != ExportTableStyle.Default
            && !capability.Features.Contains(ExportFeature.BasicStyle))
            return Error("EXPORT_UNSUPPORTED_FEATURE", "当前格式不支持文档视觉样式。");
        foreach (var block in document.Blocks)
        {
            switch (block)
            {
                case ExportHeadingBlock heading when heading.Level is < 1 or > 6:
                    return Error("文档标题级别必须在 1 到 6 之间。");
                case ExportHeadingBlock heading when string.IsNullOrWhiteSpace(heading.Text):
                    return Error("文档标题不能为空。");
                case ExportParagraphBlock paragraph when paragraph.Text.Length > 1_000_000:
                    return Error("文档段落长度超出限制。");
                case ExportTableBlock table:
                    {
                        var error = ValidateTableContent(table.Table, capability);
                        if (error is not null)
                            return error;
                        break;
                    }
            }
        }
        return null;
    }

    public static ScriptApiError? ValidateTableContent(
        ExportTableContent table,
        ExportContentCapabilities capability)
    {
        if (table.Columns.Count is 0 or > MaxColumns)
            return Error("导出列数量超出限制。");
        if (table.Rows.Count > MaxRows)
            return Error("导出数据行数超出限制。");
        var emptyColumnIndex = table.Columns
            .Select((column, index) => (column, index))
            .FirstOrDefault(item => string.IsNullOrWhiteSpace(item.column.Name));
        if (emptyColumnIndex.column is not null)
            return Error(
                "EXPORT_COLUMN_NAME_REQUIRED",
                "导出列名不能为空。",
                new Dictionary<string, object?> { ["column_index"] = emptyColumnIndex.index + 1 });
        var duplicateColumn = table.Columns
            .GroupBy(column => column.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateColumn is not null)
            return Error(
                "EXPORT_COLUMN_NAME_DUPLICATE",
                $"导出列名不能重复：{duplicateColumn.Key}。",
                new Dictionary<string, object?> { ["column"] = duplicateColumn.Key });
        if (table.Style != ExportTableStyle.Default
            && !capability.Features.Contains(ExportFeature.BasicStyle))
            return Error("EXPORT_UNSUPPORTED_FEATURE", "当前格式不支持表格视觉样式。");
        if (table.Merges.Count > 0
            && !capability.Features.Contains(ExportFeature.MergeCells))
            return Error("EXPORT_UNSUPPORTED_FEATURE", "当前格式不支持合并单元格。");
        if (table.Aggregates.Count > 0
            && !capability.Features.Contains(ExportFeature.GeneratedAggregate))
            return Error("EXPORT_UNSUPPORTED_FEATURE", "当前格式不支持合计。");
        var numberFormatColumn = table.Columns.FirstOrDefault(column => !string.IsNullOrWhiteSpace(column.NumberFormat));
        if (numberFormatColumn is not null
            && !capability.Features.Contains(ExportFeature.NumberFormat))
            return Error(
                "EXPORT_UNSUPPORTED_FEATURE",
                $"当前格式不支持列“{numberFormatColumn.Name}”的 number_format。",
                new Dictionary<string, object?> { ["column"] = numberFormatColumn.Name });
        foreach (var row in table.Rows)
        {
            if (row.Count != table.Columns.Count)
                return Error("导出数据行的单元格数量与列数量不一致。");
        }

        var columnNames = table.Columns.Select(column => column.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var aggregate in table.Aggregates)
        {
            if (!columnNames.Contains(aggregate.ColumnName))
                return Error(
                    "EXPORT_AGGREGATE_COLUMN_NOT_FOUND",
                    $"合计列不存在：{aggregate.ColumnName}。",
                    new Dictionary<string, object?> { ["column"] = aggregate.ColumnName });
            var column = table.Columns.First(column =>
                string.Equals(column.Name, aggregate.ColumnName, StringComparison.OrdinalIgnoreCase));
            if (column.Type is not (ExportColumnType.Integer or ExportColumnType.Decimal or ExportColumnType.Duration))
                return Error(
                    "EXPORT_AGGREGATE_TYPE_UNSUPPORTED",
                    $"列“{aggregate.ColumnName}”不支持 Sum 合计。",
                    new Dictionary<string, object?>
                    {
                        ["column"] = aggregate.ColumnName,
                        ["column_type"] = JsonNamingPolicy.SnakeCaseLower.ConvertName(column.Type.ToString()),
                        ["aggregation"] = "sum",
                    });
        }
        var aggregateLabels = table.Aggregates
            .Select(aggregate => aggregate.Label)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (aggregateLabels.Length > 1)
            return Error(
                "EXPORT_AGGREGATE_LABEL_CONFLICT",
                "同一合计行只能使用一个合计标签。",
                new Dictionary<string, object?> { ["labels"] = aggregateLabels });
        if (table.Aggregates.Count > 0 && GetAggregateLabelColumnIndex(table) < 0)
            return Error(
                "EXPORT_AGGREGATE_LABEL_COLUMN_MISSING",
                "合计行至少需要一个未参与聚合的列用于显示标签。",
                new Dictionary<string, object?>
                {
                    ["aggregated_columns"] = table.Aggregates.Select(aggregate => aggregate.ColumnName).ToArray(),
                });

        foreach (var merge in table.Merges)
        {
            if (merge.StartRow < 1 || merge.StartColumn < 1 || merge.RowSpan < 1 || merge.ColumnSpan < 1
                || merge.StartRow + merge.RowSpan - 1 > table.Rows.Count
                || merge.StartColumn + merge.ColumnSpan - 1 > table.Columns.Count)
                return Error("合并区域超出表格边界。");
        }

        for (var i = 0; i < table.Merges.Count; i++)
        {
            var left = table.Merges[i];
            var leftEndRow = left.StartRow + left.RowSpan - 1;
            var leftEndColumn = left.StartColumn + left.ColumnSpan - 1;
            for (var j = i + 1; j < table.Merges.Count; j++)
            {
                var right = table.Merges[j];
                var rightEndRow = right.StartRow + right.RowSpan - 1;
                var rightEndColumn = right.StartColumn + right.ColumnSpan - 1;
                if (left.StartRow <= rightEndRow && right.StartRow <= leftEndRow
                    && left.StartColumn <= rightEndColumn && right.StartColumn <= leftEndColumn)
                    return Error("合并区域不能相互重叠。");
            }
        }
        return null;
    }

    public static object? NormalizeValue(object? value, ExportColumnType type, string columnName, int rowNumber)
    {
        if (value is null)
            return null;
        try
        {
            return type switch
            {
                ExportColumnType.Text => value.ToString(),
                ExportColumnType.Integer => value switch
                {
                    int i => i,
                    long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
                    _ => int.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture),
                },
                ExportColumnType.Decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                ExportColumnType.Boolean => value is bool b ? b : bool.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!),
                ExportColumnType.Date => DateOnly.ParseExact(Convert.ToString(value, CultureInfo.InvariantCulture)!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                ExportColumnType.Time => TimeOnly.ParseExact(Convert.ToString(value, CultureInfo.InvariantCulture)!, "HH:mm:ss", CultureInfo.InvariantCulture),
                ExportColumnType.Duration => ParseDuration(value),
                ExportColumnType.DateTime => DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture),
                _ => throw new InvalidOperationException("不支持的列类型。"),
            };
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            throw new ExportHandlerException(
                "EXPORT_VALUE_INVALID",
                $"第 {rowNumber} 行的“{columnName}”值无法转换为 {type}。",
                retryable: false,
                new Dictionary<string, object?>
                {
                    ["row"] = rowNumber,
                    ["column"] = columnName,
                    ["expected_type"] = JsonNamingPolicy.SnakeCaseLower.ConvertName(type.ToString()),
                    ["value_was_null"] = false,
                },
                exception);
        }
    }

    public static string GetAggregateLabel(ExportTableContent table) =>
        table.Aggregates
            .Select(aggregate => aggregate.Label)
            .FirstOrDefault(label => !string.IsNullOrWhiteSpace(label))
        ?? "合计";

    public static int GetAggregateLabelColumnIndex(ExportTableContent table)
    {
        var aggregateColumns = table.Aggregates
            .Select(aggregate => aggregate.ColumnName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < table.Columns.Count; index++)
        {
            if (!aggregateColumns.Contains(table.Columns[index].Name))
                return index;
        }
        return -1;
    }

    private static TimeSpan ParseDuration(object value)
    {
        if (value is TimeSpan duration)
            return duration;
        if (value is double seconds)
            return TimeSpan.FromSeconds(seconds);
        if (value is float floatSeconds)
            return TimeSpan.FromSeconds(floatSeconds);
        if (value is decimal decimalSeconds)
            return TimeSpan.FromSeconds((double)decimalSeconds);
        if (value is int intSeconds)
            return TimeSpan.FromSeconds(intSeconds);
        if (value is long longSeconds)
            return TimeSpan.FromSeconds(longSeconds);

        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        var parts = text.Split(':', StringSplitOptions.TrimEntries);
        var secondsPart = 0d;
        if (parts.Length is < 2 or > 3
            || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            || (parts.Length == 3
                && !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out secondsPart)))
            throw new FormatException("持续时间必须是秒数或 HH:mm:ss。");
        var secondsValue = parts.Length == 3 ? secondsPart : 0;
        if (hours < 0 || minutes is < 0 or > 59 || secondsValue < 0 || secondsValue >= 60)
            throw new FormatException("持续时间范围无效。");
        return TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(secondsValue);
    }

    public static ExportContentKind GetContentKind(ExportRequest request) =>
        request.Content?.Kind ?? ExportContentKind.Table;

    private static ScriptApiError Error(
        string message) =>
        Error("EXPORT_INVALID_REQUEST", message);

    private static ScriptApiError Error(
        string code,
        string message,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(code, message, ScriptErrorCategory.Validation, Details: details);
}

public interface IOptionDialogApi
{
    ValueTask<OptionDialogResult> SelectOptionAsync(
        OptionDialogRequest request,
        CancellationToken cancellationToken = default);
}

public interface IContextualExportApi : IExportApi
{
    ValueTask<ExportResult> ExportAsync(
        ExportRequest request,
        ScriptHostCallContext context,
        CancellationToken cancellationToken = default);
}

public static class ExportJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        };
        options.Converters.Add(new ExportContentJsonConverter());
        return options;
    }

    private sealed class ExportContentJsonConverter : JsonConverter<ExportContent>
    {
        public override ExportContent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            return root.GetProperty("kind").GetString() switch
            {
                "table" => ReadTable(root, options),
                "document" => ReadDocument(root, options),
                _ => throw new JsonException("只支持 table 或 document 导出内容。"),
            };
        }

        public override void Write(Utf8JsonWriter writer, ExportContent value, JsonSerializerOptions options)
        {
            switch (value)
            {
                case ExportTableContent table:
                    WriteTable(writer, table, options, includeKind: true);
                    break;
                case ExportDocumentContent document:
                    WriteDocument(writer, document, options);
                    break;
                default:
                    throw new JsonException("只支持 table 或 document 导出内容。");
            }
        }

        private static ExportTableContent ReadTable(JsonElement root, JsonSerializerOptions options) => new()
        {
            Title = root.TryGetProperty("title", out var title) ? title.GetString() : null,
            Columns = root.GetProperty("columns").Deserialize<List<ExportColumn>>(options) ?? [],
            Rows = root.GetProperty("rows").EnumerateArray()
                .Select(row => row.EnumerateArray().Select(ToObject).ToArray())
                .Cast<IReadOnlyList<object?>>()
                .ToArray(),
            Aggregates = root.TryGetProperty("aggregates", out var aggregates)
                ? aggregates.Deserialize<List<ExportAggregateColumn>>(options) ?? []
                : [],
            Merges = root.TryGetProperty("merges", out var merges)
                ? merges.Deserialize<List<TableCellMerge>>(options) ?? []
                : [],
            Style = root.TryGetProperty("style", out var style)
                ? style.Deserialize<ExportTableStyle>(options)
                : ExportTableStyle.Default,
        };

        private static ExportDocumentContent ReadDocument(JsonElement root, JsonSerializerOptions options)
        {
            var blocks = new List<ExportDocumentBlock>();
            foreach (var block in root.GetProperty("blocks").EnumerateArray())
            {
                blocks.Add(block.GetProperty("kind").GetString() switch
                {
                    "heading" => new ExportHeadingBlock(
                        block.GetProperty("text").GetString() ?? string.Empty,
                        block.TryGetProperty("level", out var level) ? level.GetInt32() : 1),
                    "paragraph" => new ExportParagraphBlock(block.GetProperty("text").GetString() ?? string.Empty),
                    "table" => new ExportTableBlock(ReadTable(block.GetProperty("table"), options)),
                    _ => throw new JsonException("文档包含未知块类型。"),
                });
            }
            return new ExportDocumentContent
            {
                Title = root.TryGetProperty("title", out var title) ? title.GetString() : null,
                Blocks = blocks,
                Style = root.TryGetProperty("style", out var style)
                    ? style.Deserialize<ExportTableStyle>(options)
                    : ExportTableStyle.Default,
            };
        }

        private static void WriteDocument(
            Utf8JsonWriter writer,
            ExportDocumentContent document,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "document");
            if (document.Title is not null)
                writer.WriteString("title", document.Title);
            writer.WritePropertyName("blocks");
            writer.WriteStartArray();
            foreach (var block in document.Blocks)
            {
                writer.WriteStartObject();
                switch (block)
                {
                    case ExportHeadingBlock heading:
                        writer.WriteString("kind", "heading");
                        writer.WriteNumber("level", heading.Level);
                        writer.WriteString("text", heading.Text);
                        break;
                    case ExportParagraphBlock paragraph:
                        writer.WriteString("kind", "paragraph");
                        writer.WriteString("text", paragraph.Text);
                        break;
                    case ExportTableBlock table:
                        writer.WriteString("kind", "table");
                        writer.WritePropertyName("table");
                        WriteTable(writer, table.Table, options, includeKind: false);
                        break;
                    default:
                        throw new JsonException("文档包含未知块类型。");
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteString("style", document.Style.ToString().ToLowerInvariant());
            writer.WriteEndObject();
        }

        private static void WriteTable(
            Utf8JsonWriter writer,
            ExportTableContent table,
            JsonSerializerOptions options,
            bool includeKind)
        {
            writer.WriteStartObject();
            if (includeKind)
                writer.WriteString("kind", "table");
            if (table.Title is not null)
                writer.WriteString("title", table.Title);
            writer.WritePropertyName("columns");
            JsonSerializer.Serialize(writer, table.Columns, options);
            writer.WritePropertyName("rows");
            JsonSerializer.Serialize(writer, table.Rows, options);
            writer.WritePropertyName("aggregates");
            JsonSerializer.Serialize(writer, table.Aggregates, options);
            writer.WritePropertyName("merges");
            JsonSerializer.Serialize(writer, table.Merges, options);
            writer.WriteString("style", table.Style.ToString().ToLowerInvariant());
            writer.WriteEndObject();
        }

        private static object? ToObject(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number => element.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => element.GetRawText(),
        };
    }
}

public static class CsvTextSafety
{
    private static readonly char[] FormulaPrefixes = ['=', '+', '-', '@'];

    public static string ProtectFormulaText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length > 0 && FormulaPrefixes.Contains(value[0]) ? "'" + value : value;
    }

    public static string Escape(string? value, bool protectFormulaText = true)
    {
        var text = value ?? string.Empty;
        if (protectFormulaText)
            text = ProtectFormulaText(text);
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? '"' + text.Replace("\"", "\"\"") + '"'
            : text;
    }
}

public static class OpenXmlTemplateSafety
{
    public const long MaxPackageBytes = 20L * 1024 * 1024;
    public const long MaxExpandedBytes = 100L * 1024 * 1024;
    public const int MaxEntryCount = 2_048;

    public static IReadOnlyList<ExportDiagnostic> ValidatePackage(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
            return [new("EXPORT_TEMPLATE_STREAM_INVALID", "模板流必须支持定位。")];
        if (stream.Length > MaxPackageBytes)
            return [new("EXPORT_TEMPLATE_TOO_LARGE", "模板文件大小超过 20 MiB 限制。")];

        var originalPosition = stream.Position;
        var diagnostics = new List<ExportDiagnostic>();
        try
        {
            stream.Position = 0;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > MaxEntryCount)
                diagnostics.Add(new("EXPORT_TEMPLATE_TOO_LARGE", "模板压缩包条目数量超过限制。"));

            long expandedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                expandedBytes = checked(expandedBytes + entry.Length);
                if (expandedBytes > MaxExpandedBytes)
                {
                    diagnostics.Add(new("EXPORT_TEMPLATE_TOO_LARGE", "模板解压后的总大小超过 100 MiB 限制。"));
                    break;
                }

                var normalizedName = entry.FullName.Replace('\\', '/').ToLowerInvariant();
                if (normalizedName.Contains("vbaproject", StringComparison.Ordinal)
                    || normalizedName.Contains("/activex/", StringComparison.Ordinal)
                    || normalizedName.Contains("/embeddings/", StringComparison.Ordinal)
                    || normalizedName.Contains("oleobject", StringComparison.Ordinal))
                {
                    diagnostics.Add(new(
                        "EXPORT_TEMPLATE_UNSUPPORTED",
                        "模板包含宏、ActiveX 或嵌入对象。",
                        entry.FullName));
                }

                if (normalizedName.EndsWith(".rels", StringComparison.Ordinal))
                    ValidateRelationships(entry, diagnostics);
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or OverflowException or XmlException)
        {
            diagnostics.Add(new("EXPORT_TEMPLATE_STRUCTURE_INVALID", $"无法检查模板包结构：{exception.Message}"));
        }
        finally
        {
            stream.Position = originalPosition;
        }
        return diagnostics;
    }

    private static void ValidateRelationships(
        ZipArchiveEntry entry,
        ICollection<ExportDiagnostic> diagnostics)
    {
        using var relationshipStream = entry.Open();
        using var reader = XmlReader.Create(relationshipStream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        });
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element
                || !string.Equals(reader.LocalName, "Relationship", StringComparison.Ordinal))
                continue;
            if (!string.Equals(reader.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
                continue;
            diagnostics.Add(new(
                "EXPORT_TEMPLATE_UNSUPPORTED",
                "模板包含外部关系或外部链接。",
                entry.FullName));
        }
    }
}

public sealed class ExportHandlerException(
    string code,
    string message,
    bool retryable = false,
    IReadOnlyDictionary<string, object?>? details = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public IReadOnlyDictionary<string, object?>? Details { get; } = details;
}
