using Diary.Script.Lua;
using Diary.ScriptBase;

namespace Diary.ScriptTests;

[TestClass]
public sealed class LuaEngineTests
{
    private readonly LuaEngine _engine = new();

    [TestMethod]
    public async Task ValidateAsync_ParsesWithoutDescriptorHintOrExecution()
    {
        var result = await _engine.ValidateAsync(new ScriptValidationRequest(
            "validate.lua",
            "function application_main(context) error('must not run') end"));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("lua", result.EngineName);
    }

    [TestMethod]
    public async Task ValidateAsync_ReturnsSyntaxDiagnostics()
    {
        var result = await _engine.ValidateAsync(new ScriptValidationRequest(
            "broken.lua",
            "function application_main(context) return ( end"));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("LUA_SYNTAX_ERROR", result.Diagnostics.Single().Code);
    }

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
            "function application_main(context) return nil end",
            DescriptorHint: new ScriptDescriptorHint(
                "lua-test",
                "Lua Test",
                 ScriptScope.Application)));

        Assert.IsTrue(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(item => item.Message)));
        var execution = await result.Program!.ExecuteAsync(
            new ScriptExecutionRequest(),
            new Diary.Script.Runtime.ScriptExecutionContext());

        Assert.AreEqual(ScriptExecutionStatus.Succeeded, execution.Status);
    }

    [TestMethod]
    public async Task BuildAsync_V2DescriptorContainsMetadataParameters()
    {
        var parameters = new[]
        {
            new ScriptParameterDefinition("limit", "Limit", ScriptParameterType.Integer, Required: true),
        };

        var result = await _engine.BuildAsync(new ScriptBuildRequest(
            "parameterized.lua",
            "function application_main(context) return nil end",
            ScriptApiVersion.V2,
            new ScriptDescriptorHint(
                "parameterized-lua",
                "Parameterized Lua",
                ScriptScope.Application,
                Parameters: parameters)));

        Assert.IsTrue(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(item => item.Message)));
        Assert.AreEqual(ScriptApiVersion.V2, result.Program!.Descriptor.ApiVersion);
        Assert.AreEqual(parameters.Single(), result.Program.Descriptor.Parameters!.Single());
    }

    [TestMethod]
    public async Task BuildAsync_ReportsMissingDescriptorHint()
    {
        var result = await _engine.BuildAsync(new ScriptBuildRequest("missing.lua", "function application_main() end"));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("LUA_DESCRIPTOR_HINT_REQUIRED", result.Diagnostics.Single().Code);
    }

    [TestMethod]
    public async Task BuildAsync_PreservesSyntaxLocation()
    {
        var result = await _engine.BuildAsync(new ScriptBuildRequest(
            "broken.lua",
            "function application_main(context)\n  return (\nend",
            DescriptorHint: new ScriptDescriptorHint(
                "broken",
                "Broken",
                 ScriptScope.Application)));

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.Diagnostics.Any(item =>
            item.Code == "LUA_SYNTAX_ERROR"
            && item.SourcePath == "broken.lua"
            && item.Line is > 0));
    }
}
