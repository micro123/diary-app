using Diary.Agent.Configuration;

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

public sealed record WebAccessPolicy
{
    public AiProxyConfiguration Proxy { get; init; } = new() { Mode = AiProxyMode.System };

    public IReadOnlyList<InternalWebSitePolicy> InternalSites { get; init; } = [];

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
