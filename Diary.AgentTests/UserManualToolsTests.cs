using System.Text.Json;
using Diary.Agent.Manual;
using Diary.Agent.Tools;

namespace Diary.AgentTests;

[TestClass]
public sealed class UserManualToolsTests
{
    [TestMethod]
    public async Task SearchAndReadUseOnlyManualSectionsAndRespectOutputLimit()
    {
        var root = Path.Combine(Path.GetTempPath(), $"diary-manual-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, UserManualIndexService.HtmlFileName);
        try
        {
            var repeated = string.Concat(Enumerable.Repeat("标签规则会设置 Redmine Issue 和 Activity。", 40));
            await File.WriteAllTextAsync(path, $$"""
                <html><head><style>PRIVATE_STYLE</style></head><body>
                <main id="quarto-document-content">
                  <section id="tracker" class="level1">
                    <h1>Tracker 设置</h1>
                    <p>配置 Tracker 实例。</p>
                    <section id="redmine-tag-rules" class="level2">
                      <h2>Redmine 标签自动化</h2>
                      <p>{{repeated}}</p>
                    </section>
                  </section>
                </main>
                </body></html>
                """);
            var manual = new UserManualIndexService(path);
            var searchTool = new SearchUserManualTool(manual);
            var readTool = new ReadUserManualSectionTool(manual);
            using var searchArguments = JsonDocument.Parse("""{"query":"Redmine 标签","limit":5}""");

            var search = await searchTool.InvokeAsync(
                searchArguments.RootElement,
                CreateContext());

            Assert.IsTrue(search.Succeeded, search.Content);
            Assert.IsTrue(search.Content.Length < 1500, $"搜索结果过长：{search.Content.Length}");
            using var searchResult = JsonDocument.Parse(search.Content);
            var match = searchResult.RootElement.GetProperty("results")[0];
            Assert.AreEqual("redmine-tag-rules", match.GetProperty("sectionId").GetString());
            Assert.AreEqual("Redmine 标签自动化", match.GetProperty("title").GetString());
            Assert.IsTrue(match.GetProperty("snippet").GetString()!.Length <= 122);
            Assert.IsFalse(search.Content.Contains("PRIVATE_STYLE", StringComparison.Ordinal));

            using var readArguments = JsonDocument.Parse(
                """{"sectionId":"redmine-tag-rules","maxCharacters":500}""");
            var read = await readTool.InvokeAsync(readArguments.RootElement, CreateContext());

            Assert.IsTrue(read.Succeeded, read.Content);
            Assert.IsTrue(read.IsTruncated);
            Assert.AreEqual("DiaryApp 用户手册", read.Source);
            using var readResult = JsonDocument.Parse(read.Content);
            Assert.AreEqual(500, readResult.RootElement.GetProperty("content").GetString()!.Length);
            Assert.IsTrue(readResult.RootElement.GetProperty("isTruncated").GetBoolean());
            Assert.AreEqual(500, readResult.RootElement.GetProperty("nextOffset").GetInt32());

            using var continuationArguments = JsonDocument.Parse(
                """{"sectionId":"redmine-tag-rules","offset":500,"maxCharacters":300}""");
            var continuation = await readTool.InvokeAsync(continuationArguments.RootElement, CreateContext());

            Assert.IsTrue(continuation.Succeeded, continuation.Content);
            using var continuationResult = JsonDocument.Parse(continuation.Content);
            Assert.AreEqual(500, continuationResult.RootElement.GetProperty("offset").GetInt32());
            Assert.AreEqual(300, continuationResult.RootElement.GetProperty("content").GetString()!.Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SearchDefaultsToThreeCompactMatchesAndRejectsLargeLimit()
    {
        var root = Path.Combine(Path.GetTempPath(), $"diary-manual-search-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, UserManualIndexService.HtmlFileName);
        try
        {
            var sections = string.Join(Environment.NewLine, Enumerable.Range(1, 8).Select(index =>
                $"<section id=\"section-{index}\"><h2>代理设置 {index}</h2><p>{new string('文', 500)} 代理设置说明</p></section>"));
            await File.WriteAllTextAsync(path, $"<html><body><main id=\"quarto-document-content\">{sections}</main></body></html>");
            var tool = new SearchUserManualTool(new UserManualIndexService(path));
            using var defaultArguments = JsonDocument.Parse("""{"query":"代理设置"}""");

            var result = await tool.InvokeAsync(defaultArguments.RootElement, CreateContext());

            Assert.IsTrue(result.Succeeded, result.Content);
            Assert.IsTrue(result.Content.Length < 1500, $"搜索结果过长：{result.Content.Length}");
            using var document = JsonDocument.Parse(result.Content);
            Assert.AreEqual(3, document.RootElement.GetProperty("results").GetArrayLength());

            using var invalidArguments = JsonDocument.Parse("""{"query":"代理设置","limit":6}""");
            var invalid = await tool.InvokeAsync(invalidArguments.RootElement, CreateContext());
            Assert.IsFalse(invalid.Succeeded);
            Assert.AreEqual("invalid_arguments", invalid.ErrorCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task MissingManualReturnsExplicitUnavailableError()
    {
        var tool = new SearchUserManualTool(new UserManualIndexService(null));
        using var arguments = JsonDocument.Parse("""{"query":"设置"}""");

        var result = await tool.InvokeAsync(arguments.RootElement, CreateContext());

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("manual_unavailable", result.ErrorCode);
    }

    private static AgentToolInvocationContext CreateContext() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        EmptyServiceProvider.Instance);

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();
        public object? GetService(Type serviceType) => null;
    }
}
