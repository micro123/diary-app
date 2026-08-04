using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Messaging;
using Diary.Core.Data.Base;
using Diary.Database;
using Diary.GUIBase.Events;
using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.App.Models;

[DiAutoRegister(singleton: true)]
public class DbShareData
{
    private readonly ILogger _logger;
    public ObservableCollection<WorkTag> WorkTags { get; } = new();
    private DbInterfaceBase? DbInterface => App.Instance.UseDb;

    public DbShareData(ILogger logger)
    {
        _logger = logger;
        WeakReferenceMessenger.Default.Register<DbChangedEvent>(this, (r, m) =>
        {
            var active = false;

            _logger.LogDebug("db changed, mask = {Value:X}", m.Value);
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
