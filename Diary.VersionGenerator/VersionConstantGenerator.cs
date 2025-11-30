using System;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Diary.VersionGenerator;

[Generator]
public class VersionConstantGenerator : IIncrementalGenerator
{
    public record struct Info
    {
        public ClassDeclarationSyntax? Class;
    }
    
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var myProvider = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (node, _) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
            transform: GetVersionInfo
        ).Where(x => x is { Class: not null });

        context.RegisterSourceOutput(myProvider, Execute);
    }

    private static void ThrowHere(object? o)
    {
        throw new ArgumentException(o?.ToString());
    }
    
    private Info GetVersionInfo(GeneratorSyntaxContext syntaxContext, CancellationToken token)
    {
        static bool ClassCheck(GeneratorSyntaxContext syntaxContext, ClassDeclarationSyntax c)
        {
            foreach (var list in c.AttributeLists)
            {
                foreach (var attribute in list.Attributes)
                {
                    if (syntaxContext.SemanticModel.GetSymbolInfo(attribute).Symbol is not IMethodSymbol attributeSymbol)
                        continue;
                    var attributeContainingTypeSymbol = attributeSymbol.ContainingType;
                    var fullName = attributeContainingTypeSymbol.ToDisplayString();
                    if (fullName == "Diary.Utils.VersionConstantAttribute")
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        
        var classSyntax = (ClassDeclarationSyntax)syntaxContext.Node;
        if (!ClassCheck(syntaxContext, classSyntax))
            return default;
        return new Info() { Class = classSyntax, };
    }

    private void Execute(SourceProductionContext context, Info info)
    {
        if (info.Class is null)
            ThrowHere("info is null");

        static string GetFieldName(FieldDeclarationSyntax field)
        {
            // 必须是常量
            if (!field.Modifiers.Any(SyntaxKind.ConstKeyword))
                return string.Empty;
            return field.Declaration.Variables.First().Identifier.ValueText;
        }
        
        var members = info.Class!.Members;
        // Get Major,Minor,Patch Member
        var fieldMajor =
            members.FirstOrDefault(x => x is FieldDeclarationSyntax field && GetFieldName(field) == "Major");
        var fieldMinor =
            members.FirstOrDefault(x => x is FieldDeclarationSyntax field && GetFieldName(field) == "Minor");
        var fieldPatch =
            members.FirstOrDefault(x => x is FieldDeclarationSyntax field && GetFieldName(field) == "Patch");
        
        if (fieldMajor is null || fieldMinor is null || fieldPatch is null)
            ThrowHere("field missing");

        
        // var major = context.SemanticModel.GetSymbolInfo(classSyntax, token);
        // var minor = syntaxContext.SemanticModel.GetDeclaredSymbol(fieldMinor!, token);
        // var patch = syntaxContext.SemanticModel.GetDeclaredSymbol(fieldPatch!, token);
        
        // ThrowHere(fieldMajor.ToString());
        var majorValueText = fieldMajor!.DescendantNodes().OfType<LiteralExpressionSyntax>().FirstOrDefault()
            !.ToString();
        var minorValueText = fieldMinor!.DescendantNodes().OfType<LiteralExpressionSyntax>().FirstOrDefault()
            !.ToString();
        var patchValueText = fieldPatch!.DescendantNodes().OfType<LiteralExpressionSyntax>().FirstOrDefault()
            !.ToString();

        uint.TryParse(majorValueText, out var major);
        uint.TryParse(minorValueText, out var minor);
        uint.TryParse(patchValueText, out var patch);

        // ThrowHere($"{major}.{minor}.{patch}");
        
        var className = info.Class.Identifier.ValueText;
        
        BaseNamespaceDeclarationSyntax? nsSyntax =
            info.Class.Ancestors().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
        nsSyntax ??= info.Class.Ancestors().OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();

        var nsName = nsSyntax!.Name.ToString();

        // context.ReportDiagnostic(
        //     Diagnostic.Create(
        //         new DiagnosticDescriptor("VC002", "no problems, but not implemented yet！",
        //             $"every thing is good, {info}"
        //             , "Default", DiagnosticSeverity.Error, true), Location.None
        //     )
        // );
        //
        var code = major * 0x10000 + minor * 0x100 + patch;
        var sb = new StringBuilder();
        sb.AppendLine($"namespace {nsName};");
        sb.AppendLine($"public static partial class {className}");
        sb.AppendLine("{");
        sb.AppendLine($"    public const string VersionString = \"{major}.{minor}.{patch}\";");
        sb.AppendLine($"    public const uint VersionCode = 0x{code:X8};");
        sb.AppendLine("}");
        
        context.AddSource(className + ".g.cs", sb.ToString());
    }
}