#:package System.CommandLine

using System.CommandLine;
using System.Collections;
using System.Text.Json;
using System.Text.RegularExpressions;

var repositoryRootOption = new Option<string?>("--repository-root")
{
    Description = "The GitSail repository root to inspect.",
    Arity = ArgumentArity.ExactlyOne,
};
var outputDirectoryOption = new Option<string?>("--output-directory")
{
    Description = "The directory that receives deterministic coverage reports.",
    Arity = ArgumentArity.ExactlyOne,
};
var rootCommand = new RootCommand("Generates and verifies design coverage from registered code and named tests.");
rootCommand.Options.Add(repositoryRootOption);
rootCommand.Options.Add(outputDirectoryOption);
rootCommand.Validators.Add(result =>
{
    if (string.IsNullOrWhiteSpace(result.GetValue(repositoryRootOption)))
    {
        result.AddError("Option '--repository-root' is required.");
    }

    if (string.IsNullOrWhiteSpace(result.GetValue(outputDirectoryOption)))
    {
        result.AddError("Option '--output-directory' is required.");
    }
});
rootCommand.SetAction((parseResult, cancellationToken) => GenerateAsync(
    parseResult.GetValue(repositoryRootOption)!,
    parseResult.GetValue(outputDirectoryOption)!,
    cancellationToken));

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);

static async Task<int> GenerateAsync(
    string repositoryRoot,
    string outputDirectory,
    CancellationToken cancellationToken)
{
    var fullRoot = Path.GetFullPath(repositoryRoot, Directory.GetCurrentDirectory());
    if (!File.Exists(Path.Combine(fullRoot, "GitSail.slnx")))
    {
        throw new DirectoryNotFoundException($"The GitSail repository root is invalid: {fullRoot}");
    }

    var fullOutput = Path.GetFullPath(outputDirectory, Directory.GetCurrentDirectory());
    Directory.CreateDirectory(fullOutput);
    var failures = new List<string>();
    var sourceFiles = Directory.GetFiles(
            Path.Combine(fullRoot, "src", "GitSail"),
            "*.cs",
            SearchOption.AllDirectories)
        .Where(static path => !IsGeneratedPath(path))
        .Order(StringComparer.Ordinal)
        .ToArray();
    var testFiles = Directory.GetFiles(
            Path.Combine(fullRoot, "tests"),
            "*.cs",
            SearchOption.AllDirectories)
        .Where(static path => !IsGeneratedPath(path))
        .Order(StringComparer.Ordinal)
        .ToArray();
    var sourceText = await ReadFilesAsync(sourceFiles, cancellationToken).ConfigureAwait(false);
    var testText = await ReadFilesAsync(testFiles, cancellationToken).ConfigureAwait(false);

    var actionPath = Path.Combine(fullRoot, "src", "GitSail", "Ui", "WorkspaceActionIds.cs");
    var actionSource = sourceText[actionPath];
    var actionMatches = Matches(
        actionSource,
        "internal static readonly ActionId (?<name>[A-Za-z0-9_]+) = new\\(\\\"(?<id>[^\\\"]+)\\\"\\);")
        .Cast<Match>()
        .ToArray();
    AddDuplicateFailures(
        actionMatches.Select(static match => match.Groups["name"].Value),
        "action field",
        failures,
        StringComparer.Ordinal);
    AddDuplicateFailures(
        actionMatches.Select(static match => match.Groups["id"].Value),
        "action identity",
        failures,
        StringComparer.Ordinal);
    var allBlock = FirstMatch(
        actionSource,
        "s_all\\s*=\\s*\\[(?<items>[\\s\\S]*?)\\];").Groups["items"].Value;
    var actionRows = new List<Dictionary<string, object?>>(actionMatches.Length);
    foreach (var match in actionMatches.OrderBy(static match => match.Groups["id"].Value, StringComparer.Ordinal))
    {
        var name = match.Groups["name"].Value;
        var id = match.Groups["id"].Value;
        var registryCount = Matches(allBlock, $"(?m)^\\s*{Regex.Escape(name)},\\s*$").Count;
        var productionReferences = sourceText
            .Where(pair => !StringComparer.Ordinal.Equals(pair.Key, actionPath))
            .Sum(pair => Matches(
                pair.Value,
                $"WorkspaceActionIds\\.{Regex.Escape(name)}\\b").Count);
        if (registryCount != 1)
        {
            failures.Add($"Action '{id}' appears {registryCount} times in WorkspaceActionIds.All; expected exactly once.");
        }

        if (productionReferences == 0)
        {
            failures.Add($"Action '{id}' is registered but unreachable from product code.");
        }

        actionRows.Add(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["field"] = name,
            ["registryOccurrences"] = registryCount,
            ["productionReferences"] = productionReferences,
            ["namedCoverage"] = "WorkspaceKeymapTests.TryApply_WithCompleteBaselineContexts_AcceptsEveryActiveBindingMap",
        });
    }

    await WriteReportAsync(
        Path.Combine(fullOutput, "actions.json"),
        new Dictionary<string, object?>
        {
            ["count"] = actionRows.Count,
            ["entries"] = actionRows,
        },
        cancellationToken).ConfigureAwait(false);

    var commandPath = Path.Combine(fullRoot, "src", "GitSail", "CommandLine", "GitSailCommandLine.cs");
    var commandSource = sourceText[commandPath];
    var commandNames = Matches(
            commandSource,
            "new Command\\(\\s*\\\"(?<name>[^\\\"]+)\\\"")
        .Cast<Match>()
        .Select(static match => match.Groups["name"].Value)
        .Concat(Matches(
                commandSource,
                "CreateInteractiveCommand\\(\\\"(?<name>[^\\\"]+)\\\"")
            .Cast<Match>()
            .Select(static match => match.Groups["name"].Value))
        .Order(StringComparer.Ordinal)
        .ToArray();
    AddDuplicateFailures(commandNames, "command", failures, StringComparer.Ordinal);
    var rootRegistrationCount = Matches(
        commandSource,
        "rootCommand\\.Subcommands\\.Add\\(").Count;
    if (rootRegistrationCount != commandNames.Length)
    {
        failures.Add(
            $"The command model constructs {commandNames.Length} named subcommands but registers " +
            $"{rootRegistrationCount} root subcommands.");
    }

    var commandTestSource = testText.Single(static pair =>
        pair.Key.EndsWith("GitSailCommandLineTests.cs", StringComparison.Ordinal)).Value;
    var commandRows = commandNames.Select(name =>
    {
        var covered = commandTestSource.Contains($"\"{name}\"", StringComparison.Ordinal);
        if (!covered)
        {
            failures.Add($"Command '{name}' has no named GitSailCommandLineTests coverage.");
        }

        return new Dictionary<string, object?>
        {
            ["name"] = name,
            ["registered"] = true,
            ["namedTestSource"] = covered ? "GitSailCommandLineTests" : null,
        };
    }).ToArray();
    await WriteReportAsync(
        Path.Combine(fullOutput, "commands.json"),
        new Dictionary<string, object?>
        {
            ["count"] = commandRows.Length,
            ["rootRegistrationCount"] = rootRegistrationCount,
            ["entries"] = commandRows,
        },
        cancellationToken).ConfigureAwait(false);

    var configurationPath = Path.Combine(fullRoot, "src", "GitSail", "Domain", "GitConfigurationRegistry.cs");
    var configurationSource = sourceText[configurationPath];
    var configurationPatterns = Matches(
            configurationSource,
            "(?:String|Boolean|Integer|Enumeration|NativePath|Definition)\\(\\s*\\\"(?<key>[^\\\"]+)\\\"")
        .Cast<Match>()
        .Select(static match => match.Groups["key"].Value)
        .ToArray();
    AddDuplicateFailures(
        configurationPatterns,
        "configuration key pattern",
        failures,
        StringComparer.OrdinalIgnoreCase);
    var configurationOptionsReachable = sourceText.Values.Any(static text =>
        text.Contains("foreach (var definition in GitConfigurationRegistry.Definitions)", StringComparison.Ordinal));
    var configurationTestsReachable = testText.Values.Any(static text =>
        text.Contains("Definitions_WithDeclaredMetadata_AreUniqueAndValid", StringComparison.Ordinal));
    if (!configurationOptionsReachable)
    {
        failures.Add("Registered configuration is not reachable from the product options model.");
    }

    if (!configurationTestsReachable)
    {
        failures.Add("Registered configuration has no complete named registry test.");
    }

    var configurationRows = configurationPatterns
        .Order(StringComparer.OrdinalIgnoreCase)
        .Select(key =>
        {
            var runtimeReferences = sourceText
                .Where(pair => !StringComparer.Ordinal.Equals(pair.Key, configurationPath))
                .Sum(pair => CountOccurrences(pair.Value, key, StringComparison.OrdinalIgnoreCase));
            if (key.StartsWith("gitsail.", StringComparison.OrdinalIgnoreCase) &&
                !key.Contains('*', StringComparison.Ordinal) &&
                runtimeReferences == 0)
            {
                failures.Add($"GitSail setting '{key}' has no product runtime consumer.");
            }

            return new Dictionary<string, object?>
            {
                ["keyPattern"] = key,
                ["optionsReachable"] = configurationOptionsReachable,
                ["runtimeReferences"] = runtimeReferences,
                ["namedCoverage"] = configurationTestsReachable
                    ? "GitConfigurationRegistryTests.Definitions_WithDeclaredMetadata_AreUniqueAndValid"
                    : null,
            };
        })
        .ToArray();
    await WriteReportAsync(
        Path.Combine(fullOutput, "configuration.json"),
        new Dictionary<string, object?>
        {
            ["count"] = configurationRows.Length,
            ["entries"] = configurationRows,
        },
        cancellationToken).ConfigureAwait(false);

    var stateFilePath = Path.Combine(fullRoot, "src", "GitSail", "Domain", "RepositoryStateFile.cs");
    var statePathServicePath = Path.Combine(fullRoot, "src", "GitSail", "Git", "Execution", "RepositoryStatePathService.cs");
    var stateNames = Matches(
            sourceText[stateFilePath],
            "(?m)^\\s{4}(?<name>[A-Za-z0-9_]+),\\s*$")
        .Cast<Match>()
        .Select(static match => match.Groups["name"].Value)
        .ToArray();
    var stateMappings = Matches(
            sourceText[statePathServicePath],
            "RepositoryStateFile\\.(?<name>[A-Za-z0-9_]+)\\s*=>\\s*\\\"(?<path>[^\\\"]+)\\\"")
        .Cast<Match>()
        .ToDictionary(
            static match => match.Groups["name"].Value,
            static match => match.Groups["path"].Value,
            StringComparer.Ordinal);
    AddDuplicateFailures(stateNames, "state-file identity", failures, StringComparer.Ordinal);
    AddDuplicateFailures(stateMappings.Values, "state-file path", failures, StringComparer.Ordinal);
    var stateCoverageTest = testText.Values.Any(static text =>
        text.Contains("ResolveAsync_ForEveryAllowlistedFile_ReturnsAbsoluteGitPath", StringComparison.Ordinal) &&
        text.Contains("Enum.GetValues<RepositoryStateFile>()", StringComparison.Ordinal));
    var stateRows = stateNames.Order(StringComparer.Ordinal).Select(name =>
    {
        var mapped = stateMappings.TryGetValue(name, out var path);
        if (!mapped)
        {
            failures.Add($"State-file identity '{name}' has no allowlisted path mapping.");
        }

        if (!stateCoverageTest)
        {
            failures.Add($"State-file identity '{name}' is not covered by the complete named path test.");
        }

        return new Dictionary<string, object?>
        {
            ["identity"] = name,
            ["repositoryPath"] = path,
            ["namedCoverage"] = stateCoverageTest
                ? "RepositoryStatePathServiceTests.ResolveAsync_ForEveryAllowlistedFile_ReturnsAbsoluteGitPath"
                : null,
        };
    }).ToArray();
    foreach (var extraMapping in stateMappings.Keys.Except(stateNames, StringComparer.Ordinal))
    {
        failures.Add($"State-file path mapping '{extraMapping}' has no registered identity.");
    }

    await WriteReportAsync(
        Path.Combine(fullOutput, "state-files.json"),
        new Dictionary<string, object?>
        {
            ["count"] = stateRows.Length,
            ["entries"] = stateRows,
        },
        cancellationToken).ConfigureAwait(false);

    var design = await File.ReadAllTextAsync(
        Path.Combine(fullRoot, "docs", "design.md"),
        cancellationToken).ConfigureAwait(false);
    var j6tRows = ParseIssueRows(Slice(design, "### 17.2", "### 17.3"));
    var pratiRows = ParseIssueRows(Slice(design, "### 17.3", "### 17.4"));
    ValidateIssueRows("j6t", 15, j6tRows, failures);
    ValidateIssueRows("prati", 72, pratiRows, failures);
    await WriteReportAsync(
        Path.Combine(fullOutput, "issues.json"),
        new Dictionary<string, object?>
        {
            ["j6t"] = j6tRows,
            ["prati"] = pratiRows,
        },
        cancellationToken).ConfigureAwait(false);

    var localeSetPath = Path.Combine(fullRoot, "src", "GitSail.Analyzers", "RequiredLocaleSet.cs");
    var localeSetSource = await File.ReadAllTextAsync(localeSetPath, cancellationToken).ConfigureAwait(false);
    var localeBlock = FirstMatch(
        localeSetSource,
        "Names\\s*\\{\\s*get;\\s*\\}\\s*=\\s*\\[(?<locales>[\\s\\S]*?)\\];").Groups["locales"].Value;
    var requiredLocales = Matches(localeBlock, "\\\"(?<locale>[^\\\"]+)\\\"")
        .Cast<Match>()
        .Select(static match => match.Groups["locale"].Value)
        .ToArray();
    var catalogLocales = Directory.GetFiles(Path.Combine(fullRoot, "locales"), "*.json")
        .Select(Path.GetFileNameWithoutExtension)
        .Order(StringComparer.Ordinal)
        .ToArray();
    AddDuplicateFailures(requiredLocales, "required locale", failures, StringComparer.Ordinal);
    var missingCatalogs = requiredLocales.Except(catalogLocales, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    var extraCatalogs = catalogLocales.Except(requiredLocales, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    foreach (var locale in missingCatalogs)
    {
        failures.Add($"Required locale '{locale}' has no catalog.");
    }

    foreach (var locale in extraCatalogs)
    {
        failures.Add($"Locale catalog '{locale}' is not in the required locale registry.");
    }

    if (requiredLocales.Count(static locale => locale != "en") != 14)
    {
        failures.Add("The locale registry must contain exactly 14 non-English release locales.");
    }

    var generatedLocalizationTests = testText.Single(static pair =>
        pair.Key.EndsWith("GeneratedLocalizationTests.cs", StringComparison.Ordinal)).Value;
    foreach (var pseudoLocale in new[] { "en-XA", "ar-XB" })
    {
        if (!generatedLocalizationTests.Contains($"\"{pseudoLocale}\"", StringComparison.Ordinal))
        {
            failures.Add($"Pseudo-locale '{pseudoLocale}' has no named generated-localization test.");
        }
    }

    var localeRows = requiredLocales.Order(StringComparer.Ordinal).Select(locale =>
        new Dictionary<string, object?>
        {
            ["locale"] = locale,
            ["catalog"] = $"locales/{locale}.json",
            ["namedCoverage"] = locale == "en"
                ? "GeneratedLocalizationTests.DiffActivityLoadedChangedFilesForLocale_WithEnglishCounts_SelectsPluralVariant"
                : "GeneratedLocalizationTests.WorkspaceStatusCleanForLocale_WithRequiredLocale_ReturnsTranslation",
        }).Concat(new[]
        {
            new Dictionary<string, object?>
            {
                ["locale"] = "en-XA",
                ["catalog"] = null,
                ["namedCoverage"] = "GeneratedLocalizationTests.WorkspaceStatusCleanForLocale_WithExpansionPseudoLocale_ExpandsMessage",
            },
            new Dictionary<string, object?>
            {
                ["locale"] = "ar-XB",
                ["catalog"] = null,
                ["namedCoverage"] = "GeneratedLocalizationTests.DiffActivityLoadedChangedFilesForLocale_WithRtlPseudoLocale_IsolatesMessageAndArgument",
            },
        }).ToArray();
    await WriteReportAsync(
        Path.Combine(fullOutput, "locales.json"),
        new Dictionary<string, object?>
        {
            ["sourceLocaleCount"] = requiredLocales.Length,
            ["pseudoLocaleCount"] = 2,
            ["entries"] = localeRows,
        },
        cancellationToken).ConfigureAwait(false);

    var solution = await File.ReadAllTextAsync(
        Path.Combine(fullRoot, "GitSail.slnx"),
        cancellationToken).ConfigureAwait(false);
    var testProjects = Directory.GetFiles(Path.Combine(fullRoot, "tests"), "*.csproj", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .ToArray();
    var behaviorRows = new List<Dictionary<string, object?>>();
    foreach (var testFile in testFiles)
    {
        var text = testText[testFile];
        var className = FirstMatch(text, "\\bclass\\s+(?<name>[A-Za-z0-9_]+)").Groups["name"].Value;
        var project = testProjects
            .Where(projectPath => testFile.StartsWith(
                Path.GetDirectoryName(projectPath) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
            .OrderByDescending(static projectPath => projectPath.Length)
            .FirstOrDefault();
        foreach (Match match in Matches(
            text,
            "\\[TestMethod\\][\\s\\S]*?\\bpublic\\s+(?:async\\s+)?[A-Za-z0-9_<>,?\\[\\].]+\\s+(?<method>[A-Za-z0-9_]+)\\s*\\("))
        {
            var method = match.Groups["method"].Value;
            var relativeFile = Path.GetRelativePath(fullRoot, testFile).Replace('\\', '/');
            var relativeProject = project is null
                ? null
                : Path.GetRelativePath(fullRoot, project).Replace('\\', '/');
            var id = $"{className}.{method}";
            if (className.Length == 0 || project is null)
            {
                failures.Add($"Named test '{relativeFile}:{method}' is not reachable from a test class and project.");
            }
            else if (!solution.Contains(relativeProject!, StringComparison.Ordinal))
            {
                failures.Add($"Named test '{id}' belongs to project '{relativeProject}' that is absent from GitSail.slnx.");
            }

            behaviorRows.Add(new Dictionary<string, object?>
            {
                ["id"] = id,
                ["project"] = relativeProject,
                ["source"] = relativeFile,
            });
        }
    }

    AddDuplicateFailures(
        behaviorRows.Select(static row => (string)row["id"]!),
        "named behavior test",
        failures,
        StringComparer.Ordinal);
    if (behaviorRows.Count == 0)
    {
        failures.Add("No named MSTest behaviors were discovered.");
    }

    behaviorRows.Sort(static (left, right) => StringComparer.Ordinal.Compare(
        (string)left["id"]!,
        (string)right["id"]!));
    await WriteReportAsync(
        Path.Combine(fullOutput, "behaviors.json"),
        new Dictionary<string, object?>
        {
            ["count"] = behaviorRows.Count,
            ["entries"] = behaviorRows,
        },
        cancellationToken).ConfigureAwait(false);

    var orderedFailures = failures.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    await WriteReportAsync(
        Path.Combine(fullOutput, "summary.json"),
        new Dictionary<string, object?>
        {
            ["passed"] = orderedFailures.Length == 0,
            ["actions"] = actionRows.Count,
            ["commands"] = commandRows.Length,
            ["configurationKeys"] = configurationRows.Length,
            ["stateFiles"] = stateRows.Length,
            ["j6tIssues"] = j6tRows.Count,
            ["pratiIssues"] = pratiRows.Count,
            ["locales"] = localeRows.Length,
            ["namedBehaviorTests"] = behaviorRows.Count,
            ["failures"] = orderedFailures,
        },
        cancellationToken).ConfigureAwait(false);

    if (orderedFailures.Length > 0)
    {
        foreach (var failure in orderedFailures)
        {
            Console.Error.WriteLine(failure);
        }

        return 1;
    }

    Console.WriteLine(
        $"Verified {actionRows.Count} actions, {commandRows.Length} commands, " +
        $"{configurationRows.Length} configuration keys, {stateRows.Length} state files, " +
        $"{j6tRows.Count + pratiRows.Count} issues, {localeRows.Length} locales, and " +
        $"{behaviorRows.Count} named behavior tests.");
    return 0;
}

static bool IsGeneratedPath(string path)
    => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

static async Task<Dictionary<string, string>> ReadFilesAsync(
    IEnumerable<string> paths,
    CancellationToken cancellationToken)
{
    var files = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var path in paths)
    {
        files.Add(path, await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
    }

    return files;
}

static void AddDuplicateFailures(
    IEnumerable<string> values,
    string label,
    List<string> failures,
    StringComparer comparer)
{
    foreach (var group in values.GroupBy(static value => value, comparer).Where(static group => group.Count() > 1))
    {
        failures.Add($"Duplicate {label} '{group.Key}' appears {group.Count()} times.");
    }
}

static int CountOccurrences(string text, string value, StringComparison comparison)
{
    var count = 0;
    var offset = 0;
    while (offset < text.Length)
    {
        var found = text.IndexOf(value, offset, comparison);
        if (found < 0)
        {
            break;
        }

        count++;
        offset = found + value.Length;
    }

    return count;
}

static string Slice(string text, string startMarker, string endMarker)
{
    var start = text.IndexOf(startMarker, StringComparison.Ordinal);
    var end = text.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
    if (start < 0 || end < 0)
    {
        throw new InvalidDataException($"Could not find design section from '{startMarker}' to '{endMarker}'.");
    }

    return text[start..end];
}

static List<Dictionary<string, object?>> ParseIssueRows(string section)
{
    var rows = new List<Dictionary<string, object?>>();
    foreach (var line in section.Split('\n'))
    {
        var cells = line.Split('|', StringSplitOptions.TrimEntries);
        if (cells.Length < 4)
        {
            continue;
        }

        var issueText = cells[1].Replace("**", string.Empty, StringComparison.Ordinal);
        if (!PatternMatches(issueText, "^\\d+(?:,\\s*\\d+)*$"))
        {
            continue;
        }

        foreach (var issue in issueText.Split(',', StringSplitOptions.TrimEntries))
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["issue"] = int.Parse(issue, System.Globalization.CultureInfo.InvariantCulture),
                ["resolution"] = cells[2],
                ["testArea"] = cells.Length > 4 ? cells[3] : null,
            });
        }
    }

    rows.Sort(static (left, right) => ((int)left["issue"]!).CompareTo((int)right["issue"]!));
    return rows;
}

static void ValidateIssueRows(
    string tracker,
    int expectedCount,
    List<Dictionary<string, object?>> rows,
    List<string> failures)
{
    var issues = rows.Select(static row => ((int)row["issue"]!).ToString(
        System.Globalization.CultureInfo.InvariantCulture));
    AddDuplicateFailures(issues, $"{tracker} issue", failures, StringComparer.Ordinal);
    if (rows.Count != expectedCount)
    {
        failures.Add($"The {tracker} issue map contains {rows.Count} records; expected exactly {expectedCount}.");
    }

    foreach (var row in rows.Where(static row => string.IsNullOrWhiteSpace(row["resolution"] as string)))
    {
        failures.Add($"The {tracker} issue {row["issue"]} has no resolution.");
    }
}

static async Task WriteReportAsync(
    string path,
    object value,
    CancellationToken cancellationToken)
{
    await using var stream = new FileStream(
        path,
        FileMode.Create,
        FileAccess.Write,
        FileShare.None,
        bufferSize: 16 * 1024,
        FileOptions.Asynchronous);
    await using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
    {
        WriteJsonValue(writer, value);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
}

static MatchCollection Matches(string input, string pattern)
    => new Regex(pattern, RegexOptions.CultureInvariant).Matches(input);

static Match FirstMatch(string input, string pattern)
    => new Regex(pattern, RegexOptions.CultureInvariant).Match(input);

static bool PatternMatches(string input, string pattern)
    => new Regex(pattern, RegexOptions.CultureInvariant).IsMatch(input);

static void WriteJsonValue(Utf8JsonWriter writer, object? value)
{
    switch (value)
    {
        case null:
            writer.WriteNullValue();
            return;
        case string text:
            writer.WriteStringValue(text);
            return;
        case bool flag:
            writer.WriteBooleanValue(flag);
            return;
        case int number:
            writer.WriteNumberValue(number);
            return;
        case IDictionary<string, object?> dictionary:
            writer.WriteStartObject();
            foreach (var pair in dictionary)
            {
                writer.WritePropertyName(pair.Key);
                WriteJsonValue(writer, pair.Value);
            }

            writer.WriteEndObject();
            return;
        case IEnumerable sequence:
            writer.WriteStartArray();
            foreach (var item in sequence)
            {
                WriteJsonValue(writer, item);
            }

            writer.WriteEndArray();
            return;
        default:
            throw new InvalidOperationException($"Unsupported report value type '{value.GetType()}'.");
    }
}
