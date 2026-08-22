using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.Core.Data.Base;

namespace Diary.App.Models;

public sealed class ExtraFieldGroupViewModel
{
    public int TagId { get; }
    public string TagName { get; }
    public ObservableCollection<EditableWorkItemExtraField> Fields { get; } = new();

    public ExtraFieldGroupViewModel(int tagId, string tagName)
    {
        TagId = tagId;
        TagName = tagName;
    }
}

public partial class EditableWorkItemExtraField : ObservableObject
{
    private const NumberStyles DecimalStyles = NumberStyles.AllowLeadingWhite
        | NumberStyles.AllowTrailingWhite
        | NumberStyles.AllowLeadingSign
        | NumberStyles.AllowDecimalPoint;

    public string FieldId { get; }
    public string FieldKey { get; }
    public string Label { get; }
    public TagExtraFieldType Type { get; }
    public IReadOnlyList<string> Options { get; }
    public string Description { get; }
    public bool Enabled { get; }
    public bool IsDisabled => !Enabled;
    public bool IsReadOnly { get; }
    public bool UsesTextEditor => Type == TagExtraFieldType.Text;
    public bool UsesMultilineTextEditor => Type == TagExtraFieldType.MultilineText;
    public bool UsesIntegerEditor => Type == TagExtraFieldType.Integer;
    public bool UsesDecimalEditor => Type == TagExtraFieldType.Decimal;
    public bool UsesBooleanEditor => Type == TagExtraFieldType.Boolean;
    public bool UsesDateEditor => Type == TagExtraFieldType.Date;
    public bool UsesTimeEditor => Type == TagExtraFieldType.Time;
    public bool UsesDateTimeEditor => Type == TagExtraFieldType.DateTime;
    public bool UsesChoiceEditor => Type == TagExtraFieldType.Choice;

    [ObservableProperty]
    private string _value;

    public decimal? NumericValue
    {
        get => TryParseDecimal(Value, out var parsed) ? parsed : null;
        set => Value = value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    public bool? BooleanValue
    {
        get => bool.TryParse(Value, out var parsed) ? parsed : null;
        set => Value = value?.ToString() ?? string.Empty;
    }

    public string BooleanDisplay => BooleanValue switch
    {
        true => "是",
        false => "否",
        null => "未设置",
    };

    public DateTime? DateValue
    {
        get => DateTime.TryParseExact(
            Value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
                ? parsed
                : null;
        set => Value = value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    public TimeSpan? TimeValue
    {
        get => DateTime.TryParseExact(
            Value,
            "HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
                ? parsed.TimeOfDay
                : null;
        set => Value = value?.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    public DateTime? DateTimeDateValue
    {
        get => ParseDateTimeValue()?.Date;
        set
        {
            if (value is null)
            {
                Value = string.Empty;
                return;
            }
            var current = ParseDateTimeValue();
            SetDateTimeValue(value.Value.Date, current?.TimeOfDay ?? TimeSpan.Zero, current?.Offset);
        }
    }

    public TimeSpan? DateTimeTimeValue
    {
        get => ParseDateTimeValue()?.TimeOfDay;
        set
        {
            if (value is null)
            {
                Value = string.Empty;
                return;
            }
            var current = ParseDateTimeValue();
            SetDateTimeValue(current?.Date ?? DateTime.Today, value.Value, current?.Offset);
        }
    }

    public string? SelectedChoice
    {
        get => Options.Contains(Value, StringComparer.Ordinal) ? Value : null;
        set => Value = value ?? string.Empty;
    }

    public EditableWorkItemExtraField(WorkItemExtraField field, bool isReadOnly = false)
    {
        FieldId = field.FieldId;
        FieldKey = field.FieldKey;
        Label = field.Label;
        Type = field.Type;
        Options = field.Options;
        Description = field.Description;
        Enabled = field.Enabled;
        IsReadOnly = isReadOnly || !field.Enabled;
        _value = field.Value;
    }

    partial void OnValueChanged(string value)
    {
        OnPropertyChanged(nameof(NumericValue));
        OnPropertyChanged(nameof(BooleanValue));
        OnPropertyChanged(nameof(BooleanDisplay));
        OnPropertyChanged(nameof(DateValue));
        OnPropertyChanged(nameof(TimeValue));
        OnPropertyChanged(nameof(DateTimeDateValue));
        OnPropertyChanged(nameof(DateTimeTimeValue));
        OnPropertyChanged(nameof(SelectedChoice));
    }

    private bool CanEdit => !IsReadOnly;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void ClearValue() => Value = string.Empty;

    private static bool TryParseDecimal(string value, out decimal parsed) =>
        decimal.TryParse(value, DecimalStyles, CultureInfo.InvariantCulture, out parsed)
        || decimal.TryParse(value, DecimalStyles, CultureInfo.CurrentCulture, out parsed);

    private DateTimeOffset? ParseDateTimeValue() =>
        DateTimeOffset.TryParse(
            Value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
                ? parsed
                : null;

    private void SetDateTimeValue(DateTime date, TimeSpan time, TimeSpan? preservedOffset)
    {
        var localDateTime = DateTime.SpecifyKind(date.Date + time, DateTimeKind.Unspecified);
        var offset = preservedOffset ?? TimeZoneInfo.Local.GetUtcOffset(localDateTime);
        Value = new DateTimeOffset(localDateTime, offset).ToString("O", CultureInfo.InvariantCulture);
    }
}
