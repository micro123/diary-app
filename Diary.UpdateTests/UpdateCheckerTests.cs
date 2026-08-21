using System.Net;
using System.Text;
using System.Text.Json;
using Diary.Update;

namespace Diary.UpdateTests;

[TestClass]
public sealed class UpdateCheckerTests
{
    [TestMethod]
    public async Task NewerSequence_ReturnsUpdateAvailable()
    {
        var checker = CreateChecker(_ => JsonResponse(HttpStatusCode.OK, CreateEnvelope(sequence: 501)));

        var result = await checker.CheckAsync(CreateRequest(currentSequence: 500));

        Assert.AreEqual(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.AreEqual(501, result.Envelope?.Manifest.Sequence);
        Assert.AreEqual(
            "http://updates.local/api/v1/updates/packages/preview/501/win-x64/standard",
            result.FullPackageUri?.AbsoluteUri);
    }

    [TestMethod]
    public async Task SameSequence_ReturnsUpToDate()
    {
        var checker = CreateChecker(_ => JsonResponse(HttpStatusCode.OK, CreateEnvelope(sequence: 500)));

        var result = await checker.CheckAsync(CreateRequest(currentSequence: 500));

        Assert.AreEqual(UpdateCheckStatus.UpToDate, result.Status);
    }

    [TestMethod]
    public async Task NotFound_ReturnsNoPublishedVersion()
    {
        var checker = CreateChecker(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await checker.CheckAsync(CreateRequest());

        Assert.AreEqual(UpdateCheckStatus.NoPublishedVersion, result.Status);
    }

    [TestMethod]
    public async Task ServiceUnavailable_IsNotReportedAsNoUpdate()
    {
        var checker = CreateChecker(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var result = await checker.CheckAsync(CreateRequest());

        Assert.AreEqual(UpdateCheckStatus.TemporarilyUnavailable, result.Status);
    }

    [TestMethod]
    public async Task MismatchedDimensions_ReturnInvalidResponse()
    {
        var checker = CreateChecker(_ => JsonResponse(
            HttpStatusCode.OK,
            CreateEnvelope(sequence: 501) with
            {
                Manifest = CreateEnvelope(sequence: 501).Manifest with { Flavor = "python313" },
            }));

        var result = await checker.CheckAsync(CreateRequest());

        Assert.AreEqual(UpdateCheckStatus.InvalidResponse, result.Status);
    }

    [TestMethod]
    public async Task NewerUpdaterProtocol_ReturnsUnsupportedUpdater()
    {
        var envelope = CreateEnvelope(sequence: 501);
        envelope = envelope with
        {
            Manifest = envelope.Manifest with { MinUpdaterVersion = UpdateProtocol.UpdaterProtocolVersion + 1 },
        };
        var checker = CreateChecker(_ => JsonResponse(HttpStatusCode.OK, envelope));

        var result = await checker.CheckAsync(CreateRequest());

        Assert.AreEqual(UpdateCheckStatus.UnsupportedUpdater, result.Status);
    }

    private static UpdateChecker CreateChecker(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var client = new HttpClient(new StubHandler(responseFactory));
        return new UpdateChecker(new HttpUpdateSource(client));
    }

    private static UpdateCheckRequest CreateRequest(long currentSequence = 500) =>
        new(new Uri("http://updates.local"), "preview", "win-x64", "standard", currentSequence);

    private static UpdateManifestEnvelope CreateEnvelope(long sequence)
    {
        var manifest = new UpdateManifest
        {
            ManifestFormatVersion = 1,
            VersionId = $"1.0.0-r{sequence}",
            Sequence = sequence,
            DataVersion = "1.0.0",
            Channel = "preview",
            Rid = "win-x64",
            Flavor = "standard",
            MinUpdaterVersion = 1,
            MinIncrementalSequence = 0,
            ManifestContentId = "sha256:" + new string('0', 64),
            Files =
            [
                new UpdateManifestFile
                {
                    Path = "Diary.App.dll",
                    Size = 100,
                    Sha256 = new string('b', 64),
                    Component = "app",
                },
            ],
        };
        manifest = manifest with { ManifestContentId = UpdateManifestValidator.ComputeContentId(manifest) };
        return new UpdateManifestEnvelope
        {
            Manifest = manifest,
            FullPackage = new UpdatePackageDescriptor
            {
                Size = 200,
                Sha256 = new string('c', 64),
            },
        };
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object value) => new(status)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(value, UpdateJson.CompactOptions),
            Encoding.UTF8,
            "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responseFactory(request));
    }
}
