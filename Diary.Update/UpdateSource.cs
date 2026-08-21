using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Diary.Update;

public interface IUpdateSource
{
    ValueTask<UpdateManifestEnvelope?> GetLatestAsync(
        Uri serverUri,
        string channel,
        string rid,
        string flavor,
        CancellationToken cancellationToken = default);
}

public sealed class UpdateSourceException(
    string message,
    bool retryable,
    HttpStatusCode? statusCode = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public bool Retryable { get; } = retryable;
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

public sealed class HttpUpdateSource(HttpClient httpClient) : IUpdateSource
{
    public async ValueTask<UpdateManifestEnvelope?> GetLatestAsync(
        Uri serverUri,
        string channel,
        string rid,
        string flavor,
        CancellationToken cancellationToken = default)
    {
        var uri = UpdateUris.Latest(serverUri, channel, rid, flavor);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new UpdateSourceException("无法连接更新服务器。", true, innerException: exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateSourceException("连接更新服务器超时。", true, innerException: exception);
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (!response.IsSuccessStatusCode)
            {
                var retryable = response.StatusCode is HttpStatusCode.TooManyRequests
                    or HttpStatusCode.RequestTimeout
                    or HttpStatusCode.BadGateway
                    or HttpStatusCode.ServiceUnavailable
                    or HttpStatusCode.GatewayTimeout
                    || (int)response.StatusCode >= 500;
                throw new UpdateSourceException(
                    $"更新服务器返回 HTTP {(int)response.StatusCode}。",
                    retryable,
                    response.StatusCode);
            }
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await JsonSerializer.DeserializeAsync(
                           stream,
                           UpdateJson.CompactContext.UpdateManifestEnvelope,
                           cancellationToken)
                       ?? throw new InvalidDataException("更新服务器返回空响应。");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("更新服务器响应不是合法清单。", exception);
            }
            catch (IOException exception)
            {
                throw new UpdateSourceException("读取更新服务器响应失败。", true, innerException: exception);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new UpdateSourceException("读取更新服务器响应超时。", true, innerException: exception);
            }
        }
    }
}

public static class UpdateUris
{
    public static Uri Latest(Uri serverUri, string channel, string rid, string flavor) =>
        Build(serverUri, $"api/v1/updates/latest?channel={Escape(channel)}&rid={Escape(rid)}&flavor={Escape(flavor)}");

    public static Uri FullPackage(Uri serverUri, UpdateManifest manifest) =>
        Build(serverUri,
            $"api/v1/updates/packages/{Escape(manifest.Channel)}/{manifest.Sequence}/{Escape(manifest.Rid)}/{Escape(manifest.Flavor)}");

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static Uri Build(Uri serverUri, string relative)
    {
        if (!serverUri.IsAbsoluteUri || serverUri.Scheme is not ("http" or "https"))
            throw new ArgumentException("更新服务器地址必须是 HTTP 或 HTTPS 绝对地址。", nameof(serverUri));
        var normalized = serverUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? serverUri
            : new Uri(serverUri.AbsoluteUri + "/", UriKind.Absolute);
        return new Uri(normalized, relative);
    }
}
