#:package System.CommandLine

using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;

var ridOption = new Option<string?>("--rid")
{
    Description = "The staged Native AOT runtime identifier.",
    Arity = ArgumentArity.ExactlyOne,
};
var packageDirectoryOption = new Option<string?>("--package-directory")
{
    Description = "The directory containing the staged pointer and RID packages.",
    Arity = ArgumentArity.ExactlyOne,
};
var rootCommand = new RootCommand("Installs and runs staged GitSail .NET tool packages.");
rootCommand.Options.Add(ridOption);
rootCommand.Options.Add(packageDirectoryOption);
rootCommand.Validators.Add(result =>
{
    if (string.IsNullOrWhiteSpace(result.GetValue(ridOption)))
    {
        result.AddError("Option '--rid' is required.");
    }

    if (string.IsNullOrWhiteSpace(result.GetValue(packageDirectoryOption)))
    {
        result.AddError("Option '--package-directory' is required.");
    }
});
rootCommand.SetAction((parseResult, cancellationToken) => VerifyAsync(
    parseResult.GetValue(ridOption)!,
    parseResult.GetValue(packageDirectoryOption)!,
    cancellationToken));

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);

static async Task<int> VerifyAsync(
    string rid,
    string packageDirectory,
    CancellationToken cancellationToken)
{
    var repositoryRoot = Directory.GetCurrentDirectory();
    var projectPath = Path.Combine(repositoryRoot, "src", "GitSail", "GitSail.csproj");
    if (!File.Exists(projectPath))
    {
        throw new InvalidOperationException(
            $"Run this file-based app from the GitSail repository root. Missing: {projectPath}");
    }

    var packageSource = Path.GetFullPath(packageDirectory, repositoryRoot);
    if (!Directory.Exists(packageSource))
    {
        throw new DirectoryNotFoundException($"The staged package directory is missing: {packageSource}");
    }

    var version = (await RunCheckedAsync(
        "dotnet",
        ["msbuild", projectPath, "-getProperty:Version", "-nologo"],
        repositoryRoot,
        echoOutput: false,
        environment: null,
        cancellationToken).ConfigureAwait(false)).Trim();
    if (string.IsNullOrWhiteSpace(version))
    {
        throw new InvalidOperationException("Could not read the GitSail package version from MSBuild.");
    }

    RequireFile(Path.Combine(packageSource, $"GitSail.{version}.nupkg"));
    RequireFile(Path.Combine(packageSource, $"GitSail.{rid}.{version}.nupkg"));

    await VerifyToolPathInstallAsync(
        repositoryRoot,
        packageSource,
        rid,
        version,
        cancellationToken).ConfigureAwait(false);
    await VerifyLocalManifestInstallAsync(
        repositoryRoot,
        packageSource,
        rid,
        version,
        cancellationToken).ConfigureAwait(false);
    return 0;
}

static async Task VerifyToolPathInstallAsync(
    string repositoryRoot,
    string packageSource,
    string rid,
    string version,
    CancellationToken cancellationToken)
{
    var toolPath = Path.Combine(
        repositoryRoot,
        "artifacts",
        "tool-install",
        $"{rid}-{Guid.NewGuid():N}");
    var installed = false;

    try
    {
        _ = await RunCheckedAsync(
            "dotnet",
            [
                "tool",
                "install",
                "GitSail",
                "--tool-path",
                toolPath,
                "--version",
                version,
                "--add-source",
                packageSource,
                "--ignore-failed-sources",
            ],
            repositoryRoot,
            echoOutput: true,
            environment: null,
            cancellationToken).ConfigureAwait(false);
        installed = true;
        var executable = FindInstalledExecutable(toolPath);

        _ = await RunCheckedAsync(
            executable,
            ["--version"],
            repositoryRoot,
            echoOutput: true,
            environment: null,
            cancellationToken).ConfigureAwait(false);
        var doctorJson = await RunCheckedAsync(
            executable,
            ["doctor", "--json"],
            repositoryRoot,
            echoOutput: false,
            environment: null,
            cancellationToken).ConfigureAwait(false);
        VerifyDoctorReport(doctorJson, rid);

        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var environment = new Dictionary<string, string?>
        {
            ["PATH"] = toolPath + Path.PathSeparator + currentPath,
        };
        _ = await RunCheckedAsync(
            "git",
            ["tui", "--version"],
            repositoryRoot,
            echoOutput: true,
            environment,
            cancellationToken).ConfigureAwait(false);
    }
    finally
    {
        try
        {
            if (installed)
            {
                _ = await RunCheckedAsync(
                    "dotnet",
                    ["tool", "uninstall", "GitSail", "--tool-path", toolPath],
                    repositoryRoot,
                    echoOutput: true,
                    environment: null,
                    CancellationToken.None).ConfigureAwait(false);
                var remainingCommand = FindExistingInstalledExecutable(toolPath);
                if (remainingCommand is not null)
                {
                    throw new InvalidOperationException(
                        $"The tool command remains after uninstall: {remainingCommand}");
                }
            }
        }
        finally
        {
            DeleteDirectory(toolPath);
        }
    }
}

static async Task VerifyLocalManifestInstallAsync(
    string repositoryRoot,
    string packageSource,
    string rid,
    string version,
    CancellationToken cancellationToken)
{
    var manifestRoot = Path.Combine(
        repositoryRoot,
        "artifacts",
        "tool-manifest",
        $"{rid}-{Guid.NewGuid():N}");
    var manifestPath = Path.Combine(manifestRoot, "dotnet-tools.json");
    var installed = false;
    Directory.CreateDirectory(manifestRoot);

    try
    {
        _ = await RunCheckedAsync(
            "dotnet",
            ["new", "tool-manifest"],
            manifestRoot,
            echoOutput: true,
            environment: null,
            cancellationToken).ConfigureAwait(false);
        RequireFile(manifestPath);

        _ = await RunCheckedAsync(
            "dotnet",
            [
                "tool",
                "install",
                "GitSail",
                "--tool-manifest",
                manifestPath,
                "--version",
                version,
                "--add-source",
                packageSource,
                "--ignore-failed-sources",
            ],
            repositoryRoot,
            echoOutput: true,
            environment: null,
            cancellationToken).ConfigureAwait(false);
        installed = true;

        _ = await RunCheckedAsync(
            "dotnet",
            [
                "tool",
                "restore",
                "--tool-manifest",
                manifestPath,
                "--add-source",
                packageSource,
                "--ignore-failed-sources",
            ],
            repositoryRoot,
            echoOutput: true,
            environment: null,
            cancellationToken).ConfigureAwait(false);
        _ = await RunCheckedAsync(
            "dotnet",
            ["tool", "run", "git-tui", "--", "--version"],
            manifestRoot,
            echoOutput: true,
            environment: null,
            cancellationToken).ConfigureAwait(false);
    }
    finally
    {
        try
        {
            if (installed)
            {
                _ = await RunCheckedAsync(
                    "dotnet",
                    ["tool", "uninstall", "GitSail", "--tool-manifest", manifestPath],
                    repositoryRoot,
                    echoOutput: true,
                    environment: null,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            DeleteDirectory(manifestRoot);
        }
    }
}

static void VerifyDoctorReport(string json, string rid)
{
    using var document = JsonDocument.Parse(json);
    var root = document.RootElement;
    if (!root.TryGetProperty("nativeAot", out var nativeAot) || !nativeAot.GetBoolean())
    {
        throw new InvalidDataException("The installed tool Doctor report does not identify Native AOT.");
    }

    if (!root.TryGetProperty("runtimeIdentifier", out var runtimeIdentifier) ||
        !string.Equals(runtimeIdentifier.GetString(), rid, StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            $"The installed tool Doctor report does not match runtime identifier '{rid}'.");
    }
}

static async Task<string> RunCheckedAsync(
    string fileName,
    IReadOnlyList<string> arguments,
    string workingDirectory,
    bool echoOutput,
    IReadOnlyDictionary<string, string?>? environment,
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

    if (environment is not null)
    {
        foreach (var variable in environment)
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }
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

    var output = await standardOutput.ConfigureAwait(false);
    var error = await standardError.ConfigureAwait(false);
    if (echoOutput && output.Length > 0)
    {
        await Console.Out.WriteAsync(output).ConfigureAwait(false);
    }

    if (error.Length > 0)
    {
        await Console.Error.WriteAsync(error).ConfigureAwait(false);
    }

    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"Process '{fileName}' exited with code {process.ExitCode}.{Environment.NewLine}{output}{error}");
    }

    return output;
}

static void RequireFile(string path)
{
    if (!File.Exists(path))
    {
        throw new FileNotFoundException("A required staged file is missing.", path);
    }
}

static string FindInstalledExecutable(string toolPath)
{
    var executable = FindExistingInstalledExecutable(toolPath);
    if (executable is not null)
    {
        return executable;
    }

    var entries = Directory.Exists(toolPath)
        ? string.Join(", ", Directory.EnumerateFileSystemEntries(toolPath).Select(Path.GetFileName))
        : "directory missing";
    throw new FileNotFoundException(
        $"The installed git-tui command is missing from '{toolPath}'. Directory contents: {entries}.");
}

static string? FindExistingInstalledExecutable(string toolPath)
{
    foreach (var fileName in new[] { "git-tui", "git-tui.exe" })
    {
        var candidate = Path.Combine(toolPath, fileName);
        if (File.Exists(candidate))
        {
            return candidate;
        }
    }

    return null;
}

static void DeleteDirectory(string path)
{
    if (Directory.Exists(path))
    {
        Directory.Delete(path, recursive: true);
    }
}
