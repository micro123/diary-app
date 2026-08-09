using Diary.ScriptBase;
using Diary.ScriptHost;

namespace Diary.ScriptTests;

[TestClass]
public sealed class ScriptIdempotencyStoreTests
{
    [TestMethod]
    public void Store_PersistsResultsAcrossInstancesAndSeparatesScopes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"diary-idempotency-{Guid.NewGuid():N}.json");
        try
        {
            var result = ScriptLogItemResult.Success(
                new ScriptWorkItem(42, "2026-08-09", "记录", 1.5, 0, "备注", []),
                new ScriptEffectSummary(1, false, "same-key", [42]));
            var first = new ScriptIdempotencyStore(path);
            first.Save("logItems.create", "same-key", result);
            first.Save("templateLogItems.create", "same-key", result with
            {
                Effects = result.Effects! with { CreatedWorkItemIds = [43] },
            });

            var second = new ScriptIdempotencyStore(path);
            Assert.IsTrue(second.TryGet("logItems.create", "same-key", out var logResult));
            Assert.IsTrue(second.TryGet("templateLogItems.create", "same-key", out var templateResult));
            Assert.AreEqual(42, logResult.Item!.Id);
            Assert.AreEqual(43, templateResult.Effects!.CreatedWorkItemIds!.Single());
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [TestMethod]
    public void Store_EvictsOldestResultsAfterCapacityIsReached()
    {
        var store = new ScriptIdempotencyStore(maxEntries: 2);
        var result = ScriptLogItemResult.Success(
            new ScriptWorkItem(1, "2026-08-09", "记录", 1, 0, null, []));

        store.Save("logItems.create", "first", result);
        store.Save("logItems.create", "second", result);
        store.Save("logItems.create", "third", result);

        Assert.IsFalse(store.TryGet("logItems.create", "first", out _));
        Assert.IsTrue(store.TryGet("logItems.create", "second", out _));
        Assert.IsTrue(store.TryGet("logItems.create", "third", out _));
    }
}
