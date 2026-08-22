using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.Core.Data.Base;
using Diary.GUIBase.ViewModels;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.App.ViewModels.Dialogs;

public sealed record TagShareExportSelection(IReadOnlySet<int> TagIds);

public sealed partial class TagShareExportItemViewModel(
    WorkTag tag,
    Action selectionChanged) : ObservableObject
{
    public int Id => tag.Id;
    public string Name => tag.Name;
    public string Details =>
        $"{(tag.Level == TagLevels.Primary ? "主标签" : "次标签")} · " +
        $"{(tag.Disabled ? "已停用" : "已启用")}";

    [ObservableProperty]
    private bool _isSelected = true;

    partial void OnIsSelectedChanged(bool value) => selectionChanged();
}

[DiAutoRegister]
public partial class TagShareExportDialogViewModel : ViewModelBase, IDialogContext
{
    public ObservableCollection<TagShareExportItemViewModel> Items { get; } = [];

    public int SelectedCount => Items.Count(item => item.IsSelected);
    public bool HasSelection => SelectedCount > 0;

    public event EventHandler<object?>? RequestClose;

    public void Initialize(IEnumerable<WorkTag> tags)
    {
        Items.Clear();
        foreach (var tag in tags
                     .OrderBy(item => item.Level)
                     .ThenBy(item => item.Name, StringComparer.CurrentCulture))
            Items.Add(new TagShareExportItemViewModel(tag, SelectionChanged));
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
    private void Export() => RequestClose?.Invoke(this, new TagShareExportSelection(
        Items.Where(item => item.IsSelected).Select(item => item.Id).ToHashSet()));

    [RelayCommand]
    private void Cancel() => Close();

    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        ExportCommand.NotifyCanExecuteChanged();
    }
}
