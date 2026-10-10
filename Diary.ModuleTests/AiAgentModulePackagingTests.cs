using System.Runtime.Loader;
using Diary.ModuleBase;
using Diary.ModuleUI;
using Diary.ScriptHost;
using Diary.Survey;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.ModuleTests;

[TestClass]
public sealed class AiAgentModulePackagingTests
{
    [TestMethod]
    public void AppOutputContainsIsolatedDefaultDisabledAiModule()
    {
        var appOutput = GetAppOutputDirectory();
        var moduleDirectory = Path.Combine(appOutput, "Modules", "diary.ai-agent");

        Assert.IsTrue(File.Exists(Path.Combine(moduleDirectory, "module.json")));
        Assert.IsTrue(File.Exists(Path.Combine(moduleDirectory, "Diary.Agent.UI.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(moduleDirectory, "Diary.Agent.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(moduleDirectory, "Diary.Agent.UI.deps.json")));
        Assert.IsFalse(File.Exists(Path.Combine(appOutput, "Diary.Agent.UI.dll")));
        Assert.IsFalse(File.Exists(Path.Combine(appOutput, "Diary.Agent.dll")));
        StringAssert.Contains(File.ReadAllText(Path.Combine(moduleDirectory, "module.json")), "\"enabledByDefault\": false");
    }

    [TestMethod]
    public void AppOutputContainsIsolatedDefaultDisabledRemoteMcpModule()
    {
        var appOutput = GetAppOutputDirectory();
        var moduleDirectory = Path.Combine(appOutput, "Modules", "diary.mcp.remote");

        Assert.IsTrue(File.Exists(Path.Combine(moduleDirectory, "module.json")));
        Assert.IsTrue(File.Exists(Path.Combine(moduleDirectory, "Diary.Mcp.Remote.dll")));
        Assert.IsTrue(File.Exists(Path.Combine(moduleDirectory, "Diary.Mcp.Remote.deps.json")));
        Assert.IsFalse(File.Exists(Path.Combine(appOutput, "Diary.Mcp.Remote.dll")));
        StringAssert.Contains(
            File.ReadAllText(Path.Combine(moduleDirectory, "module.json")),
            "\"enabledByDefault\": false");
    }

    [TestMethod]
    public async Task EnabledPackagedRemoteMcpModuleStartsAndAdvertisesReadOnlyService()
    {
        var root = Path.Combine(Path.GetTempPath(), "Diary.RemoteMcpModuleLoad", Guid.NewGuid().ToString("N"));
        var moduleRoot = Path.Combine(root, "Modules");
        var copiedModule = Path.Combine(moduleRoot, "diary.mcp.remote");
        Directory.CreateDirectory(copiedModule);
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(GetAppOutputDirectory(), "Modules", "diary.mcp.remote")))
        {
            File.Copy(file, Path.Combine(copiedModule, Path.GetFileName(file)));
        }
        var statePath = Path.Combine(root, "module-states.json");
        File.WriteAllText(statePath, """
            {"schemaVersion":1,"modules":{"diary.mcp.remote":{"enabled":true}}}
            """);
        var services = new ServiceCollection();
        services.AddLogging();
        RegisterHostStubs(services);
        services.AddSingleton<ExtendedSurveyServiceRegistry>();
        services.AddSingleton(new SurveyMcpPeerAccessPolicy(() => "127.0.0.1"));
        var sharedNames = AssemblyLoadContext.Default.Assemblies
            .Select(assembly => assembly.GetName().Name)
            .Where(name => name is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var catalog = new AppModuleCatalog(new AppModuleCatalogOptions(
            moduleRoot,
            statePath,
            Path.Combine(root, "config"),
            new Version(1, 0, 1),
            1,
            new HashSet<string>(StringComparer.Ordinal)
            {
                "script.work_items.query",
                "survey.extended.discovery",
            },
            sharedNames));

        try
        {
            catalog.DiscoverAndConfigure(services);
            await using var provider = services.BuildServiceProvider();
            await catalog.StartAsync(provider);

            var advertisement = provider.GetRequiredService<ExtendedSurveyServiceRegistry>().Snapshot().Single();
            Assert.AreEqual("diary.readonly", advertisement.ServiceId);
            CollectionAssert.Contains(advertisement.Capabilities.ToArray(), "diary_query_work_items");
            Assert.IsFalse(advertisement.Capabilities.Any(name =>
                name.Contains("create", StringComparison.OrdinalIgnoreCase)
                || name.Contains("update", StringComparison.OrdinalIgnoreCase)
                || name.Contains("delete", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            await catalog.StopAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void EnabledPackagedModuleLoadsThroughPrivateContextAndContributesUi()
    {
        var root = Path.Combine(Path.GetTempPath(), "Diary.AgentModuleLoad", Guid.NewGuid().ToString("N"));
        var moduleRoot = Path.Combine(root, "Modules");
        var copiedModule = Path.Combine(moduleRoot, "diary.ai-agent");
        Directory.CreateDirectory(copiedModule);
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(GetAppOutputDirectory(), "Modules", "diary.ai-agent")))
        {
            File.Copy(file, Path.Combine(copiedModule, Path.GetFileName(file)));
        }
        var statePath = Path.Combine(root, "module-states.json");
        File.WriteAllText(statePath, """
            {"schemaVersion":1,"modules":{"diary.ai-agent":{"enabled":true}}}
            """);
        var services = new ServiceCollection();
        RegisterHostStubs(services);
        var sharedNames = AssemblyLoadContext.Default.Assemblies
            .Select(assembly => assembly.GetName().Name)
            .Where(name => name is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        sharedNames.Add(typeof(IAppModule).Assembly.GetName().Name!);
        sharedNames.Add(typeof(INavigationContribution).Assembly.GetName().Name!);
        sharedNames.Add(typeof(IWorkItemQueryScriptApi).Assembly.GetName().Name!);
        var catalog = new AppModuleCatalog(new AppModuleCatalogOptions(
            moduleRoot,
            statePath,
            Path.Combine(root, "config"),
            new Version(1, 0, 1),
            1,
            new HashSet<string>(StringComparer.Ordinal)
            {
                "module.navigation",
                "module.settings",
                "script.work_items.query",
            },
            sharedNames));

        catalog.DiscoverAndConfigure(services);
        using var provider = services.BuildServiceProvider();
        var contribution = provider.GetServices<INavigationContribution>().Single();

        Assert.AreEqual("AI 助手", contribution.Title);
        Assert.AreNotSame(
            AssemblyLoadContext.Default,
            AssemblyLoadContext.GetLoadContext(contribution.GetType().Assembly));
        Assert.IsTrue(catalog.Diagnostics.Any(item => item.Code == "module.loaded"));
    }

    [TestMethod]
    public void PublishedAiModuleLoadsFromReleaseDirectoryWhenAvailable()
    {
        var releaseModule = Path.GetFullPath(Path.Combine(
            GetRepositoryRoot(),
            ".build-tmp",
            "publish-ai-final",
            "win-x64",
            "Modules",
            "diary.ai-agent"));
        if (!Directory.Exists(releaseModule))
            Assert.Inconclusive("尚未生成 Windows Release 发布目录。");

        var root = Path.Combine(Path.GetTempPath(), "Diary.AgentReleaseModuleLoad", Guid.NewGuid().ToString("N"));
        var copiedModule = Path.Combine(root, "Modules", "diary.ai-agent");
        Directory.CreateDirectory(copiedModule);
        foreach (var file in Directory.EnumerateFiles(releaseModule))
            File.Copy(file, Path.Combine(copiedModule, Path.GetFileName(file)));
        var statePath = Path.Combine(root, "module-states.json");
        File.WriteAllText(statePath, """
            {"schemaVersion":1,"modules":{"diary.ai-agent":{"enabled":true}}}
            """);
        var services = new ServiceCollection();
        RegisterHostStubs(services);
        var catalog = CreateCatalog(root, statePath);

        catalog.DiscoverAndConfigure(services);
        using var provider = services.BuildServiceProvider();
        var contribution = provider.GetServices<INavigationContribution>().Single();
        var loadContext = AssemblyLoadContext.GetLoadContext(contribution.GetType().Assembly);

        Assert.AreEqual("AI 助手", contribution.Title);
        Assert.AreNotSame(AssemblyLoadContext.Default, loadContext);
        StringAssert.StartsWith(
            contribution.GetType().Assembly.Location,
            copiedModule,
            StringComparison.OrdinalIgnoreCase);
        Assert.IsTrue(loadContext!.Assemblies.Any(assembly => assembly.GetName().Name == "Diary.Agent"));
        Assert.IsTrue(catalog.Diagnostics.Any(item => item.Code == "module.loaded"));
    }

    private static AppModuleCatalog CreateCatalog(string root, string statePath)
    {
        var sharedNames = AssemblyLoadContext.Default.Assemblies
            .Select(assembly => assembly.GetName().Name)
            .Where(name => name is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        sharedNames.Add(typeof(IAppModule).Assembly.GetName().Name!);
        sharedNames.Add(typeof(INavigationContribution).Assembly.GetName().Name!);
        sharedNames.Add(typeof(IWorkItemQueryScriptApi).Assembly.GetName().Name!);
        return new AppModuleCatalog(new AppModuleCatalogOptions(
            Path.Combine(root, "Modules"),
            statePath,
            Path.Combine(root, "config"),
            new Version(1, 0, 1),
            1,
            new HashSet<string>(StringComparer.Ordinal)
            {
                "module.navigation",
                "module.settings",
                "script.work_items.query",
            },
            sharedNames));
    }

    private static string GetAppOutputDirectory() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        "Diary.App",
        "bin",
        "Debug",
        "net10.0"));

    private static string GetRepositoryRoot() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        ".."));

    private static void RegisterHostStubs(IServiceCollection services)
    {
        services.AddSingleton<IWorkItemQueryScriptApi, EmptyWorkItemQueryApi>();
        services.AddSingleton<ITemplateScriptApi, EmptyTemplateApi>();
        services.AddSingleton<ITrackerInstanceScriptApi, EmptyTrackerApi>();
        services.AddSingleton<IWorkTagScriptApi, EmptyTagApi>();
        services.AddSingleton<ITagExtraFieldScriptApi, EmptyExtraFieldApi>();
        services.AddSingleton<ICurrentContextScriptApi, EmptyContextApi>();
        services.AddSingleton<IScriptValidationScriptApi, EmptyValidationApi>();
    }

    private sealed class EmptyWorkItemQueryApi : IWorkItemQueryScriptApi
    {
        public ValueTask<ScriptWorkItemQueryResult> QueryAsync(
            ScriptWorkItemQuery query,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ScriptWorkItemQueryResult.Success([], query));
    }

    private sealed class EmptyTemplateApi : ITemplateScriptApi
    {
        public IReadOnlyList<ScriptTemplateInfo> List() => [];
    }

    private sealed class EmptyTrackerApi : ITrackerInstanceScriptApi
    {
        public TrackerScriptResult Get(string pluginId, string instanceId) =>
            TrackerScriptResult.Failure(TrackerScriptErrorCode.InstanceUnavailable, "missing");

        public IReadOnlyList<ScriptTrackerInstance> List() => [];
    }

    private sealed class EmptyTagApi : IWorkTagScriptApi
    {
        public IReadOnlyList<ScriptWorkTagInfo> List() => [];
    }

    private sealed class EmptyContextApi : ICurrentContextScriptApi
    {
        public ScriptCurrentContext Get() => new("2026-10-08", null, null);
    }

    private sealed class EmptyExtraFieldApi : ITagExtraFieldScriptApi
    {
        public IReadOnlyList<ScriptTagExtraFieldInfo> List(bool includeDisabled = false) => [];
    }

    private sealed class EmptyValidationApi : IScriptValidationScriptApi
    {
        public ValueTask<ScriptValidationInfo> ValidateAsync(
            string language,
            string source,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ScriptValidationInfo(true, []));
    }
}
