using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.Script.Runtime;
using Diary.ScriptBase;

namespace Diary.App.Models;

public enum ScriptParameterFormMode
{
    Run = 1,
    MetadataDefaults = 2,
}

public enum ScriptParameterValueSource
{
    Unset = 0,
    DescriptorDefault = 1,
    MetadataOverride = 2,
    LastRun = 3,
    RunInput = 4,
    Cleared = 5,
}

public sealed record ScriptDateTimeOffsetOption(TimeSpan Offset, string Label);

public partial class ScriptParameterFieldViewModel : ObservableObject
{
    private readonly ScriptParameterDefinition _definition;
    private bool _updating;
    private DateTime? _dateTimeDate;
    private TimeSpan? _dateTimeTime;
    private readonly string _resetValue;
    private readonly bool _resetIsSet;
    private readonly ScriptParameterValueSource _resetSource;
    private ScriptParameterValueSource _valueSource;
    private ScriptDateTimeOffsetOption? _selectedDateTimeOffsetOption;

    public string Name => _definition.Name;
    public string Label => _definition.Label;
    public string DisplayLabel => _definition.Required ? $"{_definition.Label} *" : _definition.Label;
    public string Description => _definition.Description ?? string.Empty;
    public string Placeholder => _definition.Placeholder ?? string.Empty;
    public string TypeLabel => $"{_definition.Name} · {_definition.Type} · {ValueSourceLabel}";
    public ScriptParameterValueSource ValueSource
    {
        get => _valueSource;
        private set
        {
            if (!SetProperty(ref _valueSource, value))
                return;
            OnPropertyChanged(nameof(ValueSourceLabel));
            OnPropertyChanged(nameof(TypeLabel));
        }
    }
    public string ValueSourceLabel => ValueSource switch
    {
        ScriptParameterValueSource.DescriptorDefault => "脚本默认",
        ScriptParameterValueSource.MetadataOverride => "配置覆盖",
        ScriptParameterValueSource.LastRun => "上次值",
        ScriptParameterValueSource.RunInput => "本次输入",
        ScriptParameterValueSource.Cleared => "已清空",
        _ => "未设置",
    };
    public bool HasChanged => IsSet != _resetIsSet
        || !string.Equals(Value, _resetValue, StringComparison.Ordinal);
    public string Unit => _definition.Constraints?.Unit ?? string.Empty;
    public bool HasUnit => Unit.Length > 0;
    public bool HasDescription => Description.Length > 0;
    public bool HasError => Error.Length > 0;
    public bool UsesTextEditor => _definition.Type == ScriptParameterType.String;
    public bool UsesMultilineEditor => _definition.Type == ScriptParameterType.MultilineString;
    public bool UsesIntegerEditor => _definition.Type == ScriptParameterType.Integer;
    public bool UsesNumberEditor => _definition.Type == ScriptParameterType.Number;
    public bool UsesBooleanEditor => _definition.Type == ScriptParameterType.Boolean;
    public bool UsesDateEditor => _definition.Type == ScriptParameterType.Date;
    public bool UsesDateTimeEditor => _definition.Type == ScriptParameterType.DateTime;
    public bool UsesChoiceEditor => _definition.Type == ScriptParameterType.Choice;
    public bool HasSuggestions => UsesTextEditor && Suggestions.Count > 0;
    public bool HasLengthLimit => _definition.Constraints?.MaxLength is not null;
    public int CurrentLength => Value.EnumerateRunes().Count();
    public bool ShowLengthSummary => _definition.Constraints?.MaxLength is { } maximum
        && (CurrentLength > maximum || maximum - CurrentLength <= 10);
    public string LengthSummary => _definition.Constraints?.MaxLength is { } maximum
        ? $"{CurrentLength} / {maximum}"
        : string.Empty;
    public IReadOnlyList<ScriptParameterChoice> Choices => _definition.Choices ?? [];
    public IReadOnlyList<ScriptParameterChoice> Suggestions => _definition.Constraints?.Suggestions ?? [];
    public IReadOnlyList<string> BooleanOptions { get; } = ["未设置", "是", "否"];
    public decimal Minimum => ParseDecimal(_definition.Constraints?.Minimum) ?? decimal.MinValue;
    public decimal Maximum => ParseDecimal(_definition.Constraints?.Maximum) ?? decimal.MaxValue;
    public decimal Increment => ParseDecimal(_definition.Constraints?.Step)
        ?? (UsesIntegerEditor ? 1m : 0.1m);
    public DateTime? DateMinimum => ParseDate(_definition.Constraints?.Minimum);
    public DateTime? DateMaximum => ParseDate(_definition.Constraints?.Maximum);
    public DateTime? DateTimeMinimumDate => ParseDateTime(_definition.Constraints?.Minimum)?.Date;
    public DateTime? DateTimeMaximumDate => ParseDateTime(_definition.Constraints?.Maximum)?.Date;
    public string DateTimeZoneSummary
    {
        get
        {
            if (_dateTimeDate is null || _dateTimeTime is null)
                return string.Empty;
            var local = DateTime.SpecifyKind(_dateTimeDate.Value.Date + _dateTimeTime.Value, DateTimeKind.Unspecified);
            if (TimeZoneInfo.Local.IsInvalidTime(local))
                return "本地时间不存在";
            if (TimeZoneInfo.Local.IsAmbiguousTime(local))
                return "夏令时重复时段";
            return $"UTC{FormatOffset(ResolveOffset(local))}";
        }
    }
    public bool HasDateTimeZoneSummary => DateTimeZoneSummary.Length > 0;
    public IReadOnlyList<ScriptDateTimeOffsetOption> AmbiguousOffsetOptions { get; private set; } = [];
    public bool HasAmbiguousOffsets => AmbiguousOffsetOptions.Count > 1;
    public ScriptDateTimeOffsetOption? SelectedDateTimeOffsetOption
    {
        get => _selectedDateTimeOffsetOption;
        set
        {
            if (!SetProperty(ref _selectedDateTimeOffsetOption, value) || _updating)
                return;
            UpdateDateTimeValue();
        }
    }

    [ObservableProperty] private string _value = string.Empty;
    [ObservableProperty] private bool _isSet;
    [ObservableProperty] private string _error = string.Empty;
    [ObservableProperty] private ScriptParameterChoice? _selectedSuggestion;

    public decimal? NumericValue
    {
        get => ParseDecimal(Value);
        set => SetValue(value?.ToString(CultureInfo.InvariantCulture), value is not null);
    }

    public string BooleanSelection
    {
        get => !IsSet ? "未设置" : string.Equals(Value, "true", StringComparison.OrdinalIgnoreCase) ? "是" : "否";
        set => SetValue(
            value switch
            {
                "是" => "true",
                "否" => "false",
                _ => string.Empty,
            },
            value != "未设置");
    }

    public DateTime? DateValue
    {
        get => DateTime.TryParseExact(Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
        set => SetValue(value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), value is not null);
    }

    public DateTime? DateTimeDateValue
    {
        get => _dateTimeDate;
        set
        {
            _dateTimeDate = value?.Date;
            OnPropertyChanged();
            UpdateDateTimeValue();
        }
    }

    public TimeSpan? DateTimeTimeValue
    {
        get => _dateTimeTime;
        set
        {
            _dateTimeTime = value;
            OnPropertyChanged();
            UpdateDateTimeValue();
        }
    }

    public ScriptParameterChoice? SelectedChoice
    {
        get => Choices.FirstOrDefault(choice => string.Equals(choice.Value, Value, StringComparison.Ordinal));
        set => SetValue(value?.Value, value is not null);
    }

    public ScriptParameterFieldViewModel(
        ScriptParameterDefinition definition,
        string? initialValue,
        bool isSet,
        ScriptParameterValueSource initialSource,
        string? resetValue,
        bool resetIsSet,
        ScriptParameterValueSource resetSource)
    {
        _definition = definition;
        _value = initialValue ?? string.Empty;
        _isSet = isSet;
        _valueSource = initialSource;
        _resetValue = resetValue ?? string.Empty;
        _resetIsSet = resetIsSet;
        _resetSource = resetSource;
        SynchronizeDateTimeParts();
    }

    partial void OnValueChanged(string value)
    {
        if (!_updating)
        {
            IsSet = true;
            ValueSource = ScriptParameterValueSource.RunInput;
        }
        Error = string.Empty;
        OnPropertyChanged(nameof(NumericValue));
        OnPropertyChanged(nameof(BooleanSelection));
        OnPropertyChanged(nameof(DateValue));
        OnPropertyChanged(nameof(CurrentLength));
        OnPropertyChanged(nameof(ShowLengthSummary));
        OnPropertyChanged(nameof(LengthSummary));
        OnPropertyChanged(nameof(HasChanged));
        ResetFieldCommand.NotifyCanExecuteChanged();
        if (!_updating)
            SynchronizeDateTimeParts();
        OnPropertyChanged(nameof(SelectedChoice));
    }

    partial void OnIsSetChanged(bool value)
    {
        OnPropertyChanged(nameof(BooleanSelection));
        OnPropertyChanged(nameof(SelectedChoice));
        OnPropertyChanged(nameof(HasChanged));
        ResetFieldCommand.NotifyCanExecuteChanged();
    }

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnSelectedSuggestionChanged(ScriptParameterChoice? value)
    {
        if (value is not null)
            Value = value.Value;
    }

    public KeyValuePair<string, string>? BuildArgument() =>
        IsSet ? KeyValuePair.Create(Name, Value) : null;

    public void SetValidationError(string message) => Error = message;

    public bool TryGetUiValidationError(out string message)
    {
        message = string.Empty;
        if (!UsesDateTimeEditor)
            return false;
        if (_dateTimeDate is null != (_dateTimeTime is null))
        {
            message = "请同时选择日期和时间。";
            return true;
        }
        if (_dateTimeDate is null || _dateTimeTime is null)
            return false;
        var local = DateTime.SpecifyKind(_dateTimeDate.Value.Date + _dateTimeTime.Value, DateTimeKind.Unspecified);
        if (!TimeZoneInfo.Local.IsInvalidTime(local))
            return false;
        message = "所选本地时间位于夏令时跳空区间，请选择其他时间。";
        return true;
    }

    public string ToUserMessage(ScriptParameterBindingIssue issue) => issue.Code switch
    {
        "SCRIPT_ARGUMENT_REQUIRED" => "此参数为必填项。",
        "SCRIPT_ARGUMENT_CHOICE_INVALID" => "请选择列表中的有效值。",
        "SCRIPT_ARGUMENT_RANGE_INVALID" => FormatRangeMessage(),
        "SCRIPT_ARGUMENT_STEP_INVALID" => $"请输入符合步长 {_definition.Constraints?.Step} 的值。",
        "SCRIPT_ARGUMENT_LENGTH_INVALID" => FormatLengthMessage(),
        _ => "输入值格式无效。",
    };

    public void ResetToDefault()
    {
        _updating = true;
        Value = _resetValue;
        IsSet = _resetIsSet;
        ValueSource = _resetSource;
        SynchronizeDateTimeParts();
        SelectedSuggestion = null;
        Error = string.Empty;
        _updating = false;
        OnPropertyChanged(nameof(HasChanged));
        ResetFieldCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(HasChanged))]
    private void ResetField() => ResetToDefault();

    [RelayCommand]
    private void Clear()
    {
        _updating = true;
        Value = string.Empty;
        IsSet = UsesTextEditor || UsesMultilineEditor;
        ValueSource = ScriptParameterValueSource.Cleared;
        SelectedSuggestion = null;
        Error = string.Empty;
        _updating = false;
        OnPropertyChanged(nameof(BooleanSelection));
        OnPropertyChanged(nameof(SelectedChoice));
        OnPropertyChanged(nameof(CurrentLength));
        OnPropertyChanged(nameof(ShowLengthSummary));
        OnPropertyChanged(nameof(LengthSummary));
        OnPropertyChanged(nameof(HasChanged));
        ResetFieldCommand.NotifyCanExecuteChanged();
    }

    private DateTimeOffset? ParseDateTime() =>
        DateTimeOffset.TryParse(Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private void UpdateDateTimeValue()
    {
        if (_dateTimeDate is null || _dateTimeTime is null)
        {
            SetValue(null, false);
            return;
        }
        var local = DateTime.SpecifyKind(_dateTimeDate.Value.Date + _dateTimeTime.Value, DateTimeKind.Unspecified);
        RefreshDateTimeOffsetOptions(local);
        var offset = ResolveOffset(local);
        SetValue(new DateTimeOffset(local, offset).ToString("O", CultureInfo.InvariantCulture), true);
        OnPropertyChanged(nameof(DateTimeZoneSummary));
        OnPropertyChanged(nameof(HasDateTimeZoneSummary));
    }

    private void SynchronizeDateTimeParts()
    {
        var parsed = ParseDateTime();
        _dateTimeDate = parsed?.Date;
        _dateTimeTime = parsed?.TimeOfDay;
        if (_dateTimeDate is { } date && _dateTimeTime is { } time)
            RefreshDateTimeOffsetOptions(DateTime.SpecifyKind(date.Date + time, DateTimeKind.Unspecified));
        else
            SetAmbiguousOffsetOptions([]);
        OnPropertyChanged(nameof(DateTimeDateValue));
        OnPropertyChanged(nameof(DateTimeTimeValue));
        OnPropertyChanged(nameof(DateTimeZoneSummary));
        OnPropertyChanged(nameof(HasDateTimeZoneSummary));
    }

    private void SetValue(string? value, bool isSet)
    {
        var userInitiated = !_updating;
        _updating = true;
        Value = value ?? string.Empty;
        IsSet = isSet;
        _updating = false;
        if (userInitiated)
            ValueSource = ScriptParameterValueSource.RunInput;
    }

    private static decimal? ParseDecimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    private static DateTimeOffset? ParseDateTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private TimeSpan ResolveOffset(DateTime local)
    {
        if (TimeZoneInfo.Local.IsAmbiguousTime(local))
        {
            var offsets = TimeZoneInfo.Local.GetAmbiguousTimeOffsets(local);
            if (SelectedDateTimeOffsetOption is { } selected && offsets.Contains(selected.Offset))
                return selected.Offset;
            var existing = ParseDateTime()?.Offset;
            if (existing is { } current && offsets.Contains(current))
                return current;
            return offsets.Min();
        }
        return TimeZoneInfo.Local.GetUtcOffset(local);
    }

    private void RefreshDateTimeOffsetOptions(DateTime local)
    {
        if (!TimeZoneInfo.Local.IsAmbiguousTime(local))
        {
            SetAmbiguousOffsetOptions([]);
            return;
        }
        var offsets = TimeZoneInfo.Local.GetAmbiguousTimeOffsets(local).Order().ToArray();
        var options = offsets
            .Select(offset => new ScriptDateTimeOffsetOption(offset, $"UTC{FormatOffset(offset)}"))
            .ToArray();
        var preferred = SelectedDateTimeOffsetOption?.Offset ?? ParseDateTime()?.Offset ?? offsets[0];
        SetAmbiguousOffsetOptions(options, preferred);
    }

    private void SetAmbiguousOffsetOptions(
        IReadOnlyList<ScriptDateTimeOffsetOption> options,
        TimeSpan? preferred = null)
    {
        AmbiguousOffsetOptions = options;
        _selectedDateTimeOffsetOption = preferred is { } offset
            ? options.FirstOrDefault(option => option.Offset == offset) ?? options.FirstOrDefault()
            : null;
        OnPropertyChanged(nameof(AmbiguousOffsetOptions));
        OnPropertyChanged(nameof(HasAmbiguousOffsets));
        OnPropertyChanged(nameof(SelectedDateTimeOffsetOption));
    }

    private string FormatRangeMessage()
    {
        var minimum = _definition.Constraints?.Minimum;
        var maximum = _definition.Constraints?.Maximum;
        return (minimum, maximum) switch
        {
            (not null, not null) => $"请输入 {minimum} 到 {maximum} 之间的值。",
            (not null, null) => $"请输入不小于 {minimum} 的值。",
            (null, not null) => $"请输入不大于 {maximum} 的值。",
            _ => "输入值超出允许范围。",
        };
    }

    private string FormatLengthMessage()
    {
        var minimum = _definition.Constraints?.MinLength;
        var maximum = _definition.Constraints?.MaxLength;
        return (minimum, maximum) switch
        {
            (not null, not null) => $"文本长度必须在 {minimum} 到 {maximum} 个字符之间。",
            (not null, null) => $"文本至少需要 {minimum} 个字符。",
            (null, not null) => $"文本最多允许 {maximum} 个字符。",
            _ => "文本长度不符合要求。",
        };
    }

    private static string FormatOffset(TimeSpan offset) =>
        $"{(offset < TimeSpan.Zero ? "-" : "+")}{offset.Duration():hh\\:mm}";
}

public partial class ScriptParameterFormViewModel : ObservableObject
{
    private readonly ScriptDescriptor _descriptor;
    private readonly IReadOnlyDictionary<string, string>? _metadataDefaults;
    private readonly ImmutableDictionary<string, string> _descriptorDefaults;
    private readonly ImmutableDictionary<string, string> _defaults;
    private readonly Func<ValueTask>? _clearRememberedArguments;

    public ObservableCollection<ScriptParameterFieldViewModel> Fields { get; } = new();
    public ScriptParameterFormMode Mode { get; }
    public bool HasFields => Fields.Count > 0;
    public bool CanClearMemory => RestoredLastArguments && _clearRememberedArguments is not null;
    public bool IsMetadataDefaults => Mode == ScriptParameterFormMode.MetadataDefaults;
    public string ResetLabel => IsMetadataDefaults ? "全部使用脚本默认" : "恢复默认值";

    [ObservableProperty] private bool _restoredLastArguments;
    public string RestoreStatus => RestoredLastArguments ? "已填入上次使用值" : string.Empty;

    public ScriptParameterFormViewModel(
        ScriptDescriptor descriptor,
        IReadOnlyDictionary<string, string>? metadataDefaults,
        IReadOnlyDictionary<string, string>? lastArguments = null,
        Func<ValueTask>? clearRememberedArguments = null,
        ScriptParameterFormMode mode = ScriptParameterFormMode.Run)
    {
        _descriptor = descriptor;
        _metadataDefaults = metadataDefaults;
        _clearRememberedArguments = clearRememberedArguments;
        Mode = mode;
        var descriptorBinding = ScriptParameterBinder.Bind(
            descriptor,
            null,
            null,
            requireRequired: false);
        _descriptorDefaults = descriptorBinding.Succeeded
            ? descriptorBinding.Arguments
            : ImmutableDictionary<string, string>.Empty;
        var defaultBinding = ScriptParameterBinder.Bind(
            descriptor,
            metadataDefaults,
            null,
            requireRequired: false);
        _defaults = defaultBinding.Succeeded ? defaultBinding.Arguments : ImmutableDictionary<string, string>.Empty;
        foreach (var definition in descriptor.Parameters ?? [])
        {
            string? lastValue = null;
            var hasLast = mode == ScriptParameterFormMode.Run
                && lastArguments?.TryGetValue(definition.Name, out lastValue) == true;
            var hasDefault = _defaults.TryGetValue(definition.Name, out var defaultValue);
            var hasDescriptorDefault = _descriptorDefaults.TryGetValue(definition.Name, out var descriptorValue);
            var hasMetadataOverride = metadataDefaults?.ContainsKey(definition.Name) == true;
            var defaultSource = hasMetadataOverride
                ? ScriptParameterValueSource.MetadataOverride
                : hasDescriptorDefault
                    ? ScriptParameterValueSource.DescriptorDefault
                    : ScriptParameterValueSource.Unset;
            var resetValue = IsMetadataDefaults ? descriptorValue : defaultValue;
            var resetIsSet = IsMetadataDefaults ? hasDescriptorDefault : hasDefault;
            var resetSource = IsMetadataDefaults
                ? hasDescriptorDefault
                    ? ScriptParameterValueSource.DescriptorDefault
                    : ScriptParameterValueSource.Unset
                : defaultSource;
            Fields.Add(new ScriptParameterFieldViewModel(
                definition,
                hasLast ? lastValue : defaultValue,
                hasLast || hasDefault,
                hasLast ? ScriptParameterValueSource.LastRun : defaultSource,
                resetValue,
                resetIsSet,
                resetSource));
            RestoredLastArguments |= hasLast;
        }
    }

    partial void OnRestoredLastArgumentsChanged(bool value)
    {
        OnPropertyChanged(nameof(RestoreStatus));
        OnPropertyChanged(nameof(CanClearMemory));
        ClearMemoryCommand.NotifyCanExecuteChanged();
    }

    public ScriptParameterBindingResult ValidateAndBuild()
        => ValidateAndBuild(requireRequired: true);

    public ScriptParameterBindingResult ValidateAndBuild(bool requireRequired)
    {
        foreach (var field in Fields)
            field.SetValidationError(string.Empty);
        var uiIssues = Fields
            .Select(field => (Field: field, HasError: field.TryGetUiValidationError(out var message), Message: message))
            .Where(item => item.HasError)
            .ToArray();
        if (uiIssues.Length > 0)
        {
            foreach (var issue in uiIssues)
                issue.Field.SetValidationError(issue.Message);
            return ScriptParameterBindingResult.Failure(
                uiIssues.Select(issue => new ScriptDiagnostic(
                    "SCRIPT_ARGUMENT_DATETIME_LOCAL_INVALID",
                    issue.Message,
                    ScriptDiagnosticSeverity.Error,
                    ScriptDiagnosticCategory.Validation)),
                uiIssues.Select(issue => new ScriptParameterBindingIssue(
                    "SCRIPT_ARGUMENT_DATETIME_LOCAL_INVALID",
                    issue.Message,
                    issue.Field.Name)));
        }
        var supplied = Fields
            .Select(field => field.BuildArgument())
            .OfType<KeyValuePair<string, string>>()
            .ToDictionary(argument => argument.Key, argument => argument.Value, StringComparer.Ordinal);
        var metadataDefaults = IsMetadataDefaults ? null : _metadataDefaults;
        var result = ScriptParameterBinder.Bind(
            _descriptor,
            metadataDefaults,
            supplied,
            requireRequired: requireRequired);
        foreach (var issue in result.Issues)
        {
            var field = Fields.FirstOrDefault(item => string.Equals(item.Name, issue.ParameterName, StringComparison.Ordinal));
            field?.SetValidationError(field.ToUserMessage(issue));
        }
        return result;
    }

    public bool TryBuildMetadataOverrides(
        bool requireRequired,
        out ImmutableDictionary<string, string> overrides,
        out string error)
    {
        var result = ValidateAndBuild(requireRequired);
        if (!result.Succeeded)
        {
            overrides = ImmutableDictionary<string, string>.Empty;
            error = result.Issues.Any(issue => issue.ParameterName is not null)
                ? "请修正默认参数表单中的错误。"
                : result.Diagnostics.FirstOrDefault()?.Message ?? "默认参数校验失败。";
            return false;
        }

        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var pair in result.Arguments)
        {
            if (!_descriptorDefaults.TryGetValue(pair.Key, out var descriptorValue)
                || !string.Equals(pair.Value, descriptorValue, StringComparison.Ordinal))
            {
                builder[pair.Key] = pair.Value;
            }
        }
        overrides = builder.ToImmutable();
        error = string.Empty;
        return true;
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        foreach (var field in Fields)
            field.ResetToDefault();
        RestoredLastArguments = false;
    }

    [RelayCommand(CanExecute = nameof(CanClearMemory))]
    private async Task ClearMemory()
    {
        if (_clearRememberedArguments is null)
            return;
        await _clearRememberedArguments();
        ResetDefaults();
    }

}
