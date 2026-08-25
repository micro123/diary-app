using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using Diary.Core.Data.Base;
using Diary.Database;
using Diary.PluginUI;
using Diary.Utils;

namespace Diary.App.Services;

public sealed class TagSharePackageDocument
{
    public string Format { get; set; } = TagSharePackageService.FormatId;
    public int Version { get; set; } = TagSharePackageService.FormatVersion;
    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.Now;
    public List<TagSharePackageTag> Tags { get; set; } = [];
    public List<TagSharePackageTracker> Trackers { get; set; } = [];
}

public sealed class TagSharePackageTag
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Color { get; set; }
    public TagLevels Level { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
    public List<TagSharePackageExtraField> ExtraFields { get; set; } = [];
}

public sealed class TagSharePackageExtraField
{
    public string FieldKey { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public TagExtraFieldType Type { get; set; }
    public string Description { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public List<string> Options { get; set; } = [];
    public string DefaultValue { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

public sealed class TagSharePackageTracker
{
    public string Key { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<TrackerTagRulePackageItem> Rules { get; set; } = [];
}

public sealed record TagShareImportPreviewItem(
    string Key,
    string Name,
    bool Exists,
    bool HasConflict,
    string Status);

public sealed record TagSharePackagePreview(
    TagSharePackageDocument Package,
    IReadOnlyList<TagShareImportPreviewItem> Items);

public sealed record TagShareTrackerImportResult(
    string TrackerType,
    string TrackerName,
    int Imported,
    int Invalid,
    int Unavailable,
    int Skipped,
    IReadOnlyList<string> Messages);

public sealed record TagShareImportResult(
    int Created,
    int Updated,
    int Enabled,
    IReadOnlyDictionary<string, int> TagIds,
    IReadOnlyList<TagShareTrackerImportResult> Trackers,
    IReadOnlySet<string> ChangedPluginIds);

[DiAutoRegister(singleton: true)]
public sealed class TagSharePackageService
{
    public const string FileExtension = ".diarytags";
    public const string FormatId = "diary-tags";
    public const int FormatVersion = 1;
    private const long MaximumPackageBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public Task ExportAsync(
        string path,
        DbInterfaceBase database,
        IReadOnlyCollection<ITagRuleEditorContribution> contributions,
        CancellationToken cancellationToken = default)
        => ExportAsync(
            path,
            database,
            database.AllWorkTags().Select(tag => tag.Id).ToHashSet(),
            contributions,
            cancellationToken);

    public async Task ExportAsync(
        string path,
        DbInterfaceBase database,
        IReadOnlySet<int> selectedTagIds,
        IReadOnlyCollection<ITagRuleEditorContribution> contributions,
        CancellationToken cancellationToken = default)
    {
        if (selectedTagIds.Count == 0)
            throw new InvalidOperationException("至少选择一个要导出的标签。");
        var tags = database.AllWorkTags()
            .Where(tag => selectedTagIds.Contains(tag.Id))
            .OrderBy(tag => tag.Level)
            .ThenBy(tag => tag.Name)
            .ToArray();
        if (tags.Length != selectedTagIds.Count)
            throw new InvalidOperationException("部分所选标签已不存在，请重新打开导出选择窗口。");
        var tagKeys = tags.Select((tag, index) => (tag.Id, Key: $"tag-{index + 1}"))
            .ToDictionary(item => item.Id, item => item.Key);
        var document = new TagSharePackageDocument
        {
            Tags = tags.Select(tag => new TagSharePackageTag
            {
                Key = tagKeys[tag.Id],
                Name = tag.Name,
                Color = tag.Color,
                Level = tag.Level,
                Metadata = new Dictionary<string, string>(tag.Metadata, StringComparer.Ordinal),
                ExtraFields = database.GetTagExtraFieldDefinitions(tag.Id, includeDisabled: true)
                    .Select(field => new TagSharePackageExtraField
                    {
                        FieldKey = field.FieldKey,
                        Label = field.Label,
                        Type = field.Type,
                        Description = field.Description,
                        SortOrder = field.SortOrder,
                        Options = field.Options.ToList(),
                        DefaultValue = field.DefaultValue,
                        Enabled = field.Enabled,
                    })
                    .ToList(),
            }).ToList(),
        };
        var trackerIndex = 0;
        foreach (var contribution in contributions)
        {
            var selectedTagKeys = tagKeys.Values.ToHashSet(StringComparer.Ordinal);
            var rules = contribution.ExportRules(tagKeys)
                .Where(rule => selectedTagKeys.Contains(rule.TagKey)).ToArray();
            if (rules.Length == 0)
                continue;
            document.Trackers.Add(new TagSharePackageTracker
            {
                Key = $"tracker-{++trackerIndex}",
                Type = contribution.PluginId,
                Name = contribution.InstanceName,
                Rules = rules.ToList(),
            });
        }

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("无法确定标签包输出目录。"));
        var temporaryPath = fullPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public async Task<TagSharePackagePreview> PreviewImportAsync(
        string path,
        DbInterfaceBase database,
        CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
            throw new FileNotFoundException("标签包不存在。", path);
        if (file.Length <= 0 || file.Length > MaximumPackageBytes)
            throw new InvalidDataException("标签包为空或超过 4MB 限制。");
        await using var stream = file.OpenRead();
        var package = await JsonSerializer.DeserializeAsync<TagSharePackageDocument>(
            stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("标签包内容为空。");
        ValidatePackage(package);

        var existingTags = database.AllWorkTags().ToArray();
        var existingFields = database.GetAllTagExtraFieldDefinitions(includeDisabled: true)
            .ToDictionary(field => field.FieldKey, StringComparer.OrdinalIgnoreCase);
        var items = new List<TagShareImportPreviewItem>();
        foreach (var tag in package.Tags)
        {
            var exact = existingTags.FirstOrDefault(item =>
                string.Equals(item.Name, tag.Name, StringComparison.Ordinal));
            var caseOnly = exact is null && existingTags.Any(item =>
                string.Equals(item.Name, tag.Name, StringComparison.OrdinalIgnoreCase));
            var fieldConflict = tag.ExtraFields.FirstOrDefault(field =>
                existingFields.TryGetValue(field.FieldKey, out var existing)
                && (exact is null || existing.TagId != exact.Id || existing.Type != field.Type));
            var hasConflict = caseOnly || fieldConflict is not null;
            var status = caseOnly
                ? "存在仅大小写不同的本地标签，不能自动合并。"
                : fieldConflict is not null
                    ? $"字段 {fieldConflict.FieldKey} 已被其他标签使用或类型不同。"
                    : exact is null
                        ? "将新增并默认启用。"
                        : exact.Disabled
                            ? "将更新并重新启用本地标签。"
                            : "将更新本地标签。";
            items.Add(new TagShareImportPreviewItem(tag.Key, tag.Name, exact is not null, hasConflict, status));
        }
        return new TagSharePackagePreview(package, items);
    }

    public TagShareImportResult Import(
        TagSharePackagePreview preview,
        DbInterfaceBase database,
        IReadOnlySet<string> selectedTagKeys,
        IReadOnlyDictionary<string, ITagRuleEditorContribution> trackerMappings)
    {
        var selectedItems = preview.Items.Where(item => selectedTagKeys.Contains(item.Key)).ToArray();
        var conflict = selectedItems.FirstOrDefault(item => item.HasConflict);
        if (conflict is not null)
            throw new InvalidDataException($"标签“{conflict.Name}”存在未解决冲突。");

        var packageTags = preview.Package.Tags
            .Where(tag => selectedTagKeys.Contains(tag.Key))
            .ToArray();
        var existingTags = database.AllWorkTags().ToDictionary(tag => tag.Name, StringComparer.Ordinal);
        var existingFields = database.GetAllTagExtraFieldDefinitions(includeDisabled: true)
            .ToDictionary(field => field.FieldKey, StringComparer.OrdinalIgnoreCase);
        var tagIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var created = 0;
        var updated = 0;
        var enabled = 0;

        if (!database.BeginTransaction())
            throw new InvalidOperationException("无法开始标签导入事务。");
        try
        {
            foreach (var source in packageTags)
            {
                if (!existingTags.TryGetValue(source.Name, out var target))
                {
                    target = database.CreateWorkTag(
                        source.Name,
                        source.Level == TagLevels.Primary,
                        source.Color,
                        source.Metadata);
                    if (target.Id <= 0)
                        throw new InvalidOperationException($"创建标签“{source.Name}”失败。");
                    existingTags.Add(target.Name, target);
                    created++;
                }
                else
                {
                    if (target.Disabled)
                        enabled++;
                    target.Color = source.Color;
                    target.Level = source.Level;
                    target.Disabled = false;
                    var mergedMetadata = new Dictionary<string, string>(target.Metadata, StringComparer.Ordinal);
                    foreach (var (key, value) in source.Metadata)
                        mergedMetadata[key] = value;
                    target.Metadata = mergedMetadata;
                    if (!database.UpdateWorkTag(target))
                        throw new InvalidOperationException($"更新标签“{source.Name}”失败。");
                    updated++;
                }
                tagIds[source.Key] = target.Id;

                foreach (var sourceField in source.ExtraFields)
                {
                    if (existingFields.TryGetValue(sourceField.FieldKey, out var targetField))
                    {
                        var updatedField = targetField with
                        {
                            Label = sourceField.Label,
                            Description = sourceField.Description,
                            SortOrder = sourceField.SortOrder,
                            Options = sourceField.Options,
                            DefaultValue = sourceField.DefaultValue,
                            Enabled = sourceField.Enabled,
                        };
                        if (!database.UpdateTagExtraFieldDefinition(updatedField))
                            throw new InvalidOperationException($"更新字段“{sourceField.FieldKey}”失败。");
                        existingFields[sourceField.FieldKey] = updatedField;
                    }
                    else
                    {
                        var newField = new TagExtraFieldDefinition
                        {
                            FieldId = Guid.NewGuid().ToString("D"),
                            FieldKey = sourceField.FieldKey,
                            TagId = target.Id,
                            Label = sourceField.Label,
                            Type = sourceField.Type,
                            Description = sourceField.Description,
                            SortOrder = sourceField.SortOrder,
                            Options = sourceField.Options,
                            DefaultValue = sourceField.DefaultValue,
                            Enabled = sourceField.Enabled,
                        };
                        if (!database.CreateTagExtraFieldDefinition(newField))
                            throw new InvalidOperationException($"创建字段“{sourceField.FieldKey}”失败。");
                        existingFields.Add(sourceField.FieldKey, newField);
                    }
                }
            }
            if (!database.CommitTransaction())
                throw new InvalidOperationException("提交标签导入事务失败。");
        }
        catch
        {
            database.RollbackTransaction();
            throw;
        }

        var trackerResults = new List<TagShareTrackerImportResult>();
        var changedPluginIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tracker in preview.Package.Trackers)
        {
            var selectedRules = tracker.Rules.Where(rule => selectedTagKeys.Contains(rule.TagKey)).ToArray();
            if (!trackerMappings.TryGetValue(tracker.Key, out var contribution))
            {
                trackerResults.Add(new TagShareTrackerImportResult(
                    tracker.Type, tracker.Name, 0, 0, 0, selectedRules.Length,
                    selectedRules.Length == 0 ? [] : ["未关联本地 Tracker，规则已跳过。"]));
                continue;
            }
            try
            {
                var validations = contribution.ValidateImportRules(selectedRules, tagIds);
                var validRules = validations.Where(item => item.State == TrackerTagRuleValidationState.Valid)
                    .Select(item => item.Rule)
                    .ToArray();
                var imported = contribution.ImportRules(validRules, tagIds);
                if (imported > 0)
                {
                    contribution.Commit();
                    changedPluginIds.Add(contribution.PluginId);
                }
                trackerResults.Add(new TagShareTrackerImportResult(
                    tracker.Type,
                    tracker.Name,
                    imported,
                    validations.Count(item => item.State == TrackerTagRuleValidationState.Invalid),
                    validations.Count(item => item.State == TrackerTagRuleValidationState.Unavailable),
                    validRules.Length - imported,
                    validations.Where(item => item.State != TrackerTagRuleValidationState.Valid)
                        .Select(item => item.Message)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()));
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException)
            {
                trackerResults.Add(new TagShareTrackerImportResult(
                    tracker.Type,
                    tracker.Name,
                    0,
                    0,
                    selectedRules.Length,
                    0,
                    [$"Tracker 规则校验或写入失败：{exception.Message}"]));
            }
        }

        return new TagShareImportResult(created, updated, enabled, tagIds, trackerResults, changedPluginIds);
    }

    private static void ValidatePackage(TagSharePackageDocument package)
    {
        if (!string.Equals(package.Format, FormatId, StringComparison.Ordinal)
            || package.Version != FormatVersion)
            throw new InvalidDataException("不支持的标签包格式或版本。");
        if (package.Tags is null || package.Tags.Count == 0)
            throw new InvalidDataException("标签包中没有标签。");
        package.Trackers ??= [];
        if (package.Tags.Any(tag => tag is null) || package.Trackers.Any(tracker => tracker is null))
            throw new InvalidDataException("标签包包含空标签或 Tracker 项。");
        if (package.Tags.Select(tag => tag.Key).Distinct(StringComparer.Ordinal).Count() != package.Tags.Count
            || package.Tags.Select(tag => tag.Name).Distinct(StringComparer.Ordinal).Count() != package.Tags.Count)
            throw new InvalidDataException("标签包包含重复的标签键或名称。");
        var tagKeys = package.Tags.Select(tag => tag.Key).ToHashSet(StringComparer.Ordinal);
        var fieldKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in package.Tags)
        {
            if (string.IsNullOrWhiteSpace(tag.Key) || string.IsNullOrWhiteSpace(tag.Name))
                throw new InvalidDataException("标签键和名称不能为空。");
            if (!Enum.IsDefined(tag.Level) || tag.Metadata is null || tag.ExtraFields is null)
                throw new InvalidDataException($"标签“{tag.Name}”包含无效属性。");
            if (tag.Metadata.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value is null)
                || tag.ExtraFields.Any(field => field is null))
                throw new InvalidDataException($"标签“{tag.Name}”包含无效元数据或空字段。");
            foreach (var field in tag.ExtraFields)
            {
                if (!TagExtraFieldKeyRules.IsValid(field.FieldKey)
                    || string.IsNullOrWhiteSpace(field.Label)
                    || !Enum.IsDefined(field.Type)
                    || field.Options is null
                    || !fieldKeys.Add(field.FieldKey))
                    throw new InvalidDataException($"额外字段“{field.FieldKey}”无效或重复。");
                if (field.Options.Any(option => option is null))
                    throw new InvalidDataException($"额外字段“{field.FieldKey}”包含空选项。");
                if (field.Type == TagExtraFieldType.Choice && field.Options.Count == 0)
                    throw new InvalidDataException($"选项字段“{field.FieldKey}”没有配置选项。");
                if (!TagExtraFieldValueValidator.TryValidate(
                        field.Type,
                        field.DefaultValue?.Trim() ?? string.Empty,
                        field.Options,
                        out var defaultValueError))
                {
                    throw new InvalidDataException(
                        $"额外字段“{field.FieldKey}”的默认值无效：{defaultValueError}");
                }
                field.DefaultValue = field.DefaultValue?.Trim() ?? string.Empty;
            }
        }
        if (package.Trackers.Select(tracker => tracker.Key).Distinct(StringComparer.Ordinal).Count()
            != package.Trackers.Count)
            throw new InvalidDataException("标签包包含重复的 Tracker 键。");
        foreach (var tracker in package.Trackers)
        {
            if (string.IsNullOrWhiteSpace(tracker.Key)
                || string.IsNullOrWhiteSpace(tracker.Type)
                || string.IsNullOrWhiteSpace(tracker.Name))
                throw new InvalidDataException("Tracker 类型、名称和键不能为空。");
            tracker.Rules ??= [];
            if (tracker.Rules.Any(rule => rule is null
                || string.IsNullOrWhiteSpace(rule.TagKey)
                || rule.Values is null
                || rule.Values.Keys.Any(string.IsNullOrWhiteSpace)
                || !tagKeys.Contains(rule.TagKey)))
                throw new InvalidDataException($"Tracker“{tracker.Name}”包含无效标签引用。");
        }
    }
}
