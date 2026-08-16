using System.Collections.Immutable;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Diary.Script.CSharp;

public enum CSharpLanguageDiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record CSharpLanguageDiagnostic(
    string Code,
    string Message,
    CSharpLanguageDiagnosticSeverity Severity,
    string? SourcePath,
    int? Line,
    int? Column);

public sealed record CSharpLanguageCompletionItem(
    string Text,
    string Description,
    string? Documentation = null);

public sealed record CSharpLanguageHover(
    string Signature,
    string? Documentation = null);

public sealed class CSharpLanguageService
{
    public CSharpLanguageAnalysis Analyze(
        string source,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        cancellationToken.ThrowIfCancellationRequested();

        var syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            path: sourcePath,
            cancellationToken: cancellationToken);
        var compilation = CSharpCompilation.Create(
            assemblyName: $"DiaryScriptLanguage_{Guid.NewGuid():N}",
            syntaxTrees: [syntaxTree],
            references: CSharpEngine.CreateReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return new CSharpLanguageAnalysis(compilation, syntaxTree, source);
    }
}

public sealed class CSharpLanguageAnalysis
{
    private static readonly SymbolDisplayFormat SymbolFormat =
        SymbolDisplayFormat.MinimallyQualifiedFormat
            .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private readonly CSharpCompilation _compilation;
    private readonly SemanticModel _semanticModel;
    private readonly SyntaxTree _syntaxTree;
    private readonly SyntaxNode _root;
    private readonly string _source;

    public ImmutableArray<CSharpLanguageDiagnostic> Diagnostics { get; }

    internal CSharpLanguageAnalysis(
        CSharpCompilation compilation,
        SyntaxTree syntaxTree,
        string source)
    {
        _compilation = compilation;
        _semanticModel = compilation.GetSemanticModel(syntaxTree, ignoreAccessibility: false);
        _syntaxTree = syntaxTree;
        _root = syntaxTree.GetRoot();
        _source = source;
        Diagnostics = _compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Select(MapDiagnostic)
            .ToImmutableArray();
    }

    public IReadOnlyList<CSharpLanguageCompletionItem> GetCompletions(int caretOffset)
    {
        caretOffset = Math.Clamp(caretOffset, 0, _source.Length);
        var prefix = ReadIdentifierBackward(_source, caretOffset);
        var symbols = FindMemberType(caretOffset) is { } memberType
            ? memberType.GetMembers()
            : _semanticModel.LookupSymbols(caretOffset);

        return symbols
            .Where(symbol => symbol.CanBeReferencedByName)
            .Where(symbol => symbol.DeclaredAccessibility is Accessibility.Public
                or Accessibility.Internal
                or Accessibility.Protected
                or Accessibility.ProtectedOrInternal
                or Accessibility.NotApplicable)
            .GroupBy(symbol => symbol.Name, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(GetSymbolPriority).First())
            .Where(symbol => symbol.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(GetSymbolPriority)
            .ThenBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
            .Take(150)
            .Select(symbol => new CSharpLanguageCompletionItem(
                symbol.Name,
                FormatSymbol(symbol),
                GetDocumentation(symbol)))
            .ToArray();
    }

    public CSharpLanguageHover? GetHover(int caretOffset)
    {
        if (_source.Length == 0)
            return null;
        caretOffset = Math.Clamp(caretOffset, 0, _source.Length - 1);
        var token = _root.FindToken(caretOffset, findInsideTrivia: true);
        var node = token.Parent;
        ISymbol? symbol = null;
        for (var current = node; current is not null && symbol is null; current = current.Parent)
        {
            if (current is not IdentifierNameSyntax
                and not GenericNameSyntax
                and not MemberAccessExpressionSyntax
                and not InvocationExpressionSyntax)
                break;
            symbol = _semanticModel.GetSymbolInfo(current).Symbol;
        }

        if (symbol is null && node is VariableDeclaratorSyntax declarator)
            symbol = _semanticModel.GetDeclaredSymbol(declarator);
        if (symbol is null)
        {
            var memberAccess = node?.AncestorsAndSelf()
                .OfType<MemberAccessExpressionSyntax>()
                .FirstOrDefault();
            if (memberAccess is not null)
            {
                var memberType = _semanticModel.GetTypeInfo(memberAccess.Expression).Type;
                symbol = memberType?.GetMembers(token.ValueText).FirstOrDefault();
            }
        }
        if (symbol is null)
            return null;
        return new CSharpLanguageHover(FormatSymbol(symbol), GetDocumentation(symbol));
    }

    private ITypeSymbol? FindMemberType(int caretOffset)
    {
        var memberAccess = _root.DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Where(node => node.OperatorToken.SpanStart < caretOffset
                && node.Expression.SpanStart < caretOffset)
            .OrderByDescending(node => node.OperatorToken.SpanStart)
            .FirstOrDefault();
        if (memberAccess is null)
            return null;

        var type = _semanticModel.GetTypeInfo(memberAccess.Expression).Type;
        if (type is not null)
            return type;

        var symbol = _semanticModel.GetSymbolInfo(memberAccess.Expression).Symbol;
        return symbol switch
        {
            IFieldSymbol field => field.Type,
            ILocalSymbol local => local.Type,
            IParameterSymbol parameter => parameter.Type,
            IPropertySymbol property => property.Type,
            IMethodSymbol method => method.ReturnType,
            _ => null,
        };
    }

    private CSharpLanguageDiagnostic MapDiagnostic(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        var line = span.IsValid && span.StartLinePosition.Line >= 0
            ? (int?)(span.StartLinePosition.Line + 1)
            : null;
        var column = span.IsValid && span.StartLinePosition.Character >= 0
            ? (int?)(span.StartLinePosition.Character + 1)
            : null;
        return new CSharpLanguageDiagnostic(
            diagnostic.Id,
            diagnostic.GetMessage(),
            diagnostic.Severity == DiagnosticSeverity.Warning
                ? CSharpLanguageDiagnosticSeverity.Warning
                : CSharpLanguageDiagnosticSeverity.Error,
            span.Path,
            line,
            column);
    }

    private static int GetSymbolPriority(ISymbol symbol) => symbol switch
    {
        ILocalSymbol => 100,
        IParameterSymbol => 95,
        IPropertySymbol => 90,
        IFieldSymbol => 85,
        IMethodSymbol => 80,
        INamedTypeSymbol => 70,
        _ => 10,
    };

    private static string FormatSymbol(ISymbol symbol) =>
        symbol.ToDisplayString(SymbolFormat);

    private static string? GetDocumentation(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml))
            return null;
        var plainText = Regex.Replace(xml, "<[^>]+>", string.Empty);
        return WebUtility.HtmlDecode(plainText).Trim();
    }

    private static string ReadIdentifierBackward(string source, int offset)
    {
        var start = offset;
        while (start > 0 && IsIdentifierCharacter(source[start - 1]))
            start--;
        return source[start..offset];
    }

    private static bool IsIdentifierCharacter(char value) =>
        char.IsLetterOrDigit(value) || value == '_';
}
