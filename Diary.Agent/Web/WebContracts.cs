using Diary.Agent.Configuration;
using System.Text.Json.Serialization;

namespace Diary.Agent.Web;

public sealed record WebSearchRequest(string Query, int Limit = 5);

public sealed record WebSearchResultItem(
    string Title,
    Uri Url,
    string Summary,
    string Provider);

public interface IWebSearchProvider
{
    ValueTask<IReadOnlyList<WebSearchResultItem>> SearchAsync(
        WebSearchRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record InternalWebSitePolicy(
    string HostPattern,
    IReadOnlySet<int> AllowedPorts,
    string? AuthenticationHeaderName = null,
    string? AuthenticationHeaderPrefix = null,
    string? CredentialReference = null);

[JsonConverter(typeof(JsonStringEnumConverter<BrowserAccessMode>))]
public enum BrowserAccessMode
{
    Disabled,
    System,
    Executable,
    Cdp,
}

public enum BrowserWaitMode
{
    DomContentLoaded,
    Load,
    ShortDelay,
}

public sealed record BrowserAccessPolicy
{
    public BrowserAccessMode Mode { get; init; } = BrowserAccessMode.System;

    public string? ExecutablePath { get; init; }

    public Uri? CdpEndpoint { get; init; }

    public bool Headless { get; init; } = true;

    public int RenderDelayMilliseconds { get; init; } = 750;

    public int MaxLinks { get; init; } = 100;
}

public sealed record WebAccessPolicy
{
    public AiProxyConfiguration Proxy { get; init; } = new() { Mode = AiProxyMode.System };

    public IReadOnlyList<InternalWebSitePolicy> InternalSites { get; init; } = [];

    public BrowserAccessPolicy Browser { get; init; } = new();

    public int MaxRedirects { get; init; } = 3;

    public int MaxHeaderBytes { get; init; } = 64 * 1024;

    public int MaxCompressedBytes { get; init; } = 2 * 1024 * 1024;

    public int MaxDecompressedBytes { get; init; } = 8 * 1024 * 1024;

    public int MaxExtractedCharacters { get; init; } = 100_000;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

public static class WebAccessPolicyValidator
{
    public static IReadOnlyList<string> Validate(WebAccessPolicy policy)
    {
        var errors = new List<string>();
        if (policy.MaxRedirects is < 0 or > 10)
            errors.Add("网页重定向上限必须在 0 到 10 之间。");
        if (policy.MaxHeaderBytes is < 1024 or > 1024 * 1024)
            errors.Add("网页响应头预算必须在 1 KiB 到 1 MiB 之间。");
        if (policy.MaxCompressedBytes is < 1024 or > 32 * 1024 * 1024)
            errors.Add("网页压缩体预算必须在 1 KiB 到 32 MiB 之间。");
        if (policy.MaxDecompressedBytes < policy.MaxCompressedBytes
            || policy.MaxDecompressedBytes > 64 * 1024 * 1024)
        {
            errors.Add("网页解压体预算不得小于压缩体预算且不得超过 64 MiB。");
        }
        if (policy.MaxExtractedCharacters is < 1000 or > 1_000_000)
            errors.Add("网页正文字符预算必须在 1,000 到 1,000,000 之间。");
        if (policy.Timeout < TimeSpan.FromSeconds(5) || policy.Timeout > TimeSpan.FromMinutes(5))
            errors.Add("网页读取超时必须在 5 秒到 5 分钟之间。");
        var proxyErrors = AiConnectionProfileValidator.ValidateProxyConfiguration(policy.Proxy);
        foreach (var proxyError in proxyErrors)
            errors.Add($"网页代理无效：{proxyError}");
        if (policy.Browser.RenderDelayMilliseconds is < 0 or > 30_000)
            errors.Add("浏览器渲染等待必须在 0 到 30000 毫秒之间。");
        if (policy.Browser.MaxLinks is < 0 or > 1000)
            errors.Add("浏览器返回链接上限必须在 0 到 1000 之间。");
        if (policy.Browser.Mode == BrowserAccessMode.Executable
            && (string.IsNullOrWhiteSpace(policy.Browser.ExecutablePath)
                || policy.Browser.ExecutablePath.Contains('\0')
                || policy.Browser.ExecutablePath.Contains('\r')
                || policy.Browser.ExecutablePath.Contains('\n')))
        {
            errors.Add("指定浏览器模式必须配置有效的可执行文件路径。");
        }
        if (policy.Browser.Mode == BrowserAccessMode.Cdp
            && !IsValidLocalCdpEndpoint(policy.Browser.CdpEndpoint))
        {
            errors.Add("CDP 模式必须配置本机 HTTP(S) 或 WebSocket Endpoint。");
        }
        foreach (var site in policy.InternalSites)
        {
            var pattern = site.HostPattern.Trim();
            if (string.IsNullOrWhiteSpace(pattern)
                || pattern.Contains('/')
                || pattern.Contains(':')
                || pattern.Count(character => character == '*') > (pattern.StartsWith("*.", StringComparison.Ordinal) ? 1 : 0))
            {
                errors.Add($"内部站点 HostPattern 无效：{site.HostPattern}。");
            }
            if (site.AllowedPorts.Any(port => port is < 1 or > 65535))
                errors.Add($"内部站点 {site.HostPattern} 包含无效端口。");
            if (site.AuthenticationHeaderName is not null
                && !site.AuthenticationHeaderName.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
            {
                errors.Add($"内部站点 {site.HostPattern} 的认证 Header 名称无效。");
            }
            if (site.AuthenticationHeaderPrefix?.Contains('\r') == true
                || site.AuthenticationHeaderPrefix?.Contains('\n') == true)
            {
                errors.Add($"内部站点 {site.HostPattern} 的认证 Header 前缀不得包含换行。");
            }
        }
        return errors;
    }

    private static bool IsValidLocalCdpEndpoint(Uri? endpoint)
    {
        if (endpoint is null
            || !endpoint.IsAbsoluteUri
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || endpoint.Scheme is not ("http" or "https" or "ws" or "wss"))
        {
            return false;
        }
        return string.Equals(endpoint.DnsSafeHost, "localhost", StringComparison.OrdinalIgnoreCase)
               || System.Net.IPAddress.TryParse(endpoint.DnsSafeHost, out var address)
               && System.Net.IPAddress.IsLoopback(address);
    }
}

public sealed record WebFetchResult(
    Uri RequestedUrl,
    Uri FinalUrl,
    string? Title,
    string ContentType,
    DateTimeOffset FetchedAtUtc,
    string Content,
    bool IsTruncated,
    bool IsExternalContent = true);

public sealed record BrowserPageReadRequest(
    Uri Url,
    BrowserWaitMode WaitMode = BrowserWaitMode.Load,
    int? MaxCharacters = null);

public sealed record BrowserPageLink(string Text, Uri Url);

public sealed record BrowserPageReadResult(
    Uri RequestedUrl,
    Uri FinalUrl,
    string? Title,
    DateTimeOffset FetchedAtUtc,
    string Content,
    IReadOnlyList<BrowserPageLink> Links,
    bool IsTruncated,
    string Browser);

public interface IBrowserPageReader
{
    ValueTask<BrowserPageReadResult> ReadAsync(
        BrowserPageReadRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class BrowserPageReadException(
    string code,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public enum WebFetchErrorCode
{
    InvalidUrl,
    TargetBlocked,
    DnsFailure,
    RedirectLimit,
    UnsupportedContentType,
    HeadersTooLarge,
    ContentTooLarge,
    AuthenticationUnavailable,
    HttpError,
    Timeout,
    Cancelled,
    Network,
    ParseFailure,
}

public sealed class WebFetchException(
    WebFetchErrorCode code,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public WebFetchErrorCode Code { get; } = code;
}

internal sealed record ValidatedWebTarget(
    Uri Uri,
    IReadOnlyList<System.Net.IPAddress> Addresses,
    InternalWebSitePolicy? InternalSite);
