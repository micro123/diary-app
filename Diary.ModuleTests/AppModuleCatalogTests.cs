using System.Text.Json;
using Diary.ModuleBase;
using Diary.ModuleTestFixture;
using Diary.ModuleUI;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.ModuleTests;

[TestClass]
public sealed class AppModuleCatalogTests
{
    private readonly List<string> _temporaryRoots = [];

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var root in _temporaryRoots.Where(Directory.Exists))
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (UnauthorizedAccessException)
            {
                // 不可回收 ALC 在 Windows 上会保持入口 DLL 打开到测试进程退出。
            }
        }
    }

    [TestMethod]
    public void MissingModuleDirectoryProducesNoDiagnostics()
    {
        var root = CreateTemporaryRoot();
        var catalog = CreateCatalog(Path.Combine(root, "missing"), Path.Combine(root, "state.json"));

        catalog.DiscoverAndConfigure(new ServiceCollection());

        Assert.AreEqual(0, catalog.Diagnostics.Count);
        Assert.AreEqual(0, catalog.LoadedManifests.Count);
    }

    [TestMethod]
    public void DisabledModuleDoesNotLoadInvalidEntryAssembly()
    {
        var root = CreateTemporaryRoot();
        var modules = Directory.CreateDirectory(Path.Combine(root, "Modules")).FullName;
        CreateModule(modules, "sample.disabled", enabledByDefault: false, entryBytes: [1, 2, 3, 4]);

        var catalog = CreateCatalog(modules, Path.Combine(root, "state.json"));
        catalog.DiscoverAndConfigure(new ServiceCollection());

        Assert.IsTrue(catalog.Diagnostics.Any(item => item.Code == "module.disabled"));
        Assert.IsFalse(catalog.Diagnostics.Any(item => item.Code == "module.load-failed"));
        Assert.AreEqual(0, catalog.LoadedManifests.Count);
    }

    [TestMethod]
    public void ExplicitDisabledStateOverridesEnabledByDefault()
    {
        var root = CreateTemporaryRoot();
        var modules = Directory.CreateDirectory(Path.Combine(root, "Modules")).FullName;
        CreateModule(modules, "sample.explicit-disabled", enabledByDefault: true, entryBytes: [1, 2, 3, 4]);
        var statePath = Path.Combine(root, "state.json");
        File.WriteAllText(statePath, """
            {"schemaVersion":1,"modules":{"sample.explicit-disabled":{"enabled":false}}}
            """);

        var catalog = CreateCatalog(modules, statePath);
        catalog.DiscoverAndConfigure(new ServiceCollection());

        Assert.IsTrue(catalog.Diagnostics.Any(item => item.Code == "module.disabled"));
        Assert.IsFalse(catalog.Diagnostics.Any(item => item.Code == "module.load-failed"));
    }

    [TestMethod]
    public void CorruptStateDisablesAllModulesWithoutOverwritingFile()
    {
        var root = CreateTemporaryRoot();
        var modules = Directory.CreateDirectory(Path.Combine(root, "Modules")).FullName;
        CreateModule(modules, "sample.corrupt-state", enabledByDefault: true, entryBytes: [1, 2, 3, 4]);
        var statePath = Path.Combine(root, "state.json");
        const string corruptContent = "{not-json";
        File.WriteAllText(statePath, corruptContent);

        var catalog = CreateCatalog(modules, statePath);
        catalog.DiscoverAndConfigure(new ServiceCollection());

        Assert.IsTrue(catalog.Diagnostics.Any(item => item.Code == "state.corrupt"));
        Assert.IsTrue(catalog.Diagnostics.Any(item => item.Code == "module.disabled"));
        Assert.AreEqual(corruptContent, File.ReadAllText(statePath));
    }

    [TestMethod]
    public void InvalidManifestAndPathTraversalAreReportedWithoutThrowing()
    {
        var root = CreateTemporaryRoot();
        var modules = Directory.CreateDirectory(Path.Combine(root, "Modules")).FullName;
        var moduleDirectory = Directory.CreateDirectory(Path.Combine(modules, "sample.invalid")).FullName;
        File.WriteAllText(Path.Combine(root, "outside.dll"), "outside");
        WriteManifest(moduleDirectory, "sample.invalid", "../outside.dll", enabledByDefault: true);

        var catalog = CreateCatalog(modules, Path.Combine(root, "state.json"));
        catalog.DiscoverAndConfigure(new ServiceCollection());

        Assert.IsTrue(catalog.Diagnostics.Any(item => item.Code == "manifest.invalid"));
        Assert.AreEqual(0, catalog.LoadedManifests.Count);
    }

    [TestMethod]
    public void IncompatibleModuleDoesNotLoadEntryAssembly()
    {
        var root = CreateTemporaryRoot();
        var modules = Directory.CreateDirectory(Path.Combine(root, "Modules")).FullName;
        CreateModule(
            modules,
            "sample.incompatible",
            enabledByDefault: true,
            entryBytes: [1, 2, 3, 4],
            apiVersion: 2);

        var catalog = CreateCatalog(modules, Path.Combine(root, "state.json"));
        catalog.DiscoverAndConfigure(new ServiceCollection());

        Assert.IsTrue(catalog.Diagnostics.Any(item => item.Code == "module.incompatible"));
        Assert.IsFalse(catalog.Diagnostics.Any(item => item.Code == "module.load-failed"));
    }

    [TestMethod]
    public void LoadFailureDoesNotBlockOtherModules()
    {
        var root = CreateTemporaryRoot();
        var modules = Directory.CreateDirectory(Path.Combine(root, "Modules")).FullName;
        CreateModule(modules, "sample.broken", enabledByDefault: true, entryBytes: [1, 2, 3, 4]);
        CreateFixtureModule(modules, "sample.working");
        var services = new ServiceCollection();

        var catalog = CreateCatalog(modules, Path.Combine(root, "state.json"));
        catalog.DiscoverAndConfigure(services);

        Assert.IsTrue(catalog.Diagnostics.Any(item =>
            item.ModuleId == "sample.broken" && item.Code == "module.load-failed"));
        CollectionAssert.Contains(catalog.LoadedManifests.Select(item => item.Id).ToList(), "sample.working");
    }

    [TestMethod]
    public async Task LoadedModuleContributesStableUiAndRunsLifecycle()
    {
        var root = CreateTemporaryRoot();
        var modules = Directory.CreateDirectory(Path.Combine(root, "Modules")).FullName;
        CreateFixtureModule(modules, "sample.fixture");
        var services = new ServiceCollection();
        services.AddSingleton<NavigationContributionRegistry>();
        services.AddSingleton<SettingsContributionRegistry>();

        var catalog = CreateCatalog(modules, Path.Combine(root, "state.json"));
        catalog.DiscoverAndConfigure(services);
        await using var provider = services.BuildServiceProvider();
        await catalog.StartAsync(provider);

        var events = provider.GetRequiredService<IList<string>>();
        CollectionAssert.AreEqual(
            new[] { "configured:sample.fixture", "started:sample.fixture" },
            events.ToArray());
        var navigation = provider.GetRequiredService<NavigationContributionRegistry>();
        var settings = provider.GetRequiredService<SettingsContributionRegistry>();
        var navigationPage = navigation.GetPages().Single();
        var settingsPage = settings.GetPages().Single();
        Assert.AreSame(navigationPage.ViewModel, navigation.GetPages().Single().ViewModel);
        Assert.AreSame(settingsPage.ViewModel, settings.GetPages().Single().ViewModel);

        await catalog.StopAsync();
        Assert.AreEqual("stopped", events[^1]);
    }

    [TestMethod]
    public void DuplicateIdsIgnoringCaseAreRejected()
    {
        var root = CreateTemporaryRoot();
        var modules = Directory.CreateDirectory(Path.Combine(root, "Modules")).FullName;
        CreateFixtureModule(modules, "sample.case", "module-a");
        CreateFixtureModule(modules, "sample.case", "module-b");

        var catalog = CreateCatalog(modules, Path.Combine(root, "state.json"));
        catalog.DiscoverAndConfigure(new ServiceCollection());

        Assert.AreEqual(2, catalog.Diagnostics.Count(item => item.Code == "manifest.id-conflict"));
        Assert.AreEqual(0, catalog.LoadedManifests.Count);
    }

    private string CreateTemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "Diary.ModuleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _temporaryRoots.Add(root);
        return root;
    }

    private static AppModuleCatalog CreateCatalog(string moduleRoot, string statePath)
    {
        var shared = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            typeof(IAppModule).Assembly.GetName().Name!,
            typeof(INavigationContribution).Assembly.GetName().Name!,
            typeof(Diary.GUIBase.ViewModels.ViewModelBase).Assembly.GetName().Name!,
            typeof(IServiceCollection).Assembly.GetName().Name!,
            typeof(ServiceCollection).Assembly.GetName().Name!,
        };
        return new AppModuleCatalog(new AppModuleCatalogOptions(
            moduleRoot,
            statePath,
            Path.GetDirectoryName(statePath)!,
            new Version(1, 0, 0),
            1,
            new HashSet<string>(StringComparer.Ordinal)
            {
                "module.navigation",
                "module.settings",
                "script.work_items.query",
            },
            shared));
    }

    private static void CreateModule(
        string moduleRoot,
        string moduleId,
        bool enabledByDefault,
        byte[] entryBytes,
        int apiVersion = 1)
    {
        var moduleDirectory = Directory.CreateDirectory(Path.Combine(moduleRoot, moduleId)).FullName;
        File.WriteAllBytes(Path.Combine(moduleDirectory, "Entry.dll"), entryBytes);
        WriteManifest(moduleDirectory, moduleId, "Entry.dll", enabledByDefault, apiVersion);
    }

    private static void CreateFixtureModule(
        string moduleRoot,
        string moduleId,
        string? directoryName = null)
    {
        var moduleDirectory = Directory.CreateDirectory(Path.Combine(moduleRoot, directoryName ?? moduleId)).FullName;
        var sourceAssembly = typeof(FixtureModule).Assembly.Location;
        File.Copy(sourceAssembly, Path.Combine(moduleDirectory, "Diary.ModuleTestFixture.dll"));
        WriteManifest(
            moduleDirectory,
            moduleId,
            "Diary.ModuleTestFixture.dll",
            enabledByDefault: true,
            entryType: typeof(FixtureModule).FullName!);
    }

    private static void WriteManifest(
        string moduleDirectory,
        string moduleId,
        string entryAssembly,
        bool enabledByDefault,
        int apiVersion = 1,
        string entryType = "Missing.Entry")
    {
        var manifest = new
        {
            id = moduleId,
            displayName = moduleId,
            version = "1.0.0",
            apiVersion,
            entryAssembly,
            entryType,
            enabledByDefault,
            minAppVersion = "1.0.0",
            maxAppVersion = (string?)null,
            requiredCapabilities = new[] { "module.navigation", "module.settings" },
        };
        File.WriteAllText(
            Path.Combine(moduleDirectory, "module.json"),
            JsonSerializer.Serialize(manifest));
    }
}
