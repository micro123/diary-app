using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Diary.Agent.Credentials;

namespace Diary.Agent.Web;

public sealed class WebFetchService(
    WebAccessPolicy policy,
    WebTargetValidator validator,
    WebHttpClientFactory clientFactory,
    IAiCredentialStore credentials)
{
    public async ValueTask<WebFetchResult> FetchAsync(
        Uri requestedUrl,
        HttpMethod? method = null,
        CancellationToken cancellationToken = default)
    {
        method ??= HttpMethod.Get;
        if (method != HttpMethod.Get && method != HttpMethod.Head)
            throw new WebFetchException(WebFetchErrorCode.InvalidUrl, "网页读取只允许 GET 或 HEAD。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(policy.Timeout);
        try
        {
            var client = await clientFactory.GetClientAsync();
            var current = requestedUrl;
            var authenticationOrigin = requestedUrl;
            for (var redirect = 0; redirect <= policy.MaxRedirects; redirect++)
            {
                var target = await validator.ValidateAsync(current, timeout.Token);
                using var request = new HttpRequestMessage(method, current);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.UserAgent.ParseAdd("DiaryApp-AiWebFetch/1");
                if (WebTargetValidator.IsSameOrigin(authenticationOrigin, current))
                    await ApplySiteAuthenticationAsync(request, target.InternalSite, timeout.Token);
                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token);
                ValidateHeaderBudget(response);
                if (IsRedirect(response.StatusCode))
                {
                    if (redirect == policy.MaxRedirects)
                        throw new WebFetchException(WebFetchErrorCode.RedirectLimit, "网页重定向次数超过限制。");
                    var location = response.Headers.Location
                        ?? throw new WebFetchException(WebFetchErrorCode.HttpError, "网页重定向响应缺少 Location。");
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    throw new WebFetchException(
                        WebFetchErrorCode.HttpError,
                        $"网页服务返回 HTTP {(int)response.StatusCode}。");
                }
                var contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? string.Empty;
                EnsureSupportedContentType(contentType);
                if (method == HttpMethod.Head)
                {
                    return new WebFetchResult(
                        requestedUrl,
                        current,
                        null,
                        contentType,
                        DateTimeOffset.UtcNow,
                        string.Empty,
                        false);
                }
                var bytes = await ReadBodyAsync(response, timeout.Token);
                var encoding = ResolveEncoding(response.Content.Headers.ContentType);
                var rawContent = encoding.GetString(bytes);
                var (title, text) = await ExtractContentAsync(contentType, rawContent, timeout.Token);
                var truncated = text.Length > policy.MaxExtractedCharacters;
                if (truncated)
                    text = text[..policy.MaxExtractedCharacters];
                return new WebFetchResult(
                    requestedUrl,
                    current,
                    title,
                    contentType,
                    DateTimeOffset.UtcNow,
                    text,
                    truncated);
            }
            throw new WebFetchException(WebFetchErrorCode.RedirectLimit, "网页重定向次数超过限制。");
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new WebFetchException(WebFetchErrorCode.Cancelled, "网页读取已取消。", exception);
        }
        catch (OperationCanceledException exception)
        {
            throw new WebFetchException(WebFetchErrorCode.Timeout, "网页读取超时。", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new WebFetchException(WebFetchErrorCode.Network, "网页网络请求失败。", exception);
        }
    }

    private async ValueTask ApplySiteAuthenticationAsync(
        HttpRequestMessage request,
        InternalWebSitePolicy? site,
        CancellationToken cancellationToken)
    {
        if (site is null || string.IsNullOrWhiteSpace(site.CredentialReference))
            return;
        if (string.IsNullOrWhiteSpace(site.AuthenticationHeaderName)
            || site.AuthenticationHeaderName.Contains('\r')
            || site.AuthenticationHeaderName.Contains('\n')
            || site.AuthenticationHeaderName.Equals("Host", StringComparison.OrdinalIgnoreCase)
            || site.AuthenticationHeaderName.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
        {
            throw new WebFetchException(WebFetchErrorCode.AuthenticationUnavailable, "内部站点认证 Header 配置无效。");
        }
        var credential = await credentials.GetAsync(site.CredentialReference, cancellationToken)
            ?? throw new WebFetchException(WebFetchErrorCode.AuthenticationUnavailable, "内部站点认证凭据不存在。");
        request.Headers.TryAddWithoutValidation(
            site.AuthenticationHeaderName,
            (site.AuthenticationHeaderPrefix ?? string.Empty) + credential.Reveal());
    }

    private void ValidateHeaderBudget(HttpResponseMessage response)
    {
        var size = response.Headers.Sum(header => HeaderSize(header.Key, header.Value));
        size += response.Content.Headers.Sum(header => HeaderSize(header.Key, header.Value));
        if (size > policy.MaxHeaderBytes)
            throw new WebFetchException(WebFetchErrorCode.HeadersTooLarge, "网页响应头超过大小限制。");
    }

    private async ValueTask<byte[]> ReadBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > policy.MaxCompressedBytes)
            throw new WebFetchException(WebFetchErrorCode.ContentTooLarge, "网页压缩内容超过大小限制。");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        var compressed = await ReadLimitedAsync(source, policy.MaxCompressedBytes, cancellationToken);
        Stream decoded = new MemoryStream(compressed, writable: false);
        foreach (var contentEncoding in response.Content.Headers.ContentEncoding.Reverse())
        {
            decoded = contentEncoding.ToLowerInvariant() switch
            {
                "gzip" => new GZipStream(decoded, CompressionMode.Decompress),
                "deflate" => new DeflateStream(decoded, CompressionMode.Decompress),
                "br" => new BrotliStream(decoded, CompressionMode.Decompress),
                "identity" => decoded,
                _ => throw new WebFetchException(
                    WebFetchErrorCode.UnsupportedContentType,
                    $"不支持网页 Content-Encoding：{contentEncoding}。"),
            };
        }
        await using (decoded)
            return await ReadLimitedAsync(decoded, policy.MaxDecompressedBytes, cancellationToken);
    }

    private static async ValueTask<byte[]> ReadLimitedAsync(
        Stream source,
        int limit,
        CancellationToken cancellationToken)
    {
        using var destination = new MemoryStream(Math.Min(limit, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return destination.ToArray();
            if (destination.Length + read > limit)
                throw new WebFetchException(WebFetchErrorCode.ContentTooLarge, "网页内容超过大小限制。");
            destination.Write(buffer, 0, read);
        }
    }

    private static async ValueTask<(string? Title, string Text)> ExtractContentAsync(
        string contentType,
        string content,
        CancellationToken cancellationToken)
    {
        if (contentType == "text/html" || contentType == "application/xhtml+xml")
        {
            try
            {
                var parser = new HtmlParser();
                var document = await parser.ParseDocumentAsync(content, cancellationToken);
                foreach (var element in document.QuerySelectorAll(
                             "script,style,form,noscript,template,svg,canvas,iframe,object,embed,[hidden],[aria-hidden='true']"))
                {
                    element.Remove();
                }
                return (document.Title, NormalizeWhitespace(document.Body?.TextContent ?? string.Empty));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new WebFetchException(WebFetchErrorCode.ParseFailure, "HTML 正文提取失败。", exception);
            }
        }
        if (contentType == "application/json" || contentType.EndsWith("+json", StringComparison.Ordinal))
        {
            try
            {
                using var document = JsonDocument.Parse(content);
                return (null, JsonSerializer.Serialize(document.RootElement));
            }
            catch (JsonException exception)
            {
                throw new WebFetchException(WebFetchErrorCode.ParseFailure, "网页 JSON 内容无效。", exception);
            }
        }
        return (null, NormalizeWhitespace(content));
    }

    private static void EnsureSupportedContentType(string contentType)
    {
        if (contentType == "text/html"
            || contentType == "application/xhtml+xml"
            || contentType == "text/plain"
            || contentType == "application/json"
            || contentType.EndsWith("+json", StringComparison.Ordinal))
        {
            return;
        }
        throw new WebFetchException(WebFetchErrorCode.UnsupportedContentType, "网页 Content-Type 不在允许列表中。");
    }

    private static Encoding ResolveEncoding(MediaTypeHeaderValue? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType?.CharSet))
            return Encoding.UTF8;
        try
        {
            return Encoding.GetEncoding(contentType.CharSet.Trim('"'));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    private static string NormalizeWhitespace(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static int HeaderSize(string name, IEnumerable<string> values) =>
        Encoding.UTF8.GetByteCount(name) + values.Sum(value => Encoding.UTF8.GetByteCount(value)) + 4;

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently
        or HttpStatusCode.Found
        or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect;
}
