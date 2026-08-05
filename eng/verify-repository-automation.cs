#:package System.CommandLine

using System.CommandLine;
using System.Diagnostics;

var repositoryRootOption = new Option<string?>("--repository-root")
{
    Description = "The GitSail repository root to inspect.",
    Arity = ArgumentArity.ExactlyOne,
};
var rootCommand = new RootCommand("Verifies that repository automation uses only .NET file-based apps.");
rootCommand.Options.Add(repositoryRootOption);
rootCommand.Validators.Add(result =>
{
    if (string.IsNullOrWhiteSpace(result.GetValue(repositoryRootOption)))
    {
        result.AddError("Option '--repository-root' is required.");
    }
});
rootCommand.SetAction((parseResult, cancellationToken) => VerifyAsync(
    parseResult.GetValue(repositoryRootOption)!,
    cancellationToken));

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);

static async Task<int> VerifyAsync(string repositoryRoot, CancellationToken cancellationToken)
{
    var fullRoot = Path.GetFullPath(repositoryRoot, Directory.GetCurrentDirectory());
    if (!File.Exists(Path.Combine(fullRoot, "GitSail.slnx")))
    {
        throw new DirectoryNotFoundException($"The GitSail repository root is invalid: {fullRoot}");
    }

    var repositoryFiles = await GetRepositoryFilesAsync(fullRoot, cancellationToken).ConfigureAwait(false);
    var failures = new List<string>();
    var forbiddenExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".bash",
        ".bat",
        ".cake",
        ".cjs",
        ".cmd",
        ".csx",
        ".fish",
        ".fsx",
        ".js",
        ".lua",
        ".mjs",
        ".nu",
        ".pl",
        ".ps1",
        ".psd1",
        ".psm1",
        ".py",
        ".rb",
        ".sh",
        ".ts",
        ".zsh",
    };
    foreach (var repositoryFile in repositoryFiles)
    {
        if (forbiddenExtensions.Contains(Path.GetExtension(repositoryFile)))
        {
            failures.Add($"Checked-in script files are not allowed: {repositoryFile}");
        }
    }

    var automationApps = repositoryFiles
        .Where(static path => path.StartsWith("eng/", StringComparison.Ordinal))
        .Order(StringComparer.Ordinal)
        .ToArray();
    if (automationApps.Length == 0)
    {
        failures.Add("The eng directory does not contain any tracked .NET file-based apps.");
    }

    foreach (var automationApp in automationApps)
    {
        if (!automationApp.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"Every tracked file under eng must be a C# file-based app: {automationApp}");
            continue;
        }

        var source = await File.ReadAllTextAsync(
            Path.Combine(fullRoot, automationApp),
            cancellationToken).ConfigureAwait(false);
        if (!source.StartsWith("#:package System.CommandLine", StringComparison.Ordinal))
        {
            failures.Add(
                $"File-based app '{automationApp}' must use the centrally pinned System.CommandLine package.");
        }
    }

    var workflowFiles = repositoryFiles
        .Where(static path => path.StartsWith(".github/workflows/", StringComparison.Ordinal) &&
            (path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)))
        .Order(StringComparer.Ordinal)
        .ToArray();
    var workflowSources = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var workflowFile in workflowFiles)
    {
        var source = await File.ReadAllTextAsync(
            Path.Combine(fullRoot, workflowFile),
            cancellationToken).ConfigureAwait(false);
        workflowSources.Add(workflowFile, source);
        foreach (var line in source.Split('\n'))
        {
            if (line.TrimStart().StartsWith("run: |", StringComparison.Ordinal))
            {
                failures.Add($"Workflow '{workflowFile}' contains an inline shell program.");
            }

            var appStart = line.IndexOf("eng/", StringComparison.Ordinal);
            var appEnd = appStart < 0
                ? -1
                : line.IndexOf(".cs", appStart, StringComparison.Ordinal);
            if (appStart >= 0 && appEnd >= appStart &&
                !line.Contains("dotnet run --file", StringComparison.Ordinal))
            {
                var automationApp = line.Substring(appStart, appEnd + 3 - appStart);
                failures.Add(
                    $"Workflow '{workflowFile}' must invoke '{automationApp}' with 'dotnet run --file'.");
            }
        }
    }

    var testHostSources = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var testSource in repositoryFiles.Where(static path =>
        path.StartsWith("tests/", StringComparison.Ordinal) &&
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
    {
        testHostSources.Add(
            testSource,
            await File.ReadAllTextAsync(
                Path.Combine(fullRoot, testSource),
                cancellationToken).ConfigureAwait(false));
    }

    foreach (var automationApp in automationApps.Where(static path => path.EndsWith(".cs", StringComparison.Ordinal)))
    {
        var invokedDirectly = workflowSources.Values.Any(source => source.Contains(
            $"dotnet run --file {automationApp}",
            StringComparison.Ordinal));
        var exercisedByMtp = testHostSources
            .Where(pair => pair.Value.Contains(automationApp, StringComparison.Ordinal))
            .Any(pair =>
            {
                var testDirectory = Path.GetDirectoryName(pair.Key)?.Replace('\\', '/');
                return testDirectory is not null && repositoryFiles
                    .Where(path =>
                        path.StartsWith($"{testDirectory}/", StringComparison.Ordinal) &&
                        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    .Any(project => workflowSources.Values.Any(source => source.Contains(
                        project,
                        StringComparison.Ordinal)));
            });
        if (!invokedDirectly && !exercisedByMtp)
        {
            failures.Add(
                $"No workflow or MTP test host exercises the file-based app '{automationApp}'.");
        }
    }

    if (failures.Count > 0)
    {
        throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
    }

    Console.WriteLine(
        $"Verified {automationApps.Length} .NET file-based automation apps across " +
        $"{workflowFiles.Length} workflows.");
    return 0;
}

static async Task<string[]> GetRepositoryFilesAsync(
    string repositoryRoot,
    CancellationToken cancellationToken)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = "git",
        WorkingDirectory = repositoryRoot,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    startInfo.ArgumentList.Add("ls-files");
    startInfo.ArgumentList.Add("--cached");
    startInfo.ArgumentList.Add("--others");
    startInfo.ArgumentList.Add("--exclude-standard");
    startInfo.ArgumentList.Add("-z");

    using var process = new Process { StartInfo = startInfo };
    if (!process.Start())
    {
        throw new InvalidOperationException("Could not start Git to enumerate tracked repository files.");
    }

    var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
    var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
    try
    {
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }
    catch
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        throw;
    }

    var output = await standardOutput.ConfigureAwait(false);
    var error = await standardError.ConfigureAwait(false);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"Git could not enumerate tracked repository files.{Environment.NewLine}{error}");
    }

    return output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
}
