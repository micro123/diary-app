using System.Net;
using System.Security.Cryptography;
using Diary.Update;

namespace Diary.UpdateTests;

[TestClass]
public sealed class HttpUpdateSourcePackageTests
{
    [TestMethod]
    public async Task DownloadPackageAsync_WritesValidatedPackageAndReportsProgress()
    {
        var payload = "complete update package"u8.ToArray();
        using var client = CreateClient(payload);
        var source = new HttpUpdateSource(client);
        using var directory = new TemporaryDirectory();
        var target = Path.Combine(directory.Path, "package.zip");
        UpdateDownloadProgress? reported = null;

        await source.DownloadPackageAsync(
            new Uri("http://updates.local/package.zip"),
            target,
            Descriptor(payload),
            new InlineProgress<UpdateDownloadProgress>(value => reported = value));

        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(target));
        Assert.IsNotNull(reported);
        Assert.AreEqual(payload.Length, reported.BytesReceived);
        Assert.AreEqual(payload.Length, reported.TotalBytes);
    }

    [TestMethod]
    public async Task DownloadPackageAsync_WhenHashDiffers_RemovesTemporaryFile()
    {
        var payload = "tampered"u8.ToArray();
        using var client = CreateClient(payload);
        var source = new HttpUpdateSource(client);
        using var directory = new TemporaryDirectory();
        var target = Path.Combine(directory.Path, "package.zip");
        var descriptor = Descriptor("expected"u8.ToArray()) with { Size = payload.Length };

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await source.DownloadPackageAsync(
                new Uri("http://updates.local/package.zip"),
                target,
                descriptor));

        Assert.IsFalse(File.Exists(target));
        Assert.IsEmpty(Directory.EnumerateFiles(directory.Path));
    }

    [TestMethod]
    public async Task DownloadPackageAsync_WhenContentLengthDiffers_RejectsResponse()
    {
        var payload = "short"u8.ToArray();
        using var client = CreateClient(payload, declaredLength: payload.Length + 1);
        var source = new HttpUpdateSource(client);
        using var directory = new TemporaryDirectory();

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await source.DownloadPackageAsync(
                new Uri("http://updates.local/package.zip"),
                Path.Combine(directory.Path, "package.zip"),
                Descriptor(payload)));
    }

    private static HttpClient CreateClient(byte[] payload, long? declaredLength = null)
        => new(new StubHandler(() =>
        {
            var content = new ByteArrayContent(payload);
            if (declaredLength is not null)
                content.Headers.ContentLength = declaredLength;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));

    private static UpdatePackageDescriptor Descriptor(byte[] payload) => new()
    {
        Size = payload.Length,
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(payload)),
    };

    private sealed class StubHandler(Func<HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responseFactory());
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"diary-update-source-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
