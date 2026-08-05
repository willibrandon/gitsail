using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Runtime.Serialization;
using System.Text;

namespace GitSail.Analyzers;

/// <summary>
/// Generates the strongly typed GitSail localization table from validated JSON catalogs.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class LocalizationCatalogGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var catalogs = context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                Path.GetDirectoryName(file.Path)?.EndsWith("locales", StringComparison.OrdinalIgnoreCase) == true)
            .Select(static (file, cancellationToken) => new LocalizationCatalogInput(
                file.Path,
                file.GetText(cancellationToken) ?? SourceText.From(string.Empty, Encoding.UTF8)))
            .Collect();
        var requireCompleteLocales = context.AnalyzerConfigOptionsProvider.Select(
            static (options, _) =>
                options.GlobalOptions.TryGetValue(
                    "build_property.GitSailRequireCompleteLocales",
                    out var value) &&
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
        context.RegisterSourceOutput(
            catalogs.Combine(requireCompleteLocales),
            static (productionContext, input) =>
                Generate(productionContext, input.Left, input.Right));
    }

    private static void Generate(
        SourceProductionContext context,
        ImmutableArray<LocalizationCatalogInput> inputs,
        bool requireCompleteLocales)
    {
        if (inputs.IsDefaultOrEmpty)
        {
            return;
        }

        var catalogs = ImmutableArray.CreateBuilder<LocalizationCatalog>(inputs.Length);
        foreach (var input in inputs)
        {
            try
            {
                catalogs.Add(new LocalizationCatalog(input.Path, LocalizationCatalogParser.Parse(input)));
            }
            catch (SerializationException exception)
            {
                ReportParseFailure(context, input.Path, exception.Message);
            }
            catch (InvalidDataException exception)
            {
                ReportParseFailure(context, input.Path, exception.Message);
            }
            catch (ArgumentException exception)
            {
                ReportParseFailure(context, input.Path, exception.Message);
            }
        }

        var parsedCatalogs = catalogs.ToImmutable();
        if (parsedCatalogs.Length != inputs.Length ||
            !LocalizationCatalogValidator.Validate(parsedCatalogs, context, requireCompleteLocales))
        {
            return;
        }

        context.AddSource(
            "AppMessages.g.cs",
            SourceText.From(LocalizationSourceEmitter.Emit(parsedCatalogs), Encoding.UTF8));
    }

    private static void ReportParseFailure(SourceProductionContext context, string path, string message)
        => context.ReportDiagnostic(Diagnostic.Create(
            LocalizationDiagnosticDescriptors.InvalidCatalog,
            Location.None,
            path,
            message));
}
