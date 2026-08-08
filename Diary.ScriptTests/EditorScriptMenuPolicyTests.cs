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
    public void CreateRequest_UsesStructuredEditorTarget()
    {
        var item = new ScriptWorkItem(42, "2026-08-08", "item", 1, 0, null, []);
        var request = EditorScriptMenuPolicy.CreateRequest(ScriptEditorTarget.ForWorkItem(item));

        Assert.AreEqual(ScriptExecutionSource.Editor, request.Source);
        Assert.AreEqual(ScriptEditorTargetKind.WorkItem, request.Target!.Kind);
        Assert.AreEqual(item, request.Target.WorkItem);
    }

    [TestMethod]
    public void CreateRequest_DoesNotTargetUnsavedWorkItem()
    {
        var request = EditorScriptMenuPolicy.CreateRequest(ScriptEditorTarget.ForDay("2026-08-06"));

        Assert.AreEqual(ScriptEditorTargetKind.Day, request.Target!.Kind);
        Assert.AreEqual("当天", EditorScriptMenuPolicy.GetRangeLabel(ScriptEditorTargetKind.Day));
        Assert.AreEqual("当前月份", EditorScriptMenuPolicy.GetRangeLabel(ScriptEditorTargetKind.Month));
        Assert.AreEqual("当前季度", EditorScriptMenuPolicy.GetRangeLabel(ScriptEditorTargetKind.Quarter));
        Assert.AreEqual("当前年份", EditorScriptMenuPolicy.GetRangeLabel(ScriptEditorTargetKind.Year));
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
