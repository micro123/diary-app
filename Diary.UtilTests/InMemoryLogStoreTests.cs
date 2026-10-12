using Diary.Utils;
using Serilog;
using Serilog.Events;

namespace Diary.UtilTests;

[TestClass]
public class InMemoryLogStoreTests
{
    [TestMethod]
    public void SinkRetainsOnlyMostRecentEntries()
    {
        var store = new InMemoryLogStore(3);
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(new InMemoryLogSink(store))
            .CreateLogger();

        for (var index = 0; index < 5; index++)
            logger.Information("日志 {Index}", index);

        var entries = store.GetSnapshot();
        Assert.HasCount(3, entries);
        CollectionAssert.AreEqual(
            new[] { "日志 2", "日志 3", "日志 4" },
            entries.Select(entry => entry.Message).ToArray());
        CollectionAssert.AreEqual(new long[] { 3, 4, 5 }, entries.Select(entry => entry.Sequence).ToArray());
    }

    [TestMethod]
    public void SinkCapturesSourcePropertiesAndException()
    {
        var store = new InMemoryLogStore(10);
        using var logger = new LoggerConfiguration()
            .WriteTo.Sink(new InMemoryLogSink(store))
            .CreateLogger();

        logger.ForContext<InMemoryLogStoreTests>()
            .ForContext("ItemId", 42)
            .Error(new InvalidOperationException("测试异常"), "处理事项失败");

        var entries = store.GetSnapshot();
        Assert.HasCount(1, entries);
        var entry = entries[0];
        Assert.AreEqual(LogEventLevel.Error, entry.Level);
        Assert.AreEqual(typeof(InMemoryLogStoreTests).FullName, entry.Source);
        Assert.AreEqual("处理事项失败", entry.Message);
        StringAssert.Contains(entry.Properties, "ItemId: 42");
        StringAssert.Contains(entry.Exception, "InvalidOperationException");
        StringAssert.Contains(entry.ToDisplayText(), "测试异常");
    }

    [TestMethod]
    public void StoreIsThreadSafeAndKeepsCapacityLimit()
    {
        var store = new InMemoryLogStore(1000);
        Parallel.For(0, 3000, index => store.Append(new InMemoryLogEntry(
            0,
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            "test",
            index.ToString(),
            string.Empty,
            null)));

        var entries = store.GetSnapshot();
        Assert.HasCount(1000, entries);
        Assert.AreEqual(1000, entries.Select(entry => entry.Sequence).Distinct().Count());
        Assert.IsTrue(entries.Zip(entries.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence));
    }

    [TestMethod]
    public void ClearRaisesChangedOnlyWhenContentWasRemoved()
    {
        var store = new InMemoryLogStore(2);
        var changeCount = 0;
        store.Changed += (_, _) => changeCount++;

        store.Clear();
        store.Append(new InMemoryLogEntry(
            0,
            DateTimeOffset.UtcNow,
            LogEventLevel.Debug,
            "test",
            "message",
            string.Empty,
            null));
        store.Clear();
        store.Clear();

        Assert.AreEqual(2, changeCount);
        Assert.AreEqual(0, store.Count);
    }

    [TestMethod]
    public void FaultyObserverDoesNotInterruptLogCapture()
    {
        var store = new InMemoryLogStore(2);
        store.Changed += (_, _) => throw new InvalidOperationException("observer failed");

        store.Append(new InMemoryLogEntry(
            0,
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            "test",
            "message",
            string.Empty,
            null));

        Assert.AreEqual(1, store.Count);
    }
}
