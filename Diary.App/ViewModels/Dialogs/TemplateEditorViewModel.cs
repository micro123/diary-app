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
    public required string Id { get; init; }
    public required string Name { get; set; }
    public string DefaultTitle { get; set; } = string.Empty;
    public double Time { get; set; } = 0.0;
    public required ObservableCollection<WorkTag> Tags { get; set; }

    [SetsRequiredMembers]
    public TemplateViewModel(Template template, DbShareData shareData)
    {
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
        Templates.Add(new TemplateViewModel(new Template { Name = NewTemplateName }, _dbShareData));
        NewTemplateName = string.Empty;
    }

    public TemplateEditorViewModel(DbShareData dbShareData, ILogger logger)
    {
        _dbShareData = dbShareData;
        _logger = logger;

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

    [RelayCommand]
    private async Task CopyTemplateId(TemplateViewModel item)
    {
        if (await CopyStringToClipboardAsync(item.Id))
            ToastManager?.Show("模板 ID 已复制");
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
