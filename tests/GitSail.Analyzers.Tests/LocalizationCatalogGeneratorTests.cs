using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Collections.Immutable;

namespace GitSail.Analyzers.Tests;

/// <summary>
/// Verifies localization catalogs generate typed members and reject incompatible content.
/// </summary>
[TestClass]
public sealed class LocalizationCatalogGeneratorTests
{
    /// <summary>
    /// Verifies a valid English catalog emits property and plural-method members.
    /// </summary>
    [TestMethod]
    public void Run_WithValidEnglishCatalog_EmitsStronglyTypedMessages()
    {
        const string catalog = """
            {
              "locale": "en",
              "license": "MIT",
              "reviewed": true,
              "messages": [
                {
                  "id": "workspace.status.clean",
                  "text": "Working tree clean",
                  "description": "Clean status.",
                  "widthPolicy": "wrap"
                },
                {
                  "id": "operation.files.completed",
                  "description": "Completed file count.",
                  "arguments": { "count": "integer" },
                  "selector": { "kind": "plural", "argument": "count" },
                  "variants": {
                    "one": "{ $count } file",
                    "other": "{ $count } files"
                  },
                  "widthPolicy": "wrap"
                },
                {
                  "id": "operation.mode.label",
                  "description": "Selected operation mode.",
                  "arguments": { "mode": "string" },
                  "selector": { "kind": "select", "argument": "mode" },
                  "variants": {
                    "safe": "Safe mode",
                    "other": "Default mode"
                  },
                  "widthPolicy": "wrap"
                }
              ]
            }
            """;

        var result = RunGenerator(
            out var compilationDiagnostics,
            new InMemoryAdditionalText("/repo/locales/en.json", catalog));

        Assert.HasCount(0, result.Diagnostics);
        Assert.HasCount(
            0,
            compilationDiagnostics.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var generated = result.GeneratedTrees.Single().ToString();
        StringAssert.Contains(generated, "internal static string WorkspaceStatusClean");
        StringAssert.Contains(generated, "internal static string OperationFilesCompleted(long count)");
        StringAssert.Contains(generated, "PluralRules.GetCategory(locale, count)");
        StringAssert.Contains(
            generated,
            "(\"en-XA\", global::GitSail.Localization.PluralCategory.One)");
        StringAssert.Contains(generated, "internal static string OperationModeLabel(string mode)");
        StringAssert.Contains(generated, "(\"ar-XB\", \"safe\")");
    }

    /// <summary>
    /// Verifies a pattern cannot reference an undeclared named argument.
    /// </summary>
    [TestMethod]
    public void Run_WithUndeclaredArgument_ReportsInvalidCatalog()
    {
        const string catalog = """
            {
              "locale": "en",
              "license": "MIT",
              "reviewed": true,
              "messages": [
                {
                  "id": "operation.files.completed",
                  "text": "{ $count } files",
                  "description": "Completed file count.",
                  "widthPolicy": "wrap"
                }
              ]
            }
            """;

        var result = RunGenerator(
            out _,
            new InMemoryAdditionalText("/repo/locales/en.json", catalog));

        Assert.HasCount(1, result.Diagnostics);
        Assert.AreEqual(LocalizationDiagnosticDescriptors.InvalidCatalogId, result.Diagnostics[0].Id);
        StringAssert.Contains(result.Diagnostics[0].GetMessage(), "undeclared argument 'count'");
    }

    /// <summary>
    /// Verifies a translation cannot change an English named argument type.
    /// </summary>
    [TestMethod]
    public void Run_WithTranslationArgumentMismatch_ReportsIncompatibleCatalog()
    {
        const string english = """
            {
              "locale": "en",
              "license": "MIT",
              "reviewed": true,
              "messages": [
                {
                  "id": "operation.files.completed",
                  "text": "{ $count } files",
                  "description": "Completed file count.",
                  "arguments": { "count": "integer" },
                  "widthPolicy": "wrap"
                }
              ]
            }
            """;
        const string french = """
            {
              "locale": "fr",
              "license": "MIT",
              "reviewed": true,
              "messages": [
                {
                  "id": "operation.files.completed",
                  "text": "{ $count } fichiers",
                  "description": "Nombre de fichiers terminés.",
                  "arguments": { "count": "number" },
                  "widthPolicy": "wrap"
                }
              ]
            }
            """;

        var result = RunGenerator(
            out _,
            new InMemoryAdditionalText("/repo/locales/en.json", english),
            new InMemoryAdditionalText("/repo/locales/fr.json", french));

        Assert.ContainsSingle(result.Diagnostics.Where(static diagnostic =>
            diagnostic.Id == LocalizationDiagnosticDescriptors.IncompatibleCatalogId));
    }

    /// <summary>
    /// Verifies malformed JSON becomes one actionable generator diagnostic.
    /// </summary>
    [TestMethod]
    public void Run_WithMalformedJson_ReportsInvalidCatalog()
    {
        var result = RunGenerator(
            out _,
            new InMemoryAdditionalText("/repo/locales/en.json", "{ not-json"));

        Assert.ContainsSingle(result.Diagnostics);
        Assert.AreEqual(LocalizationDiagnosticDescriptors.InvalidCatalogId, result.Diagnostics[0].Id);
    }

    private static GeneratorDriverRunResult RunGenerator(
        out ImmutableArray<Diagnostic> compilationDiagnostics,
        params AdditionalText[] additionalTexts)
    {
        const string localizationRuntimeStubs = """
            namespace GitSail.Localization
            {
                internal enum PluralCategory
                {
                    Zero,
                    One,
                    Two,
                    Few,
                    Many,
                    Other,
                }

                internal static class PluralRules
                {
                    internal static PluralCategory GetCategory(string locale, long count)
                        => count == 1 ? PluralCategory.One : PluralCategory.Other;
                }

                internal static class LocaleNameNormalizer
                {
                    internal static string Normalize(string? localeName) => localeName ?? "en";
                }
            }
            """;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "LocalizationGeneratorTest",
            [CSharpSyntaxTree.ParseText(localizationRuntimeStubs, parseOptions)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new LocalizationCatalogGenerator().AsSourceGenerator()],
            additionalTexts: additionalTexts,
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var outputCompilation,
            out _);
        compilationDiagnostics = outputCompilation.GetDiagnostics();
        return driver.GetRunResult();
    }
}
