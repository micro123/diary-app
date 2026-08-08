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

    private static ScriptListItem Create(ScriptScope scope, bool buildSucceeded) => new(
        "sample.cs",
        "sample",
        "示例脚本",
        scope,
        true,
        buildSucceeded,
        buildSucceeded ? "已加载" : "加载失败",
        [],
        []);
}
