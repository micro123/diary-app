using Diary.App.ViewModels;
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
