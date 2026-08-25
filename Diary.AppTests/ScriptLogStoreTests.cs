using Diary.App.Models;
using Diary.ScriptBase;

namespace Diary.AppTests;

[TestClass]
public sealed class ScriptLogStoreTests
{
    [TestMethod]
    public void Append_PreservesEntryAndRaisesChanged()
    {
        var store = new ScriptLogStore();
        var changes = 0;
        store.Changed += (_, _) => changes++;

        store.Append(ScriptLogLevel.Warning, "script warning", "weekly_overwork");

        var entries = store.GetSnapshot();
        Assert.AreEqual(1, entries.Count);
        var entry = entries[0];
        Assert.AreEqual(ScriptLogLevel.Warning, entry.Level);
        Assert.AreEqual("script warning", entry.Message);
        Assert.AreEqual("weekly_overwork", entry.ScriptId);
        StringAssert.Matches(
            entry.DisplayText,
            new System.Text.RegularExpressions.Regex(
                @"^\[\d{2}-\d{2} \d{2}:\d{2}:\d{2}\] \[WRN\] \[weekly_overwork\] script warning$"));
        Assert.AreEqual(1, changes);
    }

    [TestMethod]
    public void FormatText_JoinsDisplayLinesForClipboardAndSelection()
    {
        var timestamp = new DateTimeOffset(2026, 8, 25, 14, 30, 0, TimeSpan.FromHours(8));
        var entries = new[]
        {
            new ScriptLogEntry(timestamp, ScriptLogLevel.Info, "starting", "weekly_overwork"),
            new ScriptLogEntry(timestamp.AddMilliseconds(125), ScriptLogLevel.Error, "failed", "weekly_overwork"),
        };

        var text = ScriptLogStore.FormatText(entries);

        var lines = text.Split(Environment.NewLine);
        Assert.AreEqual(2, lines.Length);
        Assert.AreEqual("[08-25 14:30:00] [INF] [weekly_overwork] starting", lines[0]);
        Assert.AreEqual("[08-25 14:30:00] [ERR] [weekly_overwork] failed", lines[1]);
        Assert.DoesNotContain("Execution", text);
        Assert.DoesNotContain("2026", text);
        Assert.DoesNotContain(".125", text);
    }

    [TestMethod]
    public void Append_RetainsOnlyTheNewestEntries()
    {
        var store = new ScriptLogStore();

        for (var index = 0; index <= ScriptLogStore.MaxEntryCount; index++)
            store.Append(ScriptLogLevel.Info, index.ToString());

        var entries = store.GetSnapshot();
        Assert.AreEqual(ScriptLogStore.MaxEntryCount, entries.Count);
        Assert.AreEqual("1", entries[0].Message);
        Assert.AreEqual(ScriptLogStore.MaxEntryCount.ToString(), entries[^1].Message);
    }

    [TestMethod]
    public void Clear_RemovesEntriesAndRaisesChanged()
    {
        var store = new ScriptLogStore();
        var changes = 0;
        store.Changed += (_, _) => changes++;
        store.Append(ScriptLogLevel.Info, "script info");

        store.Clear();

        Assert.AreEqual(0, store.GetSnapshot().Count);
        Assert.AreEqual(2, changes);
    }
}
