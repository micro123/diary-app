using Diary.GUIBase.ViewModels;
using Diary.ModuleBase;
using Diary.ModuleUI;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.ModuleTestFixture;

public sealed class FixtureModule : IAppModule
{
    private IList<string>? _events;

    public void ConfigureServices(IServiceCollection services, AppModuleRegistrationContext context)
    {
        _events = new List<string> { $"configured:{context.Manifest.Id}" };
        services.AddSingleton(_events);
        services.AddSingleton<INavigationContribution, FixtureNavigationContribution>();
        services.AddSingleton<ISettingsContribution, FixtureSettingsContribution>();
    }

    public ValueTask StartAsync(
        AppModuleRuntimeContext context,
        CancellationToken cancellationToken = default)
    {
        _events!.Add($"started:{context.Manifest.Id}");
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        _events!.Add("stopped");
        return ValueTask.CompletedTask;
    }
}

public sealed class FixtureNavigationContribution : INavigationContribution
{
    public string Id => "fixture.navigation";

    public string Title => "Fixture Navigation";

    public string Icon => "mdi-test-tube";

    public int Order => 20;

    public ViewModelBase CreateViewModel(IServiceProvider services) => new FixtureViewModel();
}

public sealed class FixtureSettingsContribution : ISettingsContribution
{
    public string Id => "fixture.settings";

    public string Title => "Fixture Settings";

    public int Order => 20;

    public ViewModelBase CreateViewModel(IServiceProvider services) => new FixtureViewModel();
}

public sealed class FixtureViewModel : ViewModelBase;
