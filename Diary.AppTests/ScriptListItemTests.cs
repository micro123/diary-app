using Diary.App.ViewModels;
using Diary.Script.Runtime;
using Diary.ScriptBase;

namespace Diary.AppTests;

[TestClass]
public sealed class ScriptListItemTests
{
    [TestMethod]
    public void IsRunnable_RequiresSuccessfulApplicationScript()
    {
        var failed = Create(ScriptScope.Application, buildSucceeded: false);
        var editor = Create(ScriptScope.Editor, buildSucceeded: true);
        var application = Create(ScriptScope.Application, buildSucceeded: true);

        Assert.IsFalse(failed.IsRunnable);
        Assert.IsFalse(editor.IsRunnable);
        Assert.IsTrue(application.IsRunnable);
    }

    [TestMethod]
    public void IsAutomationAndEntryKindLabel_DeriveFromEntryKind()
    {
        var automation = Create(ScriptScope.Application, buildSucceeded: true, ScriptEntryKind.Automation);
        var query = Create(ScriptScope.Application, buildSucceeded: true, ScriptEntryKind.Query);
        var application = Create(ScriptScope.Application, buildSucceeded: true, ScriptEntryKind.Application);

        Assert.IsTrue(automation.IsAutomation);
        Assert.IsFalse(query.IsAutomation);
        Assert.IsFalse(application.IsAutomation);
        Assert.AreEqual("自动化入口", automation.EntryKindLabel);
        Assert.AreEqual("查询入口", query.EntryKindLabel);
        Assert.AreEqual("应用入口", application.EntryKindLabel);
    }

    [TestMethod]
    public void ApiVersionLabel_PrefersDescriptorAndSupportsFutureVersions()
    {
        var legacy = Create(ScriptScope.Application, buildSucceeded: true);
        var metadataV2 = legacy with { Metadata = new ScriptFileMetadata(ApiVersion: ScriptApiVersion.V2) };
        var descriptorV2 = metadataV2 with
        {
            Descriptor = new ScriptDescriptor(
                "sample",
                "示例脚本",
                ScriptApiVersion.V2,
                ScriptScope.Application),
        };
        var future = legacy with
        {
            Descriptor = new ScriptDescriptor(
                "sample",
                "示例脚本",
                (ScriptApiVersion)3,
                ScriptScope.Application),
        };

        Assert.AreEqual("V1", legacy.ApiVersionLabel);
        Assert.AreEqual("V2", metadataV2.ApiVersionLabel);
        Assert.AreEqual("V2", descriptorV2.ApiVersionLabel);
        Assert.AreEqual("V3", future.ApiVersionLabel);
        Assert.AreEqual("脚本 API V3", future.ApiVersionDescription);
    }

    [TestMethod]
    public void HistoryItem_FormatsEffectsSummary()
    {
        var item = CreateHistory(new ScriptEffectSummary(
            AppendedCount: 1,
            IdempotencyKey: "auto-daily-check:2026-08-13",
            CreatedWorkItemIds: [42]));

        Assert.IsTrue(item.HasEffects);
        Assert.AreEqual(
            "新增 1 条工作记录；幂等键：auto-daily-check:2026-08-13；新建 ID：42",
            item.EffectsSummary);
    }

    [TestMethod]
    public void HistoryItem_PreviewAndIdempotentReplayEffects()
    {
        var preview = CreateHistory(new ScriptEffectSummary(
            Preview: true,
            IdempotencyKey: "preview-key"));
        Assert.AreEqual("预览执行，未写入；幂等键：preview-key", preview.EffectsSummary);

        var replay = CreateHistory(new ScriptEffectSummary(
            AppendedCount: 0,
            IdempotencyKey: "auto-daily-check:2026-08-13"));
        Assert.AreEqual("幂等重放，未重复追加；幂等键：auto-daily-check:2026-08-13", replay.EffectsSummary);
    }

    [TestMethod]
    public void HistoryItem_WithoutEffectsHasNoSummary()
    {
        var item = CreateHistory(null);

        Assert.IsFalse(item.HasEffects);
        Assert.AreEqual(string.Empty, item.EffectsSummary);
    }

    private static ScriptHistoryListItem CreateHistory(ScriptEffectSummary? effects) => new(
        "sample",
        "示例脚本",
        nameof(ScriptExecutionStatus.Succeeded),
        nameof(ScriptExecutionSource.Manual),
        "2026-08-14 09:00:00",
        "120 ms",
        [],
        "log",
        effects);

    private static ScriptListItem Create(
        ScriptScope scope,
        bool buildSucceeded,
        ScriptEntryKind entryKind = ScriptEntryKind.Application) => new(
        "sample.cs",
        "sample",
        "示例脚本",
        scope,
        buildSucceeded,
        buildSucceeded ? "已加载" : "加载失败",
        [],
        [],
        EntryKind: entryKind);
}
