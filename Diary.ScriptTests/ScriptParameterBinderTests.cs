using System.Collections.Immutable;
using Diary.Script.Runtime;
using Diary.ScriptBase;

namespace Diary.ScriptTests;

[TestClass]
public sealed class ScriptParameterBinderTests
{
    [TestMethod]
    public void ValidateDescriptor_RejectsParametersForV1()
    {
        var descriptor = CreateDescriptor(
            ScriptApiVersion.V1,
            new ScriptParameterDefinition("name", "Name", ScriptParameterType.String));

        var diagnostics = ScriptParameterBinder.ValidateDescriptor(descriptor, "legacy.fake");

        Assert.AreEqual("SCRIPT_PARAMETERS_REQUIRE_V2", diagnostics.Single().Code);
        Assert.AreEqual("legacy.fake", diagnostics.Single().SourcePath);
    }

    [TestMethod]
    public void Bind_NormalizesAllV2ParameterTypes()
    {
        var descriptor = CreateDescriptor(
            ScriptApiVersion.V2,
            new("title", "Title", ScriptParameterType.String, Required: true),
            new("body", "Body", ScriptParameterType.MultilineString),
            new("count", "Count", ScriptParameterType.Integer),
            new("ratio", "Ratio", ScriptParameterType.Number),
            new("enabled", "Enabled", ScriptParameterType.Boolean),
            new("date", "Date", ScriptParameterType.Date),
            new("at", "At", ScriptParameterType.DateTime),
            new(
                "format",
                "Format",
                ScriptParameterType.Choice,
                Choices: [new("markdown", "Markdown"), new("csv", "CSV")]));
        var supplied = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["title"] = "Summary",
            ["body"] = "line 1\r\nline 2\rline 3",
            ["count"] = "+0042",
            ["ratio"] = "-001.2500",
            ["enabled"] = "True",
            ["date"] = "2026-08-25",
            ["at"] = "2026-08-25T09:30:00+08:00",
            ["format"] = "markdown",
        };

        var result = ScriptParameterBinder.Bind(descriptor, null, supplied);

        Assert.IsTrue(result.Succeeded, JoinDiagnostics(result));
        Assert.AreEqual("Summary", result.Arguments["title"]);
        Assert.AreEqual("line 1\nline 2\nline 3", result.Arguments["body"]);
        Assert.AreEqual("42", result.Arguments["count"]);
        Assert.AreEqual("-1.2500", result.Arguments["ratio"]);
        Assert.AreEqual("true", result.Arguments["enabled"]);
        Assert.AreEqual("2026-08-25", result.Arguments["date"]);
        Assert.AreEqual("2026-08-25T09:30:00.0000000+08:00", result.Arguments["at"]);
        Assert.AreEqual("markdown", result.Arguments["format"]);
    }

    [TestMethod]
    public void Bind_UsesDefinitionThenMetadataThenSuppliedPriority()
    {
        var descriptor = CreateDescriptor(
            ScriptApiVersion.V2,
            new("format", "Format", ScriptParameterType.String, DefaultValue: "descriptor"),
            new("limit", "Limit", ScriptParameterType.Integer, DefaultValue: "1"));
        var metadata = new Dictionary<string, string>
        {
            ["format"] = "metadata",
            ["limit"] = "2",
        };
        var supplied = new Dictionary<string, string>
        {
            ["format"] = "request",
        };

        var result = ScriptParameterBinder.Bind(descriptor, metadata, supplied);

        Assert.IsTrue(result.Succeeded, JoinDiagnostics(result));
        Assert.AreEqual("request", result.Arguments["format"]);
        Assert.AreEqual("2", result.Arguments["limit"]);
    }

    [TestMethod]
    public void Bind_RejectsUnknownAndMissingRequiredArguments()
    {
        var descriptor = CreateDescriptor(
            ScriptApiVersion.V2,
            new ScriptParameterDefinition("required", "Required", ScriptParameterType.String, Required: true));

        var result = ScriptParameterBinder.Bind(
            descriptor,
            null,
            new Dictionary<string, string> { ["unexpected"] = "value" });

        Assert.IsFalse(result.Succeeded);
        CollectionAssert.AreEquivalent(
            new[] { "SCRIPT_ARGUMENT_UNKNOWN" },
            result.Diagnostics.Select(item => item.Code).ToArray());

        var missing = ScriptParameterBinder.Bind(descriptor, null, null);
        Assert.AreEqual("SCRIPT_ARGUMENT_REQUIRED", missing.Diagnostics.Single().Code);
    }

    [TestMethod]
    public void Bind_RejectsInvalidChoiceDateTimeAndDecimalThousandsSeparator()
    {
        var descriptor = CreateDescriptor(
            ScriptApiVersion.V2,
            new("choice", "Choice", ScriptParameterType.Choice, Choices: [new("a", "A")]),
            new("at", "At", ScriptParameterType.DateTime),
            new("number", "Number", ScriptParameterType.Number));

        var result = ScriptParameterBinder.Bind(
            descriptor,
            null,
            new Dictionary<string, string>
            {
                ["choice"] = "b",
                ["at"] = "2026-08-25T09:30:00",
                ["number"] = "1,000.5",
            });

        Assert.IsFalse(result.Succeeded);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "SCRIPT_ARGUMENT_CHOICE_INVALID",
                "SCRIPT_ARGUMENT_TYPE_INVALID",
                "SCRIPT_ARGUMENT_TYPE_INVALID",
            },
            result.Diagnostics.Select(item => item.Code).ToArray());
    }

    [TestMethod]
    public void Bind_AllowsIncompleteRequiredValuesDuringNonAutomationLoadValidation()
    {
        var descriptor = CreateDescriptor(
            ScriptApiVersion.V2,
            new ScriptParameterDefinition("required", "Required", ScriptParameterType.Integer, Required: true));

        var result = ScriptParameterBinder.Bind(
            descriptor,
            null,
            null,
            requireRequired: false);

        Assert.IsTrue(result.Succeeded, JoinDiagnostics(result));
        Assert.IsFalse(result.Arguments.ContainsKey("required"));
    }

    [TestMethod]
    public void Bind_RejectsArgumentsOverTotalSizeLimit()
    {
        var descriptor = CreateDescriptor(
            ScriptApiVersion.V2,
            Enumerable.Range(0, 5)
                .Select(index => new ScriptParameterDefinition(
                    $"value{index}",
                    $"Value {index}",
                    ScriptParameterType.MultilineString))
                .ToArray());
        var supplied = Enumerable.Range(0, 5)
            .ToImmutableDictionary(
                index => $"value{index}",
                _ => new string('x', 14 * 1024),
                StringComparer.Ordinal);

        var result = ScriptParameterBinder.Bind(descriptor, null, supplied);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("SCRIPT_ARGUMENTS_TOO_LARGE", result.Diagnostics.Single().Code);
    }

    [TestMethod]
    public void ValidateDescriptor_RejectsInvalidSchemaAndDefault()
    {
        var descriptor = CreateDescriptor(
            ScriptApiVersion.V2,
            new("duplicate", "First", ScriptParameterType.String),
            new("duplicate", "Second", ScriptParameterType.String),
            new("count", "Count", ScriptParameterType.Integer, DefaultValue: "not-an-integer"));

        var diagnostics = ScriptParameterBinder.ValidateDescriptor(descriptor);

        CollectionAssert.AreEquivalent(
            new[] { "SCRIPT_PARAMETER_DUPLICATE", "SCRIPT_PARAMETER_DEFAULT_INVALID" },
            diagnostics.Select(item => item.Code).ToArray());
    }

    private static ScriptDescriptor CreateDescriptor(
        ScriptApiVersion apiVersion,
        params ScriptParameterDefinition[] parameters) =>
        new(
            "parameter-test",
            "Parameter test",
            apiVersion,
            ScriptScope.Application,
            Parameters: parameters);

    private static string JoinDiagnostics(ScriptParameterBindingResult result) =>
        string.Join(Environment.NewLine, result.Diagnostics.Select(item => $"{item.Code}: {item.Message}"));
}
