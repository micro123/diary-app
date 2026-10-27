using System.Text.Json;
using Avalonia.Input;
using Diary.Agent.Configuration;
using Diary.Agent.Tools;
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

    [TestMethod]
    public void OnlyNonEmptyUserMessagesExposeQuickActions()
    {
        var user = new AiChatMessageViewModel("你", "重新整理本月工作");
        var assistant = new AiChatMessageViewModel("AI", "这是整理结果");

        Assert.IsTrue(user.IsUser);
        Assert.IsTrue(user.HasUserActions);
        Assert.IsFalse(assistant.IsUser);
        Assert.IsFalse(assistant.HasUserActions);

        user.Content = "   ";
        Assert.IsFalse(user.HasUserActions);
    }

    [TestMethod]
    public void AgentJsonViewsDisplayUnicodeAndKeepValidJsonEscaping()
    {
        const string escaped = """{"title":"\u52A0\u73ED","html":"\u003Ctag\u003E"}""";

        var formatted = AiAgentPageViewModel.FormatToolOutput(escaped);

        StringAssert.Contains(formatted, "加班");
        Assert.IsFalse(formatted.Contains("\\u52A0", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(formatted, "\\u003Ctag\\u003E");
        using var document = JsonDocument.Parse(formatted);
        var confirmation = new AiExternalToolConfirmationViewModel(new ExternalToolConfirmationRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "DiaryApp 内置工具",
            "测试工具",
            document.RootElement.Clone()));
        StringAssert.Contains(confirmation.ArgumentsJson, "加班");
        Assert.IsFalse(confirmation.ArgumentsJson.Contains("\\u52A0", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void SessionReasoningOverrideDoesNotMutateSavedProfile()
    {
        var profile = new AiConnectionProfile
        {
            Id = "test-profile",
            DisplayName = "测试连接",
            Protocol = AiProtocol.AnthropicMessages,
            BaseUri = new Uri("https://example.com/"),
            Model = "test-model",
            Reasoning = new AiReasoningOptions
            {
                Effort = AiReasoningEffort.High,
                BudgetTokens = 4096,
                RequestSummary = false,
            },
        };

        var unchanged = AiAgentPageViewModel.CreateSessionProfile(
            profile,
            AiReasoningEffort.Disabled,
            isReasoningOverridden: false);
        var overridden = AiAgentPageViewModel.CreateSessionProfile(
            profile,
            AiReasoningEffort.Medium,
            isReasoningOverridden: true);

        Assert.AreSame(profile, unchanged);
        Assert.AreNotSame(profile, overridden);
        Assert.AreEqual(AiReasoningEffort.Medium, overridden.Reasoning.Effort);
        Assert.IsNull(overridden.Reasoning.BudgetTokens);
        Assert.IsFalse(overridden.Reasoning.RequestSummary);
        Assert.AreEqual(AiReasoningEffort.High, profile.Reasoning.Effort);
        Assert.AreEqual(4096, profile.Reasoning.BudgetTokens);
    }
}
