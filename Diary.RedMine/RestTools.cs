using System.Net;
using Diary.Core.Data.AppConfig;
using RestSharp;
using RestSharp.Serializers.NewtonsoftJson;

namespace Diary.RedMine;

internal static class RestTools
{
    private static RedMineConfig Cfg => AllConfig.Instance.RedMineSettings;
    
    public static RestClient? BasicClient()
    {
        if (!Cfg.Valid())
            return null;

        var options = new RestClientOptions(Cfg.RedMineServerUrl);
        if (Cfg.EnableProxy)
        {
            options.Proxy = new WebProxy(Cfg.ProxyServer);
        }
        return new RestClient(options, configureSerialization: s => s.UseNewtonsoftJson());
    }

    public static RestRequest HttpGet(string query)
    {
        var request = new RestRequest(query);
        request.AddHeader("X-Redmine-API-Key", Cfg.RedMineApiKey);
        return request;
    }
}