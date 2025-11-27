using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Messaging;
using Diary.App.Messages;
using Diary.Core.Data.Base;
using Diary.Core.Data.Display;
using Diary.Core.Data.RedMine;
using Diary.Database;
using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.App.Models;

[DiAutoRegister(singleton: true)]
public class DbShareData
{
    public ObservableCollection<WorkTag> WorkTags { get; } = new();
    public ObservableCollection<RedMineIssueDisplay> RedMineIssues { get; } = new();
    public ObservableCollection<RedMineActivity> RedMineActivities { get; } = new();

    private DbInterfaceBase? DbInterface => App.Current.UseDb;
    
    public DbShareData()
    {
        WeakReferenceMessenger.Default.Register<DbChangedEvent>(this, (r, m) =>
        {
            App.Current.Logger.LogDebug("db changed, mask = {0:X}", m.Value);
            if (0 != (m.Value & DbChangedEvent.RedMineIssue))
            {
                LoadIssues();
            }
            if (0 != (m.Value & DbChangedEvent.RedMineActivity))
            {
                LoadActivities();
            }
            if (0 != (m.Value & DbChangedEvent.WorkTags))
            {
                LoadTags();
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
        var activities = DbInterface!.GetRedMineActivities();
        RedMineActivities.Clear();
        foreach (var activity in activities)
        {
            RedMineActivities.Add(activity);
        }
    }

    private void LoadIssues()
    {
        var issues = DbInterface!.GetRedMineIssues(null);
        RedMineIssues.Clear();
        foreach (var issue in issues)
            RedMineIssues.Add(issue);
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