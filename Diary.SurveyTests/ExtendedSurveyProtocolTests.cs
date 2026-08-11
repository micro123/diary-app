using Diary.Survey;

namespace Diary.SurveyTests;

[TestClass]
[DoNotParallelize]
public sealed class ExtendedSurveyProtocolTests
{
    [TestMethod]
    public void RequestAndResponseUseStableJsonContract()
    {
        var request = new ExtendedSurveyRequest
        {
            RequestId = "request-1",
            StartDate = "2026-08-01",
            EndDate = "2026-08-31",
            Text = "发布",
            TagNames = ["项目A", "项目B"],
            TagFilter = "Any",
            Priority = 2,
        };

        var content = ExtendedSurveyProtocol.SerializeRequest(request);
        Assert.IsTrue(ExtendedSurveyProtocol.TryDeserializeRequest(content, out var parsed));
        Assert.IsNotNull(parsed);
        Assert.AreEqual(ExtendedSurveyProtocol.Version, parsed.Version);
        Assert.AreEqual("request-1", parsed.RequestId);
        Assert.AreEqual("custom_statistics", parsed.Kind);
        CollectionAssert.AreEqual(new[] { "项目A", "项目B" }, parsed.TagNames);

        var responseContent = ExtendedSurveyProtocol.SerializeSuccess("request-1", "{\"hours\":1.5}");
        Assert.IsTrue(responseContent.Contains("\"ok\":true", StringComparison.Ordinal));
        Assert.IsTrue(responseContent.Contains("\"hours\":1.5", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ExtendedPortSupportsSurveyRespondentRoundTrip()
    {
        var surveyor = new AppSurveyor(SurveyPorts.Extended);
        var respondent = new AppRespondent(SurveyPorts.Extended);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        respondent.ReceiveMessage += (_, content) =>
        {
            if (ExtendedSurveyProtocol.TryDeserializeRequest(content, out var request) && request is not null)
            {
                respondent.Send(ExtendedSurveyProtocol.SerializeSuccess(
                    request.RequestId,
                    "{\"hostname\":\"host\",\"username\":\"user\",\"hours\":2}"));
            }
        };
        surveyor.ReceiveMessage += (_, content) => received.TrySetResult(content);

        try
        {
            Assert.IsTrue(surveyor.StartServer());
            Assert.IsTrue(respondent.Connect("127.0.0.1"));
            var request = ExtendedSurveyProtocol.SerializeRequest(new ExtendedSurveyRequest
            {
                RequestId = Guid.NewGuid().ToString("N"),
                StartDate = "2026-08-01",
                EndDate = "2026-08-31",
            });
            await SurveyUntilReply(surveyor, request, received.Task);
            StringAssert.Contains(await received.Task, "\"request_id\"");
        }
        finally
        {
            await respondent.ShutdownAsync();
            await surveyor.StopServerAsync();
        }
    }

    private static async Task SurveyUntilReply(AppSurveyor surveyor, string request, Task reply)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var retryTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));

        while (!reply.IsCompleted)
        {
            await surveyor.SurveyAsync(request);
            var completed = await Task.WhenAny(reply, retryTimer.WaitForNextTickAsync(timeout.Token).AsTask());
            if (completed == reply)
                await reply;
        }
    }
}
