using System.Collections.Immutable;
using System.Reflection;
using Diary.ScriptBase;
using Diary.ScriptHost;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Diary.Script.CSharp;

public sealed class CSharpEngine : IScriptEngineV1
{
    private static readonly string[] ReferenceNames =
    [
        "System.Private.CoreLib.dll",
        "System.Runtime.dll",
        "System.Collections.dll",
        "System.Collections.Immutable.dll",
        "System.Threading.dll",
        "System.Threading.Tasks.Extensions.dll",
    ];

    private readonly ImmutableArray<MetadataReference> _references;

    public CSharpEngine()
    {
        _references = CreateReferences();
    }

    public string Name => "csharp";
    public string StableName => Name;
    public string Version => typeof(CSharpEngine).Assembly.GetName().Version?.ToString() ?? "1.0";

    public ScriptMatchResult Match(ScriptMatchRequest request) =>
        new(request.SourcePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));

    public ValueTask<ScriptBuildResult> BuildAsync(
        ScriptBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var syntaxTree = CSharpSyntaxTree.ParseText(request.Source, path: request.SourcePath);
        var compilation = CSharpCompilation.Create(
            assemblyName: $"DiaryScript_{Guid.NewGuid():N}",
            syntaxTrees: [syntaxTree],
            references: _references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var output = new MemoryStream();
        var emit = compilation.Emit(output, cancellationToken: cancellationToken);
        var diagnostics = emit.Diagnostics
            .Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error)
            .Select(MapDiagnostic)
            .ToImmutableArray();
        if (!emit.Success)
            return ValueTask.FromResult(new ScriptBuildResult(false, null, diagnostics));

        try
        {
            var assembly = Assembly.Load(output.ToArray());
            var programs = assembly.GetTypes()
                .Where(type => typeof(IScriptProgramV1).IsAssignableFrom(type)
                    && !type.IsAbstract
                    && type.GetConstructor(Type.EmptyTypes) is not null)
                .ToArray();
            if (programs.Length != 1)
                return ValueTask.FromResult(ScriptBuildResult.Failure(new ScriptDiagnostic(
                    "CSHARP_ENTRYPOINT_COUNT",
                    "The script must contain exactly one public parameterless IScriptProgramV1 implementation.",
                    ScriptDiagnosticSeverity.Error,
                    ScriptDiagnosticCategory.Validation,
                    request.SourcePath)));

            if (Activator.CreateInstance(programs[0]) is not IScriptProgramV1 program)
                return ValueTask.FromResult(ScriptBuildResult.Failure(new ScriptDiagnostic(
                    "CSHARP_ENTRYPOINT_INVALID",
                    "The script entry point could not be created.",
                    ScriptDiagnosticSeverity.Error,
                    ScriptDiagnosticCategory.Engine,
                    request.SourcePath)));

            return ValueTask.FromResult(ScriptBuildResult.Success(program));
        }
        catch (Exception)
        {
            return ValueTask.FromResult(ScriptBuildResult.Failure(new ScriptDiagnostic(
                "CSHARP_LOAD_FAILED",
                "The compiled script could not be loaded.",
                ScriptDiagnosticSeverity.Error,
                ScriptDiagnosticCategory.Engine,
                request.SourcePath)));
        }
    }

    private static ImmutableArray<MetadataReference> CreateReferences()
    {
        var trustedAssemblies = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => (Name: Path.GetFileName(path), Path: path))
            .Where(item => item.Name is not null)
            .GroupBy(item => item.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Path, StringComparer.OrdinalIgnoreCase);
        var paths = ReferenceNames
            .Where(trustedAssemblies.ContainsKey)
            .Select(name => trustedAssemblies[name])
            .Concat(
            [
                typeof(object).Assembly.Location,
                typeof(CancellationToken).Assembly.Location,
                typeof(ValueTask).Assembly.Location,
                typeof(ImmutableArray<>).Assembly.Location,
                typeof(ScriptDescriptor).Assembly.Location,
                typeof(IWorkItemQueryScriptApi).Assembly.Location,
            ])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return paths
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToImmutableArray();
    }

    private static ScriptDiagnostic MapDiagnostic(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        var line = span.IsValid && span.StartLinePosition.Line >= 0
            ? (int?)(span.StartLinePosition.Line + 1)
            : null;
        var column = span.IsValid && span.StartLinePosition.Character >= 0
            ? (int?)(span.StartLinePosition.Character + 1)
            : null;
        return new ScriptDiagnostic(
            diagnostic.Id,
            diagnostic.GetMessage(),
            diagnostic.Severity == DiagnosticSeverity.Warning
                ? ScriptDiagnosticSeverity.Warning
                : ScriptDiagnosticSeverity.Error,
            ScriptDiagnosticCategory.Syntax,
            span.Path,
            line,
            column);
    }
}
