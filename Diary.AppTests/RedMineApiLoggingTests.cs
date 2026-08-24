using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Diary.RedMine;
using Microsoft.Extensions.Logging;

namespace Diary.AppTests;

[TestClass]
public sealed class RedMineApiLoggingTests
{
    [TestMethod]
    public async Task GetUserInfo_DoesNotLogResponseBodyOrApiKey()
    {
        const string responseSecret = "response-secret-api-key";
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var responseTask = ServeCurrentUserAsync(listener, responseSecret);
        var logger = new RecordingLogger<RedMineApi>();
        var api = new RedMineApi(new RedMineConfig
        {
            RedMineServerUrl = $"http://127.0.0.1:{port}/",
            RedMineApiKey = "request-api-key",
        }, logger);

        Assert.IsTrue(api.GetUserInfo(out var user));
        await responseTask;

        Assert.AreEqual(1, user.Id);
        Assert.AreEqual("admin", user.Login);
        var messages = string.Join(Environment.NewLine, logger.Entries.Select(entry => entry.Message));
        Assert.IsFalse(messages.Contains(responseSecret, StringComparison.Ordinal));
        Assert.IsFalse(messages.Contains("api_key", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(messages, "Loaded RedMine current user 1/admin");
    }

    [TestMethod]
    public async Task Dispose_WhileRequestIsInFlight_AllowsRequestToComplete()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseTask = ServeDelayedCurrentUserAsync(listener, requestReceived, releaseResponse);
        var api = new RedMineApi(new RedMineConfig
        {
            RedMineServerUrl = $"http://127.0.0.1:{port}/",
            RedMineApiKey = "request-api-key",
        });
        var requestTask = Task.Run(() =>
        {
            var success = api.GetUserInfo(out var user);
            return (Success: success, User: user);
        });

        await requestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        api.Dispose();
        releaseResponse.SetResult();

        var result = await requestTask.WaitAsync(TimeSpan.FromSeconds(5));
        await responseTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(result.Success);
        Assert.IsNotNull(result.User);
        Assert.AreEqual("admin", result.User.Login);
        Assert.IsFalse(api.GetUserInfo(out _));
    }

    private static async Task ServeCurrentUserAsync(TcpListener listener, string secret)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
        {
        }

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            user = new
            {
                id = 1,
                login = "admin",
                firstname = "Redmine",
                lastname = "Admin",
                api_key = secret,
            },
        }));
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    private static async Task ServeDelayedCurrentUserAsync(
        TcpListener listener,
        TaskCompletionSource requestReceived,
        TaskCompletionSource releaseResponse)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
        {
        }

        requestReceived.SetResult();
        await releaseResponse.Task;
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            user = new
            {
                id = 1,
                login = "admin",
                firstname = "Redmine",
                lastname = "Admin",
            },
        }));
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
