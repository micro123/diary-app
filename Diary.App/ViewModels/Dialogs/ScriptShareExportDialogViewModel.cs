using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.GUIBase.ViewModels;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.App.ViewModels.Dialogs;

public sealed record ScriptShareExportSelection(IReadOnlyList<ScriptListItem> Scripts);

public sealed partial class ScriptShareExportItemViewModel(
    ScriptListItem script,
    Action selectionChanged) : ObservableObject
{
    public ScriptListItem Script { get; } = script;
    public string Name => Script.Name;
    public string Id => Script.Id;
    public string Details => $"{Script.Language} · {Script.ScopeLabel} · {Script.EntryKindLabel}";

    [ObservableProperty]
    private bool _isSelected = true;

    partial void OnIsSelectedChanged(bool value) => selectionChanged();
}

[DiAutoRegister]
public partial class ScriptShareExportDialogViewModel : ViewModelBase, IDialogContext
{
    public ObservableCollection<ScriptShareExportItemViewModel> Items { get; } = [];

    public int SelectedCount => Items.Count(item => item.IsSelected);
    public bool HasSelection => SelectedCount > 0;

    public event EventHandler<object?>? RequestClose;

    public void Initialize(IEnumerable<ScriptListItem> scripts)
    {
        Items.Clear();
        foreach (var script in scripts
                     .Where(item => item.BuildSucceeded)
                     .OrderBy(item => item.Name, StringComparer.CurrentCulture))
            Items.Add(new ScriptShareExportItemViewModel(script, SelectionChanged));
        SelectionChanged();
    }

    public void Close() => RequestClose?.Invoke(this, null);

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in Items)
            item.IsSelected = true;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var item in Items)
            item.IsSelected = false;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Export() => RequestClose?.Invoke(this, new ScriptShareExportSelection(
        Items.Where(item => item.IsSelected).Select(item => item.Script).ToArray()));

    [RelayCommand]
    private void Cancel() => Close();

    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        ExportCommand.NotifyCanExecuteChanged();
    }
}
