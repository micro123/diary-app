using Microsoft.Extensions.DependencyInjection;

namespace Diary.ModuleBase;

public interface IAppModule
{
    void ConfigureServices(IServiceCollection services, AppModuleRegistrationContext context);

    ValueTask StartAsync(
        AppModuleRuntimeContext context,
        CancellationToken cancellationToken = default);

    ValueTask StopAsync(CancellationToken cancellationToken = default);
}

public sealed record AppModuleRegistrationContext(
    AppModuleManifest Manifest,
    string ModuleDirectory,
    string ConfigurationDirectory,
    IReadOnlySet<string> HostCapabilities);

public sealed record AppModuleRuntimeContext(
    AppModuleManifest Manifest,
    string ModuleDirectory,
    string ConfigurationDirectory,
    IServiceProvider Services);

public sealed record AppModuleManifest
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public int ApiVersion { get; init; }

    public string EntryAssembly { get; init; } = string.Empty;

    public string EntryType { get; init; } = string.Empty;

    public bool EnabledByDefault { get; init; }

    public string? MinAppVersion { get; init; }

    public string? MaxAppVersion { get; init; }

    public IReadOnlyList<string> RequiredCapabilities { get; init; } = [];
}
