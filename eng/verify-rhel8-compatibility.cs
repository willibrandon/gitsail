#:package System.CommandLine

using System.CommandLine;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

var ridOption = new Option<string?>("--rid")
{
    Description = "The glibc Linux runtime identifier to verify.",
    Arity = ArgumentArity.ExactlyOne,
};
var publishDirectoryOption = new Option<string?>("--publish-directory")
{
    Description = "The directory containing the Native AOT payload.",
    Arity = ArgumentArity.ExactlyOne,
};
var evidenceDirectoryOption = new Option<string?>("--evidence-directory")
{
    Description = "The directory that will receive the RHEL 8 compatibility report.",
    Arity = ArgumentArity.ExactlyOne,
};
var rootCommand = new RootCommand(
    "Executes a GitSail Native AOT payload in a pinned native-architecture RHEL 8 container.");
rootCommand.Options.Add(ridOption);
rootCommand.Options.Add(publishDirectoryOption);
rootCommand.Options.Add(evidenceDirectoryOption);
rootCommand.Validators.Add(result =>
{
    if (result.GetValue(ridOption) is not ("linux-x64" or "linux-arm64"))
    {
        result.AddError("Option '--rid' must be 'linux-x64' or 'linux-arm64'.");
    }

    if (string.IsNullOrWhiteSpace(result.GetValue(publishDirectoryOption)))
    {
        result.AddError("Option '--publish-directory' is required.");
    }

    if (string.IsNullOrWhiteSpace(result.GetValue(evidenceDirectoryOption)))
    {
        result.AddError("Option '--evidence-directory' is required.");
    }
});
rootCommand.SetAction((parseResult, cancellationToken) => VerifyAsync(
    parseResult.GetValue(ridOption)!,
    parseResult.GetValue(publishDirectoryOption)!,
    parseResult.GetValue(evidenceDirectoryOption)!,
    cancellationToken));

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);

static async Task<int> VerifyAsync(
    string rid,
    string publishDirectory,
    string evidenceDirectory,
    CancellationToken cancellationToken)
{
    if (!OperatingSystem.IsLinux())
    {
        throw new PlatformNotSupportedException("RHEL 8 compatibility verification requires a Linux host.");
    }

    var expectedArchitecture = rid == "linux-x64" ? Architecture.X64 : Architecture.Arm64;
    if (RuntimeInformation.OSArchitecture != expectedArchitecture)
    {
        throw new PlatformNotSupportedException(
            $"Runtime identifier '{rid}' requires a native {expectedArchitecture} host, not " +
            $"{RuntimeInformation.OSArchitecture}.");
    }

    var workingDirectory = Directory.GetCurrentDirectory();
    var publishRoot = Path.GetFullPath(publishDirectory, workingDirectory);
    var evidenceRoot = Path.GetFullPath(evidenceDirectory, workingDirectory);
    var applicationPath = Path.Combine(publishRoot, "git-tui");
    if (!File.Exists(applicationPath))
    {
        throw new FileNotFoundException("The GitSail Native AOT payload is missing.", applicationPath);
    }

    const string image =
        "registry.access.redhat.com/ubi8/dotnet-90-runtime:latest@" +
        "sha256:0f3727a3551ec3feace6d87432450f6df11af2a365ac8df23082181135da5cdf";
    var platform = rid == "linux-x64" ? "linux/amd64" : "linux/arm64";
    var userId = await RunCapturedAsync(
        "id",
        ["--user"],
        workingDirectory,
        cancellationToken).ConfigureAwait(false);
    var groupId = await RunCapturedAsync(
        "id",
        ["--group"],
        workingDirectory,
        cancellationToken).ConfigureAwait(false);
    var user = $"{userId}:{groupId}";

    var osRelease = await RunContainerAsync(
        image,
        platform,
        publishRoot,
        user,
        workingDirectory,
        "/usr/bin/cat",
        ["/etc/os-release"],
        cancellationToken).ConfigureAwait(false);
    var osFields = ParseOsRelease(osRelease);
    if (!osFields.TryGetValue("ID", out var operatingSystemId) || operatingSystemId != "rhel" ||
        !osFields.TryGetValue("VERSION_ID", out var operatingSystemVersion) ||
        !operatingSystemVersion.StartsWith("8.", StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            $"The compatibility image is not RHEL 8: {osRelease.ReplaceLineEndings(" ")}");
    }

    var glibcPackage = await QueryPackageAsync(
        image,
        platform,
        publishRoot,
        user,
        workingDirectory,
        "glibc",
        cancellationToken).ConfigureAwait(false);
    if (!glibcPackage.StartsWith("glibc 2.28-", StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            $"The RHEL 8 compatibility image does not carry glibc 2.28: {glibcPackage}");
    }

    var icuPackage = await QueryPackageAsync(
        image,
        platform,
        publishRoot,
        user,
        workingDirectory,
        "libicu",
        cancellationToken).ConfigureAwait(false);
    var versionOutput = await RunApplicationAsync(
        image,
        platform,
        publishRoot,
        user,
        workingDirectory,
        ["--version"],
        cancellationToken).ConfigureAwait(false);
    if (!versionOutput.StartsWith("GitSail ", StringComparison.Ordinal))
    {
        throw new InvalidDataException($"The RHEL 8 version output is invalid: {versionOutput}");
    }

    var helpOutput = await RunApplicationAsync(
        image,
        platform,
        publishRoot,
        user,
        workingDirectory,
        ["--help"],
        cancellationToken).ConfigureAwait(false);
    if (!helpOutput.Contains("Usage:", StringComparison.Ordinal) ||
        !helpOutput.Contains("doctor", StringComparison.Ordinal))
    {
        throw new InvalidDataException("The RHEL 8 help output is incomplete.");
    }

    var doctorOutput = await RunApplicationAsync(
        image,
        platform,
        publishRoot,
        user,
        workingDirectory,
        ["doctor", "--json"],
        cancellationToken).ConfigureAwait(false);
    using var doctor = JsonDocument.Parse(doctorOutput);
    var doctorRoot = doctor.RootElement;
    if (doctorRoot.GetProperty("product").GetString() != "GitSail" ||
        doctorRoot.GetProperty("runtimeIdentifier").GetString() != rid ||
        !doctorRoot.GetProperty("nativeAot").GetBoolean())
    {
        throw new InvalidDataException("The RHEL 8 Doctor report does not describe the verified payload.");
    }

    Directory.CreateDirectory(evidenceRoot);
    var evidencePath = Path.Combine(evidenceRoot, $"{rid}-rhel8-compatibility.json");
    await WriteEvidenceAsync(
        evidencePath,
        rid,
        expectedArchitecture,
        image,
        platform,
        operatingSystemVersion,
        glibcPackage,
        icuPackage,
        versionOutput,
        cancellationToken).ConfigureAwait(false);
    Console.WriteLine($"Verified {rid} on RHEL {operatingSystemVersion} with {glibcPackage}.");
    return 0;
}

static async Task<string> QueryPackageAsync(
    string image,
    string platform,
    string publishDirectory,
    string user,
    string workingDirectory,
    string packageName,
    CancellationToken cancellationToken)
    => await RunContainerAsync(
        image,
        platform,
        publishDirectory,
        user,
        workingDirectory,
        "/usr/bin/rpm",
        [
            "--query",
            "--queryformat",
            "%{NAME} %{VERSION}-%{RELEASE}.%{ARCH}\\n",
            packageName,
        ],
        cancellationToken).ConfigureAwait(false);

static async Task<string> RunApplicationAsync(
    string image,
    string platform,
    string publishDirectory,
    string user,
    string workingDirectory,
    IReadOnlyList<string> arguments,
    CancellationToken cancellationToken)
    => await RunContainerAsync(
        image,
        platform,
        publishDirectory,
        user,
        workingDirectory,
        "/app/git-tui",
        arguments,
        cancellationToken).ConfigureAwait(false);

static async Task<string> RunContainerAsync(
    string image,
    string platform,
    string publishDirectory,
    string user,
    string workingDirectory,
    string entryPoint,
    IReadOnlyList<string> arguments,
    CancellationToken cancellationToken)
{
    var dockerArguments = new List<string>
    {
        "run",
        "--rm",
        "--platform",
        platform,
        "--network",
        "none",
        "--read-only",
        "--cap-drop",
        "ALL",
        "--security-opt",
        "no-new-privileges=true",
        "--user",
        user,
        "--tmpfs",
        "/tmp:rw,nosuid,nodev,mode=1777",
        "--volume",
        $"{publishDirectory}:/app:ro",
        "--workdir",
        "/app",
        "--env",
        "HOME=/tmp",
        "--env",
        "TMPDIR=/tmp",
        "--entrypoint",
        entryPoint,
        image,
    };
    dockerArguments.AddRange(arguments);
    return await RunCapturedAsync(
        "docker",
        dockerArguments,
        workingDirectory,
        cancellationToken).ConfigureAwait(false);
}

static Dictionary<string, string> ParseOsRelease(string contents)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var line in contents.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        var separator = line.IndexOf('=');
        if (separator <= 0)
        {
            continue;
        }

        var value = line[(separator + 1)..].Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1];
        }

        result[line[..separator]] = value;
    }

    return result;
}

static async Task WriteEvidenceAsync(
    string path,
    string rid,
    Architecture architecture,
    string image,
    string platform,
    string operatingSystemVersion,
    string glibcPackage,
    string icuPackage,
    string versionOutput,
    CancellationToken cancellationToken)
{
    await using var output = new FileStream(
        path,
        FileMode.Create,
        FileAccess.Write,
        FileShare.None,
        bufferSize: 4096,
        FileOptions.Asynchronous);
    await using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
    writer.WriteStartObject();
    writer.WriteNumber("schemaVersion", 1);
    writer.WriteString("runtimeIdentifier", rid);
    writer.WriteString("architecture", architecture.ToString());
    writer.WriteString("containerImage", image);
    writer.WriteString("containerPlatform", platform);
    writer.WriteString("operatingSystem", "Red Hat Enterprise Linux");
    writer.WriteString("operatingSystemVersion", operatingSystemVersion);
    writer.WriteString("glibcPackage", glibcPackage);
    writer.WriteString("icuPackage", icuPackage);
    writer.WriteString("applicationVersion", versionOutput);
    writer.WriteStartArray("verifiedCommands");
    writer.WriteStringValue("git-tui --version");
    writer.WriteStringValue("git-tui --help");
    writer.WriteStringValue("git-tui doctor --json");
    writer.WriteEndArray();
    writer.WriteEndObject();
    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
}

static async Task<string> RunCapturedAsync(
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

    var output = (await standardOutput.ConfigureAwait(false)).Trim();
    var error = (await standardError.ConfigureAwait(false)).Trim();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"Process '{fileName}' exited with code {process.ExitCode}." +
            $"{Environment.NewLine}{output}{Environment.NewLine}{error}");
    }

    if (output.Length == 0)
    {
        throw new InvalidDataException($"Process '{fileName}' returned an empty result.");
    }

    return output;
}
