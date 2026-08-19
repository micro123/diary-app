using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.Services;
using Diary.GUIBase.ViewModels;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.App.ViewModels.Dialogs;

public sealed record ScriptShareImportSelection(IReadOnlyList<ScriptShareImportDecision> Decisions);

public sealed partial class ScriptShareImportItemViewModel(
    ScriptShareImportPreviewItem item,
    Action selectionChanged) : ObservableObject
{
    public ScriptShareImportPreviewItem Item { get; } = item;
    public string Name => Item.Name;
    public string Id => Item.Id;
    public string Details => $"{Item.Language} · {(Item.Scope == ScriptBase.ScriptScope.Editor ? "编辑器脚本" : "应用脚本")} · {EntryKindLabel}";
    private string EntryKindLabel => Item.EntryKind switch
    {
        ScriptBase.ScriptEntryKind.Automation => "自动化入口",
        ScriptBase.ScriptEntryKind.Query => "查询入口",
        ScriptBase.ScriptEntryKind.Editor => "编辑器入口",
        _ => "应用入口",
    };
    public string Status => Item.Status;
    public bool HasConflict => Item.HasConflict;

    [ObservableProperty]
    private bool _isSelected = !item.HasConflict;

    partial void OnIsSelectedChanged(bool value) => selectionChanged();
}

[DiAutoRegister]
public partial class ScriptShareImportDialogViewModel : ViewModelBase, IDialogContext
{
    public ObservableCollection<ScriptShareImportItemViewModel> Items { get; } = [];

    public int SelectedCount => Items.Count(item => item.IsSelected);
    public int ConflictCount => Items.Count(item => item.HasConflict);
    public bool HasSelection => SelectedCount > 0;
    public bool HasConflicts => ConflictCount > 0;

    public event EventHandler<object?>? RequestClose;

    public void Initialize(ScriptSharePackagePreview preview)
    {
        Items.Clear();
        foreach (var item in preview.Items)
            Items.Add(new ScriptShareImportItemViewModel(item, SelectionChanged));
        SelectionChanged();
    }

    public void Close() => RequestClose?.Invoke(this, null);

    [RelayCommand]
    private void SelectSafe()
    {
        foreach (var item in Items)
            item.IsSelected = !item.HasConflict;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Import() => RequestClose?.Invoke(this, new ScriptShareImportSelection(
        Items.Where(item => item.IsSelected)
            .Select(item => new ScriptShareImportDecision(item.Id, item.HasConflict))
            .ToArray()));

    [RelayCommand]
    private void Cancel() => Close();

    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(ConflictCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasConflicts));
        ImportCommand.NotifyCanExecuteChanged();
    }
}
