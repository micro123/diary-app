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

        store.Append(ScriptLogLevel.Warning, "script warning");

        var entries = store.GetSnapshot();
        Assert.AreEqual(1, entries.Count);
        var entry = entries[0];
        Assert.AreEqual(ScriptLogLevel.Warning, entry.Level);
        Assert.AreEqual("script warning", entry.Message);
        Assert.Contains("[警告]", entry.DisplayText);
        Assert.AreEqual(1, changes);
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
