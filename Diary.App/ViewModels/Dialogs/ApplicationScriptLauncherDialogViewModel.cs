using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.GUIBase.ViewModels;
using Diary.ScriptBase;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.App.ViewModels.Dialogs;

[DiAutoRegister]
public partial class ApplicationScriptLauncherDialogViewModel : ViewModelBase, IDialogContext
{
    public ObservableCollection<ScriptListItem> Scripts { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private ScriptListItem? _selectedScript;

    public bool HasScripts => Scripts.Count > 0;

    public event EventHandler<object?>? RequestClose;

    public void Initialize(IEnumerable<ScriptListItem> scripts)
    {
        Scripts.Clear();
        foreach (var script in scripts
                     .Where(item => item.IsRunnable && item.EntryKind == ScriptEntryKind.Application)
                     .OrderBy(item => item.Name, StringComparer.CurrentCulture))
        {
            Scripts.Add(script);
        }

        SelectedScript = Scripts.FirstOrDefault();
        OnPropertyChanged(nameof(HasScripts));
    }

    public void Close() => RequestClose?.Invoke(this, null);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Run()
    {
        if (SelectedScript is not null)
            RequestClose?.Invoke(this, SelectedScript);
    }

    [RelayCommand]
    private void Cancel() => Close();

    private bool CanRun() => SelectedScript is not null;
}
