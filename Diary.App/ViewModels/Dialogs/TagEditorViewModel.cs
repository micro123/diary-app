using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.Models;
using Diary.GUIBase.Converters;
using Diary.GUIBase.Events;
using Diary.GUIBase.Utils;
using Diary.GUIBase.ViewModels;
using Diary.PluginUI;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;
using Microsoft.Extensions.Logging;

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

    private bool _changed = false;

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

    public void Close()
    {
        RequestClose?.Invoke(this, null);
    }

    public event EventHandler<object?>? RequestClose;

    [RelayCommand]
    private void Save()
    {
        bool changed = _changed;
        foreach (var tag in AllTags)
        {
            changed |= tag.ApplyChanges();
        }
        if (changed)
            EventDispatcher.DbChanged(DbChangedEvent.WorkTags);
        foreach (var pluginId in RuleContributions.Select(item => item.PluginId).Distinct())
        {
            if (!_lifecycleCoordinator.SaveConfiguration(pluginId))
                _logger.LogWarning("保存标签规则配置失败: {PluginId}", pluginId);
        }
        RequestClose?.Invoke(this, null);
    }

    [RelayCommand]
    private void DelTag(EditableWorkTag tag)
    {
        if (tag.Delete())
        {
            AllTags.Remove(tag);
        }
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
            // success
            NewTagName = string.Empty; // clear name
            LoadTags();
        }
        else
        {
            EventDispatcher.Notify("错误", "添加标签失败了，可能是重复的标签名！");
        }
    }

    private bool CanAddTag()
    {
        return !string.IsNullOrWhiteSpace(NewTagName);
    }

    private void LoadTags()
    {
        var all = App.Instance.UseDb?.AllWorkTags();
        if (all == null) return;
        AllTags.Clear();
        foreach (var tag in all)
        {
            AllTags.Add(new EditableWorkTag(tag));
        }
        SelectedTag = AllTags.FirstOrDefault();
    }
}
