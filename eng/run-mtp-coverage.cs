#:package System.CommandLine

using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;

var repositoryRootOption = new Option<string?>("--repository-root")
{
    Description = "The GitSail repository whose managed test hosts must collect coverage.",
    Arity = ArgumentArity.ExactlyOne,
};
var configurationOption = new Option<string?>("--configuration")
{
    Description = "The already-built configuration to test.",
    Arity = ArgumentArity.ExactlyOne,
};
var outputDirectoryOption = new Option<string?>("--output-directory")
{
    Description = "The directory that receives per-project TRX, Cobertura, and console output.",
    Arity = ArgumentArity.ExactlyOne,
};
var rootCommand = new RootCommand(
    "Runs every executable MSTest host on Microsoft Testing Platform with Cobertura coverage enabled.");
rootCommand.Options.Add(repositoryRootOption);
rootCommand.Options.Add(configurationOption);
rootCommand.Options.Add(outputDirectoryOption);
rootCommand.Validators.Add(result =>
{
    if (string.IsNullOrWhiteSpace(result.GetValue(repositoryRootOption)))
    {
        result.AddError("Option '--repository-root' is required.");
    }

    if (string.IsNullOrWhiteSpace(result.GetValue(configurationOption)))
    {
        result.AddError("Option '--configuration' is required.");
    }

    if (string.IsNullOrWhiteSpace(result.GetValue(outputDirectoryOption)))
    {
        result.AddError("Option '--output-directory' is required.");
    }
});
rootCommand.SetAction((parseResult, cancellationToken) => RunAsync(
    parseResult.GetValue(repositoryRootOption)!,
    parseResult.GetValue(configurationOption)!,
    parseResult.GetValue(outputDirectoryOption)!,
    cancellationToken));

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);

static async Task<int> RunAsync(
    string repositoryRoot,
    string configuration,
    string outputDirectory,
    CancellationToken cancellationToken)
{
    var fullRoot = Path.GetFullPath(repositoryRoot, Directory.GetCurrentDirectory());
    if (!File.Exists(Path.Combine(fullRoot, "GitSail.slnx")))
    {
        throw new DirectoryNotFoundException($"The GitSail repository root is invalid: {fullRoot}");
    }

    var fullOutputDirectory = Path.GetFullPath(outputDirectory, fullRoot);
    EnsureSafeOutputDirectory(fullRoot, fullOutputDirectory);
    var coverageSettingsPath = Path.Combine(fullRoot, "tests", "code-coverage.xml");
    if (!File.Exists(coverageSettingsPath))
    {
        throw new FileNotFoundException("The MTP coverage settings file is missing.", coverageSettingsPath);
    }

    var testRoot = Path.Combine(fullRoot, "tests");
    var projects = Directory.EnumerateFiles(testRoot, "*.csproj", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .ToArray();
    if (projects.Length == 0)
    {
        throw new InvalidDataException("The repository contains no managed test projects.");
    }

    long totalCoveredLines = 0;
    long totalValidLines = 0;
    foreach (var project in projects)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var projectName = Path.GetFileNameWithoutExtension(project);
        var projectOutputDirectory = Path.Combine(fullOutputDirectory, projectName);
        if (Directory.Exists(projectOutputDirectory))
        {
            Directory.Delete(projectOutputDirectory, recursive: true);
        }

        Directory.CreateDirectory(projectOutputDirectory);
        var result = await RunTestHostAsync(
            fullRoot,
            project,
            projectName,
            configuration,
            projectOutputDirectory,
            coverageSettingsPath,
            cancellationToken).ConfigureAwait(false);
        var standardOutputPath = Path.Combine(projectOutputDirectory, $"{projectName}.stdout.txt");
        var standardErrorPath = Path.Combine(projectOutputDirectory, $"{projectName}.stderr.txt");
        await File.WriteAllTextAsync(
            standardOutputPath,
            result.StandardOutput,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            standardErrorPath,
            result.StandardError,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Coverage failed for '{projectName}'; its MTP host exited with code {result.ExitCode}." +
                Environment.NewLine + result.StandardOutput + result.StandardError);
        }

        var trxPath = Path.Combine(projectOutputDirectory, $"{projectName}.trx");
        ValidateTrx(trxPath, projectName);
        var coveragePath = Path.Combine(projectOutputDirectory, $"{projectName}.cobertura.xml");
        var (coveredLines, validLines) = ValidateCoverage(coveragePath, projectName);
        totalCoveredLines += coveredLines;
        totalValidLines += validLines;
        Console.WriteLine(
            $"{projectName}: {coveredLines.ToString(CultureInfo.InvariantCulture)} of " +
            $"{validLines.ToString(CultureInfo.InvariantCulture)} instrumented lines covered.");
    }

    Console.WriteLine(
        $"Collected validated TRX and Cobertura evidence from {projects.Length} executable MTP hosts; " +
        $"aggregate line observations were {totalCoveredLines.ToString(CultureInfo.InvariantCulture)} of " +
        $"{totalValidLines.ToString(CultureInfo.InvariantCulture)}.");
    return 0;
}

static void EnsureSafeOutputDirectory(string repositoryRoot, string outputDirectory)
{
    var artifactsDirectory = Path.GetFullPath(Path.Combine(repositoryRoot, "artifacts"));
    var relativePath = Path.GetRelativePath(artifactsDirectory, outputDirectory);
    if (relativePath == "." ||
        Path.IsPathRooted(relativePath) ||
        relativePath == ".." ||
        relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            "The coverage output directory must be a child of the repository's artifacts directory.");
    }
}

static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunTestHostAsync(
    string repositoryRoot,
    string project,
    string projectName,
    string configuration,
    string outputDirectory,
    string coverageSettingsPath,
    CancellationToken cancellationToken)
{
    return await RunProcessAsync(
        "dotnet",
        [
            "run",
            "--project",
            project,
            "--configuration",
            configuration,
            "--no-build",
            "--",
            "--results-directory",
            outputDirectory,
            "--report-trx",
            "--report-trx-filename",
            $"{projectName}.trx",
            "--coverage",
            "--coverage-output",
            $"{projectName}.cobertura.xml",
            "--coverage-output-format",
            "cobertura",
            "--coverage-settings",
            coverageSettingsPath,
            "--minimum-expected-tests",
            "1",
        ],
        repositoryRoot,
        cancellationToken).ConfigureAwait(false);
}

static void ValidateTrx(string path, string projectName)
{
    if (!File.Exists(path) || new FileInfo(path).Length == 0)
    {
        throw new InvalidDataException($"MTP did not produce a non-empty TRX report for '{projectName}'.");
    }

    var document = XDocument.Load(path, LoadOptions.None);
    if (document.Root?.Name.LocalName != "TestRun")
    {
        throw new InvalidDataException($"The test report for '{projectName}' is not TRX.");
    }
}

static (long CoveredLines, long ValidLines) ValidateCoverage(string path, string projectName)
{
    if (!File.Exists(path) || new FileInfo(path).Length == 0)
    {
        throw new InvalidDataException(
            $"MTP did not produce a non-empty Cobertura report for '{projectName}'.");
    }

    var document = XDocument.Load(path, LoadOptions.None);
    var root = document.Root;
    if (root?.Name.LocalName != "coverage" ||
        !long.TryParse(root.Attribute("lines-covered")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var coveredLines) ||
        !long.TryParse(root.Attribute("lines-valid")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var validLines) ||
        coveredLines <= 0 ||
        validLines <= 0 ||
        coveredLines > validLines ||
        !root.Descendants().Any(element => element.Name.LocalName == "package"))
    {
        throw new InvalidDataException(
            $"The coverage report for '{projectName}' is not a populated Cobertura document.");
    }

    return (coveredLines, validLines);
}

static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunProcessAsync(
    string fileName,
    IReadOnlyList<string> arguments,
    string workingDirectory,
    CancellationToken cancellationToken)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = fileName,
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = new Process { StartInfo = startInfo };
    if (!process.Start())
    {
        throw new InvalidOperationException($"Could not start process '{fileName}'.");
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

    return (
        process.ExitCode,
        await standardOutput.ConfigureAwait(false),
        await standardError.ConfigureAwait(false));
}
