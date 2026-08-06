using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Diary.ScriptBase;
using Diary.ScriptHost;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Diary.Script.CSharp;

public sealed class CSharpEngine : IScriptEngineV1
{
    private static readonly string[] ForbiddenNamespacePrefixes =
    [
        "System.IO",
        "System.Net",
        "System.Reflection",
        "System.Runtime.InteropServices",
        "Diary.Database",
        "Microsoft.Extensions.DependencyInjection",
    ];

    private static readonly HashSet<string> ForbiddenTypes = new(StringComparer.Ordinal)
    {
        "System.AppDomain",
        "System.Environment",
        "System.Diagnostics.Process",
        "System.Type",
    };

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
    private readonly string? _cacheDirectory;

    public CSharpEngine(string? cacheDirectory = null)
    {
        _references = CreateReferences();
        _cacheDirectory = cacheDirectory;
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

        var cachePath = GetCachePath(request);
        if (cachePath is not null && File.Exists(cachePath))
        {
            try
            {
                var cached = LoadProgram(File.ReadAllBytes(cachePath), request.SourcePath);
                if (cached.Succeeded)
                {
                    return ValueTask.FromResult(new ScriptBuildResult(
                        true,
                        cached.Program,
                        cached.Diagnostics.Add(new ScriptDiagnostic(
                            "SCRIPT_CACHE_HIT",
                            "The compiled script was loaded from cache.",
                            ScriptDiagnosticSeverity.Info,
                            ScriptDiagnosticCategory.Engine,
                            request.SourcePath))));
                }
                File.Delete(cachePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        var syntaxTree = CSharpSyntaxTree.ParseText(request.Source, path: request.SourcePath);
        var compilation = CSharpCompilation.Create(
            assemblyName: $"DiaryScript_{Guid.NewGuid():N}",
            syntaxTrees: [syntaxTree],
            references: _references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var policyDiagnostics = ValidatePolicy(compilation, syntaxTree, request.SourcePath);
        if (!policyDiagnostics.IsEmpty)
            return ValueTask.FromResult(new ScriptBuildResult(false, null, policyDiagnostics));

        using var output = new MemoryStream();
        var emit = compilation.Emit(output, cancellationToken: cancellationToken);
        var diagnostics = emit.Diagnostics
            .Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error)
            .Select(MapDiagnostic)
            .ToImmutableArray();
        if (!emit.Success)
            return ValueTask.FromResult(new ScriptBuildResult(false, null, diagnostics));

        var assemblyBytes = output.ToArray();
        if (cachePath is not null)
            WriteCache(cachePath, assemblyBytes);
        return ValueTask.FromResult(LoadProgram(assemblyBytes, request.SourcePath, diagnostics));
    }

    private static ScriptBuildResult LoadProgram(
        byte[] assemblyBytes,
        string sourcePath,
        ImmutableArray<ScriptDiagnostic> diagnostics = default)
    {
        ScriptLoadContext? loadContext = null;
        try
        {
            loadContext = new ScriptLoadContext();
            using var assemblyStream = new MemoryStream(assemblyBytes, writable: false);
            var assembly = loadContext.LoadFromStream(assemblyStream);
            var programs = assembly.GetTypes()
                .Where(type => typeof(IScriptProgramV1).IsAssignableFrom(type)
                    && !type.IsAbstract
                    && type.GetConstructor(Type.EmptyTypes) is not null)
                .ToArray();
            if (programs.Length != 1)
            {
                loadContext.Unload();
                return ScriptBuildResult.Failure(new ScriptDiagnostic(
                    "CSHARP_ENTRYPOINT_COUNT",
                    "The script must contain exactly one public parameterless IScriptProgramV1 implementation.",
                    ScriptDiagnosticSeverity.Error,
                    ScriptDiagnosticCategory.Validation,
                    sourcePath));
            }

            if (Activator.CreateInstance(programs[0]) is not IScriptProgramV1 program)
            {
                loadContext.Unload();
                return ScriptBuildResult.Failure(new ScriptDiagnostic(
                    "CSHARP_ENTRYPOINT_INVALID",
                    "The script entry point could not be created.",
                    ScriptDiagnosticSeverity.Error,
                    ScriptDiagnosticCategory.Engine,
                    sourcePath));
            }

            var compiledProgram = new CollectibleProgram(program, loadContext);
            loadContext = null;
            return new ScriptBuildResult(
                true,
                compiledProgram,
                diagnostics.IsDefault ? ImmutableArray<ScriptDiagnostic>.Empty : diagnostics);
        }
        catch (Exception)
        {
            loadContext?.Unload();
            return ScriptBuildResult.Failure(new ScriptDiagnostic(
                "CSHARP_LOAD_FAILED",
                "The compiled script could not be loaded.",
                ScriptDiagnosticSeverity.Error,
                ScriptDiagnosticCategory.Engine,
                sourcePath));
        }
    }

    private string? GetCachePath(ScriptBuildRequest request)
    {
        if (string.IsNullOrWhiteSpace(_cacheDirectory))
            return null;
        var cacheInput = $"{StableName}\n{Version}\n{request.ApiVersion}\ntrusted-v1\n{request.Source}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(cacheInput)));
        return Path.Combine(_cacheDirectory, hash + ".dll");
    }

    private static void WriteCache(string cachePath, byte[] assemblyBytes)
    {
        var temporaryPath = cachePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllBytes(temporaryPath, assemblyBytes);
            File.Move(temporaryPath, cachePath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
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

    private static ImmutableArray<ScriptDiagnostic> ValidatePolicy(
        CSharpCompilation compilation,
        SyntaxTree syntaxTree,
        string sourcePath)
    {
        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var diagnostics = ImmutableArray.CreateBuilder<ScriptDiagnostic>();
        var locations = new HashSet<int>();
        foreach (var node in syntaxTree.GetRoot().DescendantNodes()
                     .Where(node => node is IdentifierNameSyntax or MemberAccessExpressionSyntax or UsingDirectiveSyntax))
        {
            var symbol = semanticModel.GetSymbolInfo(node).Symbol;
            if (symbol is null)
                continue;
            var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
            var typeName = type?.ToDisplayString();
            var namespaceName = (type ?? symbol).ContainingNamespace?.ToDisplayString() ?? string.Empty;
            if (!ForbiddenTypes.Contains(typeName ?? string.Empty)
                && !ForbiddenNamespacePrefixes.Any(prefix =>
                    namespaceName.Equals(prefix, StringComparison.Ordinal)
                    || namespaceName.StartsWith(prefix + ".", StringComparison.Ordinal)))
            {
                continue;
            }

            var lineSpan = node.GetLocation().GetLineSpan();
            if (!locations.Add(node.SpanStart))
                continue;
            diagnostics.Add(new ScriptDiagnostic(
                "CSHARP_API_FORBIDDEN",
                "The script uses an API that is not allowed by the host policy.",
                ScriptDiagnosticSeverity.Error,
                ScriptDiagnosticCategory.Security,
                sourcePath,
                lineSpan.StartLinePosition.Line + 1,
                lineSpan.StartLinePosition.Character + 1));
        }
        return diagnostics.ToImmutable();
    }

    private sealed class ScriptLoadContext() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name == typeof(ScriptDescriptor).Assembly.GetName().Name)
                return typeof(ScriptDescriptor).Assembly;
            if (assemblyName.Name == typeof(IWorkItemQueryScriptApi).Assembly.GetName().Name)
                return typeof(IWorkItemQueryScriptApi).Assembly;
            return null;
        }
    }

    private sealed class CollectibleProgram : IScriptProgramV1, IDisposable
    {
        private IScriptProgramV1? _program;
        private ScriptLoadContext? _loadContext;

        public CollectibleProgram(IScriptProgramV1 program, ScriptLoadContext loadContext)
        {
            _program = program;
            _loadContext = loadContext;
            Descriptor = program.Descriptor;
        }

        public ScriptDescriptor Descriptor { get; }

        public ValueTask<ScriptExecutionResult> ExecuteAsync(
            ScriptExecutionRequest request,
            IScriptExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            var program = _program;
            return program is not null
                ? program.ExecuteAsync(request, context, cancellationToken)
                : ValueTask.FromResult(new ScriptExecutionResult(
                    ScriptExecutionStatus.Rejected,
                    [new ScriptDiagnostic(
                        "SCRIPT_PROGRAM_UNLOADED",
                        "The script program has been unloaded.",
                        ScriptDiagnosticSeverity.Error,
                        ScriptDiagnosticCategory.Runtime)]));
        }

        public void Dispose()
        {
            _program = null;
            Interlocked.Exchange(ref _loadContext, null)?.Unload();
        }
    }
}
