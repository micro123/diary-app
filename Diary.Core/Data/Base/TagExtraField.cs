namespace Diary.Core.Data.Base;

public enum TagExtraFieldType
{
    Text,
    MultilineText,
    Integer,
    Decimal,
    Boolean,
    Date,
    Time,
    DateTime,
    Choice,
}

public sealed record TagExtraFieldDefinition
{
    public string FieldId { get; set; } = Guid.NewGuid().ToString("D");
    public string FieldKey { get; set; } = string.Empty;
    public int TagId { get; set; }
    public string Label { get; set; } = string.Empty;
    public TagExtraFieldType Type { get; set; } = TagExtraFieldType.Text;
    public string Description { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public IReadOnlyList<string> Options { get; set; } = Array.Empty<string>();
    public string DefaultValue { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

public sealed record WorkItemExtraFieldValue
{
    public int WorkItemId { get; set; }
    public string FieldId { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public sealed record WorkItemExtraField
{
    public string FieldId { get; init; } = string.Empty;
    public string FieldKey { get; init; } = string.Empty;
    public int TagId { get; init; }
    public string TagName { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public TagExtraFieldType Type { get; init; }
    public string Description { get; init; } = string.Empty;
    public int SortOrder { get; init; }
    public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();
    public string DefaultValue { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public string Value { get; init; } = string.Empty;
}

public static class TagExtraFieldKeyRules
{
    public static bool IsValid(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;
        var trimmed = key.Trim();
        if (trimmed.Length > 128)
            return false;
        foreach (var ch in trimmed)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-'))
                return false;
        }
        return true;
    }

    public static string Normalize(string key) => key.Trim().ToLowerInvariant();
}

public static class TagExtraFieldValueValidator
{
    public static bool TryValidate(
        TagExtraFieldType type,
        string value,
        IReadOnlyCollection<string> options,
        out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var valid = type switch
        {
            TagExtraFieldType.Text or TagExtraFieldType.MultilineText => true,
            TagExtraFieldType.Integer => int.TryParse(value, out _),
            TagExtraFieldType.Decimal => decimal.TryParse(value, out _),
            TagExtraFieldType.Boolean => bool.TryParse(value, out _),
            TagExtraFieldType.Date => DateTime.TryParseExact(
                value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _),
            TagExtraFieldType.Time => DateTime.TryParseExact(
                value, "HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _),
            TagExtraFieldType.DateTime => DateTimeOffset.TryParse(
                value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out _),
            TagExtraFieldType.Choice => options.Contains(value, StringComparer.Ordinal),
            _ => false,
        };
        if (!valid)
            error = type == TagExtraFieldType.Choice
                ? "必须选择已配置的选项。"
                : $"值不符合字段类型“{type}”。";
        return valid;
    }
}
