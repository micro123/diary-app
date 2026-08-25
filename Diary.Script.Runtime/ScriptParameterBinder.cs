using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Diary.ScriptBase;

namespace Diary.Script.Runtime;

public sealed record ScriptParameterBindingResult(
    bool Succeeded,
    ImmutableDictionary<string, string> Arguments,
    ImmutableArray<ScriptDiagnostic> Diagnostics)
{
    public static ScriptParameterBindingResult Success(ImmutableDictionary<string, string> arguments) =>
        new(true, arguments, []);

    public static ScriptParameterBindingResult Failure(IEnumerable<ScriptDiagnostic> diagnostics) =>
        new(false, ImmutableDictionary<string, string>.Empty, [.. diagnostics]);
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
        ApplyInput(definitions, values, metadataDefaults, diagnostics, sourcePath);
        ApplyInput(definitions, values, suppliedArguments, diagnostics, sourcePath);
        if (diagnostics.Count > 0)
            return ScriptParameterBindingResult.Failure(diagnostics);

        var normalized = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var parameter in definitions.Values)
        {
            if (!values.TryGetValue(parameter.Name, out var value)
                || IsOmitted(parameter, value))
            {
                if (parameter.Required && requireRequired)
                {
                    diagnostics.Add(Diagnostic(
                        "SCRIPT_ARGUMENT_REQUIRED",
                        $"Required script argument '{parameter.Name}' is missing.",
                        sourcePath));
                }
                continue;
            }

            if (!TryNormalize(parameter, value, out var normalizedValue, out var errorCode, out var errorMessage))
            {
                diagnostics.Add(Diagnostic(
                    errorCode,
                    $"Script argument '{parameter.Name}' is invalid: {errorMessage}",
                    sourcePath));
                continue;
            }
            normalized[parameter.Name] = normalizedValue;
        }

        if (diagnostics.Count > 0)
            return ScriptParameterBindingResult.Failure(diagnostics);
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
        string? sourcePath)
    {
        if (input is null)
            return;
        foreach (var pair in input)
        {
            if (!definitions.ContainsKey(pair.Key))
            {
                diagnostics.Add(Diagnostic(
                    "SCRIPT_ARGUMENT_UNKNOWN",
                    $"Unknown script argument '{pair.Key}'.",
                    sourcePath));
                continue;
            }
            if (pair.Value is null || pair.Value.Length > MaxValueLength)
            {
                diagnostics.Add(Diagnostic(
                    "SCRIPT_ARGUMENT_TYPE_INVALID",
                    $"Script argument '{pair.Key}' exceeds the value length limit.",
                    sourcePath));
                continue;
            }
            values[pair.Key] = pair.Value;
        }
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
                return true;
            case ScriptParameterType.MultilineString:
                if (parameter.Required && string.IsNullOrWhiteSpace(value))
                {
                    errorCode = "SCRIPT_ARGUMENT_REQUIRED";
                    errorMessage = "A non-empty text value is required.";
                    return false;
                }
                normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
                return true;
            case ScriptParameterType.Integer:
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                    return false;
                normalized = integer.ToString(CultureInfo.InvariantCulture);
                return true;
            case ScriptParameterType.Number:
                if (!decimal.TryParse(
                        value,
                        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture,
                        out var number))
                    return false;
                normalized = number.ToString(CultureInfo.InvariantCulture);
                return true;
            case ScriptParameterType.Boolean:
                if (!bool.TryParse(value, out var boolean))
                    return false;
                normalized = boolean ? "true" : "false";
                return true;
            case ScriptParameterType.Date:
                if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    return false;
                normalized = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                return true;
            case ScriptParameterType.DateTime:
                if (!DateTimeOffsetSuffixPattern().IsMatch(value)
                    || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dateTime))
                    return false;
                normalized = dateTime.ToString("O", CultureInfo.InvariantCulture);
                return true;
            case ScriptParameterType.Choice:
                if (!(parameter.Choices ?? []).Any(choice => string.Equals(choice.Value, value, StringComparison.Ordinal)))
                {
                    errorCode = "SCRIPT_ARGUMENT_CHOICE_INVALID";
                    errorMessage = "The value is not one of the declared choices.";
                    return false;
                }
                normalized = value;
                return true;
            default:
                return false;
        }
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
