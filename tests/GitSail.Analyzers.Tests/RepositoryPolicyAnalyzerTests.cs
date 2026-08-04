using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;

namespace GitSail.Analyzers.Tests;

/// <summary>
/// Verifies source-file and XML-documentation policy diagnostics.
/// </summary>
[TestClass]
public sealed class RepositoryPolicyAnalyzerTests
{
    /// <summary>
    /// Verifies a second type in one source file is rejected.
    /// </summary>
    [TestMethod]
    public async Task AnalyzeAsync_WithTwoTypes_ReportsOneTypePerFile()
    {
        const string source = """
            /// <summary>
            /// Describes the first type.
            /// </summary>
            internal sealed class FirstType
            {
            }

            /// <summary>
            /// Describes the second type.
            /// </summary>
            internal sealed class SecondType
            {
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        Assert.HasCount(1, diagnostics);
        Assert.AreEqual(RepositoryPolicyAnalyzer.OneTypePerFileId, diagnostics[0].Id);
    }

    /// <summary>
    /// Verifies an undocumented internal member is rejected.
    /// </summary>
    [TestMethod]
    public async Task AnalyzeAsync_WithUndocumentedInternalMember_ReportsDocumentationRequired()
    {
        const string source = """
            /// <summary>
            /// Describes the containing type.
            /// </summary>
            internal sealed class DocumentedType
            {
                internal void MissingDocumentation()
                {
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        Assert.HasCount(1, diagnostics);
        Assert.AreEqual(RepositoryPolicyAnalyzer.DocumentationRequiredId, diagnostics[0].Id);
    }

    /// <summary>
    /// Verifies a summary written entirely on one line is rejected.
    /// </summary>
    [TestMethod]
    public async Task AnalyzeAsync_WithSingleLineSummary_ReportsThreeLineSummary()
    {
        const string source = """
            /// <summary>Describes the type.</summary>
            internal sealed class DocumentedType
            {
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        Assert.HasCount(1, diagnostics);
        Assert.AreEqual(RepositoryPolicyAnalyzer.ThreeLineSummaryId, diagnostics[0].Id);
    }

    /// <summary>
    /// Verifies three-line summaries and inherited documentation satisfy the policy.
    /// </summary>
    [TestMethod]
    public async Task AnalyzeAsync_WithRepositoryDocumentationStyle_ReportsNoDiagnostics()
    {
        const string source = """
            /// <summary>
            /// Describes the complete type.
            /// </summary>
            public sealed class DocumentedType
            {
                /// <inheritdoc />
                public override string ToString()
                {
                    return string.Empty;
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source);

        Assert.HasCount(0, diagnostics);
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Preview),
            path: "PolicySample.cs");
        var compilation = CSharpCompilation.Create(
            "PolicySample",
            [syntaxTree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var diagnostics = await compilation
            .WithAnalyzers([new RepositoryPolicyAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        return [.. diagnostics.OrderBy(static diagnostic => diagnostic.Location.SourceSpan.Start)];
    }
}
