using Diary.Script.Runtime;
using Diary.ScriptBase;

namespace Diary.ScriptTests;

[TestClass]
public sealed class ScriptDirectoryLoaderTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), $"diary-script-tests-{Guid.NewGuid():N}");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    [TestMethod]
    public async Task LoadAsync_RegistersScriptAndCreatesDirectories()
    {
        var loader = CreateLoader(out var catalog);
        var sourcePath = await WriteScriptAsync("application", "sample.fake", "sample");

        var result = await loader.LoadAsync(_root);

        Assert.AreEqual(1, result.Entries.Length);
        Assert.IsTrue(result.Entries[0].BuildResult!.Succeeded);
        Assert.IsTrue(catalog.TryGet("sample", out _));
        Assert.IsTrue(Directory.Exists(Path.Combine(_root, "editor")));
        Assert.AreEqual(sourcePath, result.Entries[0].SourcePath);
    }

    [TestMethod]
    public async Task LoadAsync_IgnoresLegacyDisabledFlag()
    {
        var loader = CreateLoader(out var catalog);
        var sourcePath = await WriteScriptAsync("application", "disabled.fake", "disabled");
        await File.WriteAllTextAsync(sourcePath + ".json", """{"enabled":false}""");

        var result = await loader.LoadAsync(_root);

        Assert.AreEqual(1, result.Entries.Length);
        Assert.IsTrue(result.Entries[0].BuildResult!.Succeeded);
        Assert.IsTrue(catalog.TryGet("disabled", out _));
    }

    [TestMethod]
    public async Task LoadAsync_IgnoresLegacyDisabledPackageFlag()
    {
        var loader = CreateLoader(out var catalog);
        var packageDirectory = Path.Combine(_root, "application", "disabled-package");
        Directory.CreateDirectory(packageDirectory);
        await File.WriteAllTextAsync(Path.Combine(packageDirectory, "main.fake"), "disabled-package");
        await File.WriteAllTextAsync(
            Path.Combine(packageDirectory, "manifest.json"),
            """{"entry":"main.fake","id":"disabled-package","enabled":false}""");

        var result = await loader.LoadAsync(_root);

        var entry = result.Entries.Single(item => item.SourcePath.EndsWith("main.fake", StringComparison.Ordinal));
        Assert.IsTrue(entry.BuildResult!.Succeeded);
        Assert.IsTrue(catalog.TryGet("disabled-package", out _));
    }

    [TestMethod]
    public async Task LoadAsync_ReportsInvalidMetadataWithoutStoppingOtherScripts()
    {
        var loader = CreateLoader(out var catalog);
        var brokenPath = await WriteScriptAsync("application", "broken.fake", "broken");
        await File.WriteAllTextAsync(brokenPath + ".json", "{");
        await WriteScriptAsync("application", "good.fake", "good");

        var result = await loader.LoadAsync(_root);

        Assert.IsTrue(result.Diagnostics.Any(item => item.Code == "SCRIPT_METADATA_INVALID"));
        Assert.IsTrue(catalog.TryGet("good", out _));
        Assert.IsFalse(catalog.TryGet("broken", out _));
    }

    [TestMethod]
    public async Task LoadAsync_RejectsMetadataThatDoesNotMatchDescriptor()
    {
        var loader = CreateLoader(out var catalog);
        var sourcePath = await WriteScriptAsync("application", "mismatch.fake", "actual");
        await File.WriteAllTextAsync(sourcePath + ".json", """{"id":"declared"}""");

        var result = await loader.LoadAsync(_root);

        Assert.IsTrue(result.Diagnostics.Any(item => item.Code == "SCRIPT_METADATA_MISMATCH"));
        Assert.IsFalse(result.Entries.Single().BuildResult!.Succeeded);
        Assert.IsFalse(catalog.TryGet("actual", out _));
    }

    [TestMethod]
    public async Task LoadAsync_RejectsScopeThatDoesNotMatchDirectory()
    {
        var loader = CreateLoader(out var catalog);
        await WriteScriptAsync("editor", "wrong.fake", "application:wrong");

        var result = await loader.LoadAsync(_root);

        Assert.AreEqual("SCRIPT_ENTRY_KIND_MISMATCH", result.Diagnostics.Single().Code);
        Assert.IsFalse(result.Entries.Single().BuildResult!.Succeeded);
        Assert.IsFalse(catalog.TryGet("wrong", out _));
    }

    [TestMethod]
    public async Task LoadAsync_ReportsDuplicateIdAndIgnoresUnsupportedFiles()
    {
        var loader = CreateLoader(out var catalog);
        await WriteScriptAsync("application", "first.fake", "same");
        await WriteScriptAsync("application", "second.fake", "same");
        await WriteScriptAsync("application", "notes.txt", "ignored");

        var result = await loader.LoadAsync(_root);

        Assert.AreEqual(2, result.Entries.Length);
        Assert.IsTrue(result.Diagnostics.Any(item => item.Code == "SCRIPT_ID_DUPLICATE"));
        Assert.IsTrue(catalog.TryGet("same", out _));
    }

    [TestMethod]
    public async Task LoadAsync_LoadsPackageAndRejectsEntryOutsidePackage()
    {
        var loader = CreateLoader(out var catalog);
        var packageDirectory = Path.Combine(_root, "application", "package");
        Directory.CreateDirectory(packageDirectory);
        await File.WriteAllTextAsync(Path.Combine(packageDirectory, "main.fake"), "packaged");
        await File.WriteAllTextAsync(
            Path.Combine(packageDirectory, "manifest.json"),
            """{"entry":"main.fake","id":"packaged"}""");
        var invalidPackage = Path.Combine(_root, "application", "invalid");
        Directory.CreateDirectory(invalidPackage);
        await File.WriteAllTextAsync(
            Path.Combine(invalidPackage, "manifest.json"),
            """{"entry":"../outside.fake"}""");

        var result = await loader.LoadAsync(_root);

        Assert.IsTrue(catalog.TryGet("packaged", out _));
        Assert.IsTrue(result.Diagnostics.Any(item => item.Code == "SCRIPT_PACKAGE_INVALID"));
        Assert.IsTrue(result.Entries.Any(entry => entry.BuildResult?.Succeeded == true));
        Assert.IsTrue(catalog.TryGet("packaged", out _));
    }

    [TestMethod]
    public async Task LoadAsync_PreservesSelectedEngineForWorkerRouting()
    {
        var registry = new ScriptEngineRegistry();
        registry.Register(new EngineProbe("lua", ".lua"));
        registry.Register(new EngineProbe("python", ".py"));
        var catalog = new ScriptCatalog();
        var loader = new ScriptDirectoryLoader(registry, new ScriptBuildService(registry), catalog);
        await WriteScriptAsync("application", "lua-app.lua", "lua-app");
        await WriteScriptAsync("application", "python-app.py", "python-app");

        var result = await loader.LoadAsync(_root);

        Assert.IsTrue(result.Entries.All(entry => entry.BuildResult?.Succeeded == true));
        Assert.AreEqual("lua", CatalogSource(catalog, "lua-app").EngineName);
        Assert.AreEqual("python", CatalogSource(catalog, "python-app").EngineName);
    }

    private static ScriptSourceInfo CatalogSource(ScriptCatalog catalog, string id)
    {
        Assert.IsTrue(catalog.TryGetSource(id, out var source));
        return source!;
    }

    [TestMethod]
    public async Task LoadAsync_PassesAutomationScheduleThroughEntryMetadata()
    {
        var loader = CreateLoader(out _);
        var sourcePath = await WriteScriptAsync("application", "auto.fake", "auto");
        await File.WriteAllTextAsync(sourcePath + ".json",
            """{"entryKind":3,"schedule":"daily 09:00"}""");

        var result = await loader.LoadAsync(_root);

        var entry = result.Entries.Single();
        Assert.IsTrue(entry.BuildResult!.Succeeded);
        Assert.AreEqual("daily 09:00", entry.Metadata!.Schedule);
        Assert.IsFalse(entry.Metadata.RunOnStartup);
    }

    [TestMethod]
    public async Task LoadAsync_AllowsEventOnlyAutomationAndPassesTriggers()
    {
        var loader = CreateLoader(out _);
        var sourcePath = await WriteScriptAsync("application", "auto-event.fake", "auto-event");
        await File.WriteAllTextAsync(sourcePath + ".json",
            """{"entryKind":3,"triggers":[3,5]}""");

        var result = await loader.LoadAsync(_root);

        var entry = result.Entries.Single();
        Assert.IsTrue(entry.BuildResult!.Succeeded);
        CollectionAssert.AreEquivalent(
            new[] { ScriptAutomationTriggerKind.WorkItemCreated, ScriptAutomationTriggerKind.TagAdded },
            entry.Metadata!.Triggers!.ToArray());
    }

    [TestMethod]
    public async Task LoadAsync_RejectsTriggersOnNonAutomationScript()
    {
        var loader = CreateLoader(out _);
        var sourcePath = await WriteScriptAsync("application", "plain-event.fake", "plain-event");
        await File.WriteAllTextAsync(sourcePath + ".json", """{"triggers":[3]}""");

        var result = await loader.LoadAsync(_root);

        var entry = result.Entries.Single();
        Assert.IsFalse(entry.BuildResult!.Succeeded);
        Assert.AreEqual("SCRIPT_SCHEDULE_INVALID", entry.BuildResult.Diagnostics.Single().Code);
    }

    [TestMethod]
    public async Task LoadAsync_RejectsInvalidAutomationSchedule()
    {
        var loader = CreateLoader(out _);
        var sourcePath = await WriteScriptAsync("application", "auto-bad.fake", "auto-bad");
        await File.WriteAllTextAsync(sourcePath + ".json",
            """{"entryKind":3,"schedule":"hourly"}""");

        var result = await loader.LoadAsync(_root);

        var entry = result.Entries.Single();
        Assert.IsFalse(entry.BuildResult!.Succeeded);
        Assert.AreEqual("SCRIPT_SCHEDULE_INVALID", entry.BuildResult.Diagnostics.Single().Code);
    }

    [TestMethod]
    public async Task LoadAsync_RejectsScheduleOnNonAutomationScript()
    {
        var loader = CreateLoader(out _);
        var sourcePath = await WriteScriptAsync("application", "plain.fake", "plain");
        await File.WriteAllTextAsync(sourcePath + ".json", """{"schedule":"daily 09:00"}""");

        var result = await loader.LoadAsync(_root);

        var entry = result.Entries.Single();
        Assert.IsFalse(entry.BuildResult!.Succeeded);
        Assert.AreEqual("SCRIPT_SCHEDULE_INVALID", entry.BuildResult.Diagnostics.Single().Code);
    }

    [TestMethod]
    public async Task LoadAsync_V2MetadataExposesParametersAndStoresDefaults()
    {
        var loader = CreateLoader(out var catalog);
        var sourcePath = await WriteScriptAsync("application", "parameterized.fake", "parameterized");
        await File.WriteAllTextAsync(sourcePath + ".json", """
            {
              "apiVersion": "V2",
              "parameters": [
                {
                  "name": "limit",
                  "label": "Limit",
                  "type": "Integer",
                  "required": true
                }
              ],
              "defaultArguments": {
                "limit": "25"
              }
            }
            """);

        var result = await loader.LoadAsync(_root);

        var entry = result.Entries.Single();
        Assert.IsTrue(entry.BuildResult!.Succeeded, JoinDiagnostics(entry.BuildResult.Diagnostics));
        Assert.AreEqual(ScriptApiVersion.V2, entry.BuildResult.Program!.Descriptor.ApiVersion);
        Assert.AreEqual("limit", entry.BuildResult.Program.Descriptor.Parameters!.Single().Name);
        Assert.IsTrue(catalog.TryGetSource("parameterized", out var source));
        Assert.AreEqual("25", source!.DefaultArguments!["limit"]);
    }

    [TestMethod]
    public async Task LoadAsync_RejectsInvalidV2MetadataDefault()
    {
        var loader = CreateLoader(out var catalog);
        var sourcePath = await WriteScriptAsync("application", "invalid-default.fake", "invalid-default");
        await File.WriteAllTextAsync(sourcePath + ".json", """
            {
              "apiVersion": "V2",
              "parameters": [
                { "name": "limit", "label": "Limit", "type": "Integer" }
              ],
              "defaultArguments": { "limit": "not-an-integer" }
            }
            """);

        var result = await loader.LoadAsync(_root);

        var entry = result.Entries.Single();
        Assert.IsFalse(entry.BuildResult!.Succeeded);
        Assert.AreEqual("SCRIPT_ARGUMENT_TYPE_INVALID", entry.BuildResult.Diagnostics.Single().Code);
        Assert.IsFalse(catalog.TryGet("invalid-default", out _));
    }

    [TestMethod]
    public async Task LoadAsync_AutomationV2RequiresDefaultsForRequiredParameters()
    {
        var loader = CreateLoader(out var catalog);
        var sourcePath = await WriteScriptAsync("application", "automation-v2.fake", "automation-v2");
        await File.WriteAllTextAsync(sourcePath + ".json", """
            {
              "apiVersion": "V2",
              "entryKind": "Automation",
              "runOnStartup": true,
              "parameters": [
                { "name": "project", "label": "Project", "type": "String", "required": true }
              ]
            }
            """);

        var result = await loader.LoadAsync(_root);

        var entry = result.Entries.Single();
        Assert.IsFalse(entry.BuildResult!.Succeeded);
        Assert.AreEqual("SCRIPT_ARGUMENT_REQUIRED", entry.BuildResult.Diagnostics.Single().Code);
        Assert.IsFalse(catalog.TryGet("automation-v2", out _));
    }

    [TestMethod]
    public async Task LoadAsync_ApplicationV2MayRequireRuntimeInput()
    {
        var loader = CreateLoader(out var catalog);
        var sourcePath = await WriteScriptAsync("application", "manual-v2.fake", "manual-v2");
        await File.WriteAllTextAsync(sourcePath + ".json", """
            {
              "apiVersion": "V2",
              "parameters": [
                { "name": "project", "label": "Project", "type": "String", "required": true }
              ]
            }
            """);

        var result = await loader.LoadAsync(_root);

        var entry = result.Entries.Single();
        Assert.IsTrue(entry.BuildResult!.Succeeded, JoinDiagnostics(entry.BuildResult.Diagnostics));
        Assert.IsTrue(catalog.TryGet("manual-v2", out _));
    }

    private static ScriptDirectoryLoader CreateLoader(out ScriptCatalog catalog)
    {
        var registry = new ScriptEngineRegistry();
        registry.Register(new FakeEngine());
        catalog = new ScriptCatalog();
        return new ScriptDirectoryLoader(registry, new ScriptBuildService(registry), catalog);
    }

    private async Task<string> WriteScriptAsync(string directory, string fileName, string source)
    {
        var path = Path.Combine(_root, directory, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, source);
        return path;
    }

    private sealed class FakeEngine : IScriptEngineV1
    {
        public string Name => "fake";
        public string Version => "1.0";

        public ScriptMatchResult Match(ScriptMatchRequest request) =>
            new(request.SourcePath.EndsWith(".fake", StringComparison.OrdinalIgnoreCase));

        public ValueTask<ScriptBuildResult> BuildAsync(
            ScriptBuildRequest request,
            CancellationToken cancellationToken = default)
        {
            var editor = request.Source.StartsWith("editor:", StringComparison.Ordinal);
            var id = request.Source.Contains(':', StringComparison.Ordinal)
                ? request.Source[(request.Source.IndexOf(':') + 1)..]
                : request.Source;
            return ValueTask.FromResult(ScriptBuildResult.Success(new FakeProgram(
                new ScriptDescriptor(
                    id,
                    id,
                    request.ApiVersion,
                    editor ? ScriptScope.Editor : ScriptScope.Application,
                    EntryKind: request.DescriptorHint?.EntryKind ?? ScriptEntryKind.Application,
                    Parameters: request.DescriptorHint?.Parameters))));
        }
    }

    private static string JoinDiagnostics(IEnumerable<ScriptDiagnostic> diagnostics) =>
        string.Join(Environment.NewLine, diagnostics.Select(item => $"{item.Code}: {item.Message}"));

    private sealed class EngineProbe(string name, string extension) : IScriptEngineV1
    {
        public string Name => name;
        public string Version => "1.0";

        public ScriptMatchResult Match(ScriptMatchRequest request) =>
            new(request.SourcePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

        public ValueTask<ScriptBuildResult> BuildAsync(
            ScriptBuildRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ScriptBuildResult.Success(new FakeProgram(new(
                Path.GetFileNameWithoutExtension(request.SourcePath),
                Path.GetFileNameWithoutExtension(request.SourcePath),
                request.ApiVersion,
                 ScriptScope.Application))));
    }

    private sealed class FakeProgram(ScriptDescriptor descriptor) : IScriptProgramV1
    {
        public ScriptDescriptor Descriptor => descriptor;

        public ValueTask<ScriptExecutionResult> ExecuteAsync(
            ScriptExecutionRequest request,
            IScriptExecutionContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ScriptExecutionResult.Succeeded());
    }
}
