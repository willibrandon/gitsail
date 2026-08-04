using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;

namespace GitSail.Analyzers;

/// <summary>
/// Enforces source-file and XML-documentation rules that are part of the repository contract.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RepositoryPolicyAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Identifies a source file containing more than one declared type.
    /// </summary>
    public const string OneTypePerFileId = "GITSAIL0001";

    /// <summary>
    /// Identifies an externally or internally visible declaration without XML documentation.
    /// </summary>
    public const string DocumentationRequiredId = "GITSAIL0002";

    /// <summary>
    /// Identifies an XML summary that is not written with separate opening, content, and closing lines.
    /// </summary>
    public const string ThreeLineSummaryId = "GITSAIL0003";

    private static readonly DiagnosticDescriptor s_oneTypePerFile = new(
        OneTypePerFileId,
        "Use one type per file",
        "Type '{0}' must be moved to its own file",
        "GitSail.SourcePolicy",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Each C# source file must declare at most one class, record, struct, interface, enum, or delegate.");

    private static readonly DiagnosticDescriptor s_documentationRequired = new(
        DocumentationRequiredId,
        "Add XML documentation",
        "Declaration '{0}' requires XML documentation",
        "GitSail.SourcePolicy",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Public, protected, and internal declarations require XML documentation.");

    private static readonly DiagnosticDescriptor s_threeLineSummary = new(
        ThreeLineSummaryId,
        "Use a physical three-line XML summary",
        "Summary for '{0}' must put the opening tag, content, and closing tag on separate lines",
        "GitSail.SourcePolicy",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "XML summary tags must occupy separate lines with content between them.");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => [s_oneTypePerFile, s_documentationRequired, s_threeLineSummary];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxTreeAction(AnalyzeSyntaxTree);
    }

    private static void AnalyzeSyntaxTree(SyntaxTreeAnalysisContext context)
    {
        var root = context.Tree.GetCompilationUnitRoot(context.CancellationToken);
        if (IsGenerated(root, context.Tree.FilePath))
        {
            return;
        }

        var types = root.DescendantNodes()
            .Where(static node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
            .OfType<MemberDeclarationSyntax>()
            .ToArray();
        foreach (var declaration in types.Skip(1))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                s_oneTypePerFile,
                GetIdentifierLocation(declaration),
                GetDeclarationName(declaration)));
        }

        foreach (var declaration in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            if (!RequiresDocumentation(declaration))
            {
                continue;
            }

            var documentation = declaration.GetLeadingTrivia()
                .Select(static trivia => trivia.GetStructure())
                .OfType<DocumentationCommentTriviaSyntax>()
                .LastOrDefault();
            if (documentation is null)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    s_documentationRequired,
                    GetIdentifierLocation(declaration),
                    GetDeclarationName(declaration)));
                continue;
            }

            var text = documentation.ToFullString();
            if (ContainsInheritdoc(text))
            {
                continue;
            }

            if (!ContainsSummary(text))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    s_documentationRequired,
                    GetIdentifierLocation(declaration),
                    GetDeclarationName(declaration)));
            }
            else if (!HasPhysicalThreeLineSummary(text))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    s_threeLineSummary,
                    GetIdentifierLocation(declaration),
                    GetDeclarationName(declaration)));
            }
        }
    }

    private static bool RequiresDocumentation(MemberDeclarationSyntax declaration)
    {
        if (declaration is EnumMemberDeclarationSyntax)
        {
            return true;
        }

        if (declaration.Parent is InterfaceDeclarationSyntax)
        {
            return true;
        }

        var modifiers = GetModifiers(declaration);
        if (modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.PublicKeyword) ||
            modifier.IsKind(SyntaxKind.InternalKeyword) ||
            modifier.IsKind(SyntaxKind.ProtectedKeyword)))
        {
            return true;
        }

        return (declaration.Parent is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax) &&
            declaration is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax;
    }

    private static SyntaxTokenList GetModifiers(MemberDeclarationSyntax declaration)
        => declaration switch
        {
            BaseTypeDeclarationSyntax type => type.Modifiers,
            BaseMethodDeclarationSyntax method => method.Modifiers,
            BasePropertyDeclarationSyntax property => property.Modifiers,
            BaseFieldDeclarationSyntax field => field.Modifiers,
            DelegateDeclarationSyntax @delegate => @delegate.Modifiers,
            _ => default,
        };

    private static bool IsGenerated(CompilationUnitSyntax root, string filePath)
        => filePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
            filePath.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
            root.GetLeadingTrivia().ToFullString().Contains("<auto-generated", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsInheritdoc(string text)
        => text.Contains("<inheritdoc", StringComparison.Ordinal);

    private static bool ContainsSummary(string text)
        => text.Contains("<summary>", StringComparison.Ordinal) &&
            text.Contains("</summary>", StringComparison.Ordinal);

    private static bool HasPhysicalThreeLineSummary(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var openingLine = -1;
        var closingLine = -1;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line == "/// <summary>")
            {
                openingLine = index;
            }
            else if (line == "/// </summary>")
            {
                closingLine = index;
                break;
            }
        }

        return openingLine >= 0 && closingLine >= openingLine + 2;
    }

    private static Location GetIdentifierLocation(MemberDeclarationSyntax declaration)
        => declaration switch
        {
            BaseTypeDeclarationSyntax type => type.Identifier.GetLocation(),
            DelegateDeclarationSyntax @delegate => @delegate.Identifier.GetLocation(),
            MethodDeclarationSyntax method => method.Identifier.GetLocation(),
            PropertyDeclarationSyntax property => property.Identifier.GetLocation(),
            EventDeclarationSyntax @event => @event.Identifier.GetLocation(),
            ConstructorDeclarationSyntax constructor => constructor.Identifier.GetLocation(),
            DestructorDeclarationSyntax destructor => destructor.Identifier.GetLocation(),
            EnumMemberDeclarationSyntax member => member.Identifier.GetLocation(),
            FieldDeclarationSyntax field => field.Declaration.Variables.First().Identifier.GetLocation(),
            EventFieldDeclarationSyntax eventField => eventField.Declaration.Variables.First().Identifier.GetLocation(),
            OperatorDeclarationSyntax @operator => @operator.OperatorToken.GetLocation(),
            ConversionOperatorDeclarationSyntax conversion => conversion.Type.GetLocation(),
            IndexerDeclarationSyntax indexer => indexer.ThisKeyword.GetLocation(),
            _ => declaration.GetLocation(),
        };

    private static string GetDeclarationName(MemberDeclarationSyntax declaration)
        => declaration switch
        {
            BaseTypeDeclarationSyntax type => type.Identifier.ValueText,
            DelegateDeclarationSyntax @delegate => @delegate.Identifier.ValueText,
            MethodDeclarationSyntax method => method.Identifier.ValueText,
            PropertyDeclarationSyntax property => property.Identifier.ValueText,
            EventDeclarationSyntax @event => @event.Identifier.ValueText,
            ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
            DestructorDeclarationSyntax destructor => destructor.Identifier.ValueText,
            EnumMemberDeclarationSyntax member => member.Identifier.ValueText,
            FieldDeclarationSyntax field => field.Declaration.Variables.First().Identifier.ValueText,
            EventFieldDeclarationSyntax eventField => eventField.Declaration.Variables.First().Identifier.ValueText,
            OperatorDeclarationSyntax @operator => $"operator {@operator.OperatorToken.ValueText}",
            ConversionOperatorDeclarationSyntax conversion => $"operator {conversion.Type}",
            IndexerDeclarationSyntax => "this[]",
            _ => declaration.Kind().ToString(),
        };
}
