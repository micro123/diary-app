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
    public async Task LoadAsync_RegistersEnabledScriptAndCreatesDirectories()
    {
        var loader = CreateLoader(out var catalog);
        var sourcePath = await WriteScriptAsync("application", "enabled.fake", "enabled");

        var result = await loader.LoadAsync(_root);

        Assert.AreEqual(1, result.Entries.Length);
        Assert.IsTrue(result.Entries[0].BuildResult!.Succeeded);
        Assert.IsTrue(catalog.TryGet("enabled", out _));
        Assert.IsTrue(Directory.Exists(Path.Combine(_root, "editor")));
        Assert.AreEqual(sourcePath, result.Entries[0].SourcePath);
    }

    [TestMethod]
    public async Task LoadAsync_DoesNotRegisterDisabledScript()
    {
        var loader = CreateLoader(out var catalog);
        var sourcePath = await WriteScriptAsync("application", "disabled.fake", "disabled");
        await File.WriteAllTextAsync(sourcePath + ".json", """{"enabled":false}""");

        var result = await loader.LoadAsync(_root);

        Assert.AreEqual(1, result.Entries.Length);
        Assert.IsFalse(result.Entries[0].Enabled);
        Assert.IsNull(result.Entries[0].BuildResult);
        Assert.IsFalse(catalog.TryGet("disabled", out _));
    }

    [TestMethod]
    public async Task LoadAsync_DoesNotRegisterDisabledPackage()
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
        Assert.IsFalse(entry.Enabled);
        Assert.IsNull(entry.BuildResult);
        Assert.IsFalse(catalog.TryGet("disabled-package", out _));
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
        Assert.IsFalse(result.Entries.Single().Enabled);
        Assert.IsFalse(catalog.TryGet("actual", out _));
    }

    [TestMethod]
    public async Task LoadAsync_RejectsScopeThatDoesNotMatchDirectory()
    {
        var loader = CreateLoader(out var catalog);
        await WriteScriptAsync("editor", "wrong.fake", "application:wrong");

        var result = await loader.LoadAsync(_root);

        Assert.AreEqual("SCRIPT_SCOPE_MISMATCH", result.Diagnostics.Single().Code);
        Assert.IsFalse(result.Entries.Single().Enabled);
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
        Assert.IsTrue(result.Entries.Any(entry => entry.Enabled));
        Assert.IsTrue(catalog.TryGet("packaged", out _));
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
                    ScriptCapability.None))));
        }
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
