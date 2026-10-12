using Diary.App.ViewModels.Dialogs;
using Diary.Utils;
using Serilog.Events;

namespace Diary.AppTests;

[TestClass]
public class LogViewerDialogViewModelTests
{
    [TestMethod]
    public void InitializeShowsNewestEntriesFirstAndSelectsLatest()
    {
        var store = CreateStore(
            (LogEventLevel.Information, "first"),
            (LogEventLevel.Warning, "second"));
        var viewModel = new LogViewerDialogViewModel(store);

        viewModel.Initialize();

        Assert.HasCount(2, viewModel.Entries);
        Assert.AreEqual("second", viewModel.Entries[0].Message);
        Assert.AreSame(viewModel.Entries[0], viewModel.SelectedEntry);
        StringAssert.Contains(viewModel.Summary, "2/1000");

        Append(store, LogEventLevel.Error, "third", "test", string.Empty);
        viewModel.RefreshCommand.Execute(null);
        Assert.AreEqual("third", viewModel.Entries[0].Message);
        viewModel.Deactivate();
    }

    [TestMethod]
    public void LevelAndTextFiltersApplyToMessageAndDetails()
    {
        var store = new InMemoryLogStore(1000);
        Append(store, LogEventLevel.Information, "同步完成", "Tracker", "Count: 3");
        Append(store, LogEventLevel.Error, "请求失败", "Redmine", "Endpoint: issues.json");
        var viewModel = new LogViewerDialogViewModel(store);
        viewModel.Initialize();

        viewModel.SelectedLevel = viewModel.LevelOptions.Single(option => option.Name == "错误与严重");
        Assert.HasCount(1, viewModel.Entries);
        Assert.AreEqual("请求失败", viewModel.Entries[0].Message);

        viewModel.SelectedLevel = viewModel.LevelOptions[0];
        viewModel.SearchText = "issues.json";
        Assert.HasCount(1, viewModel.Entries);
        Assert.AreEqual("Redmine", viewModel.Entries[0].Source);
        viewModel.Deactivate();
    }

    [TestMethod]
    public void ClearCommandClearsStoreAndVisibleEntries()
    {
        var store = CreateStore((LogEventLevel.Information, "message"));
        var viewModel = new LogViewerDialogViewModel(store);
        viewModel.Initialize();

        viewModel.ClearCommand.Execute(null);

        Assert.AreEqual(0, store.Count);
        Assert.IsFalse(viewModel.HasEntries);
        Assert.HasCount(0, viewModel.Entries);
        viewModel.Deactivate();
    }

    [TestMethod]
    public void ResumeRefreshLoadsLogsCapturedWhilePaused()
    {
        var store = CreateStore((LogEventLevel.Information, "first"));
        var viewModel = new LogViewerDialogViewModel(store);
        viewModel.Initialize();
        viewModel.ToggleRefreshCommand.Execute(null);

        Append(store, LogEventLevel.Warning, "while paused", "test", string.Empty);
        Assert.AreEqual("first", viewModel.Entries[0].Message);

        viewModel.ToggleRefreshCommand.Execute(null);
        Assert.IsFalse(viewModel.IsRefreshPaused);
        Assert.AreEqual("while paused", viewModel.Entries[0].Message);
        viewModel.Deactivate();
    }

    private static InMemoryLogStore CreateStore(params (LogEventLevel Level, string Message)[] entries)
    {
        var store = new InMemoryLogStore(1000);
        foreach (var entry in entries)
            Append(store, entry.Level, entry.Message, "test", string.Empty);
        return store;
    }

    private static void Append(
        InMemoryLogStore store,
        LogEventLevel level,
        string message,
        string source,
        string properties)
    {
        store.Append(new InMemoryLogEntry(
            0,
            DateTimeOffset.UtcNow,
            level,
            source,
            message,
            properties,
            null));
    }
}
