using Diary.Mcp;
using Diary.Script.CSharp;
using Diary.ScriptBase;

namespace Diary.ScriptTests;

[TestClass]
public sealed class ScriptValidationServiceTests
{
    private readonly ScriptValidationService _service = new(
        [(IScriptValidatorV1)new CSharpEngine()]);

    [TestMethod]
    public async Task ValidateAsync_RejectsUnsupportedLanguageAndOversizedSource()
    {
        var unsupported = await _service.ValidateAsync("javascript", "const value = 1;");
        var oversized = await _service.ValidateAsync(
            "csharp",
            new string('a', ScriptValidationService.MaxSourceBytes + 1));

        Assert.IsFalse(unsupported.Succeeded);
        Assert.AreEqual("SCRIPT_LANGUAGE_UNSUPPORTED", unsupported.Diagnostics.Single().Code);
        Assert.IsFalse(oversized.Succeeded);
        Assert.AreEqual("SCRIPT_SOURCE_TOO_LARGE", oversized.Diagnostics.Single().Code);
    }

    [TestMethod]
    public async Task ValidateAsync_UsesVirtualPathWithoutReturningIt()
    {
        var result = await _service.ValidateAsync("c#", "public sealed class Broken {");

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("csharp", result.Language);
        Assert.IsTrue(result.Diagnostics.Count > 0);
        Assert.IsFalse(result.Diagnostics.Any(diagnostic =>
            diagnostic.Message.Contains("ai-script.cs", StringComparison.Ordinal)));
    }
}
