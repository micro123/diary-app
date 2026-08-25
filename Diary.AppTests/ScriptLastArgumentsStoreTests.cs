using Diary.App.Services;
using Diary.ScriptBase;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diary.AppTests;

[TestClass]
public sealed class ScriptLastArgumentsStoreTests
{
    [TestMethod]
    public async Task SaveAndRestoreV2ArgumentsByScope()
    {
        using var temporary = new TemporaryDirectory();
        var store = CreateStore(temporary.Path);
        var descriptor = CreateDescriptor(maximum: "24");
        var day = new ScriptLastArgumentsScope("test", ScriptEntryKind.Editor, ScriptEditorTargetKind.Day);
        var month = new ScriptLastArgumentsScope("test", ScriptEntryKind.Editor, ScriptEditorTargetKind.Month);

        await store.SaveV2Async(day, descriptor, new Dictionary<string, string> { ["hours"] = "8" });

        Assert.AreEqual("8", (await store.GetAsync(day, descriptor))!.Arguments!["hours"]);
        Assert.IsNull(await store.GetAsync(month, descriptor));
    }

    [TestMethod]
    public async Task SchemaChangeKeepsValidFieldsAndDropsInvalidFields()
    {
        using var temporary = new TemporaryDirectory();
        var scope = new ScriptLastArgumentsScope("test", ScriptEntryKind.Application);
        var store = CreateStore(temporary.Path);
        await store.SaveV2Async(
            scope,
            CreateDescriptor(maximum: "24"),
            new Dictionary<string, string>
            {
                ["hours"] = "20",
                ["title"] = "ok",
            });

        var restored = await store.GetAsync(scope, CreateDescriptor(maximum: "12"));

        Assert.IsNotNull(restored);
        Assert.IsFalse(restored.Arguments!.ContainsKey("hours"));
        Assert.AreEqual("ok", restored.Arguments["title"]);
    }

    [TestMethod]
    public async Task CorruptFileIsIgnoredAndCanBeReplaced()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = Path.Combine(temporary.Path, "last-arguments.json");
        await File.WriteAllTextAsync(statePath, "not json");
        var store = new ScriptLastArgumentsStore(statePath, NullLogger.Instance);
        var scope = new ScriptLastArgumentsScope("test", ScriptEntryKind.Application);
        var descriptor = CreateDescriptor(maximum: "24");

        Assert.IsNull(await store.GetAsync(scope, descriptor));
        await store.SaveV2Async(scope, descriptor, new Dictionary<string, string> { ["hours"] = "4" });

        var reloaded = CreateStore(temporary.Path);
        Assert.AreEqual("4", (await reloaded.GetAsync(scope, descriptor))!.Arguments!["hours"]);
    }

    [TestMethod]
    public async Task ClearAllRemovesEveryRememberedScope()
    {
        using var temporary = new TemporaryDirectory();
        var store = CreateStore(temporary.Path);
        var descriptor = CreateDescriptor(maximum: "24");
        var application = new ScriptLastArgumentsScope("test", ScriptEntryKind.Application);
        var editor = new ScriptLastArgumentsScope("test", ScriptEntryKind.Editor, ScriptEditorTargetKind.Day);
        await store.SaveV2Async(application, descriptor, new Dictionary<string, string> { ["hours"] = "4" });
        await store.SaveV2Async(editor, descriptor, new Dictionary<string, string> { ["hours"] = "8" });

        await store.ClearAllAsync();

        Assert.IsNull(await store.GetAsync(application, descriptor));
        Assert.IsNull(await store.GetAsync(editor, descriptor));
    }

    private static ScriptLastArgumentsStore CreateStore(string directory) =>
        new(Path.Combine(directory, "last-arguments.json"), NullLogger.Instance);

    private static ScriptDescriptor CreateDescriptor(string maximum) =>
        new(
            "test",
            "Test",
            ScriptApiVersion.V2,
            ScriptScope.Application,
            EntryKind: ScriptEntryKind.Application,
            Parameters:
            [
                new ScriptParameterDefinition(
                    "hours",
                    "Hours",
                    ScriptParameterType.Number,
                    Constraints: new(Minimum: "0", Maximum: maximum, Step: "0.5")),
                new ScriptParameterDefinition(
                    "title",
                    "Title",
                    ScriptParameterType.String,
                    Constraints: new(MaxLength: 10)),
            ]);

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"DiaryApp-ScriptLastArguments-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, true);
    }
}
