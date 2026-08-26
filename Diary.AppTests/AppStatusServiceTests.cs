using Diary.App.Services;
using Diary.App.ViewModels;
using Diary.Core.Constants;
using Diary.GUIBase.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Avalonia.Controls.Notifications;

namespace Diary.AppTests;

[TestClass]
public sealed class AppStatusServiceTests
{
    [TestMethod]
    public void PersistentStatuses_ArePublishedInSnapshot()
    {
        var service = new AppStatusService();
        var changed = 0;
        service.Changed += (_, _) => changed++;

        service.SetDatabase(new AppStatusItem("SQLite", "已连接", AppStatusLevel.Success));
        service.SetTracker(new AppStatusItem("Tracker 1/2", "一个实例不可用", AppStatusLevel.Warning));
        service.SetUpdate(new AppStatusItem("可更新", "版本 2", AppStatusLevel.Warning));

        var snapshot = service.GetSnapshot();
        Assert.AreEqual("SQLite", snapshot.Database.Text);
        Assert.AreEqual(AppStatusLevel.Success, snapshot.Database.Level);
        Assert.AreEqual("Tracker 1/2", snapshot.Tracker.Text);
        Assert.AreEqual("可更新", snapshot.Update?.Text);
        Assert.AreEqual(3, changed);
    }

    [TestMethod]
    public void TaskHandle_ReportsProgressAndRemovesTaskWhenDisposed()
    {
        var service = new AppStatusService();

        using (var task = service.BeginTask("下载更新"))
        {
            task.Report(0.5, "已下载一半");
            var active = service.GetSnapshot().Tasks.Single();
            Assert.AreEqual("下载更新", active.Name);
            Assert.AreEqual(0.5, active.Progress);
            Assert.AreEqual("已下载一半", active.Detail);
        }

        Assert.IsEmpty(service.GetSnapshot().Tasks);
    }

    [TestMethod]
    public void StatusBarViewModel_MapsOptionalAndTaskStates()
    {
        var service = new AppStatusService();
        service.SetDatabase(new AppStatusItem("PostgreSQL", "远程数据库", AppStatusLevel.Success));
        service.ShowMessage(
            "保存完成",
            AppStatusLevel.Success,
            duration: TimeSpan.FromMinutes(1));
        service.SetUpdate(new AppStatusItem("更新失败", "网络错误", AppStatusLevel.Error));
        using var task = service.BeginTask("备份数据库", "正在写入文件");
        task.Report(0.25, "已完成四分之一");
        var history = new NotificationHistoryService(
            Path.Combine(Path.GetTempPath(), $"diary-status-tests-{Guid.NewGuid():N}", "history.json"),
            NullLogger.Instance);
        history.Add(
            "数据库备份完成",
            "备份文件已保存",
            NotificationType.Success,
            NotificationRetention.Session);

        using var viewModel = new StatusBarViewModel(service, history);

        Assert.AreEqual("PostgreSQL", viewModel.Database.Text);
        Assert.IsTrue(viewModel.Database.IsSuccess);
        Assert.IsTrue(viewModel.HasMessage);
        Assert.AreEqual("保存完成", viewModel.Message.Text);
        Assert.IsTrue(viewModel.HasUpdate);
        Assert.IsTrue(viewModel.Update.IsError);
        Assert.IsTrue(viewModel.HasTasks);
        Assert.AreEqual("备份数据库 25%", viewModel.TaskSummary.Text);
        Assert.AreEqual(25, viewModel.Tasks.Single().ProgressValue);
        Assert.IsTrue(viewModel.HasNotifications);
        Assert.AreEqual("1", viewModel.UnreadNotificationText);
    }

    [TestMethod]
    public void Report_RejectsProgressOutsideValidRange()
    {
        var service = new AppStatusService();
        using var task = service.BeginTask("测试任务");

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => task.Report(-0.1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => task.Report(1.1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => task.Report(double.NaN));
    }

    [TestMethod]
    public void StatusBarViewModel_HidesOpenPathActionWhenTargetNoLongerExists()
    {
        var service = new AppStatusService();
        var history = new NotificationHistoryService(
            Path.Combine(Path.GetTempPath(), $"diary-status-tests-{Guid.NewGuid():N}", "history.json"),
            NullLogger.Instance);
        history.Add(
            "导出完成",
            "文件可能已经移动。",
            NotificationType.Success,
            NotificationRetention.Session,
            new NotificationAction("打开文件", CommandNames.OpenPath, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));

        using var viewModel = new StatusBarViewModel(service, history);

        Assert.IsFalse(viewModel.Notifications.Single().HasAction);
    }
}
