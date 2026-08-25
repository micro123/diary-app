using Diary.App.Models;
using Diary.ScriptBase;

namespace Diary.AppTests;

[TestClass]
public sealed class ScriptParameterFormViewModelTests
{
    [TestMethod]
    public void MetadataDefaults_InitializesFromMetadataAndOnlyWritesOverrides()
    {
        var form = CreateForm(
            CreateDescriptor(new ScriptParameterDefinition(
                "count",
                "数量",
                ScriptParameterType.Integer,
                DefaultValue: "1")),
            new Dictionary<string, string> { ["count"] = "2" });

        Assert.AreEqual("2", form.Fields.Single().Value);
        Assert.AreEqual(ScriptParameterValueSource.MetadataOverride, form.Fields.Single().ValueSource);

        form.Fields.Single().Value = "1";
        Assert.IsTrue(form.TryBuildMetadataOverrides(false, out var inherited, out _));
        Assert.IsEmpty(inherited);

        form.Fields.Single().Value = "3";
        Assert.IsTrue(form.TryBuildMetadataOverrides(false, out var overridden, out _));
        Assert.AreEqual("3", overridden["count"]);
    }

    [TestMethod]
    public void RunForm_TracksLastRunAndCanResetSingleFieldToConfiguredDefault()
    {
        var descriptor = CreateDescriptor(new ScriptParameterDefinition(
            "count",
            "数量",
            ScriptParameterType.Integer,
            DefaultValue: "1"));
        var form = new ScriptParameterFormViewModel(
            descriptor,
            new Dictionary<string, string> { ["count"] = "2" },
            new Dictionary<string, string> { ["count"] = "9" });
        var field = form.Fields.Single();

        Assert.AreEqual(ScriptParameterValueSource.LastRun, field.ValueSource);
        Assert.IsTrue(field.HasChanged);

        field.NumericValue = 3;
        Assert.AreEqual(ScriptParameterValueSource.RunInput, field.ValueSource);

        field.ResetFieldCommand.Execute(null);

        Assert.AreEqual("2", field.Value);
        Assert.AreEqual(ScriptParameterValueSource.MetadataOverride, field.ValueSource);
        Assert.IsFalse(field.HasChanged);
    }

    [TestMethod]
    public void MetadataDefaults_DoesNotRestoreLastRunArguments()
    {
        var descriptor = CreateDescriptor(new ScriptParameterDefinition(
            "count",
            "数量",
            ScriptParameterType.Integer,
            DefaultValue: "1"));

        var form = new ScriptParameterFormViewModel(
            descriptor,
            new Dictionary<string, string> { ["count"] = "2" },
            new Dictionary<string, string> { ["count"] = "9" },
            mode: ScriptParameterFormMode.MetadataDefaults);

        Assert.AreEqual("2", form.Fields.Single().Value);
        Assert.IsFalse(form.RestoredLastArguments);
    }

    [TestMethod]
    public void MetadataDefaults_RequiresAutomationRequiredValues()
    {
        var form = CreateForm(CreateDescriptor(new ScriptParameterDefinition(
            "project",
            "项目",
            ScriptParameterType.String,
            Required: true)));

        Assert.IsFalse(form.TryBuildMetadataOverrides(true, out _, out var error));
        Assert.IsTrue(form.Fields.Single().HasError);
        StringAssert.Contains(error, "表单");
    }

    [TestMethod]
    public void MetadataDefaults_AllowsManualRequiredValuesToRemainUnset()
    {
        var form = CreateForm(CreateDescriptor(new ScriptParameterDefinition(
            "project",
            "项目",
            ScriptParameterType.String,
            Required: true)));

        Assert.IsTrue(form.TryBuildMetadataOverrides(false, out var overrides, out _));
        Assert.IsEmpty(overrides);
    }

    [TestMethod]
    public void MetadataDefaults_RejectsChoiceAndRangeViolations()
    {
        var descriptor = CreateDescriptor(
            new ScriptParameterDefinition(
                "range",
                "范围",
                ScriptParameterType.Choice,
                Choices:
                [
                    new ScriptParameterChoice("week", "本周"),
                    new ScriptParameterChoice("month", "本月"),
                ]),
            new ScriptParameterDefinition(
                "hours",
                "工时",
                ScriptParameterType.Number,
                Constraints: new(Minimum: "0", Maximum: "24", Step: "0.5")));
        var form = CreateForm(descriptor);
        form.Fields.Single(field => field.Name == "range").Value = "year";
        form.Fields.Single(field => field.Name == "hours").Value = "25";

        Assert.IsFalse(form.TryBuildMetadataOverrides(false, out _, out _));
        StringAssert.Contains(form.Fields.Single(field => field.Name == "range").Error, "列表");
        StringAssert.Contains(form.Fields.Single(field => field.Name == "hours").Error, "0 到 24");
    }

    [TestMethod]
    public void MetadataDefaults_CanOverrideOptionalTextDefaultWithEmptyValue()
    {
        var form = CreateForm(CreateDescriptor(new ScriptParameterDefinition(
            "title",
            "标题",
            ScriptParameterType.String,
            DefaultValue: "日报")));
        form.Fields.Single().Value = string.Empty;

        Assert.IsTrue(form.TryBuildMetadataOverrides(false, out var overrides, out _));
        Assert.IsTrue(overrides.ContainsKey("title"));
        Assert.AreEqual(string.Empty, overrides["title"]);
    }

    [TestMethod]
    public void FormSupportsMaximumParameterCount()
    {
        var parameters = Enumerable.Range(1, 32)
            .Select(index => new ScriptParameterDefinition(
                $"value{index}",
                $"参数 {index}",
                ScriptParameterType.String,
                DefaultValue: index.ToString()))
            .ToArray();

        var form = CreateForm(CreateDescriptor(parameters));

        Assert.HasCount(32, form.Fields);
        Assert.IsTrue(form.TryBuildMetadataOverrides(false, out var overrides, out _));
        Assert.IsEmpty(overrides);
    }

    private static ScriptParameterFormViewModel CreateForm(
        ScriptDescriptor descriptor,
        IReadOnlyDictionary<string, string>? metadataDefaults = null) =>
        new(descriptor, metadataDefaults, mode: ScriptParameterFormMode.MetadataDefaults);

    private static ScriptDescriptor CreateDescriptor(params ScriptParameterDefinition[] parameters) =>
        new(
            "typed-form",
            "类型化表单",
            ScriptApiVersion.V2,
            ScriptScope.Application,
            Parameters: parameters);
}
