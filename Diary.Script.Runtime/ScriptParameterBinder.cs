using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Diary.ScriptBase;

namespace Diary.Script.Runtime;

public sealed record ScriptParameterBindingIssue(
    string Code,
    string Message,
    string? ParameterName = null);

public sealed record ScriptParameterBindingResult(
    bool Succeeded,
    ImmutableDictionary<string, string> Arguments,
    ImmutableArray<ScriptDiagnostic> Diagnostics)
{
    public ImmutableArray<ScriptParameterBindingIssue> Issues { get; init; } = [];

    public static ScriptParameterBindingResult Success(ImmutableDictionary<string, string> arguments) =>
        new(true, arguments, []);

    public static ScriptParameterBindingResult Failure(IEnumerable<ScriptDiagnostic> diagnostics) =>
        new(false, ImmutableDictionary<string, string>.Empty, [.. diagnostics]);

    public static ScriptParameterBindingResult Failure(
        IEnumerable<ScriptDiagnostic> diagnostics,
        IEnumerable<ScriptParameterBindingIssue> issues) =>
        new(false, ImmutableDictionary<string, string>.Empty, [.. diagnostics])
        {
            Issues = [.. issues],
        };
}

public static partial class ScriptParameterBinder
{
    public const int MaxParameterCount = 32;
    public const int MaxNameLength = 64;
    public const int MaxLabelLength = 128;
    public const int MaxDescriptionLength = 1024;
    public const int MaxValueLength = 16 * 1024;
    public const int MaxTotalValueBytes = 64 * 1024;
    public const int MaxChoiceCount = 100;
    public const int MaxUnitLength = 32;

    public static ImmutableArray<ScriptDiagnostic> ValidateDescriptor(
        ScriptDescriptor descriptor,
        string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var parameters = descriptor.Parameters ?? [];
        var diagnostics = ImmutableArray.CreateBuilder<ScriptDiagnostic>();
        if (descriptor.ApiVersion == ScriptApiVersion.V1)
        {
            if (parameters.Count > 0)
                diagnostics.Add(Diagnostic(
                    "SCRIPT_PARAMETERS_REQUIRE_V2",
                    "Script parameter definitions require Script API V2.",
                    sourcePath));
            return diagnostics.ToImmutable();
        }

        if (descriptor.ApiVersion != ScriptApiVersion.V2)
            return diagnostics.ToImmutable();
        if (parameters.Count > MaxParameterCount)
        {
            diagnostics.Add(Diagnostic(
                "SCRIPT_PARAMETER_SCHEMA_INVALID",
                $"A script may declare at most {MaxParameterCount} parameters.",
                sourcePath));
            return diagnostics.ToImmutable();
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            if (parameter is null
                || string.IsNullOrWhiteSpace(parameter.Name)
                || parameter.Name.Length > MaxNameLength
                || !ParameterNamePattern().IsMatch(parameter.Name)
                || string.IsNullOrWhiteSpace(parameter.Label)
                || parameter.Label.Length > MaxLabelLength
                || parameter.Description?.Length > MaxDescriptionLength
                || parameter.Placeholder?.Length > MaxDescriptionLength
                || !Enum.IsDefined(parameter.Type))
            {
                diagnostics.Add(Diagnostic(
                    "SCRIPT_PARAMETER_SCHEMA_INVALID",
                    $"The script parameter schema is invalid for '{parameter?.Name ?? "<unknown>"}'.",
                    sourcePath));
                continue;
            }
            if (!names.Add(parameter.Name))
            {
                diagnostics.Add(Diagnostic(
                    "SCRIPT_PARAMETER_DUPLICATE",
                    $"The script parameter name '{parameter.Name}' is duplicated.",
                    sourcePath));
                continue;
            }

            var choices = parameter.Choices ?? [];
            if (parameter.Type == ScriptParameterType.Choice)
            {
                if (choices.Count is < 1 or > MaxChoiceCount
                    || choices.Any(choice => choice is null
                        || string.IsNullOrEmpty(choice.Value)
                        || choice.Value.Length > MaxValueLength
                        || string.IsNullOrWhiteSpace(choice.Label)
                        || choice.Label.Length > MaxLabelLength)
                    || choices.Select(choice => choice.Value).Distinct(StringComparer.Ordinal).Count() != choices.Count)
                {
                    diagnostics.Add(Diagnostic(
                        "SCRIPT_PARAMETER_SCHEMA_INVALID",
                        $"Choice parameter '{parameter.Name}' has invalid or duplicate choices.",
                        sourcePath));
                    continue;
                }
            }
            else if (choices.Count > 0)
            {
                diagnostics.Add(Diagnostic(
                    "SCRIPT_PARAMETER_SCHEMA_INVALID",
                    $"Only Choice parameters may declare choices ('{parameter.Name}').",
                    sourcePath));
                continue;
            }

            if (!TryValidateConstraints(parameter, out var constraintError))
            {
                diagnostics.Add(Diagnostic(
                    "SCRIPT_PARAMETER_CONSTRAINT_INVALID",
                    $"The constraints for parameter '{parameter.Name}' are invalid: {constraintError}",
                    sourcePath));
                continue;
            }

            if (parameter.DefaultValue is not null
                && !TryNormalize(parameter, parameter.DefaultValue, out _, out _, out var errorMessage))
            {
                diagnostics.Add(Diagnostic(
                    "SCRIPT_PARAMETER_DEFAULT_INVALID",
                    $"The default value for parameter '{parameter.Name}' is invalid: {errorMessage}",
                    sourcePath));
            }
        }
        return diagnostics.ToImmutable();
    }

    public static ScriptParameterBindingResult Bind(
        ScriptDescriptor descriptor,
        IReadOnlyDictionary<string, string>? metadataDefaults,
        IReadOnlyDictionary<string, string>? suppliedArguments,
        string? sourcePath = null,
        bool requireRequired = true)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var schemaDiagnostics = ValidateDescriptor(descriptor, sourcePath);
        if (!schemaDiagnostics.IsEmpty)
            return ScriptParameterBindingResult.Failure(schemaDiagnostics);

        if (descriptor.ApiVersion == ScriptApiVersion.V1)
        {
            var legacy = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            Merge(legacy, metadataDefaults);
            Merge(legacy, suppliedArguments);
            return ScriptParameterBindingResult.Success(legacy.ToImmutable());
        }

        var definitions = (descriptor.Parameters ?? []).ToDictionary(parameter => parameter.Name, StringComparer.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in definitions.Values)
        {
            if (parameter.DefaultValue is not null)
                values[parameter.Name] = parameter.DefaultValue;
        }

        var diagnostics = ImmutableArray.CreateBuilder<ScriptDiagnostic>();
        var issues = ImmutableArray.CreateBuilder<ScriptParameterBindingIssue>();
        ApplyInput(definitions, values, metadataDefaults, diagnostics, issues, sourcePath);
        ApplyInput(definitions, values, suppliedArguments, diagnostics, issues, sourcePath);
        if (diagnostics.Count > 0)
            return ScriptParameterBindingResult.Failure(diagnostics, issues);

        var normalized = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var parameter in definitions.Values)
        {
            if (!values.TryGetValue(parameter.Name, out var value)
                || IsOmitted(parameter, value))
            {
                if (parameter.Required && requireRequired)
                {
                    const string code = "SCRIPT_ARGUMENT_REQUIRED";
                    var message = $"Required script argument '{parameter.Name}' is missing.";
                    diagnostics.Add(Diagnostic(code, message, sourcePath));
                    issues.Add(new ScriptParameterBindingIssue(code, message, parameter.Name));
                }
                continue;
            }

            if (!TryNormalize(parameter, value, out var normalizedValue, out var errorCode, out var errorMessage))
            {
                var message = $"Script argument '{parameter.Name}' is invalid: {errorMessage}";
                diagnostics.Add(Diagnostic(errorCode, message, sourcePath));
                issues.Add(new ScriptParameterBindingIssue(errorCode, message, parameter.Name));
                continue;
            }
            normalized[parameter.Name] = normalizedValue;
        }

        if (diagnostics.Count > 0)
            return ScriptParameterBindingResult.Failure(diagnostics, issues);
        var totalBytes = normalized.Sum(pair => Encoding.UTF8.GetByteCount(pair.Key) + Encoding.UTF8.GetByteCount(pair.Value));
        if (totalBytes > MaxTotalValueBytes)
        {
            return ScriptParameterBindingResult.Failure([
                Diagnostic(
                    "SCRIPT_ARGUMENTS_TOO_LARGE",
                    $"Script arguments exceed the {MaxTotalValueBytes} byte limit.",
                    sourcePath),
            ]);
        }
        return ScriptParameterBindingResult.Success(normalized.ToImmutable());
    }

    private static void ApplyInput(
        IReadOnlyDictionary<string, ScriptParameterDefinition> definitions,
        IDictionary<string, string> values,
        IReadOnlyDictionary<string, string>? input,
        ImmutableArray<ScriptDiagnostic>.Builder diagnostics,
        ImmutableArray<ScriptParameterBindingIssue>.Builder issues,
        string? sourcePath)
    {
        if (input is null)
            return;
        foreach (var pair in input)
        {
            if (!definitions.ContainsKey(pair.Key))
            {
                const string code = "SCRIPT_ARGUMENT_UNKNOWN";
                var message = $"Unknown script argument '{pair.Key}'.";
                diagnostics.Add(Diagnostic(code, message, sourcePath));
                issues.Add(new ScriptParameterBindingIssue(code, message, pair.Key));
                continue;
            }
            if (pair.Value is null || pair.Value.Length > MaxValueLength)
            {
                const string code = "SCRIPT_ARGUMENT_TYPE_INVALID";
                var message = $"Script argument '{pair.Key}' exceeds the value length limit.";
                diagnostics.Add(Diagnostic(code, message, sourcePath));
                issues.Add(new ScriptParameterBindingIssue(code, message, pair.Key));
                continue;
            }
            values[pair.Key] = pair.Value;
        }
    }

    private static bool TryValidateConstraints(
        ScriptParameterDefinition parameter,
        out string errorMessage)
    {
        errorMessage = string.Empty;
        var constraints = parameter.Constraints;
        if (constraints is null)
            return true;

        var suggestions = constraints.Suggestions;
        if (suggestions is not null
            && (suggestions.Count is < 1 or > MaxChoiceCount
                || suggestions.Any(choice => choice is null
                    || string.IsNullOrEmpty(choice.Value)
                    || choice.Value.Length > MaxValueLength
                    || string.IsNullOrWhiteSpace(choice.Label)
                    || choice.Label.Length > MaxLabelLength)
                || suggestions.Select(choice => choice.Value)
                    .Distinct(StringComparer.Ordinal).Count() != suggestions.Count))
        {
            errorMessage = "Suggestions are empty, oversized, invalid, or contain duplicate values.";
            return false;
        }

        if (constraints.Unit is not null
            && (string.IsNullOrWhiteSpace(constraints.Unit) || constraints.Unit.Length > MaxUnitLength))
        {
            errorMessage = $"Unit must be non-empty and no longer than {MaxUnitLength} characters.";
            return false;
        }

        if (constraints.MinLength is < 0
            || constraints.MaxLength is < 0
            || constraints.MinLength is { } minLength
            && constraints.MaxLength is { } maxLength
            && minLength > maxLength)
        {
            errorMessage = "Text length constraints are invalid.";
            return false;
        }

        var hasRange = constraints.Minimum is not null || constraints.Maximum is not null;
        var hasLength = constraints.MinLength is not null || constraints.MaxLength is not null;
        switch (parameter.Type)
        {
            case ScriptParameterType.String:
                if (hasRange || constraints.Step is not null || constraints.Unit is not null)
                    return InvalidConstraintCombination("String", out errorMessage);
                return true;
            case ScriptParameterType.MultilineString:
                if (hasRange || constraints.Step is not null || suggestions is not null || constraints.Unit is not null)
                    return InvalidConstraintCombination("MultilineString", out errorMessage);
                return true;
            case ScriptParameterType.Integer:
                if (hasLength || suggestions is not null)
                    return InvalidConstraintCombination("Integer", out errorMessage);
                return TryValidateIntegerBounds(constraints, out errorMessage);
            case ScriptParameterType.Number:
                if (hasLength || suggestions is not null)
                    return InvalidConstraintCombination("Number", out errorMessage);
                return TryValidateNumberBounds(constraints, out errorMessage);
            case ScriptParameterType.Date:
                if (constraints.Step is not null || hasLength || suggestions is not null || constraints.Unit is not null)
                    return InvalidConstraintCombination("Date", out errorMessage);
                return TryValidateDateBounds(constraints, out errorMessage);
            case ScriptParameterType.DateTime:
                if (constraints.Step is not null || hasLength || suggestions is not null || constraints.Unit is not null)
                    return InvalidConstraintCombination("DateTime", out errorMessage);
                return TryValidateDateTimeBounds(constraints, out errorMessage);
            case ScriptParameterType.Boolean:
            case ScriptParameterType.Choice:
                if (hasRange || constraints.Step is not null || hasLength || suggestions is not null || constraints.Unit is not null)
                    return InvalidConstraintCombination(parameter.Type.ToString(), out errorMessage);
                return true;
            default:
                errorMessage = "The parameter type is not supported.";
                return false;
        }
    }

    private static bool InvalidConstraintCombination(string type, out string errorMessage)
    {
        errorMessage = $"One or more constraints are not supported by {type}.";
        return false;
    }

    private static bool TryValidateIntegerBounds(
        ScriptParameterConstraints constraints,
        out string errorMessage)
    {
        if (!TryParseOptionalLong(constraints.Minimum, out var minimum)
            || !TryParseOptionalLong(constraints.Maximum, out var maximum)
            || !TryParseOptionalLong(constraints.Step, out var step)
            || minimum is { } min && maximum is { } max && min > max
            || step is <= 0)
        {
            errorMessage = "Integer minimum, maximum, or step is invalid.";
            return false;
        }
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryValidateNumberBounds(
        ScriptParameterConstraints constraints,
        out string errorMessage)
    {
        if (!TryParseOptionalDecimal(constraints.Minimum, out var minimum)
            || !TryParseOptionalDecimal(constraints.Maximum, out var maximum)
            || !TryParseOptionalDecimal(constraints.Step, out var step)
            || minimum is { } min && maximum is { } max && min > max
            || step is <= 0)
        {
            errorMessage = "Number minimum, maximum, or step is invalid.";
            return false;
        }
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryValidateDateBounds(
        ScriptParameterConstraints constraints,
        out string errorMessage)
    {
        if (!TryParseOptionalDate(constraints.Minimum, out var minimum)
            || !TryParseOptionalDate(constraints.Maximum, out var maximum)
            || minimum is { } min && maximum is { } max && min > max)
        {
            errorMessage = "Date minimum or maximum is invalid.";
            return false;
        }
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryValidateDateTimeBounds(
        ScriptParameterConstraints constraints,
        out string errorMessage)
    {
        if (!TryParseOptionalDateTime(constraints.Minimum, out var minimum)
            || !TryParseOptionalDateTime(constraints.Maximum, out var maximum)
            || minimum is { } min && maximum is { } max && min > max)
        {
            errorMessage = "DateTime minimum or maximum is invalid.";
            return false;
        }
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryNormalize(
        ScriptParameterDefinition parameter,
        string value,
        out string normalized,
        out string errorCode,
        out string errorMessage)
    {
        normalized = string.Empty;
        errorCode = "SCRIPT_ARGUMENT_TYPE_INVALID";
        errorMessage = $"Expected {parameter.Type}.";
        if (value.Length > MaxValueLength)
        {
            errorMessage = $"The value exceeds {MaxValueLength} characters.";
            return false;
        }

        switch (parameter.Type)
        {
            case ScriptParameterType.String:
                if (value.Contains('\r') || value.Contains('\n'))
                {
                    errorMessage = "Single-line text cannot contain line breaks.";
                    return false;
                }
                if (parameter.Required && string.IsNullOrWhiteSpace(value))
                {
                    errorCode = "SCRIPT_ARGUMENT_REQUIRED";
                    errorMessage = "A non-empty text value is required.";
                    return false;
                }
                normalized = value;
                break;
            case ScriptParameterType.MultilineString:
                if (parameter.Required && string.IsNullOrWhiteSpace(value))
                {
                    errorCode = "SCRIPT_ARGUMENT_REQUIRED";
                    errorMessage = "A non-empty text value is required.";
                    return false;
                }
                normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
                break;
            case ScriptParameterType.Integer:
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                    return false;
                normalized = integer.ToString(CultureInfo.InvariantCulture);
                break;
            case ScriptParameterType.Number:
                if (!decimal.TryParse(
                        value,
                        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture,
                        out var number))
                    return false;
                normalized = number.ToString(CultureInfo.InvariantCulture);
                break;
            case ScriptParameterType.Boolean:
                if (!bool.TryParse(value, out var boolean))
                    return false;
                normalized = boolean ? "true" : "false";
                break;
            case ScriptParameterType.Date:
                if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    return false;
                normalized = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                break;
            case ScriptParameterType.DateTime:
                if (!DateTimeOffsetSuffixPattern().IsMatch(value)
                    || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dateTime))
                    return false;
                normalized = dateTime.ToString("O", CultureInfo.InvariantCulture);
                break;
            case ScriptParameterType.Choice:
                if (!(parameter.Choices ?? []).Any(choice => string.Equals(choice.Value, value, StringComparison.Ordinal)))
                {
                    errorCode = "SCRIPT_ARGUMENT_CHOICE_INVALID";
                    errorMessage = "The value is not one of the declared choices.";
                    return false;
                }
                normalized = value;
                break;
            default:
                return false;
        }

        return TryValidateValueConstraints(parameter, normalized, out errorCode, out errorMessage);
    }

    private static bool TryValidateValueConstraints(
        ScriptParameterDefinition parameter,
        string normalized,
        out string errorCode,
        out string errorMessage)
    {
        errorCode = string.Empty;
        errorMessage = string.Empty;
        var constraints = parameter.Constraints;
        if (constraints is null)
            return true;

        if (parameter.Type is ScriptParameterType.String or ScriptParameterType.MultilineString)
        {
            var length = normalized.EnumerateRunes().Count();
            if (constraints.MinLength is { } minimumLength && length < minimumLength
                || constraints.MaxLength is { } maximumLength && length > maximumLength)
            {
                errorCode = "SCRIPT_ARGUMENT_LENGTH_INVALID";
                errorMessage = constraints.MinLength is { } min && constraints.MaxLength is { } max
                    ? $"The text length must be between {min} and {max}."
                    : constraints.MinLength is { } minimum
                        ? $"The text length must be at least {minimum}."
                        : $"The text length must not exceed {constraints.MaxLength}.";
                return false;
            }
            return true;
        }

        switch (parameter.Type)
        {
            case ScriptParameterType.Integer:
                {
                    var value = long.Parse(normalized, CultureInfo.InvariantCulture);
                    TryParseOptionalLong(constraints.Minimum, out var minimum);
                    TryParseOptionalLong(constraints.Maximum, out var maximum);
                    TryParseOptionalLong(constraints.Step, out var step);
                    if (minimum is { } min && value < min || maximum is { } max && value > max)
                        return RangeFailure(constraints, out errorCode, out errorMessage);
                    if (step is { } increment && ((decimal)value - (minimum ?? 0)) % increment != 0)
                        return StepFailure(increment.ToString(CultureInfo.InvariantCulture), out errorCode, out errorMessage);
                    return true;
                }
            case ScriptParameterType.Number:
                {
                    var value = decimal.Parse(normalized, CultureInfo.InvariantCulture);
                    TryParseOptionalDecimal(constraints.Minimum, out var minimum);
                    TryParseOptionalDecimal(constraints.Maximum, out var maximum);
                    TryParseOptionalDecimal(constraints.Step, out var step);
                    if (minimum is { } min && value < min || maximum is { } max && value > max)
                        return RangeFailure(constraints, out errorCode, out errorMessage);
                    if (step is { } increment && (value - (minimum ?? 0)) % increment != 0)
                        return StepFailure(increment.ToString(CultureInfo.InvariantCulture), out errorCode, out errorMessage);
                    return true;
                }
            case ScriptParameterType.Date:
                {
                    var value = DateOnly.ParseExact(normalized, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    TryParseOptionalDate(constraints.Minimum, out var minimum);
                    TryParseOptionalDate(constraints.Maximum, out var maximum);
                    return minimum is { } min && value < min || maximum is { } max && value > max
                        ? RangeFailure(constraints, out errorCode, out errorMessage)
                        : true;
                }
            case ScriptParameterType.DateTime:
                {
                    var value = DateTimeOffset.Parse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                    TryParseOptionalDateTime(constraints.Minimum, out var minimum);
                    TryParseOptionalDateTime(constraints.Maximum, out var maximum);
                    return minimum is { } min && value < min || maximum is { } max && value > max
                        ? RangeFailure(constraints, out errorCode, out errorMessage)
                        : true;
                }
            default:
                return true;
        }
    }

    private static bool RangeFailure(
        ScriptParameterConstraints constraints,
        out string errorCode,
        out string errorMessage)
    {
        errorCode = "SCRIPT_ARGUMENT_RANGE_INVALID";
        errorMessage = constraints.Minimum is not null && constraints.Maximum is not null
            ? $"The value must be between {constraints.Minimum} and {constraints.Maximum}."
            : constraints.Minimum is not null
                ? $"The value must be at least {constraints.Minimum}."
                : $"The value must not exceed {constraints.Maximum}.";
        return false;
    }

    private static bool StepFailure(string step, out string errorCode, out string errorMessage)
    {
        errorCode = "SCRIPT_ARGUMENT_STEP_INVALID";
        errorMessage = $"The value does not align to step {step}.";
        return false;
    }

    private static bool TryParseOptionalLong(string? value, out long? parsed)
    {
        parsed = null;
        if (value is null)
            return true;
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
            return false;
        parsed = result;
        return true;
    }

    private static bool TryParseOptionalDecimal(string? value, out decimal? parsed)
    {
        parsed = null;
        if (value is null)
            return true;
        if (!decimal.TryParse(
                value,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var result))
            return false;
        parsed = result;
        return true;
    }

    private static bool TryParseOptionalDate(string? value, out DateOnly? parsed)
    {
        parsed = null;
        if (value is null)
            return true;
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
            return false;
        parsed = result;
        return true;
    }

    private static bool TryParseOptionalDateTime(string? value, out DateTimeOffset? parsed)
    {
        parsed = null;
        if (value is null)
            return true;
        if (!DateTimeOffsetSuffixPattern().IsMatch(value)
            || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result))
            return false;
        parsed = result;
        return true;
    }

    private static bool IsOmitted(ScriptParameterDefinition parameter, string value) =>
        value.Length == 0 && parameter.Type is not (ScriptParameterType.String or ScriptParameterType.MultilineString);

    private static void Merge(
        ImmutableDictionary<string, string>.Builder target,
        IReadOnlyDictionary<string, string>? source)
    {
        if (source is null)
            return;
        foreach (var pair in source)
            target[pair.Key] = pair.Value;
    }

    private static ScriptDiagnostic Diagnostic(
        string code,
        string message,
        string? sourcePath) =>
        new(code, message, ScriptDiagnosticSeverity.Error, ScriptDiagnosticCategory.Validation, sourcePath);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterNamePattern();

    [GeneratedRegex("(?:Z|[+-][0-9]{2}:[0-9]{2})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DateTimeOffsetSuffixPattern();
}
