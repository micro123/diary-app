using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.Models;
using Diary.Core.Data.App;
using Diary.Core.Data.Base;
using Diary.Core.Utils;
using Diary.GUIBase.Events;
using Diary.GUIBase.Utils;
using Diary.GUIBase.ViewModels;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace Diary.App.ViewModels.Dialogs;

public partial class TemplateViewModel : ObservableObject
{
    private readonly IReadOnlyCollection<WorkTag> _allTags;

    public required string Id { get; init; }
    public required string Name { get; set; }
    public string DefaultTitle { get; set; } = string.Empty;
    public double Time { get; set; } = 0.0;
    public required ObservableCollection<WorkTag> Tags { get; set; }
    public IReadOnlyList<WorkTag> AvailableTags => ResolveAvailableTags(_allTags, Tags);
    public bool HasAvailableTags => AvailableTags.Count > 0;

    [SetsRequiredMembers]
    public TemplateViewModel(Template template, DbShareData shareData)
    {
        _allTags = shareData.WorkTags;
        Id = template.Id;
        Name = template.Name;
        DefaultTitle = template.DefaultTitle;
        Time = template.DefaultTime;
        Tags = new ObservableCollection<WorkTag>();
        foreach (var tagId in template.DefaultWorkTags)
        {
            var wt = shareData.WorkTags.FirstOrDefault(x => x.Id == tagId);
            if (wt != null)
                Tags.Add(wt);
        }
    }

    public Template ToTemplate()
    {
        return new Template
        {
            Id = Id,
            Name = Name,
            DefaultTitle = DefaultTitle,
            DefaultTime = Time,
            DefaultWorkTags = Tags.Select(x => x.Id).ToList(),
        };
    }

    [RelayCommand]
    private void AddTag(WorkTag tag)
    {
        if (AvailableTags.All(candidate => candidate.Id != tag.Id))
            return;
        Tags.Add(tag);
        NotifyAvailableTagsChanged();
    }

    [RelayCommand]
    private void RemoveTag(WorkTag tag)
    {
        Tags.Remove(tag);
        if (tag.Level == TagLevels.Primary)
            Tags.Clear();
        NotifyAvailableTagsChanged();
    }

    internal static IReadOnlyList<WorkTag> ResolveAvailableTags(
        IEnumerable<WorkTag> allTags,
        IReadOnlyCollection<WorkTag> selectedTags)
    {
        var expectedLevel = selectedTags.Count == 0
            ? TagLevels.Primary
            : TagLevels.Secondary;
        var selectedIds = selectedTags.Select(tag => tag.Id).ToHashSet();
        return allTags
            .Where(tag => !tag.Disabled
                && tag.Level == expectedLevel
                && !selectedIds.Contains(tag.Id))
            .ToArray();
    }

    private void NotifyAvailableTagsChanged()
    {
        OnPropertyChanged(nameof(AvailableTags));
        OnPropertyChanged(nameof(HasAvailableTags));
    }
}

[DiAutoRegister]
public partial class TemplateEditorViewModel : ViewModelBase, IDialogContext
{
    private readonly DbShareData _dbShareData;
    private readonly ILogger _logger;
    private readonly Func<object, bool> _save;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTemplateCommand))]
    private string _newTemplateName = string.Empty;

    [ObservableProperty] private ObservableCollection<TemplateViewModel> _templates = new();

    private bool CanAdd => !string.IsNullOrWhiteSpace(NewTemplateName);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddTemplate()
    {
        Templates.Add(new TemplateViewModel(new Template { Name = NewTemplateName }, _dbShareData));
        NewTemplateName = string.Empty;
    }

    public TemplateEditorViewModel(DbShareData dbShareData, ILogger logger)
        : this(dbShareData, logger, EasySaveLoad.Save)
    {
    }

    internal TemplateEditorViewModel(DbShareData dbShareData, ILogger logger, Func<object, bool> save)
    {
        _dbShareData = dbShareData;
        _logger = logger;
        _save = save;

        LoadTemplates();
    }

    private void LoadTemplates()
    {
        var templates = TemplateManager.Instance.Templates;
        foreach (var item in templates.Select(t => new TemplateViewModel(t, _dbShareData)))
        {
            Templates.Add(item);
        }
    }

    [RelayCommand]
    private void Save()
    {
        if (!SaveTemplates())
        {
            EventDispatcher.ShowToast("模板保存失败，请重试");
            return;
        }

        RequestClose?.Invoke(this, true);
    }

    [RelayCommand]
    private void Delete(TemplateViewModel item)
    {
        Templates.Remove(item);
    }

    [RelayCommand]
    private async Task CopyTemplateId(TemplateViewModel item)
    {
        if (await CopyStringToClipboardAsync(item.Id))
            ToastManager?.Show("模板 ID 已复制");
    }

    private bool SaveTemplates()
    {
        var templates = Enumerable.Select<TemplateViewModel, Template>(Templates, x => x.ToTemplate()).ToList();
        var manager = TemplateManager.Instance;
        var previousTemplates = manager.Templates;
        manager.Templates = templates;
        try
        {
            if (!_save(manager))
            {
                manager.Templates = previousTemplates;
                _logger.LogError("保存模板配置失败");
                return false;
            }

            EventDispatcher.Msg(new TemplateChangedEvent());
            return true;
        }
        catch (Exception exception)
        {
            manager.Templates = previousTemplates;
            _logger.LogError(exception, "保存模板配置失败");
            return false;
        }
    }

    public void Close()
    {
        // 模板编辑要求显式保存后才能关闭；忽略 Esc 等宿主关闭请求。
    }

    public event EventHandler<object?>? RequestClose;
}
