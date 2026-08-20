using Diary.Script.Py;
using Diary.ScriptBase;

namespace Diary.ScriptTests;

[TestClass]
public sealed class PythonEngineTests
{
    [TestMethod]
    public void Match_IsCaseInsensitiveAndRejectsOtherExtensions()
    {
        var engine = new PythonEngine();

        Assert.IsTrue(engine.Match(new ScriptMatchRequest("test.PY")).IsMatch);
        Assert.IsFalse(engine.Match(new ScriptMatchRequest("test.lua")).IsMatch);
    }

    [TestMethod]
    public async Task BuildAsync_ReportsMissingDescriptorHintStably()
    {
        var engine = new PythonEngine(new PythonRuntimeResolver(_ => null));

        var result = await engine.BuildAsync(new ScriptBuildRequest("missing.py", "def application_main(context):\n    return None"));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("PYTHON_DESCRIPTOR_HINT_REQUIRED", result.Diagnostics.Single().Code);
        Assert.AreEqual("missing.py", result.Diagnostics.Single().SourcePath);
    }

    [TestMethod]
    public async Task BuildAsync_UsesHintAsDescriptorAuthority()
    {
        var runtime = new PythonRuntimeResolver();
        var resolved = await runtime.ResolveAsync();
        if (!resolved.Succeeded)
            Assert.Inconclusive("A usable Python 3.10+ runtime is required for this test.");

        var engine = new PythonEngine(runtime);
        var result = await engine.BuildAsync(new ScriptBuildRequest(
            "hint.py",
            "def application_main(context):\n    return None",
            DescriptorHint: new ScriptDescriptorHint(
                "metadata-id",
                "Metadata Name",
                 ScriptScope.Editor)));

        Assert.IsTrue(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(item => item.Message)));
        Assert.AreEqual("metadata-id", result.Program!.Descriptor.Id);
        Assert.AreEqual("Metadata Name", result.Program.Descriptor.Name);
        Assert.AreEqual(ScriptScope.Editor, result.Program.Descriptor.Scope);
    }

    [TestMethod]
    public async Task BuildAsync_AcceptsUnicodeSourceThroughSyntaxProbe()
    {
        var runtime = new PythonRuntimeResolver();
        var resolved = await runtime.ResolveAsync();
        if (!resolved.Succeeded)
            Assert.Inconclusive("A usable Python 3.10+ runtime is required for this test.");

        var engine = new PythonEngine(runtime);
        var result = await engine.BuildAsync(new ScriptBuildRequest(
            "unicode.py",
            "def application_main(context):\n    context.log.info('中文日志')\n    return None",
            DescriptorHint: new ScriptDescriptorHint(
                "unicode",
                "Unicode",
                ScriptScope.Application)));

        Assert.IsTrue(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(item => item.Message)));
    }

    [TestMethod]
    public async Task BuildAsync_PreservesSyntaxLocation()
    {
        var runtime = new PythonRuntimeResolver();
        var resolved = await runtime.ResolveAsync();
        if (!resolved.Succeeded)
            Assert.Inconclusive("A usable Python 3.10+ runtime is required for this test.");

        var engine = new PythonEngine(runtime);
        var result = await engine.BuildAsync(new ScriptBuildRequest(
            "broken.py",
            "def application_main(context):\n    return (",
            DescriptorHint: new ScriptDescriptorHint(
                "broken",
                "Broken",
                 ScriptScope.Application)));

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.Diagnostics.Any(item =>
            item.Code == "PYTHON_SYNTAX_ERROR"
            && item.SourcePath == "broken.py"
            && item.Line is > 0
            && item.Column is > 0));
    }

    [TestMethod]
    public async Task ResolveAsync_ReportsMissingConfiguredPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-python-{Guid.NewGuid():N}");

        var result = await new PythonRuntimeResolver(_ => null).ResolveAsync(path);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("PYTHON_RUNTIME_NOT_FOUND", result.Diagnostics.Single().Code);
        StringAssert.Contains(result.Diagnostics.Single().Message, path);
    }

    [TestMethod]
    public async Task ResolveAsync_RejectsRelativeExplicitPath()
    {
        var result = await new PythonRuntimeResolver(_ => null).ResolveAsync("python3");

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("PYTHON_RUNTIME_PATH_NOT_ABSOLUTE", result.Diagnostics.Single().Code);
    }
}
