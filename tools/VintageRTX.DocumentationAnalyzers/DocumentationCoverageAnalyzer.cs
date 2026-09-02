using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace VintageRTX.DocumentationAnalyzers;

/// <summary>
/// Enforces an IntelliSense contract on every named authored declaration, regardless of
/// accessibility. Compiler warning CS1591 only covers externally visible API and therefore
/// cannot prove the private implementation coverage required by this project.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DocumentationCoverageAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Identifies a declaration whose leading trivia contains no XML documentation structure.
    /// The diagnostic is an error so an undocumented change cannot silently lower coverage.
    /// </summary>
    public const string DiagnosticId = "VRTXDOC001";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Declaration must have IntelliSense documentation",
        "'{0}' has no XML documentation comment",
        "Documentation",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "All named VintageRTX declarations, including private implementation members, require XML documentation.");

    private static readonly DiagnosticDescriptor CoverageSummary = new(
        "VRTXDOC000",
        "IntelliSense documentation coverage",
        "IntelliSense documentation coverage: {0}/{1} declarations ({2}%)",
        "Documentation",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Exact documented/total declaration metric for the current compilation.");

    /// <summary>
    /// Returns the single coverage rule emitted by this analyzer.
    /// </summary>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule, CoverageSummary);

    /// <summary>
    /// Registers syntax-only actions so coverage remains deterministic even when Vintage Story
    /// runtime references are unavailable to an editor design-time build.
    /// </summary>
    /// <param name="context">Roslyn registration context for the current compilation.</param>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        SyntaxKind[] declarationKinds =
        [
            SyntaxKind.ClassDeclaration,
            SyntaxKind.StructDeclaration,
            SyntaxKind.InterfaceDeclaration,
            SyntaxKind.RecordDeclaration,
            SyntaxKind.RecordStructDeclaration,
            SyntaxKind.EnumDeclaration,
            SyntaxKind.DelegateDeclaration,
            SyntaxKind.ConstructorDeclaration,
            SyntaxKind.DestructorDeclaration,
            SyntaxKind.MethodDeclaration,
            SyntaxKind.OperatorDeclaration,
            SyntaxKind.ConversionOperatorDeclaration,
            SyntaxKind.PropertyDeclaration,
            SyntaxKind.IndexerDeclaration,
            SyntaxKind.FieldDeclaration,
            SyntaxKind.EventDeclaration,
            SyntaxKind.EventFieldDeclaration,
            SyntaxKind.EnumMemberDeclaration
        ];

        context.RegisterCompilationStartAction(startContext =>
        {
            int documentedCount = 0;
            int totalCount = 0;
            startContext.RegisterSyntaxNodeAction(nodeContext =>
            {
                DocumentationState state = AnalyzeDeclaration(nodeContext);
                if (state is DocumentationState.Excluded)
                {
                    return;
                }

                Interlocked.Increment(ref totalCount);
                if (state is DocumentationState.Documented)
                {
                    Interlocked.Increment(ref documentedCount);
                }
            }, declarationKinds);
            startContext.RegisterCompilationEndAction(endContext =>
            {
                double percentage = totalCount == 0 ? 100.0 : documentedCount * 100.0 / totalCount;
                endContext.ReportDiagnostic(Diagnostic.Create(
                    CoverageSummary,
                    Location.None,
                    documentedCount,
                    totalCount,
                    percentage.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)));
            });
        });
    }

    /// <summary>
    /// Reports every declaration lacking a structured <c>///</c> or <c>/** */</c> XML comment.
    /// Multi-variable fields are intentionally counted as one declaration because C# attaches
    /// documentation to the field declaration rather than to each variable declarator.
    /// </summary>
    /// <param name="context">Syntax node and diagnostic sink supplied by Roslyn.</param>
    private static DocumentationState AnalyzeDeclaration(SyntaxNodeAnalysisContext context)
    {
        if (context.Node.SyntaxTree.FilePath.Contains("\\obj\\") ||
            IsCompilerGenerated(context.Node) ||
            !IsDocumentationContract(context.Node))
        {
            return DocumentationState.Excluded;
        }

        SyntaxTriviaList trivia = context.Node.GetLeadingTrivia();
        bool documented = trivia.Any(item => item.HasStructure &&
            item.GetStructure() is DocumentationCommentTriviaSyntax);

        if (!documented)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, GetIdentifier(context.Node).GetLocation(), GetName(context.Node)));
            return DocumentationState.Missing;
        }

        return DocumentationState.Documented;
    }

    /// <summary>
    /// Defines the denominator used by the project: all named types and behavior-bearing members
    /// at every accessibility, plus enum values, events, constants, and non-private fields. Mutable
    /// private storage is excluded because it is not an IntelliSense contract; its invariants belong
    /// on the methods and properties that own those transitions.
    /// </summary>
    /// <param name="node">Candidate syntax declaration.</param>
    /// <returns><see langword="true"/> when the declaration contributes to coverage.</returns>
    private static bool IsDocumentationContract(SyntaxNode node)
    {
        if (node is not FieldDeclarationSyntax field)
        {
            return true;
        }

        bool isConstant = field.Modifiers.Any(SyntaxKind.ConstKeyword);
        bool explicitlyPrivate = field.Modifiers.Any(SyntaxKind.PrivateKeyword);
        bool hasVisibleModifier = field.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PublicKeyword) ||
            modifier.IsKind(SyntaxKind.InternalKeyword) || modifier.IsKind(SyntaxKind.ProtectedKeyword));
        return isConstant || hasVisibleModifier || !explicitlyPrivate && field.Parent is InterfaceDeclarationSyntax;
    }

    /// <summary>
    /// Prevents declarations explicitly marked as generated from polluting the authored-code
    /// denominator while retaining ordinary private and nested declarations.
    /// </summary>
    /// <param name="node">Candidate declaration.</param>
    /// <returns><see langword="true"/> only for declarations carrying a generated-code attribute.</returns>
    private static bool IsCompilerGenerated(SyntaxNode node)
    {
        return node is MemberDeclarationSyntax member && member.AttributeLists
            .SelectMany(list => list.Attributes)
            .Any(attribute => attribute.Name.ToString().EndsWith("GeneratedCode", System.StringComparison.Ordinal) ||
                              attribute.Name.ToString().EndsWith("GeneratedCodeAttribute", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// Selects the narrow identifier span used by Visual Studio to place the actionable error.
    /// </summary>
    /// <param name="node">Declaration being diagnosed.</param>
    /// <returns>The identifier token, or the declaration's first token for operator forms.</returns>
    private static SyntaxToken GetIdentifier(SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax type => type.Identifier,
        DelegateDeclarationSyntax declaration => declaration.Identifier,
        ConstructorDeclarationSyntax declaration => declaration.Identifier,
        DestructorDeclarationSyntax declaration => declaration.Identifier,
        MethodDeclarationSyntax declaration => declaration.Identifier,
        PropertyDeclarationSyntax declaration => declaration.Identifier,
        VariableDeclarationSyntax declaration => declaration.Variables.First().Identifier,
        FieldDeclarationSyntax declaration => declaration.Declaration.Variables.First().Identifier,
        EventFieldDeclarationSyntax declaration => declaration.Declaration.Variables.First().Identifier,
        EventDeclarationSyntax declaration => declaration.Identifier,
        EnumMemberDeclarationSyntax declaration => declaration.Identifier,
        _ => node.GetFirstToken()
    };

    /// <summary>
    /// Produces the stable declaration label shown in build and Error List output.
    /// </summary>
    /// <param name="node">Declaration being diagnosed.</param>
    /// <returns>A source-level member name suitable for developer navigation.</returns>
    private static string GetName(SyntaxNode node) => GetIdentifier(node).ValueText;

    /// <summary>Internal tri-state used to keep diagnostic and metric denominators identical.</summary>
    private enum DocumentationState
    {
        /// <summary>The declaration is generated or non-contract storage and is not counted.</summary>
        Excluded,
        /// <summary>The declaration contributes to the denominator but has no XML comment.</summary>
        Missing,
        /// <summary>The declaration contributes to both numerator and denominator.</summary>
        Documented
    }
}
