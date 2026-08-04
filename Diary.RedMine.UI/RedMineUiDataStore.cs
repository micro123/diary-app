using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Messaging;
using Diary.Database;
using Diary.GUIBase;
using Diary.GUIBase.Events;
using Diary.PluginBase;
using Diary.RedMine;
using Diary.RedMine.Models;
using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.RedMine.UI;

[DiAutoRegister(singleton: true, serviceType: typeof(IRedMineUiData))]
public sealed class RedMineUiDataStore : IRedMineUiData
{
    private static readonly IReadOnlyList<IPluginMigration> RedMineMigrations =
        new RedMinePlugin().GetMigrations().ToArray();
    private readonly ILogger _logger;
    private readonly string _instanceId;
    private readonly IRedMineDb? _database;
    public ObservableCollection<RedMineIssueDisplay> RedMineIssues { get; } = new();
    public ObservableCollection<RedMineIssueDisplay> RedMineIssuesOpen { get; } = new();
    public ObservableCollection<RedMineActivity> RedMineActivities { get; } = new();

    public RedMineUiDataStore(
        ILogger logger,
        string? instanceId = null,
        IRedMineDb? database = null)
    {
        _logger = logger;
        _instanceId = instanceId ?? RedMinePluginConstants.DefaultInstanceId;
        _database = database;
        WeakReferenceMessenger.Default.Register<DbChangedEvent>(this, (_, message) =>
        {
            if ((message.Value & RedMineUiEvents.IssueChanged) != 0) LoadIssues();
            if ((message.Value & RedMineUiEvents.ActivityChanged) != 0) LoadActivities();
        });
    }

    public void InitLoad()
    {
        LoadIssues();
        LoadActivities();
    }

    private void LoadActivities()
    {
        var db = _database ?? BaseApp.Instance.UseDb?.GetExtension<IRedMineDb>(_instanceId, RedMineMigrations);
        if (db is null) return;
        RedMineActivities.Clear();
        foreach (var activity in db.GetRedMineActivities()) RedMineActivities.Add(activity);
    }

    private void LoadIssues()
    {
        var db = _database ?? BaseApp.Instance.UseDb?.GetExtension<IRedMineDb>(_instanceId, RedMineMigrations);
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
