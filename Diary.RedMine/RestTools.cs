using System.Net;
using RestSharp;
using RestSharp.Serializers.NewtonsoftJson;

namespace Diary.RedMine;

internal static class RestTools
{
    private static RedMinePluginConfig Cfg => RedMineConfigurationStore.Current;

    private static RestClient? _cachedClient;
    private static string _cachedUrl = string.Empty;
    private static bool _cachedUseProxy;
    private static string _cachedProxyServer = string.Empty;

    public static RestClient? BasicClient()
    {
        if (!Cfg.Valid())
        {
            _cachedClient = null;
            return null;
        }

        var url = Cfg.RedMineServerUrl;
        var useProxy = Cfg.EnableProxy;
        var proxyServer = useProxy ? Cfg.ProxyServer : string.Empty;

        if (_cachedClient != null
            && _cachedUrl == url
            && _cachedUseProxy == useProxy
            && _cachedProxyServer == proxyServer)
        {
            return _cachedClient;
        }

        var options = new RestClientOptions(url);
        if (useProxy)
        {
            options.Proxy = new WebProxy(proxyServer);
        }
        _cachedClient = new RestClient(options, configureSerialization: s => s.UseNewtonsoftJson());
        _cachedUrl = url;
        _cachedUseProxy = useProxy;
        _cachedProxyServer = proxyServer;

        return _cachedClient;
    }

    public static RestRequest HttpGet(string query)
    {
        var request = new RestRequest(query);
        request.AddHeader("X-Redmine-API-Key", Cfg.RedMineApiKey);
        return request;
    }

    public static RestRequest HttpPost(string query)
    {
        var request = new RestRequest(query, Method.Post);
        request.AddHeader("X-Redmine-API-Key", Cfg.RedMineApiKey);
        return request;
    }
}
