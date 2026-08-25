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
    public void StableValues_AreExplicitAndCurrentVersionIsV2()
    {
        Assert.AreEqual(1, EnumValue<ScriptApiVersion>("V1"));
        Assert.AreEqual(2, EnumValue<ScriptApiVersion>("V2"));
        CollectionAssert.AreEquivalent(
            new[] { ScriptApiVersion.V1, ScriptApiVersion.V2 },
            ScriptApiVersions.Supported.ToArray());
        Assert.AreEqual(3, EnumValue<ScriptDiagnosticSeverity>("Error"));
        Assert.AreEqual(6, EnumValue<ScriptDiagnosticCategory>("Host"));
        Assert.AreEqual(4, EnumValue<ScriptExecutionStatus>("Rejected"));
        Assert.AreEqual(5, EnumValue<ScriptExecutionStatus>("TimedOut"));
        Assert.AreEqual(5, EnumValue<ScriptQueryErrorCode>("Cancelled"));
    }

    [TestMethod]
    public void Contracts_RoundTripWithSystemTextJson()
    {
        var parameters = new[]
        {
            new ScriptParameterDefinition(
                "format",
                "Format",
                ScriptParameterType.Choice,
                Required: true,
                DefaultValue: "markdown",
                Choices:
                [
                    new("markdown", "Markdown"),
                    new("csv", "CSV"),
                ]),
        };
        var descriptor = new ScriptDescriptor(
            "daily-summary",
            "Daily summary",
            ScriptApiVersion.V2,
            ScriptScope.Editor,
            "Read-only summary",
            Parameters: parameters);
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

        var restoredDescriptor = JsonSerializer.Deserialize<ScriptDescriptor>(descriptorJson);
        Assert.IsNotNull(restoredDescriptor);
        Assert.AreEqual(descriptor.Id, restoredDescriptor.Id);
        Assert.AreEqual(descriptor.ApiVersion, restoredDescriptor.ApiVersion);
        var restoredParameter = restoredDescriptor.Parameters!.Single();
        Assert.AreEqual(parameters.Single().Name, restoredParameter.Name);
        Assert.AreEqual(parameters.Single().Type, restoredParameter.Type);
        Assert.AreEqual(parameters.Single().DefaultValue, restoredParameter.DefaultValue);
        CollectionAssert.AreEqual(
            parameters.Single().Choices!.ToArray(),
            restoredParameter.Choices!.ToArray());
        Assert.AreEqual(diagnostic, JsonSerializer.Deserialize<ScriptDiagnostic>(diagnosticJson));
        StringAssert.Contains(descriptorJson, "\"ApiVersion\":2");
        StringAssert.Contains(diagnosticJson, "\"Severity\":2");

        var buildRequest = new ScriptBuildRequest("script.test", "source");
        var executionRequest = new ScriptExecutionRequest(
            ScriptEditorTarget.ForMonth(2026, 8),
            AutomationEventData: ImmutableDictionary<string, string>.Empty.Add("workItemId", "42"));
        Assert.AreEqual(buildRequest, JsonSerializer.Deserialize<ScriptBuildRequest>(
            JsonSerializer.Serialize(buildRequest)));
        var restoredExecutionRequest = JsonSerializer.Deserialize<ScriptExecutionRequest>(
            JsonSerializer.Serialize(executionRequest));
        Assert.IsNotNull(restoredExecutionRequest);
        Assert.AreEqual(executionRequest.Target, restoredExecutionRequest.Target);
        Assert.AreEqual("42", restoredExecutionRequest.AutomationEventData!["workItemId"]);

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

        var month = ScriptEditorTargetResolver.GetDateRange(executionRequest.Target!);
        Assert.AreEqual(new ScriptDateRange("2026-08-01", "2026-08-31"), month);
    }

    [TestMethod]
    public void ScriptDtos_AreImmutableAndContainNoSensitiveSurface()
    {
        var sourceTags = ImmutableArray.Create(new ScriptWorkTag(1, "tag", 2, 0, false));
        var item = new ScriptWorkItem(3, "2026-08-01", "work", 1.5, 2, "note", sourceTags);
        sourceTags = sourceTags.Add(new ScriptWorkTag(2, "other", 3, 1, false));

        Assert.AreEqual(1, item.Tags.Length);
        Assert.IsTrue(item.Tags[0].IsPrimary);
        Assert.IsFalse(sourceTags[1].IsPrimary);
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

    [TestMethod]
    public void EditorTargets_ResolveNaturalDateRangesAndWorkItemSnapshot()
    {
        Assert.AreEqual(new ScriptDateRange("2026-01-01", "2026-12-31"),
            ScriptEditorTargetResolver.GetDateRange(ScriptEditorTarget.ForYear(2026)));
        Assert.AreEqual(new ScriptDateRange("2026-04-01", "2026-06-30"),
            ScriptEditorTargetResolver.GetDateRange(ScriptEditorTarget.ForQuarter(2026, 2)));
        Assert.AreEqual(new ScriptDateRange("2026-02-01", "2026-02-28"),
            ScriptEditorTargetResolver.GetDateRange(ScriptEditorTarget.ForMonth(2026, 2)));
        Assert.AreEqual(new ScriptDateRange("2026-02-08", "2026-02-08"),
            ScriptEditorTargetResolver.GetDateRange(ScriptEditorTarget.ForDay("2026-02-08")));
        Assert.AreEqual(new ScriptDateRange("2026-08-10", "2026-08-16"),
            ScriptEditorTargetResolver.GetDateRange(ScriptEditorTarget.ForWeek("2026-08-10")));
        Assert.ThrowsExactly<ArgumentException>(() =>
            ScriptEditorTargetResolver.GetDateRange(ScriptEditorTarget.ForWeek("2026-08-11")));
        Assert.ThrowsExactly<ArgumentException>(() =>
            ScriptEditorTargetResolver.GetDateRange(ScriptEditorTarget.ForWeek("2026-8-10")));

        var item = new ScriptWorkItem(7, "2026-02-08", "事项", 1, 0, null, []);
        Assert.IsNull(ScriptEditorTargetResolver.GetDateRange(ScriptEditorTarget.ForWorkItem(item)));
    }

    private static int EnumValue<TEnum>(string name) where TEnum : struct, Enum =>
        Convert.ToInt32(Enum.Parse<TEnum>(name));
}
