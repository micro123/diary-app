using System.Net;
using System.Text.Json;
using Diary.Jira;

namespace Diary.JiraTests;

[TestClass]
public sealed class JiraApiTests
{
    [TestMethod]
    public async Task SearchProjectsAsync_UsesJiraV3EndpointAndBasicAuthentication()
    {
        HttpRequestMessage? request = null;
        using var client = new HttpClient(new RecordingHandler(message =>
        {
            request = message;
            return Task.FromResult(JsonResponse("{\"values\":[{\"key\":\"APP\",\"name\":\"应用\",\"description\":\"项目\",\"archived\":false}]}"));
        }));
        var api = new JiraApi(new JiraConfig
        {
            ServerUrl = "https://jira.example",
            UserName = "user@example.com",
            ApiToken = "secret",
        }, client);

        var result = await api.SearchProjectsAsync("应用");

        Assert.IsTrue(result.Success);
        Assert.AreEqual("APP", result.Value![0].Key);
        Assert.AreEqual("/rest/api/3/project/search?startAt=0&maxResults=50&query=%E5%BA%94%E7%94%A8", request!.RequestUri!.PathAndQuery);
        Assert.AreEqual("Basic", request.Headers.Authorization!.Scheme);
        Assert.AreEqual("dXNlckBleGFtcGxlLmNvbTpzZWNyZXQ=", request.Headers.Authorization.Parameter);
    }

    [TestMethod]
    public async Task AddWorklogAsync_SerializesSecondsAndAdfComment()
    {
        HttpRequestMessage? request = null;
        string? body = null;
        using var client = new HttpClient(new RecordingHandler(async message =>
        {
            request = message;
            body = await message.Content!.ReadAsStringAsync();
            return JsonResponse("{\"id\":\"10001\",\"timeSpentSeconds\":5400,\"started\":\"2026-08-09T00:00:00.000+0000\"}");
        }));
        var api = new JiraApi(new JiraConfig
        {
            ServerUrl = "https://jira.example/",
            UseBearerToken = true,
            ApiToken = "pat",
        }, client);

        var result = await api.AddWorklogAsync("APP-42", new DateOnly(2026, 8, 9), 1.5, "完成日报");
        using var json = JsonDocument.Parse(body!);

        Assert.IsTrue(result.Success);
        Assert.AreEqual("10001", result.Value!.Id);
        Assert.AreEqual(HttpMethod.Post, request!.Method);
        Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
        Assert.AreEqual(5400, json.RootElement.GetProperty("timeSpentSeconds").GetInt64());
        Assert.AreEqual("2026-08-09T00:00:00.000+0000", json.RootElement.GetProperty("started").GetString());
        Assert.AreEqual("完成日报", json.RootElement.GetProperty("comment").GetProperty("content")[0].GetProperty("content")[0].GetProperty("text").GetString());
    }

    [TestMethod]
    public async Task FailedResponse_ReturnsStructuredStatusAndMessage()
    {
        using var client = new HttpClient(new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("{\"errorMessages\":[\"没有记录工时权限\"]}")
        })));
        var api = new JiraApi(new JiraConfig
        {
            ServerUrl = "https://jira.example",
            UseBearerToken = true,
            ApiToken = "pat",
        }, client);

        var result = await api.AddWorklogAsync("APP-42", new DateOnly(2026, 8, 9), 1, null);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(403, result.StatusCode);
        StringAssert.Contains(result.Error, "没有记录工时权限");
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request);
    }
}
