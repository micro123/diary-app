using System.Collections.ObjectModel;
using Diary.App.ViewModels;

namespace Diary.App.Models;

public sealed class ScriptStartupDiagnosticsStore
{
    public ObservableCollection<ScriptDiagnosticListItem> Diagnostics { get; } = new();

    public bool LoadFailed { get; private set; }

    public void Replace(IEnumerable<ScriptDiagnosticListItem> diagnostics, bool loadFailed = false)
    {
        Diagnostics.Clear();
        foreach (var diagnostic in diagnostics)
            Diagnostics.Add(diagnostic);
        LoadFailed = loadFailed;
    }
}
