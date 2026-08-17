using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.Models;
using Diary.Core.Data.Base;
using Diary.GUIBase.Converters;
using Diary.GUIBase.Events;
using Diary.GUIBase.Utils;
using Diary.GUIBase.ViewModels;
using Diary.PluginUI;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;
using Microsoft.Extensions.Logging;
using Ursa.Controls;

namespace Diary.App.ViewModels.Dialogs;

[DiAutoRegister]
public partial class TagEditorViewModel : ViewModelBase, IDialogContext
{
    private readonly ILogger _logger;
    private readonly TrackerPluginLifecycleCoordinator _lifecycleCoordinator;
    public string Title => "标签编辑器";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewTagCommand))]
    private string _newTagName = string.Empty;
    [ObservableProperty] private bool _newIsPrimary = true;
    [ObservableProperty] private HsvColor _newTagColor = default;

    [ObservableProperty] private ObservableCollection<EditableWorkTag> _allTags = new();
    [ObservableProperty] private EditableWorkTag? _selectedTag;
    public ObservableCollection<ITagRuleEditorContribution> RuleContributions { get; } = new();

    private bool _changed;

    public TagEditorViewModel(
        ILogger logger,
        TrackerUiContributionRegistry trackerRegistry,
        TrackerPluginLifecycleCoordinator lifecycleCoordinator)
    {
        _logger = logger;
        _lifecycleCoordinator = lifecycleCoordinator;
        foreach (var contribution in trackerRegistry.Contributions)
        {
            var ruleContribution = contribution.CreateTagRuleEditorContribution();
            if (ruleContribution is not null)
                RuleContributions.Add(ruleContribution);
        }
        LoadTags();
    }

    partial void OnSelectedTagChanged(EditableWorkTag? value)
    {
        if (value is null)
            return;
        foreach (var contribution in RuleContributions)
            contribution.SelectTag(value.Tag);
    }

    public void Close() => Cancel();

    public event EventHandler<object?>? RequestClose;

    [RelayCommand]
    private void Save()
    {
        if (!ValidateExtraFieldKeys(out var fieldError))
        {
            EventDispatcher.Notify("错误", fieldError!);
            return;
        }

        bool changed = _changed;
        foreach (var tag in AllTags)
        {
            var tagChanged = tag.ApplyChanges(out var error);
            if (error is not null)
            {
                EventDispatcher.Notify("错误", error);
                return;
            }

            changed |= tagChanged;
        }
        if (changed)
            EventDispatcher.DbChanged(DbChangedEvent.WorkTags);
        foreach (var pluginId in RuleContributions.Select(item => item.PluginId).Distinct())
        {
            foreach (var contribution in RuleContributions.Where(item => item.PluginId == pluginId))
                contribution.Commit();
            if (!_lifecycleCoordinator.SaveConfiguration(pluginId))
                _logger.LogWarning("保存标签规则配置失败: {PluginId}", pluginId);
        }
        RequestClose?.Invoke(this, null);
    }

    [RelayCommand]
    private void Cancel()
    {
        ReloadRules();
        RequestClose?.Invoke(this, null);
    }

    public void ReloadRules()
    {
        foreach (var contribution in RuleContributions)
            contribution.Reload();
    }

    [RelayCommand]
    private void DelTag(EditableWorkTag tag)
    {
        if (tag.Delete())
        {
            AllTags.Remove(tag);
            SelectedTag = AllTags.FirstOrDefault();
        }
    }

    [RelayCommand]
    private async Task AddExtraField()
    {
        await EditExtraField(null);
    }

    [RelayCommand]
    private async Task EditExtraField(EditableTagExtraField? field)
    {
        var tag = SelectedTag;
        if (tag is null)
            return;

        var draft = field?.Clone() ?? new EditableTagExtraField(tag.Id);
        var editor = new TagExtraFieldEditorViewModel(draft);
        var accepted = await OverlayDialog.ShowCustomModal<bool>(
            editor,
            options: new OverlayDialogOptions
            {
                Title = editor.Title,
                CanDragMove = false,
                CanResize = false,
                CanLightDismiss = false,
                IsCloseButtonVisible = false,
                Mode = DialogMode.None,
            });
        if (!accepted)
            return;

        if (field is null)
            tag.ExtraFields.Add(draft);
        else
            field.CopyFrom(draft);
        _changed = true;
    }

    [RelayCommand(CanExecute = nameof(CanAddTag))]
    private void NewTag()
    {
        _logger.LogInformation("new tag, name = {name}, primary = {primary}, color = {color}", NewTagName, NewIsPrimary, NewTagColor);
        int rgb = HsvColorConverter.FromHsv(NewTagColor);
        var tag = App.Instance.UseDb!.CreateWorkTag(NewTagName, NewIsPrimary, rgb);
        if (tag.Id > 0)
        {
            _changed = true;
            NewTagName = string.Empty;
            LoadTags();
        }
        else
        {
            EventDispatcher.Notify("错误", "添加标签失败了，可能是重复的标签名！");
        }
    }

    private bool CanAddTag() => !string.IsNullOrWhiteSpace(NewTagName);

    private bool ValidateExtraFieldKeys(out string? error)
    {
        var fields = AllTags.SelectMany(tag => tag.ExtraFields);
        var keys = new Dictionary<string, EditableTagExtraField>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            if (!field.Validate(out error))
                return false;

            var normalized = TagExtraFieldKeyRules.Normalize(field.FieldKey.Trim());
            if (!keys.TryAdd(normalized, field))
            {
                error = $"字段标识已存在：{field.FieldKey.Trim()}";
                return false;
            }
        }

        error = null;
        return true;
    }

    private void LoadTags()
    {
        var all = App.Instance.UseDb?.AllWorkTags();
        if (all == null)
            return;
        AllTags.Clear();
        foreach (var tag in all)
            AllTags.Add(new EditableWorkTag(tag));
        SelectedTag = AllTags.FirstOrDefault();
    }
}
