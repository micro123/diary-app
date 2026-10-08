using System.Net;
using System.Net.Sockets;

namespace Diary.Agent.Web;

public sealed class WebTargetValidator(WebAccessPolicy policy)
{
    private static readonly HashSet<string> BlockedHostNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "metadata.google.internal",
        "metadata.azure.internal",
        "instance-data.ec2.internal",
    };

    internal async ValueTask<ValidatedWebTarget> ValidateAsync(
        Uri uri,
        CancellationToken cancellationToken = default)
    {
        var internalSite = FindInternalSite(uri);
        ValidateUriShape(uri, internalSite is not null);
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(uri.DnsSafeHost, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken);
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            throw new WebFetchException(WebFetchErrorCode.DnsFailure, "网页目标域名解析失败。", exception);
        }
        if (addresses.Length == 0)
            throw new WebFetchException(WebFetchErrorCode.DnsFailure, "网页目标没有可用地址。");
        if (internalSite is null && addresses.Any(IsBlockedPublicAddress))
            throw new WebFetchException(WebFetchErrorCode.TargetBlocked, "网页目标解析到本机、私网、保留或元数据地址。");
        return new ValidatedWebTarget(uri, addresses, internalSite);
    }

    public static bool IsSameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.DnsSafeHost, right.DnsSafeHost, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    private static void ValidateUriShape(Uri uri, bool explicitlyAllowedInternalSite)
    {
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new WebFetchException(WebFetchErrorCode.InvalidUrl, "网页地址必须是绝对 HTTP/HTTPS URI。");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new WebFetchException(WebFetchErrorCode.InvalidUrl, "网页地址不得包含 userinfo。");
        if (!explicitlyAllowedInternalSite
            && (BlockedHostNames.Contains(uri.DnsSafeHost)
            || uri.DnsSafeHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
           )
        {
            throw new WebFetchException(WebFetchErrorCode.TargetBlocked, "网页目标属于本机或元数据主机。");
        }
    }

    private InternalWebSitePolicy? FindInternalSite(Uri uri)
    {
        foreach (var site in policy.InternalSites)
        {
            var pattern = site.HostPattern.Trim();
            var hostMatches = pattern.StartsWith("*.", StringComparison.Ordinal)
                ? uri.DnsSafeHost.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
                    && uri.DnsSafeHost.Length > pattern.Length - 1
                : string.Equals(uri.DnsSafeHost, pattern, StringComparison.OrdinalIgnoreCase);
            if (!hostMatches)
                continue;
            if (site.AllowedPorts.Count == 0 || site.AllowedPorts.Contains(uri.Port))
                return site;
            throw new WebFetchException(WebFetchErrorCode.TargetBlocked, "内部站点端口不在显式白名单中。");
        }
        return null;
    }

    private static bool IsBlockedPublicAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.None)
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.IPv6None)
            || address.Equals(IPAddress.IPv6Loopback)
            || address.IsIPv6LinkLocal
            || address.IsIPv6Multicast
            || address.IsIPv6SiteLocal)
        {
            return true;
        }
        if (address.IsIPv4MappedToIPv6)
            return IsBlockedPublicAddress(address.MapToIPv4());
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return (bytes[0] & 0xFE) == 0xFC;
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return true;
        return bytes[0] switch
        {
            0 => true,
            10 => true,
            100 when bytes[1] is >= 64 and <= 127 => true,
            127 => true,
            169 when bytes[1] == 254 => true,
            172 when bytes[1] is >= 16 and <= 31 => true,
            192 when bytes[1] == 0 => true,
            192 when bytes[1] == 168 => true,
            198 when bytes[1] is 18 or 19 => true,
            198 when bytes[1] == 51 && bytes[2] == 100 => true,
            203 when bytes[1] == 0 && bytes[2] == 113 => true,
            >= 224 => true,
            _ => address.Equals(IPAddress.Parse("100.100.100.200")),
        };
    }
}
