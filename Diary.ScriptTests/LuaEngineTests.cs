using Diary.Script.Lua;
using Diary.ScriptBase;

namespace Diary.ScriptTests;

[TestClass]
public sealed class LuaEngineTests
{
    private readonly LuaEngine _engine = new();

    [TestMethod]
    public void Match_IsCaseInsensitiveAndRejectsOtherExtensions()
    {
        Assert.IsTrue(_engine.Match(new ScriptMatchRequest("test.LUA")).IsMatch);
        Assert.IsFalse(_engine.Match(new ScriptMatchRequest("test.py")).IsMatch);
    }

    [TestMethod]
    public async Task BuildAsync_UsesMetadataHintAndExecutesEntrypoint()
    {
        var result = await _engine.BuildAsync(new ScriptBuildRequest(
            "test.lua",
            "function main(context) return nil end",
            DescriptorHint: new ScriptDescriptorHint(
                "lua-test",
                "Lua Test",
                ScriptScope.Application,
                ScriptCapability.None)));

        Assert.IsTrue(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(item => item.Message)));
        var execution = await result.Program!.ExecuteAsync(
            new ScriptExecutionRequest(new ScriptTarget(ScriptScope.Application)),
            new Diary.Script.Runtime.ScriptExecutionContext(ScriptCapability.None));

        Assert.AreEqual(ScriptExecutionStatus.Succeeded, execution.Status);
    }

    [TestMethod]
    public async Task BuildAsync_ReportsMissingDescriptorHint()
    {
        var result = await _engine.BuildAsync(new ScriptBuildRequest("missing.lua", "function main() end"));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("LUA_DESCRIPTOR_HINT_REQUIRED", result.Diagnostics.Single().Code);
    }

    [TestMethod]
    public async Task BuildAsync_PreservesSyntaxLocation()
    {
        var result = await _engine.BuildAsync(new ScriptBuildRequest(
            "broken.lua",
            "function main(context)\n  return (\nend",
            DescriptorHint: new ScriptDescriptorHint(
                "broken",
                "Broken",
                ScriptScope.Application,
                ScriptCapability.None)));

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.Diagnostics.Any(item =>
            item.Code == "LUA_SYNTAX_ERROR"
            && item.SourcePath == "broken.lua"
            && item.Line is > 0));
    }
}
