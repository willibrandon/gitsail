using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using System.Diagnostics;

namespace GitSail.Analyzers.Tests;

/// <summary>
/// Verifies the application process boundary cannot be bypassed.
/// </summary>
[TestClass]
public sealed class ProcessBoundaryAnalyzerTests
{
    /// <summary>
    /// Verifies ordinary application code cannot start a process.
    /// </summary>
    [TestMethod]
    public async Task AnalyzeAsync_WithProcessStartOutsideRunner_ReportsProcessBoundary()
    {
        const string source = """
            using System.Diagnostics;

            namespace GitSail.Features;

            internal sealed class UnsafeService
            {
                internal void Run()
                {
                    Process.Start("git");
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source, "git-tui");

        Assert.HasCount(1, diagnostics);
        Assert.AreEqual(ProcessBoundaryAnalyzer.ProcessBoundaryId, diagnostics[0].Id);
    }

    /// <summary>
    /// Verifies ordinary application code cannot prepare an unreviewed process invocation.
    /// </summary>
    [TestMethod]
    public async Task AnalyzeAsync_WithProcessStartInfoOutsideRunner_ReportsProcessBoundary()
    {
        const string source = """
            using System.Diagnostics;

            namespace GitSail.Features;

            internal sealed class UnsafeService
            {
                internal ProcessStartInfo Create()
                {
                    return new ProcessStartInfo("git");
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source, "git-tui");

        Assert.HasCount(1, diagnostics);
        Assert.AreEqual(ProcessBoundaryAnalyzer.ProcessBoundaryId, diagnostics[0].Id);
    }

    /// <summary>
    /// Verifies an application runner may construct and start a process.
    /// </summary>
    [TestMethod]
    public async Task AnalyzeAsync_WithApprovedApplicationRunner_ReportsNoDiagnostics()
    {
        const string source = """
            using System.Diagnostics;

            namespace GitSail.Git.Execution;

            internal interface IChildProcessRunner
            {
            }

            internal sealed class ChildProcessRunner : IChildProcessRunner
            {
                internal void Run()
                {
                    using var process = new Process
                    {
                        StartInfo = new ProcessStartInfo("git"),
                    };
                    process.Start();
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source, "git-tui");

        Assert.HasCount(0, diagnostics);
    }

    /// <summary>
    /// Verifies the fixed Windows tool adapter may start its co-located payload.
    /// </summary>
    [TestMethod]
    public async Task AnalyzeAsync_WithApprovedToolLauncher_ReportsNoDiagnostics()
    {
        const string source = """
            using System.Diagnostics;

            namespace GitSail.ToolLauncher;

            internal static class ToolLauncher
            {
                internal static void Run()
                {
                    Process.Start(new ProcessStartInfo("git-tui.exe"));
                }
            }
            """;

        var diagnostics = await AnalyzeAsync(source, "GitSail.ToolLauncher");

        Assert.HasCount(0, diagnostics);
    }

    /// <summary>
    /// Verifies repository tools are outside the shipped application's process boundary.
    /// </summary>
    [TestMethod]
    public async Task AnalyzeAsync_WithBuildToolAssembly_ReportsNoDiagnostics()
    {
        const string source = """
            using System.Diagnostics;

            Process.Start("git");
            """;

        var diagnostics = await AnalyzeAsync(source, "GitSail.BuildTools");

        Assert.HasCount(0, diagnostics);
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(
        string source,
        string assemblyName)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Preview),
            path: "ProcessBoundarySample.cs");
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [syntaxTree],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Process).Assembly.Location),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var diagnostics = await compilation
            .WithAnalyzers([new ProcessBoundaryAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        return [.. diagnostics.OrderBy(static diagnostic => diagnostic.Location.SourceSpan.Start)];
    }
}
