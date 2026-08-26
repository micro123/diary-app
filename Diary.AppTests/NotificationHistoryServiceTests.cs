using Avalonia.Controls.Notifications;
using Diary.App.Services;
using Diary.GUIBase.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diary.AppTests;

[TestClass]
public sealed class NotificationHistoryServiceTests
{
    [TestMethod]
    public void EventDefaults_MatchRetentionPolicy()
    {
        var toast = new ToastEvent("已复制");
        var notification = new NotifyOptions("错误", "数据库不可用");

        Assert.AreEqual(NotificationRetention.Transient, toast.Retention);
        Assert.AreEqual(NotificationRetention.Persistent, notification.Retention);
    }

    [TestMethod]
    public async Task PersistentEntries_SurviveReload_WhileOtherRetentionDoesNot()
    {
        using var directory = new TemporaryDirectory();
        var statePath = Path.Combine(directory.Path, "history.json");
        var service = new NotificationHistoryService(statePath, NullLogger.Instance);

        service.Add("临时", null, NotificationType.Information, NotificationRetention.Transient);
        service.Add("会话", null, NotificationType.Information, NotificationRetention.Session);
        service.Add("持久", "需要重启后查看", NotificationType.Warning, NotificationRetention.Persistent);
        await service.FlushAsync();

        var reloaded = new NotificationHistoryService(statePath, NullLogger.Instance);
        var entry = reloaded.GetSnapshot().Single();
        Assert.AreEqual("持久", entry.Title);
        Assert.AreEqual(NotificationRetention.Persistent, entry.Retention);
    }

    [TestMethod]
    public void DuplicateWithinWindow_IsMergedAndBecomesUnreadAgain()
    {
        using var directory = new TemporaryDirectory();
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero));
        var service = new NotificationHistoryService(
            Path.Combine(directory.Path, "history.json"),
            NullLogger.Instance,
            time);

        var first = service.Add(
            "更新服务不可用",
            "连接超时",
            NotificationType.Warning,
            NotificationRetention.Session)!;
        service.MarkAllRead();
        time.Advance(TimeSpan.FromMinutes(5));
        var second = service.Add(
            "更新服务不可用",
            "连接超时",
            NotificationType.Warning,
            NotificationRetention.Session)!;

        Assert.AreEqual(first.Id, second.Id);
        Assert.AreEqual(2, second.OccurrenceCount);
        Assert.IsFalse(second.IsRead);
        Assert.AreEqual(1, service.UnreadCount);
    }

    [TestMethod]
    public void Add_RedactsSensitiveValuesAndTruncatesLongText()
    {
        using var directory = new TemporaryDirectory();
        var service = new NotificationHistoryService(
            Path.Combine(directory.Path, "history.json"),
            NullLogger.Instance);

        var entry = service.Add(
            new string('题', NotificationHistoryService.MaxTitleLength + 20),
            "token=abc123 password:secret Authorization: Bearer auth-value "
                + new string('文', NotificationHistoryService.MaxMessageLength),
            NotificationType.Error,
            NotificationRetention.Session)!;

        Assert.AreEqual(NotificationHistoryService.MaxTitleLength, entry.Title.Length);
        Assert.IsLessThanOrEqualTo(NotificationHistoryService.MaxMessageLength, entry.Message.Length);
        Assert.Contains("token=[REDACTED]", entry.Message);
        Assert.Contains("password=[REDACTED]", entry.Message);
        Assert.Contains("Authorization=[REDACTED]", entry.Message);
        Assert.DoesNotContain("abc123", entry.Message);
        Assert.DoesNotContain("auth-value", entry.Message);
    }

    [TestMethod]
    public void Capacity_PrefersRemovingOldestReadEntry()
    {
        using var directory = new TemporaryDirectory();
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero));
        var service = new NotificationHistoryService(
            Path.Combine(directory.Path, "history.json"),
            NullLogger.Instance,
            time);

        var oldest = service.Add(
            "最早通知",
            null,
            NotificationType.Information,
            NotificationRetention.Session)!;
        service.MarkRead(oldest.Id);
        for (var index = 0; index < NotificationHistoryService.MaxEntries; index++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            service.Add(
                $"通知 {index}",
                null,
                NotificationType.Information,
                NotificationRetention.Session);
        }

        var snapshot = service.GetSnapshot();
        Assert.HasCount(NotificationHistoryService.MaxEntries, snapshot);
        Assert.IsFalse(snapshot.Any(entry => entry.Id == oldest.Id));
    }

    [TestMethod]
    public async Task SessionEntryEviction_PersistsRemovalOfPersistentEntry()
    {
        using var directory = new TemporaryDirectory();
        var statePath = Path.Combine(directory.Path, "history.json");
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero));
        var service = new NotificationHistoryService(statePath, NullLogger.Instance, time);
        var persistent = service.Add(
            "旧持久通知",
            null,
            NotificationType.Warning,
            NotificationRetention.Persistent)!;
        service.MarkRead(persistent.Id);

        for (var index = 0; index < NotificationHistoryService.MaxEntries; index++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            service.Add(
                $"会话通知 {index}",
                null,
                NotificationType.Information,
                NotificationRetention.Session);
        }
        await service.FlushAsync();

        var reloaded = new NotificationHistoryService(statePath, NullLogger.Instance, time);
        Assert.IsEmpty(reloaded.GetSnapshot());
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"diary-notification-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, true);
        }
    }
}
