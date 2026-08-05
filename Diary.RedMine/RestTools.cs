using System.Net;
using RestSharp;
using RestSharp.Serializers.NewtonsoftJson;

namespace Diary.RedMine;

internal static class RestTools
{
    private sealed record CachedClient(
        RestClient Client,
        string Url,
        bool UseProxy,
        string ProxyServer);

    private static readonly object CacheLock = new();
    private static readonly Dictionary<RedMineConfig, CachedClient> CachedClients = new();

    public static RestClient? BasicClient(RedMineConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (!cfg.Valid())
            return null;

        var url = cfg.RedMineServerUrl;
        var useProxy = cfg.EnableProxy;
        var proxyServer = useProxy ? cfg.ProxyServer : string.Empty;

        lock (CacheLock)
        {
            if (CachedClients.TryGetValue(cfg, out var cached)
                && cached.Url == url
                && cached.UseProxy == useProxy
                && cached.ProxyServer == proxyServer)
                return cached.Client;

            var options = new RestClientOptions(url);
            if (useProxy)
                options.Proxy = new WebProxy(proxyServer);
            var client = new RestClient(options, configureSerialization: s => s.UseNewtonsoftJson());
            CachedClients[cfg] = new CachedClient(client, url, useProxy, proxyServer);

            return client;
        }
    }

    public static RestRequest HttpGet(RedMineConfig cfg, string query)
    {
        var request = new RestRequest(query);
        request.AddHeader("X-Redmine-API-Key", cfg.RedMineApiKey);
        return request;
    }

    public static RestRequest HttpPost(RedMineConfig cfg, string query)
    {
        var request = new RestRequest(query, Method.Post);
        request.AddHeader("X-Redmine-API-Key", cfg.RedMineApiKey);
        return request;
    }
}
