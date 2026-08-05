#:package System.CommandLine

using System.CommandLine;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

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
var evidenceDirectoryOption = new Option<string?>("--evidence-directory")
{
    Description = "The directory containing retained hashes, package manifests, and dependency evidence.",
    Arity = ArgumentArity.ExactlyOne,
};
var rootCommand = new RootCommand("Installs and runs staged GitSail .NET tool packages.");
rootCommand.Options.Add(ridOption);
rootCommand.Options.Add(packageDirectoryOption);
rootCommand.Options.Add(evidenceDirectoryOption);
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

    if (string.IsNullOrWhiteSpace(result.GetValue(evidenceDirectoryOption)))
    {
        result.AddError("Option '--evidence-directory' is required.");
    }
});
rootCommand.SetAction((parseResult, cancellationToken) => VerifyAsync(
    parseResult.GetValue(ridOption)!,
    parseResult.GetValue(packageDirectoryOption)!,
    parseResult.GetValue(evidenceDirectoryOption)!,
    cancellationToken));

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);

static async Task<int> VerifyAsync(
    string rid,
    string packageDirectory,
    string evidenceDirectory,
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

    var evidenceRoot = Path.GetFullPath(evidenceDirectory, repositoryRoot);
    if (!Directory.Exists(evidenceRoot))
    {
        throw new DirectoryNotFoundException($"The retained evidence directory is missing: {evidenceRoot}");
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

    var pointerPackagePath = Path.Combine(packageSource, $"GitSail.{version}.nupkg");
    RequireFile(pointerPackagePath);
    VerifyPointerPackage(pointerPackagePath);
    var ridPackagePath = Path.Combine(packageSource, $"GitSail.{rid}.{version}.nupkg");
    RequireFile(ridPackagePath);
    VerifyRidPackage(ridPackagePath, rid, version);
    await VerifyPackageEvidenceAsync(
        packageSource,
        evidenceRoot,
        rid,
        cancellationToken).ConfigureAwait(false);

    await VerifyToolPathInstallAsync(
        repositoryRoot,
        packageSource,
        rid,
        version,
        cancellationToken).ConfigureAwait(false);
    await VerifyGlobalLifecycleAsync(
        repositoryRoot,
        pointerPackagePath,
        ridPackagePath,
        rid,
        version,
        cancellationToken).ConfigureAwait(false);
    await VerifyLocalManifestInstallAsync(
        repositoryRoot,
        packageSource,
        rid,
        version,
        cancellationToken).ConfigureAwait(false);
    Console.WriteLine($"Verified the complete staged GitSail {version} package lifecycle for {rid}.");
    return 0;
}

static void VerifyPointerPackage(string packagePath)
{
    using var archive = ZipFile.OpenRead(packagePath);
    VerifyExactPackageEntries(
        archive,
        [
            "_rels/.rels",
            "GitSail.nuspec",
            "LICENSE",
            "README.md",
            "tools/net10.0/any/DotnetToolSettings.xml",
            "[Content_Types].xml",
        ]);
}

static async Task VerifyPackageEvidenceAsync(
    string packageSource,
    string evidenceRoot,
    string rid,
    CancellationToken cancellationToken)
{
    var packagePaths = Directory.EnumerateFiles(packageSource, "*.nupkg", SearchOption.TopDirectoryOnly)
        .Order(StringComparer.Ordinal)
        .ToArray();
    if (packagePaths.Length != 2)
    {
        throw new InvalidDataException(
            $"The staged package directory must contain exactly one pointer and one RID package; found " +
            $"{packagePaths.Length}.");
    }

    var expectedSha256 = new Dictionary<string, string>(StringComparer.Ordinal);
    var expectedSha512 = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var packagePath in packagePaths)
    {
        var fileName = Path.GetFileName(packagePath);
        expectedSha256[fileName] = await ComputeHashAsync(
            packagePath,
            HashAlgorithmName.SHA256,
            cancellationToken).ConfigureAwait(false);
        expectedSha512[fileName] = await ComputeHashAsync(
            packagePath,
            HashAlgorithmName.SHA512,
            cancellationToken).ConfigureAwait(false);
    }

    await VerifyHashRecordAsync(
        Path.Combine(evidenceRoot, $"{rid}-packages.sha256"),
        expectedSha256,
        cancellationToken).ConfigureAwait(false);
    await VerifyHashRecordAsync(
        Path.Combine(evidenceRoot, $"{rid}-packages.sha512"),
        expectedSha512,
        cancellationToken).ConfigureAwait(false);
    await VerifyPackageManifestAsync(
        Path.Combine(evidenceRoot, $"{rid}-package-contents.json"),
        rid,
        packagePaths,
        expectedSha256,
        expectedSha512,
        cancellationToken).ConfigureAwait(false);
    await VerifySupplyChainEvidenceAsync(evidenceRoot, rid, cancellationToken).ConfigureAwait(false);
}

static async Task VerifyHashRecordAsync(
    string path,
    IReadOnlyDictionary<string, string> expected,
    CancellationToken cancellationToken)
{
    RequireFile(path);
    var actual = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
    {
        var separator = line.IndexOf("  ", StringComparison.Ordinal);
        if (separator <= 0 || separator + 2 >= line.Length)
        {
            throw new InvalidDataException($"Hash record '{path}' contains an invalid line: '{line}'.");
        }

        actual.Add(line[(separator + 2)..], line[..separator]);
    }

    if (actual.Count != expected.Count ||
        expected.Any(pair =>
            !actual.TryGetValue(pair.Key, out var hash) ||
            !string.Equals(hash, pair.Value, StringComparison.Ordinal)))
    {
        throw new InvalidDataException($"Hash record '{path}' does not match the staged packages.");
    }
}

static async Task VerifyPackageManifestAsync(
    string path,
    string rid,
    IReadOnlyList<string> packagePaths,
    IReadOnlyDictionary<string, string> expectedSha256,
    IReadOnlyDictionary<string, string> expectedSha512,
    CancellationToken cancellationToken)
{
    RequireFile(path);
    await using var input = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    using var document = await JsonDocument.ParseAsync(input, cancellationToken: cancellationToken)
        .ConfigureAwait(false);
    var root = document.RootElement;
    if (root.GetProperty("schemaVersion").GetInt32() != 1 ||
        root.GetProperty("runtimeIdentifier").GetString() != rid)
    {
        throw new InvalidDataException($"Package manifest '{path}' has an invalid identity.");
    }

    var packageRecords = root.GetProperty("packages").EnumerateArray().ToArray();
    if (packageRecords.Length != packagePaths.Count)
    {
        throw new InvalidDataException(
            $"Package manifest '{path}' contains {packageRecords.Length} packages instead of {packagePaths.Count}.");
    }

    foreach (var packagePath in packagePaths)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fileName = Path.GetFileName(packagePath);
        var records = packageRecords
            .Where(record => record.GetProperty("name").GetString() == fileName)
            .ToArray();
        if (records.Length != 1)
        {
            throw new InvalidDataException(
                $"Package manifest '{path}' must contain exactly one record for '{fileName}'.");
        }

        var record = records[0];
        if (record.GetProperty("size").GetInt64() != new FileInfo(packagePath).Length ||
            record.GetProperty("sha256").GetString() != expectedSha256[fileName] ||
            record.GetProperty("sha512").GetString() != expectedSha512[fileName])
        {
            throw new InvalidDataException(
                $"Package manifest '{path}' has invalid size or hashes for '{fileName}'.");
        }

        using var archive = ZipFile.OpenRead(packagePath);
        var entryRecords = record.GetProperty("entries").EnumerateArray().ToArray();
        if (entryRecords.Length != archive.Entries.Count)
        {
            throw new InvalidDataException(
                $"Package manifest '{path}' has {entryRecords.Length} entries for '{fileName}' instead of " +
                $"{archive.Entries.Count}.");
        }

        foreach (var entry in archive.Entries)
        {
            var matchingEntries = entryRecords
                .Where(candidate => candidate.GetProperty("path").GetString() == entry.FullName)
                .ToArray();
            if (matchingEntries.Length != 1)
            {
                throw new InvalidDataException(
                    $"Package manifest '{path}' must contain exactly one '{entry.FullName}' entry.");
            }

            await using var entryStream = entry.Open();
            var entryHash = await ComputeStreamHashAsync(
                entryStream,
                HashAlgorithmName.SHA256,
                cancellationToken).ConfigureAwait(false);
            var entryRecord = matchingEntries[0];
            if (entryRecord.GetProperty("size").GetInt64() != entry.Length ||
                entryRecord.GetProperty("compressedSize").GetInt64() != entry.CompressedLength ||
                entryRecord.GetProperty("sha256").GetString() != entryHash)
            {
                throw new InvalidDataException(
                    $"Package manifest '{path}' has invalid metadata for '{entry.FullName}'.");
            }
        }
    }
}

static async Task VerifySupplyChainEvidenceAsync(
    string evidenceRoot,
    string rid,
    CancellationToken cancellationToken)
{
    var cycloneDxPath = Path.Combine(evidenceRoot, $"{rid}-cyclonedx.json");
    var spdxPath = Path.Combine(evidenceRoot, $"{rid}-spdx.json");
    var licensesPath = Path.Combine(evidenceRoot, $"{rid}-dependency-licenses.json");
    var vulnerabilitiesPath = Path.Combine(evidenceRoot, $"{rid}-vulnerabilities.json");
    foreach (var path in new[] { cycloneDxPath, spdxPath, licensesPath, vulnerabilitiesPath })
    {
        RequireFile(path);
    }

    using var cycloneDx = await ReadJsonAsync(cycloneDxPath, cancellationToken).ConfigureAwait(false);
    if (cycloneDx.RootElement.GetProperty("bomFormat").GetString() != "CycloneDX" ||
        cycloneDx.RootElement.GetProperty("specVersion").GetString() != "1.6")
    {
        throw new InvalidDataException($"CycloneDX evidence '{cycloneDxPath}' has an invalid identity.");
    }

    using var spdx = await ReadJsonAsync(spdxPath, cancellationToken).ConfigureAwait(false);
    if (spdx.RootElement.GetProperty("spdxVersion").GetString() != "SPDX-2.3" ||
        spdx.RootElement.GetProperty("dataLicense").GetString() != "CC0-1.0")
    {
        throw new InvalidDataException($"SPDX evidence '{spdxPath}' has an invalid identity.");
    }

    using var licenses = await ReadJsonAsync(licensesPath, cancellationToken).ConfigureAwait(false);
    if (licenses.RootElement.GetProperty("schemaVersion").GetInt32() != 1 ||
        licenses.RootElement.GetProperty("runtimeIdentifier").GetString() != rid ||
        !licenses.RootElement.GetProperty("passed").GetBoolean())
    {
        throw new InvalidDataException($"Dependency-license evidence '{licensesPath}' is not passing.");
    }

    using var vulnerabilities = await ReadJsonAsync(vulnerabilitiesPath, cancellationToken).ConfigureAwait(false);
    if (vulnerabilities.RootElement.GetProperty("schemaVersion").GetInt32() != 1 ||
        vulnerabilities.RootElement.GetProperty("runtimeIdentifier").GetString() != rid ||
        vulnerabilities.RootElement.GetProperty("status").GetString() != "no-known-vulnerabilities")
    {
        throw new InvalidDataException($"Vulnerability evidence '{vulnerabilitiesPath}' is not passing.");
    }
}

static async Task<JsonDocument> ReadJsonAsync(string path, CancellationToken cancellationToken)
{
    await using var input = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    return await JsonDocument.ParseAsync(input, cancellationToken: cancellationToken).ConfigureAwait(false);
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
                "--no-http-cache",
            ],
            repositoryRoot,
            echoOutput: true,
            environment: null,
            cancellationToken).ConfigureAwait(false);
        installed = true;
        var executable = FindInstalledExecutable(toolPath);
        VerifyInstalledCommandPermissions(executable);
        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var environment = new Dictionary<string, string?>
        {
            ["PATH"] = toolPath + Path.PathSeparator + currentPath,
        };

        _ = await RunInstalledToolAsync(
            executable,
            ["--version"],
            echoOutput: true,
            environment,
            cancellationToken).ConfigureAwait(false);
        var doctorJson = await RunInstalledToolAsync(
            executable,
            ["doctor", "--json"],
            echoOutput: false,
            environment,
            cancellationToken).ConfigureAwait(false);
        VerifyDoctorReport(doctorJson, rid);
        await VerifyEmbeddedDocumentationAsync(
            executable,
            environment,
            cancellationToken).ConfigureAwait(false);

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

static async Task VerifyGlobalLifecycleAsync(
    string repositoryRoot,
    string pointerPackagePath,
    string ridPackagePath,
    string rid,
    string version,
    CancellationToken cancellationToken)
{
    var lifecycleRoot = Path.Combine(
        repositoryRoot,
        "artifacts",
        "global-tool-lifecycle",
        $"{rid}-{Guid.NewGuid():N}");
    var home = Path.Combine(lifecycleRoot, "home");
    var packages = Path.Combine(lifecycleRoot, "nuget-packages");
    var httpCache = Path.Combine(lifecycleRoot, "nuget-http-cache");
    var state = Path.Combine(lifecycleRoot, "state");
    var toolPath = Path.Combine(home, ".dotnet", "tools");
    var lifecycleSource = Path.Combine(lifecycleRoot, "source");
    Directory.CreateDirectory(home);
    Directory.CreateDirectory(packages);
    Directory.CreateDirectory(httpCache);
    Directory.CreateDirectory(state);
    Directory.CreateDirectory(lifecycleSource);
    var stateSentinel = Path.Combine(state, "preserved-user-state.txt");
    var stateContent = $"GitSail package lifecycle state sentinel {Guid.NewGuid():N}";
    await File.WriteAllTextAsync(stateSentinel, stateContent, cancellationToken).ConfigureAwait(false);

    var previousVersion = CreatePreviousVersion(version);
    File.Copy(pointerPackagePath, Path.Combine(lifecycleSource, Path.GetFileName(pointerPackagePath)));
    File.Copy(ridPackagePath, Path.Combine(lifecycleSource, Path.GetFileName(ridPackagePath)));
    CreateVersionedPackage(
        pointerPackagePath,
        Path.Combine(lifecycleSource, $"GitSail.{previousVersion}.nupkg"),
        rid,
        version,
        previousVersion);
    CreateVersionedPackage(
        ridPackagePath,
        Path.Combine(lifecycleSource, $"GitSail.{rid}.{previousVersion}.nupkg"),
        rid,
        version,
        previousVersion);

    var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
    var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["DOTNET_CLI_HOME"] = home,
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["DOTNET_CLI_UI_LANGUAGE"] = "en-US",
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
        ["HOME"] = home,
        ["NUGET_HTTP_CACHE_PATH"] = httpCache,
        ["NUGET_PACKAGES"] = packages,
        ["PATH"] = toolPath + Path.PathSeparator + currentPath,
        ["USERPROFILE"] = home,
        ["XDG_CACHE_HOME"] = Path.Combine(lifecycleRoot, "cache"),
        ["XDG_CONFIG_HOME"] = Path.Combine(lifecycleRoot, "config"),
        ["XDG_STATE_HOME"] = state,
    };
    var installed = false;

    try
    {
        _ = await RunCheckedAsync(
            "dotnet",
            [
                "tool",
                "install",
                "GitSail",
                "--global",
                "--version",
                previousVersion,
                "--add-source",
                lifecycleSource,
                "--no-http-cache",
            ],
            repositoryRoot,
            echoOutput: true,
            environment,
            cancellationToken).ConfigureAwait(false);
        installed = true;
        await VerifyGlobalToolVersionAsync(
            repositoryRoot,
            previousVersion,
            environment,
            cancellationToken).ConfigureAwait(false);

        _ = await RunCheckedAsync(
            "dotnet",
            [
                "tool",
                "update",
                "GitSail",
                "--global",
                "--version",
                version,
                "--add-source",
                lifecycleSource,
                "--no-http-cache",
            ],
            repositoryRoot,
            echoOutput: true,
            environment,
            cancellationToken).ConfigureAwait(false);
        await VerifyGlobalToolVersionAsync(
            repositoryRoot,
            version,
            environment,
            cancellationToken).ConfigureAwait(false);

        _ = await RunCheckedAsync(
            "dotnet",
            [
                "tool",
                "update",
                "GitSail",
                "--global",
                "--version",
                previousVersion,
                "--allow-downgrade",
                "--add-source",
                lifecycleSource,
                "--no-http-cache",
            ],
            repositoryRoot,
            echoOutput: true,
            environment,
            cancellationToken).ConfigureAwait(false);
        await VerifyGlobalToolVersionAsync(
            repositoryRoot,
            previousVersion,
            environment,
            cancellationToken).ConfigureAwait(false);

        _ = await RunCheckedAsync(
            "dotnet",
            [
                "tool",
                "update",
                "GitSail",
                "--global",
                "--version",
                version,
                "--add-source",
                lifecycleSource,
                "--no-http-cache",
            ],
            repositoryRoot,
            echoOutput: true,
            environment,
            cancellationToken).ConfigureAwait(false);
        await VerifyGlobalToolVersionAsync(
            repositoryRoot,
            version,
            environment,
            cancellationToken).ConfigureAwait(false);

        var executable = FindInstalledExecutable(toolPath);
        VerifyInstalledCommandPermissions(executable);
        var displayedVersion = await RunInstalledToolAsync(
            executable,
            ["--version"],
            echoOutput: false,
            environment,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(displayedVersion))
        {
            throw new InvalidDataException("The globally installed tool returned an empty product version.");
        }

        var doctorJson = await RunInstalledToolAsync(
            executable,
            ["doctor", "--json"],
            echoOutput: false,
            environment,
            cancellationToken).ConfigureAwait(false);
        VerifyDoctorReport(doctorJson, rid);
        await VerifyEmbeddedDocumentationAsync(
            executable,
            environment,
            cancellationToken).ConfigureAwait(false);
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
                    ["tool", "uninstall", "GitSail", "--global"],
                    repositoryRoot,
                    echoOutput: true,
                    environment,
                    CancellationToken.None).ConfigureAwait(false);
                if (FindExistingInstalledExecutable(toolPath) is { } remainingCommand)
                {
                    throw new InvalidOperationException(
                        $"The global tool command remains after uninstall: {remainingCommand}");
                }
            }

            var retainedState = await File.ReadAllTextAsync(stateSentinel, CancellationToken.None)
                .ConfigureAwait(false);
            if (!string.Equals(retainedState, stateContent, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The package lifecycle changed documented user state.");
            }
        }
        finally
        {
            DeleteDirectory(lifecycleRoot);
        }
    }
}

static async Task VerifyGlobalToolVersionAsync(
    string repositoryRoot,
    string expectedVersion,
    IReadOnlyDictionary<string, string?> environment,
    CancellationToken cancellationToken)
{
    var json = await RunCheckedAsync(
        "dotnet",
        ["tool", "list", "GitSail", "--global", "--format", "json"],
        repositoryRoot,
        echoOutput: false,
        environment,
        cancellationToken).ConfigureAwait(false);
    using var document = JsonDocument.Parse(json);
    var tools = document.RootElement.GetProperty("data").EnumerateArray().ToArray();
    if (tools.Length != 1 ||
        !string.Equals(tools[0].GetProperty("packageId").GetString(), "GitSail", StringComparison.OrdinalIgnoreCase) ||
        tools[0].GetProperty("version").GetString() != expectedVersion ||
        tools[0].GetProperty("commands").EnumerateArray().Single().GetString() != "git-tui")
    {
        throw new InvalidDataException(
            $"The isolated global tool inventory does not contain GitSail {expectedVersion} exactly once.");
    }
}

static string CreatePreviousVersion(string version)
{
    var coreVersion = version.Split('-', 2, StringSplitOptions.None)[0];
    var components = coreVersion.Split('.');
    if (components.Length != 3 ||
        components.Any(component =>
            !int.TryParse(component, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out _)))
    {
        throw new InvalidDataException(
            $"The package lifecycle requires a three-component semantic version; found '{version}'.");
    }

    var major = int.Parse(components[0], System.Globalization.CultureInfo.InvariantCulture);
    var minor = int.Parse(components[1], System.Globalization.CultureInfo.InvariantCulture);
    var patch = int.Parse(components[2], System.Globalization.CultureInfo.InvariantCulture);
    if (patch > 0)
    {
        patch--;
    }
    else if (minor > 0)
    {
        minor--;
    }
    else if (major > 0)
    {
        major--;
    }
    else
    {
        throw new InvalidDataException("Package version 0.0.0 has no lower lifecycle-test version.");
    }

    return string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{major}.{minor}.{patch}");
}

static void CreateVersionedPackage(
    string sourcePath,
    string destinationPath,
    string rid,
    string currentVersion,
    string packageVersion)
{
    using var source = ZipFile.OpenRead(sourcePath);
    using var destinationFile = new FileStream(
        destinationPath,
        FileMode.CreateNew,
        FileAccess.ReadWrite,
        FileShare.None);
    using var destination = new ZipArchive(destinationFile, ZipArchiveMode.Create, leaveOpen: false);
    foreach (var sourceEntry in source.Entries)
    {
        var destinationEntry = destination.CreateEntry(sourceEntry.FullName, CompressionLevel.Optimal);
        destinationEntry.LastWriteTime = sourceEntry.LastWriteTime;
        destinationEntry.ExternalAttributes = sourceEntry.ExternalAttributes;
        using var sourceStream = sourceEntry.Open();
        using var destinationStream = destinationEntry.Open();
        if (sourceEntry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) ||
            sourceEntry.FullName.EndsWith(".psmdcp", StringComparison.OrdinalIgnoreCase))
        {
            RewritePackageVersionXml(sourceStream, destinationStream, packageVersion);
        }
        else if (sourceEntry.FullName.EndsWith(
            $"/shims/{rid}/git-tui.exe",
            StringComparison.OrdinalIgnoreCase))
        {
            RewriteWindowsLauncherPath(
                sourceStream,
                destinationStream,
                rid,
                currentVersion,
                packageVersion);
        }
        else
        {
            sourceStream.CopyTo(destinationStream);
        }
    }
}

static void RewritePackageVersionXml(Stream source, Stream destination, string version)
{
    var document = XDocument.Load(source, LoadOptions.PreserveWhitespace);
    var versionElements = document
        .Descendants()
        .Where(element => element.Name.LocalName == "version")
        .ToArray();
    if (versionElements.Length != 1)
    {
        throw new InvalidDataException(
            $"A lifecycle-test package XML part contains {versionElements.Length} version elements.");
    }

    versionElements[0].Value = version;
    document.Save(destination, SaveOptions.DisableFormatting);
}

static void RewriteWindowsLauncherPath(
    Stream source,
    Stream destination,
    string rid,
    string currentVersion,
    string packageVersion)
{
    using var buffer = new MemoryStream();
    source.CopyTo(buffer);
    var bytes = buffer.ToArray();
    var currentPath = Encoding.UTF8.GetBytes(
        $".store/gitsail/{currentVersion}/gitsail.{rid}/{currentVersion}/tools/any/{rid}/" +
        "GitSail.ToolLauncher.dll");
    var packagePath = Encoding.UTF8.GetBytes(
        $".store/gitsail/{packageVersion}/gitsail.{rid}/{packageVersion}/tools/any/{rid}/" +
        "GitSail.ToolLauncher.dll");
    if (packagePath.Length > currentPath.Length)
    {
        throw new InvalidDataException(
            "The generated lower package version does not fit the Windows apphost path slot.");
    }

    var index = bytes.AsSpan().IndexOf(currentPath);
    if (index < 0)
    {
        throw new InvalidDataException("The Windows apphost does not contain its current package-store path.");
    }

    packagePath.CopyTo(bytes.AsSpan(index, packagePath.Length));
    bytes.AsSpan(index + packagePath.Length, currentPath.Length - packagePath.Length).Clear();
    destination.Write(bytes);
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
                "--no-http-cache",
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
                "--no-http-cache",
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

    if (!root.TryGetProperty("command", out var command) ||
        !command.TryGetProperty("pathStatus", out var pathStatus) ||
        pathStatus.GetString() is not { } pathStatusText ||
        !pathStatusText.StartsWith("available on PATH at ", StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            "The installed tool Doctor report does not identify the installed command on PATH.");
    }
}

static async Task VerifyEmbeddedDocumentationAsync(
    string executable,
    IReadOnlyDictionary<string, string?> environment,
    CancellationToken cancellationToken)
{
    var help = await RunInstalledToolAsync(
        executable,
        ["help"],
        echoOutput: false,
        environment,
        cancellationToken).ConfigureAwait(false);
    foreach (var requiredText in new[]
    {
        "Usage:",
        "Offline manual:",
        "Installation and invocation",
        "completion <bash|fish|powershell|zsh>",
    })
    {
        RequireText(help, requiredText, "embedded offline manual");
    }

    var expectedShellMarkers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["bash"] = "complete -o default -F _git_tui_complete git-tui",
        ["zsh"] = "#compdef git-tui",
        ["fish"] = "complete -c git-tui",
        ["powershell"] = "Register-ArgumentCompleter",
    };
    foreach (var shell in expectedShellMarkers)
    {
        var completion = await RunInstalledToolAsync(
            executable,
            ["completion", shell.Key],
            echoOutput: false,
            environment,
            cancellationToken).ConfigureAwait(false);
        RequireText(completion, "Install:", $"{shell.Key} completion instructions");
        RequireText(completion, "completion-candidates", $"{shell.Key} completion script");
        RequireText(completion, shell.Value, $"{shell.Key} completion script");
    }
}

static void VerifyInstalledCommandPermissions(string executable)
{
    if (OperatingSystem.IsWindows())
    {
        if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The Windows installed command is not an executable: {executable}");
        }

        var commandFile = Path.ChangeExtension(executable, ".cmd");
        if (File.Exists(commandFile))
        {
            throw new InvalidDataException($"The Windows tool install emitted a prohibited command file: {commandFile}");
        }

        return;
    }

    var mode = File.GetUnixFileMode(executable);
    if ((mode & UnixFileMode.UserExecute) == 0 ||
        (mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
    {
        throw new InvalidDataException(
            $"The installed command has unsafe Unix permissions '{mode}': {executable}");
    }
}

static void RequireText(string value, string expected, string subject)
{
    if (!value.Contains(expected, StringComparison.Ordinal))
    {
        throw new InvalidDataException($"The {subject} does not contain '{expected}'.");
    }
}

static Task<string> RunInstalledToolAsync(
    string executable,
    IReadOnlyList<string> arguments,
    bool echoOutput,
    IReadOnlyDictionary<string, string?> environment,
    CancellationToken cancellationToken)
{
    var workingDirectory = Path.GetDirectoryName(executable) ??
        throw new InvalidOperationException($"The installed command has no parent directory: {executable}");
    return RunCheckedAsync(
        executable,
        arguments,
        workingDirectory,
        echoOutput,
        environment,
        cancellationToken);
}

static void VerifyRidPackage(string packagePath, string rid, string version)
{
    using var archive = ZipFile.OpenRead(packagePath);
    var entryNames = archive.Entries
        .Select(entry => entry.FullName)
        .ToHashSet(StringComparer.Ordinal);
    var toolRoot = $"tools/any/{rid}/";
    var executableName = rid.StartsWith("win-", StringComparison.Ordinal) ? "git-tui.exe" : "git-tui";
    var expectedEntries = new List<string>
    {
        "_rels/.rels",
        $"GitSail.{rid}.nuspec",
        "LICENSE",
        "README.md",
        toolRoot + "DotnetToolSettings.xml",
        toolRoot + executableName,
        "[Content_Types].xml",
    };

    var launcherEntries = new[]
    {
        toolRoot + "GitSail.ToolLauncher.dll",
        toolRoot + "GitSail.ToolLauncher.deps.json",
        toolRoot + "GitSail.ToolLauncher.runtimeconfig.json",
        toolRoot + $"shims/{rid}/git-tui.exe",
    };
    if (rid.StartsWith("win-", StringComparison.Ordinal))
    {
        expectedEntries.AddRange(launcherEntries);
        foreach (var entry in launcherEntries)
        {
            RequirePackageEntry(entryNames, entry);
        }

        var shimEntryName = toolRoot + $"shims/{rid}/git-tui.exe";
        var shimEntry = archive.GetEntry(shimEntryName) ??
            throw new InvalidDataException($"The staged RID package is missing '{shimEntryName}'.");
        using var shimStream = shimEntry.Open();
        using var shimBuffer = new MemoryStream();
        shimStream.CopyTo(shimBuffer);
        var expectedLauncherPath = Encoding.UTF8.GetBytes(
            $".store/gitsail/{version}/gitsail.{rid}/{version}/tools/any/{rid}/GitSail.ToolLauncher.dll");
        if (shimBuffer.GetBuffer().AsSpan(0, checked((int)shimBuffer.Length)).IndexOf(expectedLauncherPath) < 0)
        {
            throw new InvalidDataException(
                $"The Windows command apphost in '{packagePath}' does not target its RID package launcher.");
        }
    }
    else if (launcherEntries.Any(entryNames.Contains))
    {
        throw new InvalidDataException(
            $"The non-Windows RID package '{packagePath}' contains Windows launcher files.");
    }

    VerifyExactPackageEntries(archive, expectedEntries);
}

static void VerifyExactPackageEntries(ZipArchive archive, IReadOnlyCollection<string> expectedEntries)
{
    const string corePropertiesPrefix = "package/services/metadata/core-properties/";
    const string corePropertiesSuffix = ".psmdcp";
    var actualEntries = archive.Entries
        .Select(entry => entry.FullName)
        .ToHashSet(StringComparer.Ordinal);
    var corePropertiesEntries = actualEntries
        .Where(entry =>
            entry.StartsWith(corePropertiesPrefix, StringComparison.Ordinal) &&
            entry.EndsWith(corePropertiesSuffix, StringComparison.Ordinal))
        .ToArray();
    if (corePropertiesEntries.Length != 1)
    {
        throw new InvalidDataException(
            $"The staged package must contain exactly one NuGet core-properties entry; found " +
            $"{corePropertiesEntries.Length}.");
    }

    var expected = expectedEntries.ToHashSet(StringComparer.Ordinal);
    expected.Add(corePropertiesEntries[0]);
    var missing = expected.Except(actualEntries, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    var unexpected = actualEntries.Except(expected, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    if (missing.Length > 0 || unexpected.Length > 0)
    {
        throw new InvalidDataException(
            $"The staged package contents do not match the approved runtime inventory. " +
            $"Missing: {FormatEntries(missing)}. Unexpected: {FormatEntries(unexpected)}.");
    }
}

static string FormatEntries(IReadOnlyCollection<string> entries)
    => entries.Count == 0 ? "none" : string.Join(", ", entries);

static void RequirePackageEntry(IReadOnlySet<string> entryNames, string entryName)
{
    if (!entryNames.Contains(entryName))
    {
        throw new InvalidDataException($"The staged RID package is missing '{entryName}'.");
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

static async Task<string> ComputeHashAsync(
    string path,
    HashAlgorithmName algorithm,
    CancellationToken cancellationToken)
{
    await using var input = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    return await ComputeStreamHashAsync(input, algorithm, cancellationToken).ConfigureAwait(false);
}

static async Task<string> ComputeStreamHashAsync(
    Stream input,
    HashAlgorithmName algorithm,
    CancellationToken cancellationToken)
{
    using var hash = IncrementalHash.CreateHash(algorithm);
    var buffer = new byte[64 * 1024];
    while (true)
    {
        var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            break;
        }

        hash.AppendData(buffer, 0, read);
    }

    return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
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
    var fileName = OperatingSystem.IsWindows() ? "git-tui.exe" : "git-tui";
    var candidate = Path.Combine(toolPath, fileName);
    return File.Exists(candidate) ? candidate : null;
}

static void DeleteDirectory(string path)
{
    if (Directory.Exists(path))
    {
        Directory.Delete(path, recursive: true);
    }
}
