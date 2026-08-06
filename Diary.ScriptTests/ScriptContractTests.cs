using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using Diary.ScriptBase;
using Diary.ScriptHost;

namespace Diary.ScriptTests;

[TestClass]
public sealed class ScriptContractTests
{
    [TestMethod]
    public void StableValues_AreExplicitAndVersionIsV1()
    {
        Assert.AreEqual(1, EnumValue<ScriptApiVersion>("V1"));
        Assert.AreEqual((int)ScriptApiVersions.Current, EnumValue<ScriptApiVersion>("V1"));
        Assert.AreEqual(1, EnumValue<ScriptCapability>("ReadDiary"));
        Assert.AreEqual(16, EnumValue<ScriptCapability>("Tracker"));
        Assert.AreEqual(3, EnumValue<ScriptDiagnosticSeverity>("Error"));
        Assert.AreEqual(6, EnumValue<ScriptDiagnosticCategory>("Host"));
        Assert.AreEqual(4, EnumValue<ScriptExecutionStatus>("Rejected"));
        Assert.AreEqual(5, EnumValue<ScriptExecutionStatus>("TimedOut"));
        Assert.AreEqual(5, EnumValue<ScriptQueryErrorCode>("Cancelled"));
    }

    [TestMethod]
    public void Contracts_RoundTripWithSystemTextJson()
    {
        var descriptor = new ScriptDescriptor(
            "daily-summary",
            "Daily summary",
            ScriptApiVersion.V1,
            ScriptScope.Editor,
            ScriptCapability.ReadDiary,
            "Read-only summary");
        var diagnostic = new ScriptDiagnostic(
            "TEST001",
            "message",
            ScriptDiagnosticSeverity.Warning,
            ScriptDiagnosticCategory.Validation,
            "script.test",
            2,
            4);

        var descriptorJson = JsonSerializer.Serialize(descriptor);
        var diagnosticJson = JsonSerializer.Serialize(diagnostic);

        Assert.AreEqual(descriptor, JsonSerializer.Deserialize<ScriptDescriptor>(descriptorJson));
        Assert.AreEqual(diagnostic, JsonSerializer.Deserialize<ScriptDiagnostic>(diagnosticJson));
        StringAssert.Contains(descriptorJson, "\"ApiVersion\":1");
        StringAssert.Contains(diagnosticJson, "\"Severity\":2");

        var buildRequest = new ScriptBuildRequest("script.test", "source");
        var executionRequest = new ScriptExecutionRequest(
            new ScriptTarget(ScriptScope.Editor, new EditorScriptContext("2026-08-01", "2026-08-02")));
        Assert.AreEqual(buildRequest, JsonSerializer.Deserialize<ScriptBuildRequest>(
            JsonSerializer.Serialize(buildRequest)));
        Assert.AreEqual(executionRequest, JsonSerializer.Deserialize<ScriptExecutionRequest>(
            JsonSerializer.Serialize(executionRequest)));

        var buildResult = JsonSerializer.Deserialize<ScriptBuildResult>(JsonSerializer.Serialize(
            ScriptBuildResult.Failure(diagnostic)));
        var executionResult = JsonSerializer.Deserialize<ScriptExecutionResult>(JsonSerializer.Serialize(
            new ScriptExecutionResult(ScriptExecutionStatus.Failed, [diagnostic])));
        Assert.IsNotNull(buildResult);
        Assert.IsFalse(buildResult.Succeeded);
        Assert.AreEqual("TEST001", buildResult.Diagnostics.Single().Code);
        Assert.IsNotNull(executionResult);
        Assert.AreEqual(ScriptExecutionStatus.Failed, executionResult.Status);
        Assert.AreEqual("TEST001", executionResult.Diagnostics.Single().Code);
    }

    [TestMethod]
    public void ScriptDtos_AreImmutableAndContainNoSensitiveSurface()
    {
        var sourceTags = ImmutableArray.Create(new ScriptWorkTag(1, "tag", 2, 0, false));
        var item = new ScriptWorkItem(3, "2026-08-01", "work", 1.5, 2, "note", sourceTags);
        sourceTags = sourceTags.Add(new ScriptWorkTag(2, "other", 3, 1, false));

        Assert.AreEqual(1, item.Tags.Length);
        foreach (var type in new[] { typeof(ScriptWorkItem), typeof(ScriptWorkTag), typeof(ScriptWorkItemQuery) })
        {
            Assert.IsTrue(type.IsSealed);
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                var name = property.Name.ToLowerInvariant();
                Assert.IsFalse(name.Contains("connection"));
                Assert.IsFalse(name.Contains("provider"));
                Assert.IsFalse(name.Contains("tracker"));
                Assert.IsFalse(name.Contains("password"));
                Assert.IsFalse(name.Contains("token"));
                Assert.IsFalse(property.PropertyType.Namespace?.StartsWith("Diary.Database") == true);
            }
        }
    }

    private static int EnumValue<TEnum>(string name) where TEnum : struct, Enum =>
        Convert.ToInt32(Enum.Parse<TEnum>(name));
}
