using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Diary.RedMine;

namespace Diary.RedMineTests;

[TestClass]
[TestCategory("Unit")]
public sealed class RedMineApiProjectTests
{
    [TestMethod]
    public async Task SearchProjectWithoutKeywordReadsProjectsResponse()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var responseTask = ServeProjectsAsync(listener);
        using var api = new RedMineApi(new RedMineConfig
        {
            RedMineServerUrl = $"http://127.0.0.1:{port}/",
            RedMineApiKey = "test-key",
        });

        Assert.IsTrue(api.SearchProject(out var projects, out var total));
        await responseTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, total);
        var project = projects.Single();
        Assert.AreEqual(42, project.Id);
        Assert.AreEqual("DiaryApp API Test", project.Name);
    }

    private static async Task ServeProjectsAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
        {
        }

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            projects = new[]
            {
                new
                {
                    id = 42,
                    name = "DiaryApp API Test",
                    description = "Project returned by projects.json",
                },
            },
            total_count = 1,
        }));
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }
}
