using Diary.App;

namespace Diary.AppTests;

[TestClass]
public sealed class ScriptCompletionProviderTests
{
    [TestMethod]
    public void GetCompletions_ReturnsLanguageKeywordsAndCurrentSymbols()
    {
        const string text = "public sealed class Demo { pri";

        var items = ScriptCompletionProvider.GetCompletions("demo.cs", text, text.Length);

        Assert.IsTrue(items.Any(item => item.Text == "private"));
        Assert.IsTrue(ScriptCompletionProvider.GetCompletions("demo.cs", text, text.IndexOf("Demo", StringComparison.Ordinal) + 4)
            .Any(item => item.Text == "Demo"));
    }

    [TestMethod]
    public void GetCompletions_ReturnsHostMembersAfterDot()
    {
        const string text = "context.di";

        var items = ScriptCompletionProvider.GetCompletions("demo.py", text, text.Length);

        Assert.AreEqual("diary", items.Single().Text);
    }
}
