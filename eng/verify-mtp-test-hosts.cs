#:package System.CommandLine

using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

var repositoryRootOption = new Option<string?>("--repository-root")
{
    Description = "The GitSail repository whose managed MSTest hosts must be verified.",
    Arity = ArgumentArity.ExactlyOne,
};
var configurationOption = new Option<string?>("--configuration")
{
    Description = "The already-built configuration whose executable MTP hosts must be inspected.",
    Arity = ArgumentArity.ExactlyOne,
};
var rootCommand = new RootCommand(
    "Verifies every managed test project and its executable Microsoft Testing Platform host.");
rootCommand.Options.Add(repositoryRootOption);
rootCommand.Options.Add(configurationOption);
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
});
rootCommand.SetAction((parseResult, cancellationToken) => VerifyAsync(
    parseResult.GetValue(repositoryRootOption)!,
    parseResult.GetValue(configurationOption)!,
    cancellationToken));

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);

static async Task<int> VerifyAsync(
    string repositoryRoot,
    string configuration,
    CancellationToken cancellationToken)
{
    var fullRoot = Path.GetFullPath(repositoryRoot, Directory.GetCurrentDirectory());
    var solutionPath = Path.Combine(fullRoot, "GitSail.slnx");
    if (!File.Exists(solutionPath))
    {
        throw new DirectoryNotFoundException($"The GitSail repository root is invalid: {fullRoot}");
    }

    VerifyGlobalJson(fullRoot);
    var testRoot = Path.Combine(fullRoot, "tests");
    var projects = Directory.EnumerateFiles(testRoot, "*.csproj", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .ToArray();
    if (projects.Length == 0)
    {
        throw new InvalidDataException("The repository contains no managed test projects.");
    }

    foreach (var project in projects)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VerifyProjectDeclaration(project);
        await VerifyEffectivePropertiesAsync(fullRoot, project, cancellationToken).ConfigureAwait(false);
        await VerifyExecutableHostAsync(
            fullRoot,
            project,
            configuration,
            cancellationToken).ConfigureAwait(false);
    }

    Console.WriteLine(
        $"Verified {projects.Length} executable MSTest 4.2.3 test hosts on Microsoft Testing Platform.");
    return 0;
}

static void VerifyGlobalJson(string repositoryRoot)
{
    var path = Path.Combine(repositoryRoot, "global.json");
    using var document = JsonDocument.Parse(File.ReadAllBytes(path));
    var root = document.RootElement;
    var sdk = root.GetProperty("sdk");
    if (sdk.GetProperty("version").GetString() != "10.0.302" ||
        sdk.GetProperty("rollForward").GetString() != "disable" ||
        sdk.GetProperty("allowPrerelease").GetBoolean() ||
        root.GetProperty("msbuild-sdks").GetProperty("MSTest.Sdk").GetString() != "4.2.3" ||
        root.GetProperty("test").GetProperty("runner").GetString() != "Microsoft.Testing.Platform")
    {
        throw new InvalidDataException(
            "global.json does not pin .NET 10.0.302, MSTest.Sdk 4.2.3, and Microsoft Testing Platform.");
    }
}

static void VerifyProjectDeclaration(string project)
{
    var document = XDocument.Load(project, LoadOptions.None);
    var root = document.Root ?? throw new InvalidDataException($"Test project '{project}' has no root element.");
    if (root.Attribute("Sdk")?.Value != "MSTest.Sdk")
    {
        throw new InvalidDataException($"Test project '{project}' does not use MSTest.Sdk.");
    }

    var prohibitedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.NET.Test.Sdk",
        "MSTest.TestAdapter",
        "MSTest.TestFramework",
        "NUnit",
        "NUnit3TestAdapter",
        "xunit",
        "xunit.runner.visualstudio",
    };
    var prohibited = root.Descendants()
        .Where(element => element.Name.LocalName == "PackageReference")
        .Select(element => element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value)
        .Where(package => package is not null && prohibitedPackages.Contains(package))
        .ToArray();
    if (prohibited.Length != 0)
    {
        throw new InvalidDataException(
            $"Test project '{project}' directly references prohibited test packages: " +
            string.Join(", ", prohibited));
    }
}

static async Task VerifyEffectivePropertiesAsync(
    string repositoryRoot,
    string project,
    CancellationToken cancellationToken)
{
    var result = await RunAsync(
        "dotnet",
        [
            "msbuild",
            project,
            "-getProperty:TargetFramework,OutputType,IsPackable,TestingExtensionsProfile,UseVSTest,IsTestProject",
        ],
        repositoryRoot,
        cancellationToken).ConfigureAwait(false);
    RequireSuccess(result, project, "read effective MSBuild properties");
    using var document = JsonDocument.Parse(result.StandardOutput);
    var properties = document.RootElement.GetProperty("Properties");
    RequireProperty(properties, project, "TargetFramework", "net10.0");
    RequireProperty(properties, project, "OutputType", "Exe");
    RequireProperty(properties, project, "IsPackable", "false");
    RequireProperty(properties, project, "TestingExtensionsProfile", "Default");
    RequireProperty(properties, project, "UseVSTest", "false");
    RequireProperty(properties, project, "IsTestProject", "true");
}

static void RequireProperty(JsonElement properties, string project, string name, string expected)
{
    var actual = properties.GetProperty(name).GetString();
    if (actual != expected)
    {
        throw new InvalidDataException(
            $"Test project '{project}' resolves {name}='{actual}', expected '{expected}'.");
    }
}

static async Task VerifyExecutableHostAsync(
    string repositoryRoot,
    string project,
    string configuration,
    CancellationToken cancellationToken)
{
    var result = await RunAsync(
        "dotnet",
        [
            "run",
            "--project",
            project,
            "--configuration",
            configuration,
            "--no-build",
            "--",
            "--info",
        ],
        repositoryRoot,
        cancellationToken).ConfigureAwait(false);
    RequireSuccess(result, project, "run its executable MTP host");
    var output = string.Concat(result.StandardOutput, result.StandardError);
    var requiredMarkers = new[]
    {
        "Microsoft Testing Platform:",
        "MSTest.Sdk",
        "Name: MSTest",
        "Version: 4.2.3",
        "Name: Code Coverage",
        "Name: TRX report generator",
    };
    var missing = requiredMarkers
        .Where(marker => !output.Contains(marker, StringComparison.Ordinal))
        .ToArray();
    if (missing.Length != 0)
    {
        throw new InvalidDataException(
            $"Test project '{project}' has an incomplete MTP host. Missing: {string.Join(", ", missing)}.");
    }
}

static void RequireSuccess(
    (int ExitCode, string StandardOutput, string StandardError) result,
    string project,
    string operation)
{
    if (result.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"Could not {operation} for '{project}'; dotnet exited with code {result.ExitCode}." +
            Environment.NewLine + result.StandardOutput + result.StandardError);
    }
}

static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunAsync(
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
