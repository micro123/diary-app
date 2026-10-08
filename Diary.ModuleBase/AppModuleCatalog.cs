using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.ModuleBase;

public sealed partial class AppModuleCatalog
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly AppModuleCatalogOptions _options;
    private readonly List<AppModuleDiagnostic> _diagnostics = [];
    private readonly List<LoadedAppModule> _loadedModules = [];

    public AppModuleCatalog(AppModuleCatalogOptions options)
    {
        _options = options;
    }

    public IReadOnlyList<AppModuleDiagnostic> Diagnostics => _diagnostics;

    public IReadOnlyList<AppModuleManifest> LoadedManifests =>
        _loadedModules.Select(module => module.Manifest).ToArray();

    public void DiscoverAndConfigure(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!Directory.Exists(_options.ModuleRootDirectory))
            return;

        var state = new ModuleStateStore(_options.StateFilePath).Load();
        if (state.IsCorrupt)
        {
            AddDiagnostic(
                "*",
                AppModuleState.Disabled,
                AppModuleDiagnosticSeverity.Error,
                "state.corrupt",
                $"模块状态文件损坏，所有可选模块按禁用处理：{state.Error}");
        }

        var candidates = DiscoverManifests();
        var duplicateIds = candidates
            .GroupBy(candidate => candidate.Manifest.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates.OrderBy(item => item.Manifest.Id, StringComparer.Ordinal))
        {
            var manifest = candidate.Manifest;
            if (duplicateIds.Contains(manifest.Id))
            {
                AddDiagnostic(manifest.Id, AppModuleState.Failed, AppModuleDiagnosticSeverity.Error,
                    "manifest.id-conflict", "模块 ID 存在大小写冲突或重复，已拒绝加载。");
                continue;
            }

            if (!state.IsEnabled(manifest))
            {
                AddDiagnostic(manifest.Id, AppModuleState.Disabled, AppModuleDiagnosticSeverity.Information,
                    "module.disabled", "模块未启用，入口程序集未加载。");
                continue;
            }

            if (!IsCompatible(manifest, out var incompatibility))
            {
                AddDiagnostic(manifest.Id, AppModuleState.Incompatible, AppModuleDiagnosticSeverity.Warning,
                    "module.incompatible", incompatibility);
                continue;
            }

            TryLoadAndConfigure(candidate, services);
        }
    }

    public async ValueTask StartAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        foreach (var loaded in _loadedModules)
        {
            try
            {
                await loaded.Instance.StartAsync(
                    new AppModuleRuntimeContext(
                        loaded.Manifest,
                        loaded.ModuleDirectory,
                        _options.ConfigurationDirectory,
                        services),
                    cancellationToken);
                AddDiagnostic(loaded.Manifest.Id, AppModuleState.Started, AppModuleDiagnosticSeverity.Information,
                    "module.started", "模块已启动。");
            }
            catch (Exception exception)
            {
                AddDiagnostic(loaded.Manifest.Id, AppModuleState.Failed, AppModuleDiagnosticSeverity.Error,
                    "module.start-failed", $"模块启动失败：{exception.Message}");
            }
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        foreach (var loaded in _loadedModules.AsEnumerable().Reverse())
        {
            try
            {
                await loaded.Instance.StopAsync(cancellationToken);
                AddDiagnostic(loaded.Manifest.Id, AppModuleState.Stopped, AppModuleDiagnosticSeverity.Information,
                    "module.stopped", "模块已停止。");
            }
            catch (Exception exception)
            {
                AddDiagnostic(loaded.Manifest.Id, AppModuleState.Failed, AppModuleDiagnosticSeverity.Error,
                    "module.stop-failed", $"模块停止失败：{exception.Message}");
            }
        }
    }

    private List<ModuleCandidate> DiscoverManifests()
    {
        var candidates = new List<ModuleCandidate>();
        foreach (var directory in Directory.EnumerateDirectories(
                     _options.ModuleRootDirectory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            var fallbackId = Path.GetFileName(directory);
            try
            {
                EnsureNoReparsePoint(_options.ModuleRootDirectory, directory);
                var manifestPath = Path.Combine(directory, "module.json");
                if (!File.Exists(manifestPath))
                {
                    AddDiagnostic(fallbackId, AppModuleState.Failed, AppModuleDiagnosticSeverity.Warning,
                        "manifest.missing", "模块目录缺少 module.json。");
                    continue;
                }

                using var stream = File.OpenRead(manifestPath);
                var manifest = JsonSerializer.Deserialize<AppModuleManifest>(stream, ManifestJsonOptions)
                    ?? throw new JsonException("module.json 为空。");
                ValidateManifest(manifest, directory);
                candidates.Add(new ModuleCandidate(manifest, directory));
                AddDiagnostic(manifest.Id, AppModuleState.Discovered, AppModuleDiagnosticSeverity.Information,
                    "manifest.valid", "模块 manifest 校验通过。");
            }
            catch (Exception exception) when (exception is JsonException
                                               or IOException
                                               or UnauthorizedAccessException
                                               or InvalidDataException)
            {
                AddDiagnostic(fallbackId, AppModuleState.Failed, AppModuleDiagnosticSeverity.Error,
                    "manifest.invalid", $"模块 manifest 无效：{exception.Message}");
            }
        }
        return candidates;
    }

    private void ValidateManifest(AppModuleManifest manifest, string moduleDirectory)
    {
        if (!ModuleIdRegex().IsMatch(manifest.Id))
            throw new InvalidDataException("模块 ID 必须是小写点分标识符。");
        if (string.IsNullOrWhiteSpace(manifest.DisplayName)
            || !Version.TryParse(manifest.Version, out _)
            || manifest.ApiVersion <= 0
            || string.IsNullOrWhiteSpace(manifest.EntryType))
        {
            throw new InvalidDataException("模块名称、版本、API 版本或入口类型无效。");
        }

        _ = ResolveContainedPath(moduleDirectory, manifest.EntryAssembly, mustExist: true);
        ValidateOptionalVersion(manifest.MinAppVersion, nameof(manifest.MinAppVersion));
        ValidateOptionalVersion(manifest.MaxAppVersion, nameof(manifest.MaxAppVersion));
        if (manifest.RequiredCapabilities.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("requiredCapabilities 包含空值。");
    }

    private bool IsCompatible(AppModuleManifest manifest, out string message)
    {
        if (manifest.ApiVersion != _options.ApiVersion)
        {
            message = $"模块 API 版本 {manifest.ApiVersion} 与宿主版本 {_options.ApiVersion} 不兼容。";
            return false;
        }
        if (manifest.MinAppVersion is not null && _options.HostVersion < Version.Parse(manifest.MinAppVersion))
        {
            message = $"模块要求 App >= {manifest.MinAppVersion}。";
            return false;
        }
        if (manifest.MaxAppVersion is not null && _options.HostVersion > Version.Parse(manifest.MaxAppVersion))
        {
            message = $"模块要求 App <= {manifest.MaxAppVersion}。";
            return false;
        }
        var missing = manifest.RequiredCapabilities
            .Where(capability => !_options.HostCapabilities.Contains(capability))
            .ToArray();
        if (missing.Length > 0)
        {
            message = $"宿主缺少能力：{string.Join(", ", missing)}。";
            return false;
        }
        message = string.Empty;
        return true;
    }

    private void TryLoadAndConfigure(ModuleCandidate candidate, IServiceCollection services)
    {
        try
        {
            var entryPath = ResolveContainedPath(
                candidate.ModuleDirectory,
                candidate.Manifest.EntryAssembly,
                mustExist: true);
            var loadContext = new AppModuleLoadContext(entryPath, _options.SharedAssemblyNames);
            var assembly = loadContext.LoadFromAssemblyPath(entryPath);
            var entryType = assembly.GetType(candidate.Manifest.EntryType, throwOnError: true, ignoreCase: false)!;
            if (!entryType.IsPublic
                || entryType.IsAbstract
                || entryType.GetConstructor(Type.EmptyTypes) is null
                || !typeof(IAppModule).IsAssignableFrom(entryType))
            {
                throw new InvalidDataException(
                    "模块入口必须是实现 IAppModule 的公开、非抽象、无参构造类型。");
            }

            var instance = (IAppModule)Activator.CreateInstance(entryType)!;
            instance.ConfigureServices(
                services,
                new AppModuleRegistrationContext(
                    candidate.Manifest,
                    candidate.ModuleDirectory,
                    _options.ConfigurationDirectory,
                    _options.HostCapabilities));
            _loadedModules.Add(new LoadedAppModule(
                candidate.Manifest,
                candidate.ModuleDirectory,
                loadContext,
                instance));
            AddDiagnostic(candidate.Manifest.Id, AppModuleState.Loaded, AppModuleDiagnosticSeverity.Information,
                "module.loaded", "模块已加载并完成服务注册。");
        }
        catch (Exception exception)
        {
            AddDiagnostic(candidate.Manifest.Id, AppModuleState.Failed, AppModuleDiagnosticSeverity.Error,
                "module.load-failed", $"模块加载失败：{Unwrap(exception).Message}");
        }
    }

    private static string ResolveContainedPath(string rootDirectory, string relativePath, bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathFullyQualified(relativePath))
            throw new InvalidDataException("模块入口路径必须是相对路径。");
        if (relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Contains("..", StringComparer.Ordinal))
        {
            throw new InvalidDataException("模块入口路径不得包含 ..。");
        }

        var root = Path.GetFullPath(rootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("模块入口路径逃逸模块目录。");
        if (mustExist && !File.Exists(path))
            throw new InvalidDataException("模块入口程序集不存在。");
        EnsureNoReparsePoint(root, path);
        return path;
    }

    private static void EnsureNoReparsePoint(string rootDirectory, string path)
    {
        var root = Path.GetFullPath(rootDirectory);
        var current = Path.GetFullPath(path);
        while (current.Length >= root.Length)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("模块路径不得经过符号链接或重解析点。");
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
                break;
            current = Path.GetDirectoryName(current)
                ?? throw new InvalidDataException("无法校验模块路径。");
        }
    }

    private static void ValidateOptionalVersion(string? value, string name)
    {
        if (value is not null && !Version.TryParse(value, out _))
            throw new InvalidDataException($"{name} 不是有效版本号。");
    }

    private void AddDiagnostic(
        string moduleId,
        AppModuleState state,
        AppModuleDiagnosticSeverity severity,
        string code,
        string message)
        => _diagnostics.Add(new AppModuleDiagnostic(moduleId, state, severity, code, message));

    private static Exception Unwrap(Exception exception)
        => exception is TargetInvocationException { InnerException: not null } invocation
            ? invocation.InnerException!
            : exception;

    [GeneratedRegex("^[a-z0-9]+(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ModuleIdRegex();

    private sealed record ModuleCandidate(AppModuleManifest Manifest, string ModuleDirectory);

    private sealed record LoadedAppModule(
        AppModuleManifest Manifest,
        string ModuleDirectory,
        AppModuleLoadContext LoadContext,
        IAppModule Instance);
}
