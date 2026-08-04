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

public partial class TemplateViewModel
{
    public required string Name { get; set; }
    public string DefaultTitle { get; set; } = string.Empty;
    public double Time { get; set; } = 0.0;
    public required ObservableCollection<WorkTag> Tags { get; set; }

    /// <summary>tracker 扩展编辑区（各 contributor 经 ViewLocator 渲染）。</summary>
    public ObservableCollection<ViewModelBase> TrackerEditors { get; } = new();

    private readonly Template _original;
    private readonly TemplateCoordinator _coordinator;
    private readonly List<TemplateEditorSlot> _slots;

    [SetsRequiredMembers]
    public TemplateViewModel(Template template, TemplateCoordinator coordinator, DbShareData shareData)
    {
        _original = template;
        _coordinator = coordinator;
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
        _slots = coordinator.LoadEditors(template).ToList();
        foreach (var s in _slots)
            TrackerEditors.Add(s.Editor);
    }

    public Template ToTemplate()
    {
        return new Template
        {
            Name = Name,
            DefaultTitle = DefaultTitle,
            DefaultTime = Time,
            DefaultWorkTags = Tags.Select(x => x.Id).ToList(),
            Extensions = _coordinator.SaveEditors(_slots, _original).ToList(),
            // DefaultActivity/DefaultIssue 不再写（deprecated，留默认值；旧字段仅供旧文件迁移读）
        };
    }

    [RelayCommand]
    private void AddTag(WorkTag tag)
    {
        if (Tags.Contains(tag))
            return;
        if (Tags.Any(x => x.Level == TagLevels.Primary) && tag.Level == TagLevels.Primary)
        {
            return;
        }
        Tags.Add(tag);
    }

    [RelayCommand]
    private void RemoveTag(WorkTag tag)
    {
        Tags.Remove(tag);
        if (tag.Level == TagLevels.Primary)
            Tags.Clear();
    }
}

[DiAutoRegister]
public partial class TemplateEditorViewModel : ViewModelBase, IDialogContext
{
    private readonly DbShareData _dbShareData;
    private readonly TemplateCoordinator _coordinator;
    private readonly ILogger _logger;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTemplateCommand))]
    private string _newTemplateName = string.Empty;

    [ObservableProperty] private ObservableCollection<TemplateViewModel> _templates = new();

    public ObservableCollection<WorkTag> Tags => _dbShareData.WorkTags;

    private bool CanAdd => !string.IsNullOrWhiteSpace(NewTemplateName);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddTemplate()
    {
        Templates.Add(new TemplateViewModel(new Template { Name = NewTemplateName }, _coordinator, _dbShareData));
        NewTemplateName = string.Empty;
    }

    public TemplateEditorViewModel(DbShareData dbShareData, TemplateCoordinator coordinator, ILogger logger)
    {
        _dbShareData = dbShareData;
        _coordinator = coordinator;
        _logger = logger;

        LoadTemplates();
    }

    private void LoadTemplates()
    {
        var templates = TemplateManager.Instance.Templates;
        foreach (var item in templates.Select(t => new TemplateViewModel(t, _coordinator, _dbShareData)))
        {
            Templates.Add(item);
        }
    }

    [RelayCommand]
    private void Save(string param)
    {
        if (param == "1")
            SaveTemplates();
        RequestClose?.Invoke(this, null);
    }

    [RelayCommand]
    private void Delete(TemplateViewModel item)
    {
        Templates.Remove(item);
    }

    private void SaveTemplates()
    {
        var templates = Enumerable.Select<TemplateViewModel, Template>(Templates, x => x.ToTemplate()).ToList();
        TemplateManager.Instance.Templates = templates;
        EasySaveLoad.Save(TemplateManager.Instance);
        EventDispatcher.Msg(new TemplateChangedEvent());
    }

    public void Close()
    {
        RequestClose?.Invoke(this, null);
    }

    public event EventHandler<object?>? RequestClose;
}
