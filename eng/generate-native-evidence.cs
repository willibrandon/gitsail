#:package System.CommandLine

using System.Buffers.Binary;
using System.CommandLine;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var ridOption = new Option<string?>("--rid")
{
    Description = "The Native AOT runtime identifier represented by the artifacts.",
    Arity = ArgumentArity.ExactlyOne,
};
var publishDirectoryOption = new Option<string?>("--publish-directory")
{
    Description = "The directory containing the published Native AOT payload.",
    Arity = ArgumentArity.ExactlyOne,
};
var packageDirectoryOption = new Option<string?>("--package-directory")
{
    Description = "The directory containing the pointer and RID-specific tool packages.",
    Arity = ArgumentArity.ExactlyOne,
};
var intermediateDirectoryOption = new Option<string?>("--intermediate-directory")
{
    Description = "The RID-specific MSBuild intermediate directory containing Native AOT build records.",
    Arity = ArgumentArity.ExactlyOne,
};
var evidenceDirectoryOption = new Option<string?>("--evidence-directory")
{
    Description = "The output directory for hashes, manifests, and native import reports.",
    Arity = ArgumentArity.ExactlyOne,
};
var rootCommand = new RootCommand(
    "Generates and validates retained release evidence for one Native AOT tool package.");
rootCommand.Options.Add(ridOption);
rootCommand.Options.Add(publishDirectoryOption);
rootCommand.Options.Add(packageDirectoryOption);
rootCommand.Options.Add(intermediateDirectoryOption);
rootCommand.Options.Add(evidenceDirectoryOption);
rootCommand.Validators.Add(result =>
{
    if (result.GetValue(ridOption) is not
        ("win-x64" or "win-arm64" or
         "linux-x64" or "linux-arm64" or
         "linux-musl-x64" or "linux-musl-arm64" or
         "osx-x64" or "osx-arm64"))
    {
        result.AddError("Option '--rid' must name one of GitSail's eight supported RIDs.");
    }

    if (string.IsNullOrWhiteSpace(result.GetValue(publishDirectoryOption)))
    {
        result.AddError("Option '--publish-directory' is required.");
    }

    if (string.IsNullOrWhiteSpace(result.GetValue(packageDirectoryOption)))
    {
        result.AddError("Option '--package-directory' is required.");
    }

    if (string.IsNullOrWhiteSpace(result.GetValue(intermediateDirectoryOption)))
    {
        result.AddError("Option '--intermediate-directory' is required.");
    }

    if (string.IsNullOrWhiteSpace(result.GetValue(evidenceDirectoryOption)))
    {
        result.AddError("Option '--evidence-directory' is required.");
    }
});
rootCommand.SetAction((parseResult, cancellationToken) => GenerateAsync(
    parseResult.GetValue(ridOption)!,
    parseResult.GetValue(publishDirectoryOption)!,
    parseResult.GetValue(packageDirectoryOption)!,
    parseResult.GetValue(intermediateDirectoryOption)!,
    parseResult.GetValue(evidenceDirectoryOption)!,
    cancellationToken));

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);

static async Task<int> GenerateAsync(
    string rid,
    string publishDirectory,
    string packageDirectory,
    string intermediateDirectory,
    string evidenceDirectory,
    CancellationToken cancellationToken)
{
    var workingDirectory = Directory.GetCurrentDirectory();
    var publishRoot = RequireDirectory(publishDirectory, workingDirectory, "publish");
    var packageRoot = RequireDirectory(packageDirectory, workingDirectory, "package");
    var intermediateRoot = RequireDirectory(intermediateDirectory, workingDirectory, "intermediate");
    var evidenceRoot = Path.GetFullPath(evidenceDirectory, workingDirectory);
    Directory.CreateDirectory(evidenceRoot);

    var packages = Directory.EnumerateFiles(packageRoot, "*.nupkg", SearchOption.TopDirectoryOnly)
        .Order(StringComparer.Ordinal)
        .ToArray();
    if (packages.Length != 2)
    {
        throw new InvalidDataException(
            $"Release evidence requires exactly the pointer and RID package; found {packages.Length}.");
    }

    var expectedRidMarker = $"GitSail.{rid}.";
    if (packages.Count(path => Path.GetFileName(path).StartsWith(expectedRidMarker, StringComparison.Ordinal)) != 1 ||
        packages.Count(path =>
            Path.GetFileName(path).StartsWith("GitSail.", StringComparison.Ordinal) &&
            !Path.GetFileName(path).StartsWith(expectedRidMarker, StringComparison.Ordinal)) != 1)
    {
        throw new InvalidDataException(
            $"The package directory does not contain one GitSail pointer and one '{rid}' package.");
    }

    await WritePackageHashesAsync(packages, evidenceRoot, rid, cancellationToken).ConfigureAwait(false);
    await WritePackageManifestAsync(packages, evidenceRoot, rid, cancellationToken).ConfigureAwait(false);
    await WritePayloadManifestAsync(publishRoot, evidenceRoot, rid, cancellationToken).ConfigureAwait(false);
    await WriteNativeImportReportAsync(publishRoot, evidenceRoot, rid, cancellationToken).ConfigureAwait(false);
    await WriteDiagnosticEvidenceAsync(
        publishRoot,
        intermediateRoot,
        evidenceRoot,
        rid,
        cancellationToken).ConfigureAwait(false);

    Console.WriteLine($"Generated Native AOT release evidence for {rid} in {evidenceRoot}.");
    return 0;
}

static async Task WriteDiagnosticEvidenceAsync(
    string publishRoot,
    string intermediateRoot,
    string evidenceRoot,
    string rid,
    CancellationToken cancellationToken)
{
    var sourceRevision = (await RunCheckedAsync(
        "git",
        ["rev-parse", "--verify", "HEAD"],
        cancellationToken).ConfigureAwait(false)).Trim();
    if (sourceRevision.Length != 40 || sourceRevision.Any(character => !char.IsAsciiHexDigit(character)))
    {
        throw new InvalidDataException($"Git returned an invalid source revision: '{sourceRevision}'.");
    }

    sourceRevision = sourceRevision.ToLowerInvariant();
    var trackedChanges = await RunCheckedAsync(
        "git",
        ["status", "--porcelain=v1", "--untracked-files=no"],
        cancellationToken).ConfigureAwait(false);
    if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(trackedChanges))
    {
        throw new InvalidDataException("Release evidence cannot be generated from tracked working-tree changes.");
    }

    await ValidateAndCopySourceLinkAsync(
        intermediateRoot,
        evidenceRoot,
        rid,
        sourceRevision,
        cancellationToken).ConfigureAwait(false);
    await WriteSymbolReportAsync(
        publishRoot,
        evidenceRoot,
        rid,
        sourceRevision,
        cancellationToken).ConfigureAwait(false);
    await WriteToolchainReportAsync(
        intermediateRoot,
        evidenceRoot,
        rid,
        sourceRevision,
        cancellationToken).ConfigureAwait(false);
}

static async Task ValidateAndCopySourceLinkAsync(
    string intermediateRoot,
    string evidenceRoot,
    string rid,
    string sourceRevision,
    CancellationToken cancellationToken)
{
    var sourceLinkPath = Path.Combine(intermediateRoot, "GitSail.sourcelink.json");
    if (!File.Exists(sourceLinkPath))
    {
        throw new FileNotFoundException("The Native AOT build did not produce a Source Link record.", sourceLinkPath);
    }

    await using var input = new FileStream(
        sourceLinkPath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 4096,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    using var document = await JsonDocument.ParseAsync(input, cancellationToken: cancellationToken)
        .ConfigureAwait(false);
    if (!document.RootElement.TryGetProperty("documents", out var documents) ||
        documents.ValueKind != JsonValueKind.Object)
    {
        throw new InvalidDataException("The Source Link record has no documents map.");
    }

    var mappings = documents.EnumerateObject().ToArray();
    var expectedUrl = $"https://raw.githubusercontent.com/willibrandon/gitsail/{sourceRevision}/*";
    if (mappings.Length != 1 ||
        mappings[0].Name != "/_/*" ||
        mappings[0].Value.ValueKind != JsonValueKind.String ||
        mappings[0].Value.GetString() != expectedUrl)
    {
        throw new InvalidDataException(
            $"The Source Link record must map '/_/*' to the exact source revision '{expectedUrl}'.");
    }

    await CopyEvidenceFileAsync(
        sourceLinkPath,
        Path.Combine(evidenceRoot, $"{rid}-source-link.json"),
        cancellationToken).ConfigureAwait(false);
}

static async Task WriteSymbolReportAsync(
    string publishRoot,
    string evidenceRoot,
    string rid,
    string sourceRevision,
    CancellationToken cancellationToken)
{
    var executableName = rid.StartsWith("win-", StringComparison.Ordinal) ? "git-tui.exe" : "git-tui";
    var executablePath = Path.Combine(publishRoot, executableName);
    if (!File.Exists(executablePath))
    {
        throw new FileNotFoundException("The Native AOT executable is missing.", executablePath);
    }

    string symbolKind;
    string symbolRoot;
    string symbolImage;
    string? referencedSymbolName = null;
    string[] executableIdentifiers;
    string[] symbolIdentifiers;
    if (rid.StartsWith("win-", StringComparison.Ordinal))
    {
        symbolKind = "pdb";
        symbolRoot = Path.Combine(publishRoot, "git-tui.pdb");
        symbolImage = symbolRoot;
        (executableIdentifiers, referencedSymbolName) = await ReadWindowsBuildIdentifiersAsync(
            executablePath,
            cancellationToken).ConfigureAwait(false);
        symbolIdentifiers = executableIdentifiers;
        if (!Path.GetFileName(referencedSymbolName).Equals("git-tui.pdb", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The executable's CodeView record refers to '{referencedSymbolName}', not 'git-tui.pdb'.");
        }
    }
    else if (rid.StartsWith("osx-", StringComparison.Ordinal))
    {
        symbolKind = "dSYM";
        symbolRoot = Path.Combine(publishRoot, "git-tui.dSYM");
        symbolImage = Path.Combine(symbolRoot, "Contents", "Resources", "DWARF", "git-tui");
        executableIdentifiers = await ReadMacBuildIdentifiersAsync(executablePath, cancellationToken)
            .ConfigureAwait(false);
        symbolIdentifiers = await ReadMacBuildIdentifiersAsync(symbolImage, cancellationToken)
            .ConfigureAwait(false);
    }
    else
    {
        symbolKind = "ELF debug file";
        symbolRoot = Path.Combine(publishRoot, "git-tui.dbg");
        symbolImage = symbolRoot;
        executableIdentifiers = await ReadLinuxBuildIdentifiersAsync(executablePath, cancellationToken)
            .ConfigureAwait(false);
        symbolIdentifiers = await ReadLinuxBuildIdentifiersAsync(symbolImage, cancellationToken)
            .ConfigureAwait(false);
    }

    if (File.Exists(symbolRoot))
    {
        if (new FileInfo(symbolRoot).Length == 0)
        {
            throw new InvalidDataException($"The symbol artifact is empty: {symbolRoot}");
        }
    }
    else if (!Directory.Exists(symbolRoot))
    {
        throw new FileNotFoundException("The Native AOT symbol artifact is missing.", symbolRoot);
    }

    if (!File.Exists(symbolImage))
    {
        throw new FileNotFoundException("The symbol artifact has no native debug image.", symbolImage);
    }

    if (executableIdentifiers.Length == 0 ||
        !executableIdentifiers.SequenceEqual(symbolIdentifiers, StringComparer.Ordinal))
    {
        throw new InvalidDataException(
            "The executable and retained symbol artifact do not carry the same build identifier.");
    }

    var symbolFiles = File.Exists(symbolRoot)
        ? [symbolRoot]
        : Directory.EnumerateFiles(symbolRoot, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();
    if (symbolFiles.Length == 0)
    {
        throw new InvalidDataException("The retained symbol artifact contains no files.");
    }

    await using var output = CreateEvidenceFile(evidenceRoot, $"{rid}-symbols.json");
    await using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
    writer.WriteStartObject();
    writer.WriteNumber("schemaVersion", 1);
    writer.WriteString("runtimeIdentifier", rid);
    writer.WriteString("sourceRevision", sourceRevision);
    writer.WriteString("symbolKind", symbolKind);
    writer.WriteString("sourceLinkRecord", $"{rid}-source-link.json");
    writer.WriteStartObject("executable");
    writer.WriteString("path", NormalizePath(Path.GetRelativePath(publishRoot, executablePath)));
    writer.WriteNumber("size", new FileInfo(executablePath).Length);
    writer.WriteString(
        "sha256",
        await ComputeHashAsync(executablePath, HashAlgorithmName.SHA256, cancellationToken).ConfigureAwait(false));
    writer.WriteStartArray("buildIdentifiers");
    foreach (var identifier in executableIdentifiers)
    {
        writer.WriteStringValue(identifier);
    }

    writer.WriteEndArray();
    if (referencedSymbolName is not null)
    {
        writer.WriteString("referencedSymbolName", referencedSymbolName);
    }

    writer.WriteEndObject();
    writer.WriteStartArray("symbolFiles");
    foreach (var file in symbolFiles)
    {
        writer.WriteStartObject();
        writer.WriteString("path", NormalizePath(Path.GetRelativePath(publishRoot, file)));
        writer.WriteNumber("size", new FileInfo(file).Length);
        writer.WriteString(
            "sha256",
            await ComputeHashAsync(file, HashAlgorithmName.SHA256, cancellationToken).ConfigureAwait(false));
        writer.WriteEndObject();
    }

    writer.WriteEndArray();
    writer.WriteEndObject();
    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
}

static async Task<string[]> ReadLinuxBuildIdentifiersAsync(
    string path,
    CancellationToken cancellationToken)
{
    var output = await RunCheckedAsync("readelf", ["--notes", "--wide", path], cancellationToken)
        .ConfigureAwait(false);
    const string marker = "Build ID:";
    return
    [
        .. output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith(marker, StringComparison.Ordinal))
            .Select(line => line[marker.Length..].Trim().ToLowerInvariant())
            .Where(identifier => identifier.Length > 0 && identifier.All(char.IsAsciiHexDigit))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
    ];
}

static async Task<string[]> ReadMacBuildIdentifiersAsync(
    string path,
    CancellationToken cancellationToken)
{
    var output = await RunCheckedAsync("dwarfdump", ["--uuid", path], cancellationToken).ConfigureAwait(false);
    const string marker = "UUID: ";
    return
    [
        .. output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith(marker, StringComparison.Ordinal))
            .Select(line => line[marker.Length..])
            .Select(line =>
            {
                var pathStart = line.IndexOf(") ", StringComparison.Ordinal);
                return pathStart >= 0 ? line[..(pathStart + 1)].ToLowerInvariant() : line.ToLowerInvariant();
            })
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
    ];
}

static async Task<(string[] Identifiers, string ReferencedSymbolName)> ReadWindowsBuildIdentifiersAsync(
    string path,
    CancellationToken cancellationToken)
{
    var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    using var stream = new MemoryStream(bytes, writable: false);
    using var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
    var records = reader.ReadDebugDirectory()
        .Where(entry => entry.Type == DebugDirectoryEntryType.CodeView)
        .Select(reader.ReadCodeViewDebugDirectoryData)
        .ToArray();
    if (records.Length != 1)
    {
        throw new InvalidDataException(
            $"The Windows executable must contain exactly one CodeView record; found {records.Length}.");
    }

    var record = records[0];
    return (
        [$"{record.Guid:N}-{record.Age}".ToLowerInvariant()],
        Path.GetFileName(record.Path.Replace('\\', '/')));
}

static async Task WriteToolchainReportAsync(
    string intermediateRoot,
    string evidenceRoot,
    string rid,
    string sourceRevision,
    CancellationToken cancellationToken)
{
    var nativeRoot = Path.Combine(intermediateRoot, "native");
    var compilerPathRecord = RequireFile(nativeRoot, "git-tui.ilc-path.txt");
    var compilerArguments = RequireFile(nativeRoot, "git-tui.ilc.rsp");
    var linkerPathRecord = RequireFile(nativeRoot, "git-tui.linker-path.txt");
    var linkerArguments = RequireFile(nativeRoot, "git-tui.linker.rsp");
    if (new FileInfo(compilerArguments).Length == 0 || new FileInfo(linkerArguments).Length == 0)
    {
        throw new InvalidDataException("The Native AOT compiler or linker argument record is empty.");
    }

    var compilerPath = ResolveExecutablePath(await ReadSingleLineAsync(
        compilerPathRecord,
        cancellationToken).ConfigureAwait(false));
    var linkerPath = ResolveExecutablePath(await ReadSingleLineAsync(
        linkerPathRecord,
        cancellationToken).ConfigureAwait(false));
    var compilerVersion = await RunCapturedAsync(
        compilerPath,
        ["--version"],
        cancellationToken).ConfigureAwait(false);
    if (compilerVersion.ExitCode != 0 ||
        string.IsNullOrWhiteSpace(compilerVersion.StandardOutput + compilerVersion.StandardError))
    {
        throw new InvalidDataException("The Native AOT compiler did not report its version.");
    }

    var linkerVersion = await RunCapturedAsync(
        linkerPath,
        OperatingSystem.IsWindows() ? ["/?"] : ["--version"],
        cancellationToken).ConfigureAwait(false);
    if (string.IsNullOrWhiteSpace(linkerVersion.StandardOutput + linkerVersion.StandardError))
    {
        throw new InvalidDataException("The native linker did not report its version.");
    }

    var sdkVersion = (await RunCheckedAsync("dotnet", ["--version"], cancellationToken)
        .ConfigureAwait(false)).Trim();
    var sdkInformation = await RunCheckedAsync("dotnet", ["--info"], cancellationToken).ConfigureAwait(false);
    var retainedFiles = new[]
    {
        (Source: compilerArguments, Name: $"{rid}-ilc.rsp"),
        (Source: linkerArguments, Name: $"{rid}-linker.rsp"),
    };
    foreach (var file in retainedFiles)
    {
        await CopyEvidenceFileAsync(
            file.Source,
            Path.Combine(evidenceRoot, file.Name),
            cancellationToken).ConfigureAwait(false);
    }

    await using var output = CreateEvidenceFile(evidenceRoot, $"{rid}-toolchain.json");
    await using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
    writer.WriteStartObject();
    writer.WriteNumber("schemaVersion", 1);
    writer.WriteString("runtimeIdentifier", rid);
    writer.WriteString("sourceRevision", sourceRevision);
    writer.WriteStartObject("dotnetSdk");
    writer.WriteString("version", sdkVersion);
    writer.WriteString("information", sdkInformation.Trim());
    writer.WriteEndObject();
    await WriteToolRecordAsync(
        writer,
        "nativeAotCompiler",
        compilerPath,
        compilerVersion,
        $"{rid}-ilc.rsp",
        compilerArguments,
        cancellationToken).ConfigureAwait(false);
    await WriteToolRecordAsync(
        writer,
        "nativeLinker",
        linkerPath,
        linkerVersion,
        $"{rid}-linker.rsp",
        linkerArguments,
        cancellationToken).ConfigureAwait(false);
    writer.WriteEndObject();
    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
}

static async Task WriteToolRecordAsync(
    Utf8JsonWriter writer,
    string propertyName,
    string executablePath,
    (int ExitCode, string StandardOutput, string StandardError) version,
    string argumentRecordName,
    string argumentRecordPath,
    CancellationToken cancellationToken)
{
    writer.WriteStartObject(propertyName);
    writer.WriteString("executable", executablePath);
    writer.WriteString(
        "executableSha256",
        await ComputeHashAsync(executablePath, HashAlgorithmName.SHA256, cancellationToken).ConfigureAwait(false));
    writer.WriteNumber("versionExitCode", version.ExitCode);
    writer.WriteString("versionStandardOutput", version.StandardOutput.Trim());
    writer.WriteString("versionStandardError", version.StandardError.Trim());
    writer.WriteString("argumentRecord", argumentRecordName);
    writer.WriteString(
        "argumentRecordSha256",
        await ComputeHashAsync(argumentRecordPath, HashAlgorithmName.SHA256, cancellationToken)
            .ConfigureAwait(false));
    writer.WriteEndObject();
}

static string RequireFile(string directory, string fileName)
{
    var path = Path.Combine(directory, fileName);
    if (!File.Exists(path))
    {
        throw new FileNotFoundException($"Required Native AOT build record '{fileName}' is missing.", path);
    }

    return path;
}

static async Task<string> ReadSingleLineAsync(string path, CancellationToken cancellationToken)
{
    var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
    if (lines.Length != 1 || string.IsNullOrWhiteSpace(lines[0]))
    {
        throw new InvalidDataException($"Build record '{path}' must contain exactly one non-empty line.");
    }

    return lines[0].Trim().TrimStart('\uFEFF').Trim('"');
}

static string ResolveExecutablePath(string path)
{
    if (Path.IsPathRooted(path))
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("A recorded tool executable is missing.", path);
        }

        return Path.GetFullPath(path);
    }

    var extensions = OperatingSystem.IsWindows()
        ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
        : [string.Empty];
    foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
    {
        foreach (var extension in extensions)
        {
            var candidate = Path.Combine(directory, path);
            if (OperatingSystem.IsWindows() && string.IsNullOrEmpty(Path.GetExtension(candidate)))
            {
                candidate += extension;
            }

            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }
    }

    throw new FileNotFoundException($"Recorded tool executable '{path}' was not found on PATH.");
}

static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunCapturedAsync(
    string fileName,
    IReadOnlyList<string> arguments,
    CancellationToken cancellationToken)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = fileName,
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

static async Task CopyEvidenceFileAsync(
    string sourcePath,
    string destinationPath,
    CancellationToken cancellationToken)
{
    await using var source = new FileStream(
        sourcePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    await using var destination = new FileStream(
        destinationPath,
        FileMode.Create,
        FileAccess.Write,
        FileShare.None,
        bufferSize: 64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
}

static string RequireDirectory(string path, string workingDirectory, string description)
{
    var fullPath = Path.GetFullPath(path, workingDirectory);
    if (!Directory.Exists(fullPath))
    {
        throw new DirectoryNotFoundException($"The {description} directory is missing: {fullPath}");
    }

    return fullPath;
}

static async Task WritePackageHashesAsync(
    IReadOnlyList<string> packages,
    string evidenceRoot,
    string rid,
    CancellationToken cancellationToken)
{
    var sha256 = new StringBuilder();
    var sha512 = new StringBuilder();
    foreach (var package in packages)
    {
        var fileName = Path.GetFileName(package);
        sha256.Append(await ComputeHashAsync(package, HashAlgorithmName.SHA256, cancellationToken)
            .ConfigureAwait(false));
        sha256.Append("  ");
        sha256.AppendLine(fileName);
        sha512.Append(await ComputeHashAsync(package, HashAlgorithmName.SHA512, cancellationToken)
            .ConfigureAwait(false));
        sha512.Append("  ");
        sha512.AppendLine(fileName);
    }

    await File.WriteAllTextAsync(
        Path.Combine(evidenceRoot, $"{rid}-packages.sha256"),
        sha256.ToString(),
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        cancellationToken).ConfigureAwait(false);
    await File.WriteAllTextAsync(
        Path.Combine(evidenceRoot, $"{rid}-packages.sha512"),
        sha512.ToString(),
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        cancellationToken).ConfigureAwait(false);
}

static async Task WritePackageManifestAsync(
    IReadOnlyList<string> packages,
    string evidenceRoot,
    string rid,
    CancellationToken cancellationToken)
{
    await using var output = CreateEvidenceFile(evidenceRoot, $"{rid}-package-contents.json");
    await using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
    writer.WriteStartObject();
    writer.WriteNumber("schemaVersion", 1);
    writer.WriteString("runtimeIdentifier", rid);
    writer.WriteStartArray("packages");
    foreach (var package in packages)
    {
        cancellationToken.ThrowIfCancellationRequested();
        writer.WriteStartObject();
        writer.WriteString("name", Path.GetFileName(package));
        writer.WriteNumber("size", new FileInfo(package).Length);
        writer.WriteString(
            "sha256",
            await ComputeHashAsync(package, HashAlgorithmName.SHA256, cancellationToken).ConfigureAwait(false));
        writer.WriteString(
            "sha512",
            await ComputeHashAsync(package, HashAlgorithmName.SHA512, cancellationToken).ConfigureAwait(false));
        writer.WriteStartArray("entries");
        using (var archive = ZipFile.OpenRead(package))
        {
            foreach (var entry in archive.Entries.OrderBy(entry => entry.FullName, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.WriteStartObject();
                writer.WriteString("path", entry.FullName);
                writer.WriteNumber("size", entry.Length);
                writer.WriteNumber("compressedSize", entry.CompressedLength);
                await using var entryStream = entry.Open();
                writer.WriteString(
                    "sha256",
                    await ComputeStreamHashAsync(entryStream, HashAlgorithmName.SHA256, cancellationToken)
                        .ConfigureAwait(false));
                writer.WriteEndObject();
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    writer.WriteEndArray();
    writer.WriteEndObject();
    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
}

static async Task WritePayloadManifestAsync(
    string publishRoot,
    string evidenceRoot,
    string rid,
    CancellationToken cancellationToken)
{
    var files = Directory.EnumerateFiles(publishRoot, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .ToArray();
    if (files.Length == 0)
    {
        throw new InvalidDataException("The Native AOT publish directory is empty.");
    }

    await using var output = CreateEvidenceFile(evidenceRoot, $"{rid}-payload-manifest.json");
    await using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
    writer.WriteStartObject();
    writer.WriteNumber("schemaVersion", 1);
    writer.WriteString("runtimeIdentifier", rid);
    writer.WriteStartArray("files");
    foreach (var file in files)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var relativePath = NormalizePath(Path.GetRelativePath(publishRoot, file));
        writer.WriteStartObject();
        writer.WriteString("path", relativePath);
        writer.WriteString("kind", ClassifyPayloadFile(relativePath, rid));
        writer.WriteNumber("size", new FileInfo(file).Length);
        writer.WriteString(
            "sha256",
            await ComputeHashAsync(file, HashAlgorithmName.SHA256, cancellationToken).ConfigureAwait(false));
        writer.WriteEndObject();
    }

    writer.WriteEndArray();
    writer.WriteEndObject();
    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
}

static string ClassifyPayloadFile(string relativePath, string rid)
{
    var fileName = Path.GetFileName(relativePath);
    if (relativePath.Contains("/shims/", StringComparison.Ordinal) ||
        relativePath.StartsWith("shims/", StringComparison.Ordinal))
    {
        return "command-shim";
    }

    if (relativePath.Contains(".dSYM/", StringComparison.Ordinal) ||
        Path.GetExtension(fileName) is ".dbg" or ".pdb")
    {
        return "symbol";
    }

    if (fileName == (rid.StartsWith("win-", StringComparison.Ordinal) ? "git-tui.exe" : "git-tui"))
    {
        return "application";
    }

    if (fileName is "hex1bpty.exe" or "libhex1binterop.so" or "libhex1binterop.dylib")
    {
        return "dependency-native-asset";
    }

    if (Path.GetExtension(fileName) == ".xml")
    {
        return "documentation";
    }

    return "runtime-asset";
}

static async Task WriteNativeImportReportAsync(
    string publishRoot,
    string evidenceRoot,
    string rid,
    CancellationToken cancellationToken)
{
    var nativeFiles = FindNativeFiles(publishRoot, rid);
    if (nativeFiles.Length == 0)
    {
        throw new InvalidDataException("No Native AOT application or dependency native assets were found.");
    }

    var reportPath = Path.Combine(evidenceRoot, $"{rid}-native-imports.json");
    var rejected = new List<string>();
    await using (var output = new FileStream(
        reportPath,
        FileMode.Create,
        FileAccess.Write,
        FileShare.None,
        bufferSize: 4096,
        FileOptions.Asynchronous))
    await using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", 1);
        writer.WriteString("runtimeIdentifier", rid);
        writer.WriteStartArray("files");
        foreach (var file in nativeFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var imports = await ReadNativeImportsAsync(file, rid, cancellationToken).ConfigureAwait(false);
            writer.WriteStartObject();
            writer.WriteString("path", NormalizePath(Path.GetRelativePath(publishRoot, file)));
            writer.WriteString(
                "sha256",
                await ComputeHashAsync(file, HashAlgorithmName.SHA256, cancellationToken).ConfigureAwait(false));
            writer.WriteStartArray("imports");
            foreach (var import in imports)
            {
                writer.WriteStartObject();
                writer.WriteString("name", import);
                var allowed = IsAllowedImport(rid, import);
                writer.WriteBoolean("allowed", allowed);
                writer.WriteEndObject();
                if (!allowed)
                {
                    rejected.Add($"{Path.GetFileName(file)} -> {import}");
                }
            }

            writer.WriteEndArray();
            if (rid is "linux-x64" or "linux-arm64")
            {
                var glibcVersions = await ReadGlibcVersionsAsync(file, cancellationToken).ConfigureAwait(false);
                writer.WriteStartArray("glibcVersions");
                foreach (var version in glibcVersions)
                {
                    writer.WriteStringValue(version.ToString());
                }

                writer.WriteEndArray();
                var maximumVersion = glibcVersions.LastOrDefault();
                writer.WriteString("maximumGlibcVersion", maximumVersion?.ToString());
                if (maximumVersion is null)
                {
                    rejected.Add($"{Path.GetFileName(file)} -> no GLIBC symbol-version requirements found");
                }
                else if (maximumVersion > new Version(2, 27))
                {
                    rejected.Add(
                        $"{Path.GetFileName(file)} -> GLIBC_{maximumVersion} exceeds GLIBC_2.27");
                }
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    if (rejected.Count > 0)
    {
        throw new InvalidDataException(
            $"Native imports outside the approved operating-system/runtime allowlist were found: " +
            string.Join(", ", rejected));
    }
}

static async Task<Version[]> ReadGlibcVersionsAsync(
    string path,
    CancellationToken cancellationToken)
{
    var output = await RunCheckedAsync(
        "readelf",
        ["--version-info", "--wide", path],
        cancellationToken).ConfigureAwait(false);
    var versions = new HashSet<Version>();
    var searchFrom = 0;
    const string prefix = "GLIBC_";
    while (true)
    {
        var start = output.IndexOf(prefix, searchFrom, StringComparison.Ordinal);
        if (start < 0)
        {
            break;
        }

        start += prefix.Length;
        var end = start;
        while (end < output.Length && (char.IsAsciiDigit(output[end]) || output[end] == '.'))
        {
            end++;
        }

        if (Version.TryParse(output.AsSpan(start, end - start), out var version))
        {
            versions.Add(version);
        }

        searchFrom = end;
    }

    return [.. versions.Order()];
}

static string[] FindNativeFiles(string publishRoot, string rid)
{
    var files = Directory.EnumerateFiles(publishRoot, "*", SearchOption.AllDirectories);
    return
    [
        .. files
        .Where(file => IsNativeFile(file, rid))
        .Order(StringComparer.Ordinal),
    ];
}

static bool IsNativeFile(string path, string rid)
{
    var normalizedPath = NormalizePath(path);
    if (normalizedPath.Contains(".dSYM/", StringComparison.Ordinal))
    {
        return false;
    }

    var fileName = Path.GetFileName(path);
    if (rid.StartsWith("win-", StringComparison.Ordinal))
    {
        return Path.GetExtension(fileName).Equals(".exe", StringComparison.OrdinalIgnoreCase);
    }

    if (rid.StartsWith("osx-", StringComparison.Ordinal))
    {
        return fileName == "git-tui" ||
            Path.GetExtension(fileName).Equals(".dylib", StringComparison.Ordinal);
    }

    return fileName == "git-tui" ||
        fileName.EndsWith(".so", StringComparison.Ordinal) ||
        fileName.Contains(".so.", StringComparison.Ordinal);
}

static async Task<string[]> ReadNativeImportsAsync(
    string path,
    string rid,
    CancellationToken cancellationToken)
{
    if (rid.StartsWith("win-", StringComparison.Ordinal))
    {
        return await ReadPortableExecutableImportsAsync(path, cancellationToken).ConfigureAwait(false);
    }

    var output = rid.StartsWith("osx-", StringComparison.Ordinal)
        ? await RunCheckedAsync("otool", ["-L", path], cancellationToken).ConfigureAwait(false)
        : await RunCheckedAsync("ldd", [path], cancellationToken).ConfigureAwait(false);
    return rid.StartsWith("osx-", StringComparison.Ordinal)
        ? ParseMacOSImports(output)
        : ParseLinuxImports(output);
}

static string[] ParseMacOSImports(string output)
    =>
    [
        .. output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Skip(1)
        .Select(line => line.Trim())
        .Where(line => line.Length > 0)
        .Select(line =>
        {
            var details = line.IndexOf(" (compatibility version ", StringComparison.Ordinal);
            return details >= 0 ? line[..details] : line.Split(' ', 2)[0];
        })
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal),
    ];

static string[] ParseLinuxImports(string output)
    =>
    [
        .. output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim())
        .Where(line => line.Length > 0 && line != "statically linked")
        .Select(line =>
        {
            var mapping = line.IndexOf(" => ", StringComparison.Ordinal);
            return mapping >= 0 ? line[..mapping] : line.Split(' ', 2)[0];
        })
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal),
    ];

static async Task<string[]> ReadPortableExecutableImportsAsync(
    string path,
    CancellationToken cancellationToken)
{
    var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    using var stream = new MemoryStream(bytes, writable: false);
    using var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
    if (reader.PEHeaders.CorHeader is not null)
    {
        return [];
    }

    var header = reader.PEHeaders.PEHeader ??
        throw new InvalidDataException($"Native executable '{path}' has no PE header.");
    var imports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    ReadImportDirectory(bytes, reader.PEHeaders, header.ImportTableDirectory, imports);
    ReadDelayImportDirectory(bytes, reader.PEHeaders, header.DelayImportTableDirectory, header.ImageBase, imports);
    return [.. imports.Order(StringComparer.OrdinalIgnoreCase)];
}

static void ReadImportDirectory(
    byte[] image,
    PEHeaders headers,
    DirectoryEntry directory,
    ISet<string> imports)
{
    if (directory.RelativeVirtualAddress == 0 || directory.Size == 0)
    {
        return;
    }

    var offset = RvaToOffset(headers, directory.RelativeVirtualAddress);
    var end = Math.Min(image.Length, checked(offset + directory.Size));
    while (offset + 20 <= end)
    {
        var descriptor = image.AsSpan(offset, 20);
        if (descriptor.IndexOfAnyExcept((byte)0) < 0)
        {
            break;
        }

        var nameRva = BinaryPrimitives.ReadInt32LittleEndian(descriptor[12..16]);
        imports.Add(ReadAsciiName(image, RvaToOffset(headers, nameRva)));
        offset += 20;
    }
}

static void ReadDelayImportDirectory(
    byte[] image,
    PEHeaders headers,
    DirectoryEntry directory,
    ulong imageBase,
    ISet<string> imports)
{
    if (directory.RelativeVirtualAddress == 0 || directory.Size == 0)
    {
        return;
    }

    var offset = RvaToOffset(headers, directory.RelativeVirtualAddress);
    var end = Math.Min(image.Length, checked(offset + directory.Size));
    while (offset + 32 <= end)
    {
        var descriptor = image.AsSpan(offset, 32);
        if (descriptor.IndexOfAnyExcept((byte)0) < 0)
        {
            break;
        }

        var attributes = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[..4]);
        var encodedName = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[4..8]);
        var nameRva = (attributes & 1) != 0
            ? checked((int)encodedName)
            : checked((int)(encodedName - imageBase));
        imports.Add(ReadAsciiName(image, RvaToOffset(headers, nameRva)));
        offset += 32;
    }
}

static int RvaToOffset(PEHeaders headers, int rva)
{
    foreach (var section in headers.SectionHeaders)
    {
        var length = Math.Max(section.VirtualSize, section.SizeOfRawData);
        if (rva >= section.VirtualAddress && rva < section.VirtualAddress + length)
        {
            return checked(section.PointerToRawData + rva - section.VirtualAddress);
        }
    }

    throw new InvalidDataException($"PE relative virtual address 0x{rva:X8} is outside every section.");
}

static string ReadAsciiName(byte[] image, int offset)
{
    if ((uint)offset >= (uint)image.Length)
    {
        throw new InvalidDataException("A PE import name points outside the image.");
    }

    var end = Array.IndexOf(image, (byte)0, offset);
    if (end < 0)
    {
        throw new InvalidDataException("A PE import name is not NUL-terminated.");
    }

    return Encoding.ASCII.GetString(image, offset, end - offset);
}

static bool IsAllowedImport(string rid, string import)
{
    if (rid.StartsWith("osx-", StringComparison.Ordinal))
    {
        return import.StartsWith("/usr/lib/", StringComparison.Ordinal) ||
            import.StartsWith("/System/Library/Frameworks/", StringComparison.Ordinal) ||
            import == "@rpath/libhex1binterop.dylib";
    }

    var fileName = Path.GetFileName(import);
    if (rid.StartsWith("win-", StringComparison.Ordinal))
    {
        return fileName.StartsWith("api-ms-win-", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("ext-ms-win-", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("ADVAPI32.dll", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("bcrypt.dll", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("KERNEL32.dll", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("ole32.dll", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("SHELL32.dll", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("USER32.dll", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("WS2_32.dll", StringComparison.OrdinalIgnoreCase);
    }

    return fileName is
        "ld-linux-aarch64.so.1" or
        "ld-linux-x86-64.so.2" or
        "ld-musl-aarch64.so.1" or
        "ld-musl-x86_64.so.1" or
        "libc.so.6" or
        "libdl.so.2" or
        "libgcc_s.so.1" or
        "libhex1binterop.so" or
        "libm.so.6" or
        "libpthread.so.0" or
        "libresolv.so.2" or
        "librt.so.1" or
        "libstdc++.so.6" or
        "libutil.so.1" or
        "libz.so.1" or
        "linux-gate.so.1" or
        "linux-vdso.so.1" ||
        fileName.StartsWith("libc.musl-", StringComparison.Ordinal) ||
        fileName.StartsWith("libicudata.so.", StringComparison.Ordinal) ||
        fileName.StartsWith("libicui18n.so.", StringComparison.Ordinal) ||
        fileName.StartsWith("libicuuc.so.", StringComparison.Ordinal);
}

static async Task<string> RunCheckedAsync(
    string fileName,
    IReadOnlyList<string> arguments,
    CancellationToken cancellationToken)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = fileName,
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

    var output = await standardOutput.ConfigureAwait(false);
    var error = await standardError.ConfigureAwait(false);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"Process '{fileName}' exited with code {process.ExitCode}.{Environment.NewLine}{output}{error}");
    }

    return output;
}

static FileStream CreateEvidenceFile(string evidenceRoot, string fileName)
    => new(
        Path.Combine(evidenceRoot, fileName),
        FileMode.Create,
        FileAccess.Write,
        FileShare.None,
        bufferSize: 4096,
        FileOptions.Asynchronous);

static async Task<string> ComputeHashAsync(
    string path,
    HashAlgorithmName algorithm,
    CancellationToken cancellationToken)
{
    await using var stream = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    return await ComputeStreamHashAsync(stream, algorithm, cancellationToken).ConfigureAwait(false);
}

static async Task<string> ComputeStreamHashAsync(
    Stream stream,
    HashAlgorithmName algorithm,
    CancellationToken cancellationToken)
{
    using var hash = IncrementalHash.CreateHash(algorithm);
    var buffer = new byte[64 * 1024];
    while (true)
    {
        var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            break;
        }

        hash.AppendData(buffer, 0, read);
    }

    return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
}

static string NormalizePath(string path)
    => path.Replace(Path.DirectorySeparatorChar, '/');
