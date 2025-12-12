using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.Messages;
using Diary.App.Models;
using Diary.App.Utils;
using Diary.App.ViewModels;
using Diary.Core.Data.Base;
using Diary.Core.Data.Display;
using Diary.Core.Data.RedMine;
using Diary.Core.Utils;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace Diary.App.Dialogs;

public partial class TemplateViewModel
{
    public required string Name { get; set; }
    public string DefaultTitle { get; set; } = string.Empty;
    public int RedMineActivity { get; set; } = -1;
    public int RedMineIssue { get; set; } = -1;
    public double Time { get; set; } = 0.0;
    public required ObservableCollection<WorkTag> Tags { get; set; }

    private static int FindIndex<T>(ObservableCollection<T> collection, Func<T, bool> predicate)
    {
        for (int i = 0; i < collection.Count; i++)
        {
            if (predicate(collection[i]))
                return i;
        }
        return -1;
    }
    
    public static TemplateViewModel FromTemplate(Template template, DbShareData shareData)
    {
        ObservableCollection<WorkTag> tags = new();
        if (template.DefaultWorkTags.Count > 0)
        {
            foreach (var tag in template.DefaultWorkTags)
            {
                var workTag = shareData.WorkTags.FirstOrDefault(x => x.Id == tag);
                if (workTag != null)
                    tags.Add(workTag);
            }
        }
        var result = new TemplateViewModel()
        {
            Name = template.Name,
            DefaultTitle = template.DefaultTitle,
            Time = template.DefaultTime,
            RedMineActivity = FindIndex(shareData.RedMineActivities, x => x.Id == template.DefaultActivity),
            RedMineIssue =  FindIndex(shareData.RedMineIssues, x => x.Id == template.DefaultIssue),
            Tags = tags,
        };
        return result;
    }

    public Template ToTemplate(DbShareData shareData)
    {
        var result = new Template()
        {
            Name = Name,
            DefaultTitle = DefaultTitle,
            DefaultTime = Time,
            DefaultActivity = RedMineActivity >= 0 ? shareData.RedMineActivities[RedMineActivity].Id : -1,
            DefaultIssue = RedMineIssue >= 0 ? shareData.RedMineIssues[RedMineIssue].Id : -1,
            DefaultWorkTags = Tags.Select(x => x.Id).ToList(),
        };
        return result;
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

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AddTemplateCommand))]
    private string _newTemplateName = string.Empty;

    [ObservableProperty] private ObservableCollection<TemplateViewModel> _templates = new();

    public ObservableCollection<RedMineActivity> Activities => _dbShareData.RedMineActivities;
    public ObservableCollection<RedMineIssueDisplay> Issues => _dbShareData.RedMineIssues;
    public ObservableCollection<WorkTag> Tags => _dbShareData.WorkTags;

    private bool CanAdd => !string.IsNullOrWhiteSpace(NewTemplateName);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddTemplate()
    {
        Templates.Add(new TemplateViewModel()
        {
            Name = NewTemplateName,
            Tags = new(),
        });
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
        foreach (var item in templates.Select(t => TemplateViewModel.FromTemplate(t, _dbShareData)))
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
        var templates = Templates.Select(x => x.ToTemplate(_dbShareData)).ToList();
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