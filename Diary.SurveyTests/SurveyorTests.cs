using Diary.Survey;

namespace Diary.SurveyTests;

[TestClass]
[DoNotParallelize]
public sealed class SurveyorTests
{
    [TestMethod]
    public void StartAndStopAreIdempotent()
    {
        var surveyor = new AppSurveyor();

        Assert.IsTrue(surveyor.StartServer());
        Assert.IsFalse(surveyor.StartServer());
        surveyor.StopServer();
        surveyor.StopServer();

        Assert.IsTrue(surveyor.StartServer());
        surveyor.StopServer();
    }

    [TestMethod]
    public void RapidStartAndStopDoesNotRaceReceiveLoop()
    {
        var surveyor = new AppSurveyor();

        for (var i = 0; i < 20; i++)
        {
            Assert.IsTrue(surveyor.StartServer());
            surveyor.Survey("question");
            surveyor.StopServer();
        }
    }

    [TestMethod]
    public async Task ThrowingSubscribersAreDiagnosedAndDoNotStopMessaging()
    {
        var surveyor = new AppSurveyor();
        var respondent = new AppRespondent();
        var respondentError = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var surveyorError = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replyCount = 0;

        respondent.ReceiveMessage += (_, _) => throw new InvalidOperationException("respondent subscriber failed");
        respondent.ReceiveMessage += (_, question) => respondent.Send($"reply:{question}");
        respondent.ReceiveMessageHandlerError += (_, error) => respondentError.TrySetResult(error);
        surveyor.ReceiveMessage += (_, _) => throw new InvalidOperationException("surveyor subscriber failed");
        surveyor.ReceiveMessage += (_, _) =>
        {
            if (Interlocked.Increment(ref replyCount) == 1)
                firstReply.TrySetResult();
            else
            {
                secondReply.TrySetResult();
                surveyor.StopServer();
                callbackStop.TrySetResult();
            }
        };
        surveyor.ReceiveMessageHandlerError += (_, error) => surveyorError.TrySetResult(error);

        try
        {
            Assert.IsTrue(surveyor.StartServer());
            Assert.IsTrue(respondent.Connect("127.0.0.1"));

            await SurveyUntilReply(surveyor, "first", firstReply.Task);
            Assert.IsInstanceOfType<InvalidOperationException>(await respondentError.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsInstanceOfType<InvalidOperationException>(await surveyorError.Task.WaitAsync(TimeSpan.FromSeconds(5)));

            await SurveyUntilReply(surveyor, "second", secondReply.Task);
            await callbackStop.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            respondent.Shutdown();
            surveyor.StopServer();
        }
    }

    private static async Task SurveyUntilReply(AppSurveyor surveyor, string question, Task reply)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var retryTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));

        while (!reply.IsCompleted)
        {
            surveyor.Survey(question);
            var completed = await Task.WhenAny(reply, retryTimer.WaitForNextTickAsync(timeout.Token).AsTask());
            if (completed == reply)
                await reply;
        }
    }
}
