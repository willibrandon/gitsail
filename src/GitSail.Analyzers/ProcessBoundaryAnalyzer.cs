using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using System.Collections.Immutable;

namespace GitSail.Analyzers;

/// <summary>
/// Prevents application code from bypassing the reviewed child-process boundaries.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ProcessBoundaryAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Identifies process API use outside an approved process boundary.
    /// </summary>
    public const string ProcessBoundaryId = "GITSAIL0004";

    private static readonly DiagnosticDescriptor s_processBoundary = new(
        ProcessBoundaryId,
        "Use an approved process boundary",
        "Process API '{0}' may only be used by an approved process boundary",
        "GitSail.Security",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Application processes must be created only by the reviewed child-process runners or fixed tool launcher.");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => [s_processBoundary];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static compilationContext =>
        {
            var assemblyName = compilationContext.Compilation.AssemblyName;
            if (assemblyName is not "git-tui" and not "GitSail.ToolLauncher")
            {
                return;
            }

            var processType = compilationContext.Compilation.GetTypeByMetadataName("System.Diagnostics.Process");
            var processStartInfoType = compilationContext.Compilation.GetTypeByMetadataName("System.Diagnostics.ProcessStartInfo");
            if (processType is null || processStartInfoType is null)
            {
                return;
            }

            compilationContext.RegisterOperationAction(
                operationContext => AnalyzeObjectCreation(
                    operationContext,
                    (IObjectCreationOperation)operationContext.Operation,
                    processType,
                    processStartInfoType),
                OperationKind.ObjectCreation);
            compilationContext.RegisterOperationAction(
                operationContext => AnalyzeInvocation(
                    operationContext,
                    (IInvocationOperation)operationContext.Operation,
                    processType),
                OperationKind.Invocation);
        });
    }

    private static void AnalyzeObjectCreation(
        OperationAnalysisContext context,
        IObjectCreationOperation operation,
        INamedTypeSymbol processType,
        INamedTypeSymbol processStartInfoType)
    {
        if (!SymbolEqualityComparer.Default.Equals(operation.Type, processType) &&
            !SymbolEqualityComparer.Default.Equals(operation.Type, processStartInfoType))
        {
            return;
        }

        ReportIfDisallowed(context, operation, operation.Type!.Name);
    }

    private static void AnalyzeInvocation(
        OperationAnalysisContext context,
        IInvocationOperation operation,
        INamedTypeSymbol processType)
    {
        if (!SymbolEqualityComparer.Default.Equals(operation.TargetMethod.ContainingType, processType) ||
            operation.TargetMethod.Name != nameof(System.Diagnostics.Process.Start))
        {
            return;
        }

        ReportIfDisallowed(context, operation, "Process.Start");
    }

    private static void ReportIfDisallowed(
        OperationAnalysisContext context,
        IOperation operation,
        string apiName)
    {
        if (IsApprovedBoundary(context.ContainingSymbol.ContainingType, context.Compilation.AssemblyName))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            s_processBoundary,
            operation.Syntax.GetLocation(),
            apiName));
    }

    private static bool IsApprovedBoundary(INamedTypeSymbol? containingType, string? assemblyName)
    {
        if (containingType is null)
        {
            return false;
        }

        if (assemblyName == "GitSail.ToolLauncher")
        {
            return containingType.ToDisplayString() == "GitSail.ToolLauncher.ToolLauncher";
        }

        return containingType.AllInterfaces.Any(static interfaceType =>
            interfaceType.ContainingNamespace.ToDisplayString() == "GitSail.Git.Execution" &&
            interfaceType.Name is "IChildProcessRunner" or "ITerminalChildProcessRunner");
    }
}
