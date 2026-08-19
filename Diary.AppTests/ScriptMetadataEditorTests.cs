using System.Text.Json;
using Diary.App.Models;
using Diary.Script.Runtime;
using Diary.ScriptBase;

namespace Diary.AppTests;

[TestClass]
public sealed class ScriptMetadataEditorTests
{
    [TestMethod]
    public async Task WriteAsync_CreatesMetadataFileWhenMissing()
    {
        var root = CreateRoot();
        try
        {
            var sourcePath = Path.Combine(root, "auto.cs");

            await ScriptMetadataEditor.WriteAsync(sourcePath, "名称", "描述", "daily 09:30", true);

            var json = await File.ReadAllTextAsync(sourcePath + ".json");
            var metadata = JsonSerializer.Deserialize<ScriptFileMetadata>(json);
            Assert.IsNotNull(metadata);
            Assert.AreEqual("名称", metadata!.Name);
            Assert.AreEqual("描述", metadata.Description);
            Assert.AreEqual("daily 09:30", metadata.Schedule);
            Assert.IsTrue(metadata.RunOnStartup);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task WriteAsync_WritesAutomationTriggers()
    {
        var root = CreateRoot();
        try
        {
            var sourcePath = Path.Combine(root, "auto.cs");

            await ScriptMetadataEditor.WriteAsync(
                sourcePath,
                null,
                null,
                null,
                false,
                [
                    ScriptAutomationTriggerKind.WorkItemCreated,
                    ScriptAutomationTriggerKind.TagAdded,
                    ScriptAutomationTriggerKind.WorkItemCreated,
                ]);

            var metadata = JsonSerializer.Deserialize<ScriptFileMetadata>(
                await File.ReadAllTextAsync(sourcePath + ".json"));
            Assert.IsNotNull(metadata);
            CollectionAssert.AreEqual(
                new[]
                {
                    ScriptAutomationTriggerKind.WorkItemCreated,
                    ScriptAutomationTriggerKind.TagAdded,
                },
                metadata!.Triggers!.ToArray());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task WriteAsync_PreservesUnknownFields()
    {
        var root = CreateRoot();
        try
        {
            var sourcePath = Path.Combine(root, "auto.cs");
            await File.WriteAllTextAsync(
                sourcePath + ".json",
                """{"entryKind":3,"customField":{"nested":42},"description":"旧描述"}""");

            await ScriptMetadataEditor.WriteAsync(sourcePath, "新名称", null, null, false);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(sourcePath + ".json"));
            var rootElement = document.RootElement;
            Assert.AreEqual(3, rootElement.GetProperty("entryKind").GetInt32());
            Assert.AreEqual(42, rootElement.GetProperty("customField").GetProperty("nested").GetInt32());
            Assert.AreEqual("新名称", rootElement.GetProperty("Name").GetString());
            Assert.IsFalse(rootElement.TryGetProperty("Description", out _));
            Assert.IsFalse(rootElement.TryGetProperty("Schedule", out _));
            Assert.IsFalse(rootElement.GetProperty("RunOnStartup").GetBoolean());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task WriteAsync_RejectsInvalidScheduleWithoutWriting()
    {
        var root = CreateRoot();
        try
        {
            var sourcePath = Path.Combine(root, "auto.cs");
            await File.WriteAllTextAsync(sourcePath + ".json", """{"name":"原名"}""");

            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                ScriptMetadataEditor.WriteAsync(sourcePath, null, null, "hourly", false).AsTask());

            StringAssert.Contains(await File.ReadAllTextAsync(sourcePath + ".json"), "原名");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task WriteAsync_CSharpModeRemovesIdentityAndWritesRunDefaults()
    {
        var root = CreateRoot();
        try
        {
            var sourcePath = Path.Combine(root, "sample.cs");
            await File.WriteAllTextAsync(
                sourcePath + ".json",
                """{"id":"old","name":"旧名称","engine":"csharp","scope":1,"entryKind":3,"custom":42}""");

            await ScriptMetadataEditor.WriteAsync(
                sourcePath,
                "ignored",
                "ignored",
                null,
                false,
                defaultArguments: new Dictionary<string, string> { ["range"] = "today" },
                timeoutSeconds: 60,
                updateIdentity: false);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(sourcePath + ".json"));
            Assert.IsFalse(document.RootElement.TryGetProperty("Id", out _));
            Assert.IsFalse(document.RootElement.TryGetProperty("Name", out _));
            Assert.IsFalse(document.RootElement.TryGetProperty("Engine", out _));
            Assert.IsFalse(document.RootElement.TryGetProperty("Scope", out _));
            Assert.IsFalse(document.RootElement.TryGetProperty("EntryKind", out _));
            Assert.AreEqual("today", document.RootElement.GetProperty("DefaultArguments").GetProperty("range").GetString());
            Assert.AreEqual(60, document.RootElement.GetProperty("TimeoutSeconds").GetInt32());
            Assert.AreEqual(42, document.RootElement.GetProperty("custom").GetInt32());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"diary-script-metadata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
