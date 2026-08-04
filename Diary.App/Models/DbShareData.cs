using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Messaging;
using Diary.Core.Data.Base;
using Diary.RedMine.Models;
using Diary.Database;
using Diary.GUIBase.Events;
using Diary.RedMine;
using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.App.Models;

[DiAutoRegister(singleton: true)]
public class DbShareData
{
    private readonly ILogger _logger;
    public ObservableCollection<WorkTag> WorkTags { get; } = new();
    public ObservableCollection<RedMineIssueDisplay> RedMineIssues { get; } = new();
    public ObservableCollection<RedMineIssueDisplay> RedMineIssuesOpen { get; } = new();
    public ObservableCollection<RedMineActivity> RedMineActivities { get; } = new();

    private DbInterfaceBase? DbInterface => App.Instance.UseDb;

    public DbShareData(ILogger logger)
    {
        _logger = logger;
        WeakReferenceMessenger.Default.Register<DbChangedEvent>(this, (r, m) =>
        {
            var active = false;

            _logger.LogDebug("db changed, mask = {Value:X}", m.Value);
            if (0 != (m.Value & DbChangedEvent.RedMineIssue))
            {
                active = true;
                LoadIssues();
            }
            if (0 != (m.Value & DbChangedEvent.RedMineActivity))
            {
                active = true;
                LoadActivities();
            }
            if (0 != (m.Value & DbChangedEvent.WorkTags))
            {
                active = true;
                LoadTags();
            }

            if (active)
            {
                WeakReferenceMessenger.Default.Send(new DbChangedEvent(DbChangedEvent.ShareData));
            }
        });
    }


    public void InitLoad()
    {
        LoadTags();
        LoadIssues();
        LoadActivities();
    }

    private void LoadActivities()
    {
        var activities = DbInterface!.GetExtension<IRedMineDb>()!.GetRedMineActivities();
        RedMineActivities.Clear();
        foreach (var activity in activities)
        {
            RedMineActivities.Add(activity);
        }
    }

    private void LoadIssues()
    {
        var issues = DbInterface!.GetExtension<IRedMineDb>()!.GetRedMineIssues(null);
        RedMineIssues.Clear();
        RedMineIssuesOpen.Clear();
        foreach (var issue in issues)
        {
            RedMineIssues.Add(issue);
            if (!issue.Disabled)
                RedMineIssuesOpen.Add(issue);
        }
    }

    private void LoadTags()
    {
        var tags = DbInterface!.AllWorkTags();
        WorkTags.Clear();
        foreach (var tag in tags)
        {
            WorkTags.Add(tag);
        }
    }
}
