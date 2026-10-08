using Diary.Agent.Configuration;
using Diary.Agent.Credentials;
using Diary.Agent.Networking;
using Diary.Agent.Mcp;
using Diary.Agent.Protocols;
using Diary.Agent.Runtime;
using Diary.Agent.Tools;
using Diary.Agent.Web;
using Diary.Agent.UI.ViewModels;
using Diary.ModuleBase;
using Diary.ModuleUI;
using Diary.ScriptHost;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.Agent.UI;

public sealed class AiAgentModule : IAppModule
{
    private IServiceProvider? _services;

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
        services.AddSingleton<AiConnectionProbeService>();
        services.AddSingleton<AiConnectionManager>();
        services.AddSingleton<IAgentAuditStore>(new AgentAuditStore(
            Path.Combine(settingsDirectory, "audit.jsonl")));
        services.AddSingleton(new AgentConversationStore(
            Path.Combine(settingsDirectory, "conversations.json")));
        services.AddSingleton<AgentToolExecutor>();
        services.AddSingleton<AgentConfirmationCoordinator>();
        services.AddSingleton<IAgentConfirmationService>(provider =>
            provider.GetRequiredService<AgentConfirmationCoordinator>());
        services.AddSingleton<WorkItemWriteTool>();
        services.AddSingleton<WorkItemDraftTool>();
        services.AddSingleton<ReportDraftTool>();
        services.AddSingleton<ScriptDraftTool>();
        services.AddSingleton<McpClientManager>();
        services.AddSingleton(provider =>
            provider.GetRequiredService<AiConnectionManager>().Settings.WebAccess);
        services.AddSingleton<WebTargetValidator>();
        services.AddSingleton<WebHttpClientFactory>();
        services.AddSingleton<WebFetchService>();
        services.AddSingleton<WebFetchTool>();
        services.AddSingleton(provider =>
        {
            var registry = new AgentToolRegistry();
            DiaryReadOnlyToolRegistration.RegisterAll(
                registry,
                provider.GetRequiredService<IWorkItemQueryScriptApi>(),
                provider.GetRequiredService<ITemplateScriptApi>(),
                provider.GetRequiredService<ITrackerInstanceScriptApi>(),
                provider.GetRequiredService<IWorkTagScriptApi>(),
                provider.GetRequiredService<ICurrentContextScriptApi>(),
                provider.GetRequiredService<IScriptValidationScriptApi>());
            registry.TryRegister(provider.GetRequiredService<WorkItemDraftTool>());
            registry.TryRegister(provider.GetRequiredService<ReportDraftTool>());
            registry.TryRegister(provider.GetRequiredService<ScriptDraftTool>());
            registry.TryRegister(provider.GetRequiredService<WorkItemWriteTool>());
            registry.TryRegister(provider.GetRequiredService<WebFetchTool>());
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

    public ValueTask StartAsync(
        AppModuleRuntimeContext context,
        CancellationToken cancellationToken = default)
    {
        _services = context.Services;
        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        var services = _services;
        _services = null;
        if (services is null)
            return;
        services.GetService<AgentSessionService>()?.Cancel();
        if (services.GetService<AgentConfirmationCoordinator>() is { } confirmations)
            confirmations.RejectCurrent();
        if (services.GetService<McpClientManager>() is { } mcp)
            await mcp.DisposeAsync();
        services.GetService<WebHttpClientFactory>()?.Dispose();
        services.GetService<AiHttpClientPool>()?.Dispose();
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
