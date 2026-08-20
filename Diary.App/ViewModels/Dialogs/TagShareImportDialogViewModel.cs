using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.Services;
using Diary.GUIBase.ViewModels;
using Diary.PluginUI;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.App.ViewModels.Dialogs;

public sealed record TagShareImportSelection(
    IReadOnlySet<string> TagKeys,
    IReadOnlyDictionary<string, ITagRuleEditorContribution> TrackerMappings);

public sealed partial class TagShareImportTagViewModel : ObservableObject
{
    private readonly Action _changed;

    public TagShareImportPreviewItem Item { get; }
    public string Name => Item.Name;
    public string Status => Item.Status;
    public bool HasConflict => Item.HasConflict;

    [ObservableProperty]
    private bool _isSelected;

    public TagShareImportTagViewModel(TagShareImportPreviewItem item, Action changed)
    {
        Item = item;
        _changed = changed;
        _isSelected = !item.HasConflict;
    }

    partial void OnIsSelectedChanged(bool value) => _changed();
}

public sealed record TagShareTrackerMappingOption(
    string Label,
    ITagRuleEditorContribution? Contribution);

public sealed partial class TagShareTrackerMappingViewModel : ObservableObject
{
    private readonly TagSharePackageTracker _tracker;
    private readonly IReadOnlyDictionary<string, string> _tagNames;
    private readonly Func<IReadOnlyDictionary<string, int>> _selectedTagIds;
    private readonly Action _changed;

    public string PackageKey => _tracker.Key;
    public string Source => $"{_tracker.Type} / {_tracker.Name}";
    public ObservableCollection<TagShareTrackerMappingOption> Options { get; } = [];

    [ObservableProperty] private TagShareTrackerMappingOption? _selectedOption;
    [ObservableProperty] private string _validationSummary = string.Empty;
    [ObservableProperty] private string _validationDetails = string.Empty;

    public TagShareTrackerMappingViewModel(
        TagSharePackageTracker tracker,
        IReadOnlyDictionary<string, string> tagNames,
        IEnumerable<ITagRuleEditorContribution> contributions,
        Func<IReadOnlyDictionary<string, int>> selectedTagIds,
        Action changed)
    {
        _tracker = tracker;
        _tagNames = tagNames;
        _selectedTagIds = selectedTagIds;
        _changed = changed;
        Options.Add(new TagShareTrackerMappingOption("不关联", null));
        foreach (var contribution in contributions.Where(item => item.PluginId == tracker.Type))
            Options.Add(new TagShareTrackerMappingOption(contribution.InstanceName, contribution));
        SelectedOption = Options.FirstOrDefault(option => option.Contribution is not null
            && string.Equals(option.Contribution.InstanceName, tracker.Name, StringComparison.Ordinal))
            ?? Options[0];
        RefreshValidation();
    }

    partial void OnSelectedOptionChanged(TagShareTrackerMappingOption? value)
    {
        RefreshValidation();
        _changed();
    }

    public void RefreshValidation()
    {
        var selectedRules = _tracker.Rules
            .Where(rule => _selectedTagIds().ContainsKey(rule.TagKey))
            .ToArray();
        if (SelectedOption?.Contribution is null)
        {
            ValidationSummary = selectedRules.Length == 0
                ? "没有待导入规则。"
                : $"不关联，将跳过 {selectedRules.Length} 条规则。";
            ValidationDetails = string.Empty;
            return;
        }
        var validations = SelectedOption.Contribution.ValidateImportRules(selectedRules, _selectedTagIds());
        var valid = validations.Count(item => item.State == TrackerTagRuleValidationState.Valid);
        var invalid = validations.Count(item => item.State == TrackerTagRuleValidationState.Invalid);
        var unavailable = validations.Count(item => item.State == TrackerTagRuleValidationState.Unavailable);
        ValidationSummary = $"有效 {valid}，无效 {invalid}，无法验证 {unavailable}；仅有效规则会导入。";
        ValidationDetails = string.Join(Environment.NewLine,
            validations.Where(item => item.State != TrackerTagRuleValidationState.Valid)
                .Select(item =>
                    $"{(_tagNames.TryGetValue(item.Rule.TagKey, out var name) ? name : item.Rule.TagKey)}：{item.Message}"));
    }
}

[DiAutoRegister]
public partial class TagShareImportDialogViewModel : ViewModelBase, IDialogContext
{
    public ObservableCollection<TagShareImportTagViewModel> Tags { get; } = [];
    public ObservableCollection<TagShareTrackerMappingViewModel> Trackers { get; } = [];

    public int SelectedCount => Tags.Count(item => item.IsSelected);
    public int ConflictCount => Tags.Count(item => item.HasConflict);
    public bool HasSelection => SelectedCount > 0;
    public bool HasTrackers => Trackers.Count > 0;

    public event EventHandler<object?>? RequestClose;

    public void Initialize(
        TagSharePackagePreview preview,
        IReadOnlyCollection<ITagRuleEditorContribution> contributions)
    {
        Tags.Clear();
        Trackers.Clear();
        foreach (var item in preview.Items)
            Tags.Add(new TagShareImportTagViewModel(item, SelectionChanged));
        var tagNames = preview.Package.Tags.ToDictionary(tag => tag.Key, tag => tag.Name, StringComparer.Ordinal);
        foreach (var tracker in preview.Package.Trackers)
            Trackers.Add(new TagShareTrackerMappingViewModel(
                tracker, tagNames, contributions, BuildSelectedTagIds, SelectionChanged));
        SelectionChanged();
    }

    public void Close() => RequestClose?.Invoke(this, null);

    [RelayCommand]
    private void SelectSafe()
    {
        foreach (var tag in Tags)
            tag.IsSelected = !tag.HasConflict;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Import()
    {
        var mappings = Trackers
            .Where(item => item.SelectedOption?.Contribution is not null)
            .ToDictionary(
                item => item.PackageKey,
                item => item.SelectedOption!.Contribution!,
                StringComparer.Ordinal);
        RequestClose?.Invoke(this, new TagShareImportSelection(
            Tags.Where(item => item.IsSelected)
                .Select(item => item.Item.Key)
                .ToHashSet(StringComparer.Ordinal),
            mappings));
    }

    [RelayCommand]
    private void Cancel() => Close();

    private IReadOnlyDictionary<string, int> BuildSelectedTagIds()
        => Tags.Where(item => item.IsSelected)
            .Select((item, index) => (item.Item.Key, Id: index + 1))
            .ToDictionary(item => item.Key, item => item.Id, StringComparer.Ordinal);

    private void SelectionChanged()
    {
        foreach (var tracker in Trackers)
            tracker.RefreshValidation();
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(ConflictCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasTrackers));
        ImportCommand.NotifyCanExecuteChanged();
    }
}
