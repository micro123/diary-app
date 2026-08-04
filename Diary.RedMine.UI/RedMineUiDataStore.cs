using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Messaging;
using Diary.Database;
using Diary.GUIBase;
using Diary.GUIBase.Events;
using Diary.RedMine;
using Diary.RedMine.Models;
using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.RedMine.UI;

[DiAutoRegister(singleton: true, serviceType: typeof(IRedMineUiData))]
public sealed class RedMineUiDataStore : IRedMineUiData
{
    private readonly ILogger _logger;
    public ObservableCollection<RedMineIssueDisplay> RedMineIssues { get; } = new();
    public ObservableCollection<RedMineIssueDisplay> RedMineIssuesOpen { get; } = new();
    public ObservableCollection<RedMineActivity> RedMineActivities { get; } = new();

    public RedMineUiDataStore(ILogger logger)
    {
        _logger = logger;
        WeakReferenceMessenger.Default.Register<DbChangedEvent>(this, (_, message) =>
        {
            if ((message.Value & DbChangedEvent.RedMineIssue) != 0) LoadIssues();
            if ((message.Value & DbChangedEvent.RedMineActivity) != 0) LoadActivities();
        });
    }

    public void InitLoad()
    {
        LoadIssues();
        LoadActivities();
    }

    private void LoadActivities()
    {
        var db = BaseApp.Instance.UseDb?.GetExtension<IRedMineDb>();
        if (db is null) return;
        RedMineActivities.Clear();
        foreach (var activity in db.GetRedMineActivities()) RedMineActivities.Add(activity);
    }

    private void LoadIssues()
    {
        var db = BaseApp.Instance.UseDb?.GetExtension<IRedMineDb>();
        if (db is null) return;
        var issues = db.GetRedMineIssues(null);
        RedMineIssues.Clear();
        RedMineIssuesOpen.Clear();
        foreach (var issue in issues)
        {
            RedMineIssues.Add(issue);
            if (!issue.Disabled) RedMineIssuesOpen.Add(issue);
        }
        _logger.LogDebug("Loaded {Count} RedMine issues", RedMineIssues.Count);
    }
}
