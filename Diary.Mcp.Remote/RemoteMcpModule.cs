using Diary.ModuleBase;
using Diary.ScriptHost;
using Diary.Survey;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Diary.Mcp.Remote;

public sealed class RemoteMcpModule : IAppModule
{
    private static readonly string[] ToolNames =
    [
        "diary_list_tags",
        "diary_list_extra_fields",
        "diary_get_current_context",
        "diary_query_work_items",
        "diary_summarize_work_items",
    ];

    private McpHttpServer? _server;
    private IDisposable? _advertisement;
    private ILogger<RemoteMcpModule>? _logger;

    public void ConfigureServices(IServiceCollection services, AppModuleRegistrationContext context)
    {
    }

    public async ValueTask StartAsync(
        AppModuleRuntimeContext context,
        CancellationToken cancellationToken = default)
    {
        if (_server is not null)
            return;

        _logger = context.Services.GetRequiredService<ILogger<RemoteMcpModule>>();
        var settingsPath = Path.Combine(
            context.ConfigurationDirectory,
            "remote-mcp",
            "settings.json");
        var settings = RemoteMcpModuleSettingsStore.LoadOrCreate(settingsPath);
        var peerPolicy = context.Services.GetRequiredService<SurveyMcpPeerAccessPolicy>();
        var rateLimiter = new PeerRequestRateLimiter(settings.RequestsPerMinute);
        var tools = new RemoteDiaryTools(
            context.Services.GetRequiredService<IWorkItemQueryScriptApi>(),
            context.Services.GetRequiredService<IWorkTagScriptApi>(),
            context.Services.GetRequiredService<ITagExtraFieldScriptApi>(),
            context.Services.GetRequiredService<ICurrentContextScriptApi>());
        var server = new McpHttpServer(peerPolicy, rateLimiter, tools, _logger);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            server.Start();
            var port = server.Port;
            var startedAt = DateTimeOffset.UtcNow;
            _advertisement = context.Services.GetRequiredService<ExtendedSurveyServiceRegistry>().Register(() =>
            {
                var endpoints = peerPolicy.GetAdvertisableAddresses()
                    .Select(address => $"http://{address}:{port}/mcp")
                    .ToArray();
                return new ExtendedSurveyMcpService
                {
                    ServiceId = "diary.readonly",
                    DisplayName = "DiaryApp 只读数据",
                    Transport = ExtendedSurveyProtocol.StreamableHttpTransport,
                    Endpoints = endpoints,
                    Capabilities = ToolNames,
                    StartedAt = startedAt,
                    ExpiresInSeconds = 90,
                };
            });
            _server = server;
            _logger.LogInformation(
                "远程 MCP 服务已启动。Port={Port}, RequestsPerMinute={RequestsPerMinute}",
                port,
                settings.RequestsPerMinute);
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        _advertisement?.Dispose();
        _advertisement = null;
        var server = _server;
        _server = null;
        if (server is null)
            return;

        await server.DisposeAsync();
        _logger?.LogInformation("远程 MCP 服务已停止。");
    }
}
