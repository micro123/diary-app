using Diary.Agent.Configuration;
using Diary.Agent.Credentials;
using Diary.Agent.Networking;
using Diary.Agent.Mcp;
using Diary.Agent.Manual;
using Diary.Agent.Protocols;
using Diary.Agent.Runtime;
using Diary.Agent.Tools;
using Diary.Agent.Web;
using Diary.Agent.UI.ViewModels;
using Diary.ModuleBase;
using Diary.ModuleUI;
using Diary.ScriptHost;
using Diary.Survey;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;

namespace Diary.Agent.UI;

public sealed class AiAgentModule : IAppModule
{
    private IServiceProvider? _services;
    private ExtendedSurveyServiceDirectory? _serviceDirectory;
    private EventHandler? _serviceDirectoryChanged;
    private CancellationTokenSource? _discoveryCancellation;

    public void ConfigureServices(IServiceCollection services, AppModuleRegistrationContext context)
    {
        var settingsDirectory = Path.Combine(context.ConfigurationDirectory, "ai-agent");
        services.AddSingleton(new AiConnectionStore(Path.Combine(settingsDirectory, "settings.json")));
        services.AddSingleton<IAiCredentialStore, AiCredentialStore>();
        services.AddSingleton(provider => new AiHttpClientPool(
            provider.GetRequiredService<IAiCredentialStore>(),
            () => provider.GetRequiredService<AiConnectionManager>().Settings.DefaultProxy));
        services.AddSingleton<IAiProtocolAdapter, OpenAiChatCompletionsAdapter>();
        services.AddSingleton<IAiProtocolAdapter, OpenAiResponsesAdapter>();
        services.AddSingleton<IAiProtocolAdapter, AnthropicMessagesAdapter>();
        services.AddSingleton<AiModelClient>();
        services.AddSingleton<IAgentModelGateway>(provider => provider.GetRequiredService<AiModelClient>());
        services.AddSingleton(provider => new AiConnectionProbeService(
            provider.GetRequiredService<IAgentModelGateway>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger<AiConnectionProbeService>()));
        services.AddSingleton<AiConnectionManager>();
        services.AddSingleton<IAgentAuditStore>(new AgentAuditStore(
            Path.Combine(settingsDirectory, "audit.jsonl")));
        services.AddSingleton(new AgentConversationStore(
            Path.Combine(settingsDirectory, "conversations.json")));
        services.AddSingleton<AgentToolExecutor>();
        services.AddSingleton<AgentContextCompactor>();
        services.AddSingleton<AgentConfirmationCoordinator>();
        services.AddSingleton<IAgentConfirmationService>(provider =>
            provider.GetRequiredService<AgentConfirmationCoordinator>());
        services.AddSingleton<WorkItemWriteTool>();
        services.AddSingleton<WorkItemUpdateTool>();
        services.AddSingleton<WorkItemBatchUpdateTool>();
        services.AddSingleton<TemplateWorkItemWriteTool>();
        services.AddSingleton<ClipboardWriteTool>();
        services.AddSingleton<AppNotificationWriteTool>();
        services.AddSingleton<WorkItemDraftTool>();
        services.AddSingleton<ReportDraftTool>();
        services.AddSingleton<ScriptDraftTool>();
        services.AddSingleton<UserManualIndexService>();
        services.AddSingleton<SearchUserManualTool>();
        services.AddSingleton<ReadUserManualSectionTool>();
        services.AddSingleton<McpClientManager>();
        services.AddSingleton(provider =>
            provider.GetRequiredService<AiConnectionManager>().Settings.WebAccess);
        services.AddSingleton<WebTargetValidator>();
        services.AddSingleton<WebHttpClientFactory>();
        services.AddSingleton<WebFetchService>();
        services.AddSingleton<WebFetchTool>();
        services.AddSingleton<SystemBrowserLocator>();
        services.AddSingleton<IBrowserPageReader>(provider => new CdpBrowserPageReader(
            provider.GetRequiredService<WebAccessPolicy>(),
            provider.GetRequiredService<WebTargetValidator>(),
            provider.GetRequiredService<IAiCredentialStore>(),
            provider.GetRequiredService<SystemBrowserLocator>(),
            Path.Combine(settingsDirectory, "browser-profiles")));
        services.AddSingleton<WebRenderPageTool>();
        services.AddSingleton(provider =>
        {
            var registry = new AgentToolRegistry();
            DiaryReadOnlyToolRegistration.RegisterAll(
                registry,
                provider.GetRequiredService<IWorkItemQueryScriptApi>(),
                provider.GetRequiredService<ITemplateScriptApi>(),
                provider.GetRequiredService<ITrackerInstanceScriptApi>(),
                provider.GetRequiredService<IWorkTagScriptApi>(),
                provider.GetRequiredService<ITagExtraFieldScriptApi>(),
                provider.GetRequiredService<ICurrentContextScriptApi>(),
                provider.GetRequiredService<IScriptValidationScriptApi>(),
                provider.GetService<IExportApi>());
            registry.TryRegister(provider.GetRequiredService<WorkItemDraftTool>());
            registry.TryRegister(provider.GetRequiredService<ReportDraftTool>());
            registry.TryRegister(provider.GetRequiredService<ScriptDraftTool>());
            registry.TryRegister(provider.GetRequiredService<SearchUserManualTool>());
            registry.TryRegister(provider.GetRequiredService<ReadUserManualSectionTool>());
            registry.TryRegister(provider.GetRequiredService<WorkItemWriteTool>());
            registry.TryRegister(provider.GetRequiredService<WorkItemUpdateTool>());
            registry.TryRegister(provider.GetRequiredService<WorkItemBatchUpdateTool>());
            registry.TryRegister(provider.GetRequiredService<TemplateWorkItemWriteTool>());
            registry.TryRegister(provider.GetRequiredService<ClipboardWriteTool>());
            registry.TryRegister(provider.GetRequiredService<AppNotificationWriteTool>());
            if (provider.GetService<IAgentReportExportApi>() is { } reportExports)
            {
                registry.TryRegister(new ReportExportTool(
                    reportExports,
                    provider.GetRequiredService<IAgentConfirmationService>()));
            }
            registry.TryRegister(provider.GetRequiredService<WebFetchTool>());
            registry.TryRegister(provider.GetRequiredService<WebRenderPageTool>());
            if (provider.GetService<IWebSearchProvider>() is { } searchProvider)
                registry.TryRegister(new WebSearchTool(searchProvider));
            return registry;
        });
        services.AddSingleton<AgentSessionService>();
        services.AddSingleton<AiAgentPageViewModel>();
        services.AddSingleton<AiAgentSettingsViewModel>();
        services.AddSingleton<INavigationContribution, AiNavigationContribution>();
        services.AddSingleton<ISettingsContribution, AiSettingsContribution>();
    }

    public async ValueTask StartAsync(
        AppModuleRuntimeContext context,
        CancellationToken cancellationToken = default)
    {
        _services = context.Services;
        _serviceDirectory = context.Services.GetService<ExtendedSurveyServiceDirectory>();
        if (_serviceDirectory is null)
            return;

        _discoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _serviceDirectoryChanged = (_, _) => QueueDiscoveredServerRefresh();
        _serviceDirectory.Changed += _serviceDirectoryChanged;
        await RefreshDiscoveredServersAsync(_discoveryCancellation.Token);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        var services = _services;
        _services = null;
        if (services is null)
            return;
        if (_serviceDirectory is not null && _serviceDirectoryChanged is not null)
            _serviceDirectory.Changed -= _serviceDirectoryChanged;
        _serviceDirectoryChanged = null;
        _serviceDirectory = null;
        _discoveryCancellation?.Cancel();
        _discoveryCancellation?.Dispose();
        _discoveryCancellation = null;
        services.GetService<AgentSessionService>()?.Cancel();
        if (services.GetService<AgentConfirmationCoordinator>() is { } confirmations)
            confirmations.RejectCurrent();
        if (services.GetService<McpClientManager>() is { } mcp)
            await mcp.DisposeAsync();
        services.GetService<WebHttpClientFactory>()?.Dispose();
        services.GetService<AiHttpClientPool>()?.Dispose();
    }

    private void QueueDiscoveredServerRefresh()
    {
        var cancellationToken = _discoveryCancellation?.Token ?? new CancellationToken(canceled: true);
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshDiscoveredServersAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                _services?.GetService<ILogger<AiAgentModule>>()?.LogWarning(
                    exception,
                    "刷新自动发现的 MCP 服务失败。");
            }
        }, CancellationToken.None);
    }

    private async ValueTask RefreshDiscoveredServersAsync(CancellationToken cancellationToken)
    {
        var services = _services;
        var directory = _serviceDirectory;
        if (services is null || directory is null)
            return;
        var profiles = directory.Snapshot()
            .Select(CreateDiscoveredProfile)
            .Where(profile => profile is not null)
            .Cast<McpServerProfile>()
            .ToArray();
        await services.GetRequiredService<McpClientManager>().ReplaceDiscoveredServersAsync(
            profiles,
            services.GetRequiredService<AgentToolRegistry>(),
            cancellationToken);
    }

    private static McpServerProfile? CreateDiscoveredProfile(DiscoveredExtendedSurveyMcpService discovered)
    {
        var endpoint = discovered.Service.Endpoints
            .Select(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null)
            .FirstOrDefault(uri => uri is { Scheme: "http" or "https" });
        if (endpoint is null)
            return null;
        var rawId = $"{discovered.InstanceId}:{discovered.Service.ServiceId}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawId))).ToLowerInvariant()[..24];
        var policies = (discovered.Service.Capabilities ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Where(name => !McpToolPolicyGuard.IsProhibitedDestructiveToolName(name))
            .Distinct(StringComparer.Ordinal)
            .Select(name => new McpToolPolicy
            {
                ToolName = name,
                Enabled = true,
                Risk = AgentToolRisk.ReadOnly,
            })
            .ToArray();
        return new McpServerProfile
        {
            Id = $"discovered.{hash}",
            DisplayName = $"{discovered.Hostname} · {discovered.Service.DisplayName}",
            Enabled = true,
            Transport = McpTransportKind.StreamableHttp,
            Endpoint = endpoint,
            Authentication = new AiAuthenticationConfiguration { Kind = AiAuthenticationKind.None },
            Proxy = new AiProxyConfiguration { Mode = AiProxyMode.Direct },
            Timeout = TimeSpan.FromMinutes(2),
            Tools = policies,
        };
    }
}

public sealed class AiNavigationContribution : INavigationContribution
{
    public string Id => "diary.ai-agent.navigation";

    public string Title => "AI 助手";

    public string Icon => "mdi-robot-outline";

    public int Order => 500;

    public Diary.GUIBase.ViewModels.ViewModelBase CreateViewModel(IServiceProvider services) =>
        services.GetRequiredService<AiAgentPageViewModel>();
}

public sealed class AiSettingsContribution : ISettingsContribution
{
    public string Id => "diary.ai-agent.settings";

    public string Title => "AI 助手设置";

    public int Order => 500;

    public Diary.GUIBase.ViewModels.ViewModelBase CreateViewModel(IServiceProvider services) =>
        services.GetRequiredService<AiAgentSettingsViewModel>();
}
