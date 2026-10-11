using System.Collections.Immutable;
using Diary.App.Services;
using Diary.Script.Runtime;
using Diary.ScriptBase;

namespace Diary.AppTests;

[TestClass]
public sealed class AgentCapabilityApiTests
{
    [TestMethod]
    public void ScriptContractIncludesFailedDirectoryEntryWithoutExposingAbsolutePath()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "diary-agent-tests", "broken.cs");
        var diagnostic = new ScriptDiagnostic(
            "SCRIPT_BUILD_FAILED",
            "脚本编译失败。",
            ScriptDiagnosticSeverity.Error,
            ScriptDiagnosticCategory.Syntax,
            sourcePath,
            3,
            5);
        var build = new ScriptBuildResult(false, null, [diagnostic]) { EngineName = "csharp" };
        var entry = new ScriptDirectoryEntry(
            sourcePath,
            ScriptScope.Application,
            build,
            new ScriptFileMetadata(Id: "broken-script", Name: "损坏脚本", Engine: "csharp"),
            ConfigurationState: ScriptConfigurationState.Invalid);

        var contract = AgentCapabilityApi.ToScriptContract(entry, ImmutableArray<ScriptDiagnostic>.Empty);

        Assert.AreEqual("broken-script", contract.Id);
        Assert.AreEqual("Invalid", contract.State);
        Assert.AreEqual("csharp", contract.Language);
        Assert.AreEqual("broken.cs", contract.Diagnostics.Single().SourcePath);
        Assert.IsFalse(contract.Diagnostics.Single().SourcePath!.Contains(Path.DirectorySeparatorChar));
    }

    [TestMethod]
    public void ScriptContractPreservesNeedsConfigurationState()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "diary-agent-tests", "configured.cs");
        var entry = new ScriptDirectoryEntry(
            sourcePath,
            ScriptScope.Application,
            new ScriptBuildResult(true, null, []),
            new ScriptFileMetadata(Id: "configured-script", Name: "待配置脚本"),
            ConfigurationState: ScriptConfigurationState.NeedsConfiguration,
            ConfigurationDiagnostics:
            [
                new ScriptDiagnostic(
                    "SCRIPT_CONFIGURATION_REQUIRED",
                    "缺少必填参数。",
                    ScriptDiagnosticSeverity.Warning,
                    ScriptDiagnosticCategory.Validation,
                    sourcePath),
            ]);

        var contract = AgentCapabilityApi.ToScriptContract(entry, []);

        Assert.AreEqual("NeedsConfiguration", contract.State);
        Assert.AreEqual("SCRIPT_CONFIGURATION_REQUIRED", contract.Diagnostics.Single().Code);
    }
}
