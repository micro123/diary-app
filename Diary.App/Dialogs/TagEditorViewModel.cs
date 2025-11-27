using System;
using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Diary.App.Converters;
using Diary.App.Messages;
using Diary.App.Utils;
using Diary.App.ViewModels;
using Diary.Core.Data.Base;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace Diary.App.Dialogs;

[DiAutoRegister]
public partial class TagEditorViewModel : ViewModelBase, IDialogContext
{
    private readonly ILogger _logger;
    public string Title => "标签编辑器";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewTagCommand))]
    private string _newTagName = string.Empty;
    [ObservableProperty] private bool _newIsPrimary = true;
    [ObservableProperty] private HsvColor _newTagColor = default;
    
    [ObservableProperty] private ObservableCollection<EditableWorkTag> _allTags = new();

    public TagEditorViewModel(ILogger logger)
    {
        Messenger.Register<DbChangedEvent>(this, (r, m) =>
        {
            if ((m.Value & DbChangedEvent.WorkTags) != 0)
                LoadTags();
        });
        _logger = logger;
        LoadTags();
    }

    public void Close()
    {
        RequestClose?.Invoke(this, null);
    }

    public event EventHandler<object?>? RequestClose;

    [RelayCommand]
    void Save()
    {
        bool changed = false;
        foreach (var tag in AllTags)
        {
            changed |= tag.ApplyChanges();
        }
        if (changed)
            EventDispatcher.DbChanged();
        RequestClose?.Invoke(this, null);
    }

    [RelayCommand(CanExecute = nameof(CanAddTag))]
    void NewTag()
    {
        _logger.LogInformation("new tag, name = {0}, primary = {1}, color = {2}", NewTagName, NewIsPrimary, NewTagColor);
        int rgb = HsvColorConverter.FromHsv(NewTagColor);
        var tag = App.Current.UseDb!.CreateWorkTag(NewTagName, NewIsPrimary, rgb);
        if (tag.Id > 0)
        {
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
        var all = App.Current.UseDb?.AllWorkTags();
        if (all == null) return;
        AllTags.Clear();
        foreach (var tag in all)
        {
            AllTags.Add(new  EditableWorkTag(tag));
        }
    }
}