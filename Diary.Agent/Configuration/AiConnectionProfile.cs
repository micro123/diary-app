using System.Text.RegularExpressions;

namespace Diary.Agent.Configuration;

public enum AiProtocol
{
    OpenAiChatCompletions,
    OpenAiResponses,
    AnthropicMessages,
}

public enum AiAuthenticationKind
{
    None,
    Bearer,
    Header,
}

public enum AiProxyMode
{
    Inherit,
    System,
    Direct,
    Custom,
}

public sealed record AiAuthenticationConfiguration
{
    public AiAuthenticationKind Kind { get; init; } = AiAuthenticationKind.Bearer;

    public string CredentialReference { get; init; } = string.Empty;

    public string HeaderName { get; init; } = "Authorization";

    public string HeaderPrefix { get; init; } = "Bearer ";
}

public sealed record AiProxyConfiguration
{
    public AiProxyMode Mode { get; init; } = AiProxyMode.System;

    public Uri? Address { get; init; }

    public IReadOnlyList<string> BypassList { get; init; } = [];

    public string UsernameCredentialReference { get; init; } = string.Empty;

    public string PasswordCredentialReference { get; init; } = string.Empty;
}

public sealed record AiCompatibilityOptions
{
    public bool SendToolChoice { get; init; } = true;

    public bool SendStreamOptions { get; init; } = true;

    public string AnthropicVersion { get; init; } = "2023-06-01";
}

public sealed record AiRequestHeader
{
    public required string Name { get; init; }

    public string? Value { get; init; }

    public string? CredentialReference { get; init; }

    public string Prefix { get; init; } = string.Empty;
}

public sealed record AiConnectionProfile
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required AiProtocol Protocol { get; init; }

    public required Uri BaseUri { get; init; }

    public string? RequestPathOverride { get; init; }

    public required string Model { get; init; }

    public AiAuthenticationConfiguration Authentication { get; init; } = new();

    public AiProxyConfiguration Proxy { get; init; } = new();

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public AiCompatibilityOptions Compatibility { get; init; } = new();

    public IReadOnlyList<AiRequestHeader> Headers { get; init; } = [];
}

public static partial class AiConnectionProfileValidator
{
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host",
        "Content-Length",
        "Connection",
        "Transfer-Encoding",
        "Upgrade",
        "Proxy-Connection",
    };

    public static IReadOnlyList<string> Validate(AiConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var errors = new List<string>();
        if (!IdRegex().IsMatch(profile.Id))
            errors.Add("连接 ID 只能包含小写 ASCII 字母、数字、点、下划线和短横线。");
        if (string.IsNullOrWhiteSpace(profile.DisplayName) || profile.DisplayName.Length > 100)
            errors.Add("显示名称不能为空且不能超过 100 个字符。");
        if (string.IsNullOrWhiteSpace(profile.Model) || profile.Model.Length > 200)
            errors.Add("模型名不能为空且不能超过 200 个字符。");
        ValidateBaseUri(profile.BaseUri, errors);
        ValidateRequestPath(profile.RequestPathOverride, errors);
        if (profile.ConnectTimeout < TimeSpan.FromSeconds(1)
            || profile.ConnectTimeout > TimeSpan.FromMinutes(2))
        {
            errors.Add("连接超时必须在 1 秒到 2 分钟之间。");
        }
        if (profile.RequestTimeout < TimeSpan.FromSeconds(5)
            || profile.RequestTimeout > TimeSpan.FromMinutes(10))
        {
            errors.Add("请求超时必须在 5 秒到 10 分钟之间。");
        }
        ValidateAuthentication(profile.Authentication, errors);
        ValidateProxy(profile.Proxy, errors);
        ValidateHeaders(profile, errors);
        return errors;
    }

    public static IReadOnlyList<string> ValidateProxyConfiguration(AiProxyConfiguration proxy)
    {
        var errors = new List<string>();
        ValidateProxy(proxy, errors);
        return errors;
    }

    public static Uri ResolveRequestUri(AiConnectionProfile profile, string defaultPath)
    {
        var errors = Validate(profile);
        if (errors.Count > 0)
            throw new ArgumentException(string.Join(" ", errors), nameof(profile));
        var path = profile.RequestPathOverride ?? defaultPath;
        var normalizedBase = profile.BaseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? profile.BaseUri
            : new Uri(profile.BaseUri.AbsoluteUri + '/', UriKind.Absolute);
        var resolved = new Uri(normalizedBase, path.TrimStart('/'));
        if (!string.Equals(resolved.Host, profile.BaseUri.Host, StringComparison.OrdinalIgnoreCase)
            || resolved.Port != profile.BaseUri.Port
            || resolved.Scheme != profile.BaseUri.Scheme)
        {
            throw new ArgumentException("请求路径不得改变连接主机。", nameof(profile));
        }
        return resolved;
    }

    private static void ValidateBaseUri(Uri baseUri, List<string> errors)
    {
        if (!baseUri.IsAbsoluteUri || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            errors.Add("BaseUri 必须是绝对 HTTP/HTTPS 地址。");
        if (!string.IsNullOrEmpty(baseUri.UserInfo))
            errors.Add("BaseUri 不得包含 userinfo。");
        if (!string.IsNullOrEmpty(baseUri.Fragment))
            errors.Add("BaseUri 不得包含 fragment。");
    }

    private static void ValidateRequestPath(string? path, List<string> errors)
    {
        if (path is null)
            return;
        if (string.IsNullOrWhiteSpace(path)
            || Uri.TryCreate(path, UriKind.Absolute, out _)
            || path.Contains('\r', StringComparison.Ordinal)
            || path.Contains('\n', StringComparison.Ordinal))
        {
            errors.Add("请求路径覆盖必须是无换行的相对路径。");
        }
    }

    private static void ValidateAuthentication(AiAuthenticationConfiguration authentication, List<string> errors)
    {
        if (authentication.Kind == AiAuthenticationKind.None)
            return;
        if (string.IsNullOrWhiteSpace(authentication.CredentialReference))
            errors.Add("认证凭据引用不能为空。");
        if (!IsValidHeaderName(authentication.HeaderName) || ForbiddenHeaders.Contains(authentication.HeaderName))
            errors.Add("认证 Header 名称无效或属于禁止覆盖的连接管理 Header。");
        if (HasNewLine(authentication.HeaderPrefix))
            errors.Add("认证 Header 前缀不得包含换行。");
    }

    private static void ValidateProxy(AiProxyConfiguration proxy, List<string> errors)
    {
        if (proxy.Mode != AiProxyMode.Custom)
            return;
        if (proxy.Address is null
            || !proxy.Address.IsAbsoluteUri
            || (proxy.Address.Scheme != Uri.UriSchemeHttp && proxy.Address.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add("自定义代理必须是绝对 HTTP/HTTPS 地址。");
        }
        else if (!string.IsNullOrEmpty(proxy.Address.UserInfo))
        {
            errors.Add("代理地址不得包含 userinfo。");
        }
        if (proxy.BypassList.Any(HasNewLine))
            errors.Add("代理绕过规则不得包含换行。");
    }

    private static void ValidateHeaders(AiConnectionProfile profile, List<string> errors)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in profile.Headers)
        {
            if (!IsValidHeaderName(header.Name)
                || ForbiddenHeaders.Contains(header.Name)
                || !names.Add(header.Name))
            {
                errors.Add($"额外 Header 名称无效、重复或属于禁止覆盖项：{header.Name}。");
            }
            if (string.Equals(header.Name, profile.Authentication.HeaderName, StringComparison.OrdinalIgnoreCase)
                && profile.Authentication.Kind != AiAuthenticationKind.None)
            {
                errors.Add($"额外 Header 不得覆盖连接认证 Header：{header.Name}。");
            }
            var hasValue = header.Value is not null;
            var hasCredential = !string.IsNullOrWhiteSpace(header.CredentialReference);
            if (hasValue == hasCredential)
                errors.Add($"额外 Header {header.Name} 必须且只能配置普通值或凭据引用。");
            if (HasNewLine(header.Value ?? string.Empty) || HasNewLine(header.Prefix))
                errors.Add($"额外 Header {header.Name} 的值或前缀不得包含换行。");
        }
    }

    private static bool IsValidHeaderName(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static bool HasNewLine(string value) => value.Contains('\r') || value.Contains('\n');

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdRegex();
}
