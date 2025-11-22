using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.ViewModels;
using Diary.Core.Data.Base;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.App.Dialogs;

[DiAutoRegister]
public partial class TagEditorViewModel : ViewModelBase, IDialogContext
{
    public string Title => "标签编辑器";

    [ObservableProperty] private ObservableCollection<EditableWorkTag> _allTags = new();

    public TagEditorViewModel()
    {
        var all = App.Current.UseDb?.AllWorkTags();
        if (all == null) return;
        foreach (var tag in all)
        {
            _allTags.Add(new  EditableWorkTag(tag));
        }
    }

    public void Close()
    {
        RequestClose?.Invoke(this, null);
    }

    public event EventHandler<object?>? RequestClose;

    [RelayCommand]
    void Save()
    {
        foreach (var tag in AllTags)
        {
            tag.ApplyChanges();
        }
        RequestClose?.Invoke(this, null);
    }
}