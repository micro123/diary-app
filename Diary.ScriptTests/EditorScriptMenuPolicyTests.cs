using Diary.Script.Runtime;
using Diary.ScriptBase;

namespace Diary.ScriptTests;

[TestClass]
public sealed class EditorScriptMenuPolicyTests
{
    [TestMethod]
    public void GetRunnableScripts_ReturnsOnlyEditorScriptsInStableNameOrder()
    {
        var catalog = new ScriptCatalog();
        catalog.Register(new TestProgram(new("application", "应用", ScriptApiVersion.V1, ScriptScope.Application)));
        catalog.Register(new TestProgram(new("z-editor", "Zeta", ScriptApiVersion.V1, ScriptScope.Editor)));
        catalog.Register(new TestProgram(new("a-editor", "Alpha", ScriptApiVersion.V1, ScriptScope.Editor)));

        var scripts = EditorScriptMenuPolicy.GetRunnableScripts(catalog);

        CollectionAssert.AreEqual(new[] { "Alpha", "Zeta" }, scripts.Select(item => item.Descriptor.Name).ToArray());
    }

    [TestMethod]
    public void CreateRequest_UsesEditorContextAndSavedWorkItemTarget()
    {
        var request = EditorScriptMenuPolicy.CreateRequest("2026-08-01", "2026-08-31", ScriptTimeGranularity.Month, 42);

        Assert.AreEqual(ScriptScope.Editor, request.Target.Scope);
        Assert.AreEqual(ScriptExecutionSource.Editor, request.Source);
        Assert.AreEqual("2026-08-01", request.Target.Editor!.StartDate);
        Assert.AreEqual(ScriptTimeGranularity.Month, request.Target.Editor.Granularity);
        Assert.AreEqual(ScriptBusinessTargetKind.WorkItem, request.Target.Business!.Kind);
        Assert.AreEqual("42", request.Target.Business.TargetId);
    }

    [TestMethod]
    public void CreateRequest_DoesNotTargetUnsavedWorkItem()
    {
        var request = EditorScriptMenuPolicy.CreateRequest("2026-08-06", "2026-08-06", ScriptTimeGranularity.Day);

        Assert.IsNull(request.Target.Business);
        Assert.AreEqual("当天", EditorScriptMenuPolicy.GetRangeLabel(ScriptTimeGranularity.Day));
        Assert.AreEqual("当前月份", EditorScriptMenuPolicy.GetRangeLabel(ScriptTimeGranularity.Month));
        Assert.AreEqual("当前年份", EditorScriptMenuPolicy.GetRangeLabel(ScriptTimeGranularity.Year));
    }

    private sealed class TestProgram(ScriptDescriptor descriptor) : IScriptProgramV1
    {
        public ScriptDescriptor Descriptor => descriptor;

        public ValueTask<ScriptExecutionResult> ExecuteAsync(
            ScriptExecutionRequest request,
            IScriptExecutionContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ScriptExecutionResult.Succeeded());
    }
}
