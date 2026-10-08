namespace Diary.ModuleBase;

public enum AppModuleState
{
    Discovered,
    Disabled,
    Incompatible,
    Loaded,
    Started,
    Stopped,
    Failed,
}

public enum AppModuleDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record AppModuleDiagnostic(
    string ModuleId,
    AppModuleState State,
    AppModuleDiagnosticSeverity Severity,
    string Code,
    string Message);

public sealed record AppModuleCatalogOptions(
    string ModuleRootDirectory,
    string StateFilePath,
    string ConfigurationDirectory,
    Version HostVersion,
    int ApiVersion,
    IReadOnlySet<string> HostCapabilities,
    IReadOnlySet<string> SharedAssemblyNames);
