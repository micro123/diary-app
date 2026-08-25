using Diary.App.ViewModels.Dialogs;
using Diary.Script.Runtime;
using Diary.ScriptBase;

namespace Diary.AppTests;

[TestClass]
public sealed class ScriptRunDialogViewModelTests
{
    [TestMethod]
    public void Initialize_LoadsDefaultsAndTimeout()
    {
        var viewModel = new ScriptRunDialogViewModel();

        viewModel.Initialize("示例", new ScriptFileMetadata(
            DefaultArguments: new Dictionary<string, string> { ["project"] = "Diary", ["range"] = "today" },
            TimeoutSeconds: 45));

        Assert.AreEqual("示例", viewModel.ScriptName);
        Assert.AreEqual(45, viewModel.TimeoutSeconds);
        StringAssert.Contains(viewModel.ArgumentsText, "project=Diary");
        StringAssert.Contains(viewModel.ArgumentsText, "range=today");
    }

    [TestMethod]
    public void TryParseArguments_RejectsDuplicateKeys()
    {
        var succeeded = ScriptRunDialogViewModel.TryParseArguments(
            "range=today\nrange=week",
            out _,
            out var error);

        Assert.IsFalse(succeeded);
        StringAssert.Contains(error, "参数名重复");
    }

    [TestMethod]
    public void Initialize_V2BuildsTypedFieldsAndRestoresLastArguments()
    {
        var viewModel = new ScriptRunDialogViewModel();
        var descriptor = new ScriptDescriptor(
            "typed",
            "类型化脚本",
            ScriptApiVersion.V2,
            ScriptScope.Application,
            Parameters:
            [
                new ScriptParameterDefinition(
                    "hours",
                    "工时",
                    ScriptParameterType.Number,
                    DefaultValue: "1",
                    Constraints: new(Minimum: "0", Maximum: "24", Step: "0.5", Unit: "小时")),
            ]);

        viewModel.Initialize(
            descriptor,
            new ScriptFileMetadata(DefaultArguments: new Dictionary<string, string> { ["hours"] = "2" }),
            new Dictionary<string, string> { ["hours"] = "3.5" },
            null);

        Assert.IsTrue(viewModel.IsV2);
        Assert.IsFalse(viewModel.IsV1);
        Assert.AreEqual("3.5", viewModel.ParameterForm!.Fields.Single().Value);
        Assert.IsTrue(viewModel.ParameterForm.RestoredLastArguments);
    }

    [TestMethod]
    public void V2FormMapsConstraintFailureToField()
    {
        var viewModel = new ScriptRunDialogViewModel();
        var descriptor = new ScriptDescriptor(
            "typed",
            "类型化脚本",
            ScriptApiVersion.V2,
            ScriptScope.Application,
            Parameters:
            [
                new ScriptParameterDefinition(
                    "hours",
                    "工时",
                    ScriptParameterType.Number,
                    Constraints: new(Minimum: "0", Maximum: "24", Step: "0.5")),
            ]);
        viewModel.Initialize(descriptor, null, null, null);
        viewModel.ParameterForm!.Fields.Single().Value = "2.25";

        var result = viewModel.ParameterForm.ValidateAndBuild();

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(viewModel.ParameterForm.Fields.Single().HasError);
    }
}
