namespace Diary.PluginUI;

public sealed record TrackerTagRulePackageItem(
    string TagKey,
    IReadOnlyDictionary<string, string?> Values);

public enum TrackerTagRuleValidationState
{
    Valid,
    Invalid,
    Unavailable,
}

public sealed record TrackerTagRuleValidation(
    TrackerTagRulePackageItem Rule,
    TrackerTagRuleValidationState State,
    string Message);
