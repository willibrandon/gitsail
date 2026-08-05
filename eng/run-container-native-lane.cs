#:package System.CommandLine

using System.CommandLine;
using System.Diagnostics;
using System.Runtime.InteropServices;

var repositoryRootOption = new Option<string?>("--repository-root")
{
    Description = "The GitSail repository root mounted into the native build container.",
    Arity = ArgumentArity.ExactlyOne,
};
var ridOption = new Option<string?>("--rid")
{
    Description = "The Linux musl runtime identifier to build and verify.",
    Arity = ArgumentArity.ExactlyOne,
};
var insideOption = new Option<bool>("--inside")
{
    Description = "Runs the package lane inside the pinned Native AOT container.",
    Hidden = true,
};
var rootCommand = new RootCommand(
    "Builds, installs, and executes a Linux musl Native AOT tool package in a pinned container.");
rootCommand.Options.Add(repositoryRootOption);
rootCommand.Options.Add(ridOption);
rootCommand.Options.Add(insideOption);
rootCommand.Validators.Add(result =>
{
    if (string.IsNullOrWhiteSpace(result.GetValue(repositoryRootOption)))
    {
        result.AddError("Option '--repository-root' is required.");
    }

    if (result.GetValue(ridOption) is not ("linux-musl-x64" or "linux-musl-arm64"))
    {
        result.AddError("Option '--rid' must be 'linux-musl-x64' or 'linux-musl-arm64'.");
    }
});
rootCommand.SetAction((parseResult, cancellationToken) => RunAsync(
    parseResult.GetValue(repositoryRootOption)!,
    parseResult.GetValue(ridOption)!,
    parseResult.GetValue(insideOption),
    cancellationToken));

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);

static async Task<int> RunAsync(
    string repositoryRoot,
    string rid,
    bool inside,
    CancellationToken cancellationToken)
{
    var fullRoot = Path.GetFullPath(repositoryRoot, Directory.GetCurrentDirectory());
    if (!File.Exists(Path.Combine(fullRoot, "GitSail.slnx")))
    {
        throw new DirectoryNotFoundException($"The GitSail repository root is invalid: {fullRoot}");
    }

    return inside
        ? await RunInsideContainerAsync(fullRoot, rid, cancellationToken).ConfigureAwait(false)
        : await RunContainerAsync(fullRoot, rid, cancellationToken).ConfigureAwait(false);
}

static async Task<int> RunContainerAsync(
    string repositoryRoot,
    string rid,
    CancellationToken cancellationToken)
{
    if (!OperatingSystem.IsLinux())
    {
        throw new PlatformNotSupportedException("Linux musl package containers require a Linux host.");
    }

    var expectedArchitecture = rid == "linux-musl-x64"
        ? Architecture.X64
        : Architecture.Arm64;
    if (RuntimeInformation.OSArchitecture != expectedArchitecture)
    {
        throw new PlatformNotSupportedException(
            $"Runtime identifier '{rid}' requires a native {expectedArchitecture} host, not " +
            $"{RuntimeInformation.OSArchitecture}.");
    }

    var image = rid == "linux-musl-x64"
        ? "mcr.microsoft.com/dotnet/sdk:10.0.302-alpine3.23-aot-amd64@" +
            "sha256:5edf56652242a2ffc15e48c61c392821bdbba04b1cac804e2b9873d02a2cae52"
        : "mcr.microsoft.com/dotnet/sdk:10.0.302-alpine3.23-aot-arm64v8@" +
            "sha256:359f95fe30d407333ce8f33c1e10da3fe739e339894a4fc1480d0bc8f4526cce";
    var userId = await RunCapturedAsync(
        "id",
        ["--user"],
        repositoryRoot,
        cancellationToken).ConfigureAwait(false);
    var groupId = await RunCapturedAsync(
        "id",
        ["--group"],
        repositoryRoot,
        cancellationToken).ConfigureAwait(false);
    var sourceDateEpoch = Environment.GetEnvironmentVariable("SOURCE_DATE_EPOCH");
    if (string.IsNullOrWhiteSpace(sourceDateEpoch) || sourceDateEpoch.Any(character => !char.IsAsciiDigit(character)))
    {
        throw new InvalidOperationException(
            "SOURCE_DATE_EPOCH must be exported from the checked-out revision before the container lane starts.");
    }

    await RunCheckedAsync(
        "docker",
        [
            "run",
            "--rm",
            "--user",
            $"{userId}:{groupId}",
            "--volume",
            $"{repositoryRoot}:/src",
            "--workdir",
            "/src",
            "--env",
            "CI=true",
            "--env",
            "DOTNET_CLI_HOME=/tmp/gitsail-dotnet",
            "--env",
            "DOTNET_CLI_TELEMETRY_OPTOUT=1",
            "--env",
            "DOTNET_NOLOGO=1",
            "--env",
            "HOME=/tmp",
            "--env",
            "NUGET_PACKAGES=/tmp/gitsail-nuget",
            "--env",
            "NUGET_XMLDOC_MODE=skip",
            "--env",
            $"SOURCE_DATE_EPOCH={sourceDateEpoch}",
            "--env",
            "TEMP=/tmp",
            "--env",
            "TMP=/tmp",
            "--env",
            "TMPDIR=/tmp",
            "--env",
            "XDG_CACHE_HOME=/tmp/gitsail-cache",
            "--env",
            "TZ=UTC",
            "--env",
            "LANG=C.UTF-8",
            "--env",
            "LC_ALL=C.UTF-8",
            image,
            "dotnet",
            "run",
            "--file",
            "eng/run-container-native-lane.cs",
            "--",
            "--repository-root",
            "/src",
            "--rid",
            rid,
            "--inside",
        ],
        repositoryRoot,
        cancellationToken).ConfigureAwait(false);
    return 0;
}

static async Task<int> RunInsideContainerAsync(
    string repositoryRoot,
    string rid,
    CancellationToken cancellationToken)
{
    if (!OperatingSystem.IsLinux() ||
        !RuntimeInformation.RuntimeIdentifier.StartsWith("linux-musl-", StringComparison.Ordinal))
    {
        throw new PlatformNotSupportedException(
            "The inner package lane must run on native Linux musl.");
    }

    var expectedArchitecture = rid == "linux-musl-x64"
        ? Architecture.X64
        : Architecture.Arm64;
    if (RuntimeInformation.OSArchitecture != expectedArchitecture)
    {
        throw new PlatformNotSupportedException(
            $"The inner package lane is {RuntimeInformation.OSArchitecture}, not {expectedArchitecture}.");
    }

    var projectPath = Path.Combine("src", "GitSail", "GitSail.csproj");
    var publishDirectory = Path.Combine("artifacts", "publish", rid);
    var packageDirectory = Path.Combine("artifacts", "packages", rid);
    await RunCheckedAsync(
        "dotnet",
        ["restore", "GitSail.slnx"],
        repositoryRoot,
        cancellationToken).ConfigureAwait(false);
    await RunCheckedAsync(
        "dotnet",
        ["build", "GitSail.slnx", "--configuration", "Release", "--no-restore"],
        repositoryRoot,
        cancellationToken).ConfigureAwait(false);
    await RunCheckedAsync(
        "dotnet",
        [
            "test",
            "--solution",
            "GitSail.slnx",
            "--configuration",
            "Release",
            "--no-build",
            "--no-restore",
            "--results-directory",
            Path.Combine("artifacts", "test-results", rid),
            "--",
            "--report-trx",
            "--minimum-expected-tests",
            "1",
        ],
        repositoryRoot,
        cancellationToken).ConfigureAwait(false);
    await RunCheckedAsync(
        "dotnet",
        ["restore", projectPath, "--runtime", rid],
        repositoryRoot,
        cancellationToken).ConfigureAwait(false);
    await RunCheckedAsync(
        "dotnet",
        [
            "publish",
            projectPath,
            "--configuration",
            "Release",
            "--runtime",
            rid,
            "--no-restore",
            "--output",
            publishDirectory,
        ],
        repositoryRoot,
        cancellationToken).ConfigureAwait(false);
    await RunCheckedAsync(
        "dotnet",
        [
            "run",
            "--file",
            Path.Combine("eng", "verify-native-payload.cs"),
            "--",
            "--rid",
            rid,
            "--publish-directory",
            publishDirectory,
        ],
        repositoryRoot,
        cancellationToken).ConfigureAwait(false);
    await RunCheckedWithEnvironmentAsync(
        "dotnet",
        [
            "run",
            "--project",
            Path.Combine("tests", "GitSail.AotTests", "GitSail.AotTests.csproj"),
            "--configuration",
            "Release",
            "--no-build",
            "--",
            "--results-directory",
            Path.Combine("artifacts", "test-results", rid, "aot"),
            "--report-trx",
            "--report-trx-filename",
            "GitSail.AotTests.trx",
            "--minimum-expected-tests",
            "1",
        ],
        repositoryRoot,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GITSAIL_AOT_PUBLISH_DIRECTORY"] = Path.Combine(repositoryRoot, publishDirectory),
            ["GITSAIL_AOT_RID"] = rid,
        },
        cancellationToken).ConfigureAwait(false);
    await RunCheckedAsync(
        "dotnet",
        [
            "pack",
            projectPath,
            "--configuration",
            "Release",
            "--runtime",
            rid,
            "--no-restore",
            "--output",
            packageDirectory,
        ],
        repositoryRoot,
        cancellationToken).ConfigureAwait(false);
    await RunCheckedAsync(
        "dotnet",
        [
            "pack",
            projectPath,
            "--configuration",
            "Release",
            "--no-restore",
            "--output",
            packageDirectory,
        ],
        repositoryRoot,
        cancellationToken).ConfigureAwait(false);
    await RunCheckedAsync(
        "dotnet",
        [
            "run",
            "--file",
            Path.Combine("eng", "generate-native-evidence.cs"),
            "--",
            "--rid",
            rid,
            "--publish-directory",
            publishDirectory,
            "--package-directory",
            packageDirectory,
            "--intermediate-directory",
            Path.Combine("src", "GitSail", "obj", "Release", "net10.0", rid),
            "--evidence-directory",
            Path.Combine("artifacts", "evidence", rid),
        ],
        repositoryRoot,
        cancellationToken).ConfigureAwait(false);
    await RunCheckedAsync(
        "dotnet",
        [
            "run",
            "--file",
            Path.Combine("eng", "generate-supply-chain-evidence.cs"),
            "--",
            "--rid",
            rid,
            "--assets-file",
            Path.Combine("src", "GitSail", "obj", "project.assets.json"),
            "--project",
            projectPath,
            "--package-directory",
            packageDirectory,
            "--evidence-directory",
            Path.Combine("artifacts", "evidence", rid),
        ],
        repositoryRoot,
        cancellationToken).ConfigureAwait(false);
    await RunCheckedWithEnvironmentAsync(
        "dotnet",
        [
            "run",
            "--project",
            Path.Combine("tests", "GitSail.PackageTests", "GitSail.PackageTests.csproj"),
            "--configuration",
            "Release",
            "--no-build",
            "--",
            "--results-directory",
            Path.Combine("artifacts", "test-results", rid, "package"),
            "--report-trx",
            "--report-trx-filename",
            "GitSail.PackageTests.trx",
            "--minimum-expected-tests",
            "1",
        ],
        repositoryRoot,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GITSAIL_PACKAGE_REPOSITORY_ROOT"] = repositoryRoot,
            ["GITSAIL_PACKAGE_DIRECTORY"] = Path.Combine(repositoryRoot, packageDirectory),
            ["GITSAIL_PACKAGE_EVIDENCE_DIRECTORY"] = Path.Combine(
                repositoryRoot,
                "artifacts",
                "evidence",
                rid),
            ["GITSAIL_PACKAGE_RID"] = rid,
        },
        cancellationToken).ConfigureAwait(false);
    return 0;
}

static async Task RunCheckedAsync(
    string fileName,
    IReadOnlyList<string> arguments,
    string workingDirectory,
    CancellationToken cancellationToken)
    => await RunCheckedWithEnvironmentAsync(
        fileName,
        arguments,
        workingDirectory,
        environment: null,
        cancellationToken).ConfigureAwait(false);

static async Task RunCheckedWithEnvironmentAsync(
    string fileName,
    IReadOnlyList<string> arguments,
    string workingDirectory,
    IReadOnlyDictionary<string, string>? environment,
    CancellationToken cancellationToken)
{
    using var process = StartProcess(
        fileName,
        arguments,
        workingDirectory,
        redirectOutput: false,
        environment);
    try
    {
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }
    catch
    {
        await StopProcessAsync(process).ConfigureAwait(false);
        throw;
    }

    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"Process '{fileName}' exited with code {process.ExitCode}.");
    }
}

static async Task<string> RunCapturedAsync(
    string fileName,
    IReadOnlyList<string> arguments,
    string workingDirectory,
    CancellationToken cancellationToken)
{
    using var process = StartProcess(
        fileName,
        arguments,
        workingDirectory,
        redirectOutput: true);
    var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
    var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
    try
    {
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }
    catch
    {
        await StopProcessAsync(process).ConfigureAwait(false);
        throw;
    }

    var output = (await standardOutput.ConfigureAwait(false)).Trim();
    var error = (await standardError.ConfigureAwait(false)).Trim();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"Process '{fileName}' exited with code {process.ExitCode}: {error}");
    }

    if (output.Length == 0)
    {
        throw new InvalidDataException($"Process '{fileName}' returned an empty result.");
    }

    return output;
}

static Process StartProcess(
    string fileName,
    IReadOnlyList<string> arguments,
    string workingDirectory,
    bool redirectOutput,
    IReadOnlyDictionary<string, string>? environment = null)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = fileName,
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = redirectOutput,
        RedirectStandardError = redirectOutput,
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

    var process = new Process { StartInfo = startInfo };
    if (!process.Start())
    {
        process.Dispose();
        throw new InvalidOperationException($"Could not start process '{fileName}'.");
    }

    return process;
}

static async Task StopProcessAsync(Process process)
{
    if (!process.HasExited)
    {
        process.Kill(entireProcessTree: true);
    }

    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
}
