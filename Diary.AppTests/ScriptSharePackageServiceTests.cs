using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diary.App.Services;
using Diary.Script.Runtime;
using Diary.ScriptBase;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diary.AppTests;

[TestClass]
public sealed class ScriptSharePackageServiceTests
{
    [TestMethod]
    public async Task ExportAndImportAsync_RoundTripsMultipleScriptsAndMetadata()
    {
        var sourceRoot = CreateRoot();
        var targetRoot = CreateRoot();
        var packagePath = Path.Combine(CreateRoot(), "shared.diaryscripts");
        try
        {
            var appSource = await CreateScriptAsync(sourceRoot, "application", "alpha.cs", "// alpha", """{"TimeoutSeconds":30}""");
            var editorSource = await CreateScriptAsync(
                sourceRoot,
                "editor",
                "beta.py",
                """
                def editor_main(context):
                    return None

                """,
                null);
            var service = CreateService();

            await service.ExportAsync(packagePath, sourceRoot,
            [
                new(appSource, "alpha", "Alpha", ScriptScope.Application, ScriptEntryKind.Application, "C#"),
                new(editorSource, "beta", "Beta", ScriptScope.Editor, ScriptEntryKind.Editor, "Python"),
            ]);

            var preview = await service.InspectAsync(packagePath, targetRoot, []);
            Assert.AreEqual(2, preview.Items.Count);
            Assert.IsTrue(preview.Items.All(item => !item.HasConflict));

            var result = await service.ImportAsync(
                preview,
                targetRoot,
                preview.Items.Select(item => new ScriptShareImportDecision(item.Id, false)).ToArray(),
                []);

            Assert.AreEqual(2, result.ImportedCount);
            Assert.AreEqual("// alpha", await File.ReadAllTextAsync(Path.Combine(targetRoot, "application", "alpha.cs")));
            StringAssert.Contains(
                await File.ReadAllTextAsync(Path.Combine(targetRoot, "application", "alpha.cs.json")),
                "TimeoutSeconds");
            StringAssert.Contains(
                await File.ReadAllTextAsync(Path.Combine(targetRoot, "editor", "beta.py")),
                "editor_main");
        }
        finally
        {
            DeleteRoots(sourceRoot, targetRoot, Path.GetDirectoryName(packagePath)!);
        }
    }

    [TestMethod]
    public async Task ExportAsync_UsesLoadedMetadataWhenSidecarDoesNotExist()
    {
        var sourceRoot = CreateRoot();
        var targetRoot = CreateRoot();
        var packageDirectory = CreateRoot();
        var packagePath = Path.Combine(packageDirectory, "shared.diaryscripts");
        try
        {
            var packageSourceDirectory = Path.Combine(sourceRoot, "application", "portable-lua");
            Directory.CreateDirectory(packageSourceDirectory);
            var source = Path.Combine(packageSourceDirectory, "main.lua");
            await File.WriteAllTextAsync(source, "return true");
            var metadata = new ScriptFileMetadata(
                Id: "portable-lua",
                Name: "Portable Lua",
                Engine: "lua",
                Scope: ScriptScope.Application,
                EntryKind: ScriptEntryKind.Automation,
                Schedule: "daily 08:30");
            var service = CreateService();

            await service.ExportAsync(packagePath, sourceRoot,
            [
                new(
                    source,
                    "portable-lua",
                    "Portable Lua",
                    ScriptScope.Application,
                    ScriptEntryKind.Automation,
                    "Lua",
                    metadata),
            ]);
            var preview = await service.InspectAsync(packagePath, targetRoot, []);
            await service.ImportAsync(
                preview,
                targetRoot,
                [new ScriptShareImportDecision("portable-lua", false)],
                []);

            var importedMetadataPath = Path.Combine(targetRoot, "application", "main.lua.json");
            Assert.IsTrue(File.Exists(importedMetadataPath));
            var importedMetadata = JsonSerializer.Deserialize<ScriptFileMetadata>(
                await File.ReadAllTextAsync(importedMetadataPath));
            Assert.IsNotNull(importedMetadata);
            Assert.AreEqual("portable-lua", importedMetadata.Id);
            Assert.AreEqual("daily 08:30", importedMetadata.Schedule);
        }
        finally
        {
            DeleteRoots(sourceRoot, targetRoot, packageDirectory);
        }
    }

    [TestMethod]
    public async Task ImportAsync_RequiresExplicitReplaceForConflict()
    {
        var sourceRoot = CreateRoot();
        var targetRoot = CreateRoot();
        var packageDirectory = CreateRoot();
        var packagePath = Path.Combine(packageDirectory, "shared.diaryscripts");
        try
        {
            var source = await CreateScriptAsync(sourceRoot, "application", "alpha.cs", "// new", """{"TimeoutSeconds":60}""");
            var existing = await CreateScriptAsync(targetRoot, "application", "old-alpha.cs", "// old", """{"TimeoutSeconds":10}""");
            var service = CreateService();
            await service.ExportAsync(packagePath, sourceRoot,
            [
                new(source, "alpha", "Alpha", ScriptScope.Application, ScriptEntryKind.Application, "C#"),
            ]);
            var existingItems = new[] { new ScriptShareExistingItem("alpha", existing) };
            var preview = await service.InspectAsync(packagePath, targetRoot, existingItems);
            Assert.IsTrue(preview.Items[0].HasConflict);

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ImportAsync(
                preview,
                targetRoot,
                [new ScriptShareImportDecision("alpha", false)],
                existingItems).AsTask());

            var result = await service.ImportAsync(
                preview,
                targetRoot,
                [new ScriptShareImportDecision("alpha", true)],
                existingItems);

            Assert.AreEqual(1, result.ImportedCount);
            Assert.IsTrue(File.Exists(existing));
            Assert.AreEqual("// new", await File.ReadAllTextAsync(existing));
            StringAssert.Contains(await File.ReadAllTextAsync(existing + ".json"), "60");
        }
        finally
        {
            DeleteRoots(sourceRoot, targetRoot, packageDirectory);
        }
    }

    [TestMethod]
    public async Task InspectAsync_RejectsTamperedSourceChecksum()
    {
        var sourceRoot = CreateRoot();
        var targetRoot = CreateRoot();
        var packageDirectory = CreateRoot();
        var packagePath = Path.Combine(packageDirectory, "shared.diaryscripts");
        try
        {
            var source = await CreateScriptAsync(sourceRoot, "application", "alpha.cs", "// alpha", null);
            var service = CreateService();
            await service.ExportAsync(packagePath, sourceRoot,
            [
                new(source, "alpha", "Alpha", ScriptScope.Application, ScriptEntryKind.Application, "C#"),
            ]);

            using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Update))
            {
                var sourceEntry = archive.Entries.Single(entry => entry.FullName.EndsWith("alpha.cs", StringComparison.Ordinal));
                sourceEntry.Delete();
                var replacement = archive.CreateEntry("scripts/000/alpha.cs");
                await using var writer = new StreamWriter(replacement.Open());
                await writer.WriteAsync("// tampered");
            }

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                service.InspectAsync(packagePath, targetRoot, []).AsTask());
        }
        finally
        {
            DeleteRoots(sourceRoot, targetRoot, packageDirectory);
        }
    }

    [TestMethod]
    public async Task InspectAsync_RejectsTraversalPathInManifest()
    {
        var sourceRoot = CreateRoot();
        var targetRoot = CreateRoot();
        var packageDirectory = CreateRoot();
        var packagePath = Path.Combine(packageDirectory, "shared.diaryscripts");
        try
        {
            var source = await CreateScriptAsync(sourceRoot, "application", "alpha.cs", "// alpha", null);
            var service = CreateService();
            await service.ExportAsync(packagePath, sourceRoot,
            [
                new(source, "alpha", "Alpha", ScriptScope.Application, ScriptEntryKind.Application, "C#"),
            ]);

            using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Update))
            {
                var manifestEntry = archive.GetEntry("manifest.json")!;
                JsonObject manifest;
                await using (var stream = manifestEntry.Open())
                    manifest = (await JsonNode.ParseAsync(stream))!.AsObject();
                manifestEntry.Delete();
                manifest["scripts"]![0]!["source_path"] = "../alpha.cs";
                var replacement = archive.CreateEntry("manifest.json");
                await using var streamWriter = replacement.Open();
                await JsonSerializer.SerializeAsync(streamWriter, manifest);
            }

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                service.InspectAsync(packagePath, targetRoot, []).AsTask());
        }
        finally
        {
            DeleteRoots(sourceRoot, targetRoot, packageDirectory);
        }
    }

    [TestMethod]
    public async Task ImportAsync_RollsBackEarlierFilesWhenLaterWriteFails()
    {
        var sourceRoot = CreateRoot();
        var targetRoot = CreateRoot();
        var packageDirectory = CreateRoot();
        var packagePath = Path.Combine(packageDirectory, "shared.diaryscripts");
        try
        {
            var first = await CreateScriptAsync(sourceRoot, "application", "a.cs", "// a", null);
            var second = await CreateScriptAsync(sourceRoot, "editor", "b.py", "# b", null);
            var service = CreateService();
            await service.ExportAsync(packagePath, sourceRoot,
            [
                new(first, "a-script", "A", ScriptScope.Application, ScriptEntryKind.Application, "C#"),
                new(second, "b-script", "B", ScriptScope.Editor, ScriptEntryKind.Editor, "Python"),
            ]);
            var preview = await service.InspectAsync(packagePath, targetRoot, []);
            await File.WriteAllTextAsync(Path.Combine(targetRoot, "editor"), "blocks directory creation");

            await Assert.ThrowsExactlyAsync<IOException>(() => service.ImportAsync(
                preview,
                targetRoot,
                preview.Items.Select(item => new ScriptShareImportDecision(item.Id, false)).ToArray(),
                []).AsTask());

            Assert.IsFalse(File.Exists(Path.Combine(targetRoot, "application", "a.cs")));
            Assert.IsTrue(File.Exists(Path.Combine(targetRoot, "editor")));
        }
        finally
        {
            DeleteRoots(sourceRoot, targetRoot, packageDirectory);
        }
    }

    [TestMethod]
    public async Task InspectAsync_RejectsManifestUndeclaredEntry()
    {
        var sourceRoot = CreateRoot();
        var targetRoot = CreateRoot();
        var packageDirectory = CreateRoot();
        var packagePath = Path.Combine(packageDirectory, "shared.diaryscripts");
        try
        {
            var source = await CreateScriptAsync(sourceRoot, "application", "alpha.cs", "// alpha", null);
            var service = CreateService();
            await service.ExportAsync(packagePath, sourceRoot,
            [
                new(source, "alpha", "Alpha", ScriptScope.Application, ScriptEntryKind.Application, "C#"),
            ]);
            using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Update))
            {
                var extra = archive.CreateEntry("scripts/000/hidden.txt");
                await using var writer = new StreamWriter(extra.Open());
                await writer.WriteAsync("hidden");
            }

            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                service.InspectAsync(packagePath, targetRoot, []).AsTask());
        }
        finally
        {
            DeleteRoots(sourceRoot, targetRoot, packageDirectory);
        }
    }

    private static ScriptSharePackageService CreateService() =>
        new(NullLogger<ScriptSharePackageService>.Instance);

    private static async Task<string> CreateScriptAsync(
        string root,
        string scope,
        string fileName,
        string source,
        string? metadata)
    {
        var directory = Path.Combine(root, scope);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        await File.WriteAllTextAsync(path, source);
        if (metadata is not null)
            await File.WriteAllTextAsync(path + ".json", metadata);
        return path;
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"diary-script-share-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoots(params string[] roots)
    {
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
}
