using Avalonia.Input;
using Diary.Agent.UI.ViewModels;
using Diary.Agent.UI.Views;

namespace Diary.AppTests;

[TestClass]
public sealed class AiAgentPromptHistoryTests
{
    [TestMethod]
    public void NavigatorMovesThroughPromptsAndRestoresDraft()
    {
        var history = new PromptHistoryNavigator();
        history.Record("第一条");
        history.Record("第二条");

        Assert.IsTrue(history.TryMovePrevious("未发送草稿", out var value));
        Assert.AreEqual("第二条", value);
        Assert.IsTrue(history.TryMovePrevious(value, out value));
        Assert.AreEqual("第一条", value);
        Assert.IsFalse(history.TryMovePrevious(value, out value));
        Assert.IsTrue(history.TryMoveNext(value, out value));
        Assert.AreEqual("第二条", value);
        Assert.IsTrue(history.TryMoveNext(value, out value));
        Assert.AreEqual("未发送草稿", value);
        Assert.IsFalse(history.TryMoveNext(value, out value));
    }

    [TestMethod]
    public void NavigatorCollapsesAdjacentDuplicatesAndHonorsCapacity()
    {
        var history = new PromptHistoryNavigator(capacity: 2);
        history.Record("第一条");
        history.Record("第二条");
        history.Record("第二条");
        history.Record("第三条");

        Assert.IsTrue(history.TryMovePrevious(string.Empty, out var value));
        Assert.AreEqual("第三条", value);
        Assert.IsTrue(history.TryMovePrevious(value, out value));
        Assert.AreEqual("第二条", value);
        Assert.IsFalse(history.TryMovePrevious(value, out _));
    }

    [TestMethod]
    public void HistoryKeysRespectMultilineCaretSelectionAndModifiers()
    {
        const string text = "第一行\n第二行\n第三行";

        Assert.IsTrue(AiAgentPageView.ShouldNavigateHistory(
            Key.Up, KeyModifiers.None, text, caretIndex: 2, selectionStart: 2, selectionEnd: 2));
        Assert.IsFalse(AiAgentPageView.ShouldNavigateHistory(
            Key.Up, KeyModifiers.None, text, caretIndex: 6, selectionStart: 6, selectionEnd: 6));
        Assert.IsTrue(AiAgentPageView.ShouldNavigateHistory(
            Key.Down, KeyModifiers.None, text, caretIndex: text.Length, selectionStart: text.Length, selectionEnd: text.Length));
        Assert.IsFalse(AiAgentPageView.ShouldNavigateHistory(
            Key.Down, KeyModifiers.None, text, caretIndex: 6, selectionStart: 6, selectionEnd: 6));
        Assert.IsFalse(AiAgentPageView.ShouldNavigateHistory(
            Key.Up, KeyModifiers.Shift, text, caretIndex: 2, selectionStart: 2, selectionEnd: 2));
        Assert.IsFalse(AiAgentPageView.ShouldNavigateHistory(
            Key.Up, KeyModifiers.None, text, caretIndex: 2, selectionStart: 0, selectionEnd: 2));
        Assert.IsTrue(AiAgentPageView.ShouldNavigateHistory(
            Key.Up, KeyModifiers.None, "单行", caretIndex: 1, selectionStart: 1, selectionEnd: 1));
    }
}
