using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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

public interface IUpdatePackageSource
{
    ValueTask DownloadPackageAsync(
        Uri packageUri,
        string targetPath,
        UpdatePackageDescriptor descriptor,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    ValueTask DownloadContentAsync(
        Uri contentUri,
        string targetPath,
        UpdateManifestFile descriptor,
        IProgress<UpdateDownloadProgress>? progress = null,
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

public sealed class HttpUpdateSource(HttpClient httpClient) : IUpdateSource, IUpdatePackageSource
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

    public async ValueTask DownloadPackageAsync(
        Uri packageUri,
        string targetPath,
        UpdatePackageDescriptor descriptor,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!packageUri.IsAbsoluteUri || packageUri.Scheme is not ("http" or "https"))
            throw new ArgumentException("完整包地址必须是 HTTP 或 HTTPS 绝对地址。", nameof(packageUri));
        if (descriptor.Size <= 0 || !UpdateHash.IsSha256(descriptor.Sha256))
            throw new InvalidDataException("完整包描述非法。");

        await DownloadFileAsync(
            packageUri,
            targetPath,
            descriptor.Size,
            descriptor.Sha256,
            "更新完整包",
            progress,
            cancellationToken);
    }

    public async ValueTask DownloadContentAsync(
        Uri contentUri,
        string targetPath,
        UpdateManifestFile descriptor,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!contentUri.IsAbsoluteUri || contentUri.Scheme is not ("http" or "https"))
            throw new ArgumentException("更新文件地址必须是 HTTP 或 HTTPS 绝对地址。", nameof(contentUri));
        if (descriptor.Size < 0 || !UpdateHash.IsSha256(descriptor.Sha256))
            throw new InvalidDataException($"更新文件描述非法：{descriptor.Path}");

        await DownloadFileAsync(
            contentUri,
            targetPath,
            descriptor.Size,
            descriptor.Sha256,
            $"更新文件 {descriptor.Path}",
            progress,
            cancellationToken);
    }

    private async ValueTask DownloadFileAsync(
        Uri uri,
        string targetPath,
        long expectedSize,
        string expectedSha256,
        string operation,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {

        var directory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidDataException($"{operation}目标路径没有父目录。");
        Directory.CreateDirectory(directory);
        if (File.Exists(targetPath))
        {
            await UpdateHash.VerifyFileAsync(targetPath, expectedSize, expectedSha256, cancellationToken);
            progress?.Report(new(expectedSize, expectedSize));
            return;
        }

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                throw new UpdateSourceException($"无法下载{operation}。", true, innerException: exception);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new UpdateSourceException($"下载{operation}超时。", true, innerException: exception);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    throw CreateHttpError(response.StatusCode, $"下载{operation}失败");
                if (response.Content.Headers.ContentLength is { } contentLength
                    && contentLength != expectedSize)
                {
                    throw new InvalidDataException($"{operation}的 Content-Length 与清单不匹配。");
                }

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var target = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1024 * 1024];
                long received = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                        break;
                    received += read;
                    if (received > expectedSize)
                        throw new InvalidDataException($"{operation}大小超过清单声明。");
                    digest.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress?.Report(new(received, expectedSize));
                }
                await target.FlushAsync(cancellationToken);
                if (received != expectedSize)
                    throw new InvalidDataException($"{operation}下载长度与清单不匹配。");
                var actualSha256 = Convert.ToHexStringLower(digest.GetHashAndReset());
                if (!string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal))
                    throw new InvalidDataException($"{operation}的 SHA-256 与清单不匹配。");
            }
            File.Move(temporaryPath, targetPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static UpdateSourceException CreateHttpError(HttpStatusCode statusCode, string operation)
    {
        var retryable = statusCode is HttpStatusCode.TooManyRequests
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout
            || (int)statusCode >= 500;
        return new UpdateSourceException($"{operation}：HTTP {(int)statusCode}。", retryable, statusCode);
    }
}

public static class UpdateUris
{
    public static Uri Latest(Uri serverUri, string channel, string rid, string flavor) =>
        Build(serverUri, $"api/v1/updates/latest?channel={Escape(channel)}&rid={Escape(rid)}&flavor={Escape(flavor)}");

    public static Uri FullPackage(Uri serverUri, UpdateManifest manifest) =>
        Build(serverUri,
            $"api/v1/updates/packages/{Escape(manifest.Channel)}/{manifest.Sequence}/{Escape(manifest.Rid)}/{Escape(manifest.Flavor)}");

    public static Uri Content(Uri serverUri, string sha256)
    {
        if (!UpdateHash.IsSha256(sha256))
            throw new ArgumentException("内容 SHA-256 非法。", nameof(sha256));
        return Build(serverUri, $"api/v1/updates/content/{sha256}");
    }

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
