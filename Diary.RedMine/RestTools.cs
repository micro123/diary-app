using System.Net;
using RestSharp;
using RestSharp.Serializers.NewtonsoftJson;

namespace Diary.RedMine;

internal static class RestTools
{
    public static RestClient? BasicClient(RedMineConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (!cfg.Valid())
            return null;

        var options = new RestClientOptions(cfg.RedMineServerUrl);
        if (cfg.EnableProxy)
            options.Proxy = new WebProxy(cfg.ProxyServer);
        return new RestClient(options, configureSerialization: s => s.UseNewtonsoftJson());
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

    public static RestRequest HttpPut(RedMineConfig cfg, string query)
    {
        var request = new RestRequest(query, Method.Put);
        request.AddHeader("X-Redmine-API-Key", cfg.RedMineApiKey);
        return request;
    }
}
