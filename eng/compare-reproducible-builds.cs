#:package System.CommandLine

using System.CommandLine;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

var inputDirectoryOption = new Option<string?>("--input-directory")
{
    Description = "The directory containing two independently built artifacts for every supported RID.",
    Arity = ArgumentArity.ExactlyOne,
};
var outputDirectoryOption = new Option<string?>("--output-directory")
{
    Description = "The empty directory that will receive selected packages, evidence, and comparison results.",
    Arity = ArgumentArity.ExactlyOne,
};
var sourceRevisionOption = new Option<string?>("--source-revision")
{
    Description = "The full source revision represented by both builders.",
    Arity = ArgumentArity.ExactlyOne,
};
var rootCommand = new RootCommand(
    "Compares two native builders per RID and selects one proven-reproducible release artifact set.");
rootCommand.Options.Add(inputDirectoryOption);
rootCommand.Options.Add(outputDirectoryOption);
rootCommand.Options.Add(sourceRevisionOption);
rootCommand.Validators.Add(result =>
{
    if (string.IsNullOrWhiteSpace(result.GetValue(inputDirectoryOption)))
    {
        result.AddError("Option '--input-directory' is required.");
    }

    if (string.IsNullOrWhiteSpace(result.GetValue(outputDirectoryOption)))
    {
        result.AddError("Option '--output-directory' is required.");
    }

    var revision = result.GetValue(sourceRevisionOption);
    if (revision is null ||
        revision.Length != 40 ||
        revision.Any(character => !char.IsAsciiHexDigit(character)))
    {
        result.AddError("Option '--source-revision' must be a full 40-character hexadecimal Git revision.");
    }
});
rootCommand.SetAction((parseResult, cancellationToken) => CompareAsync(
    parseResult.GetValue(inputDirectoryOption)!,
    parseResult.GetValue(outputDirectoryOption)!,
    parseResult.GetValue(sourceRevisionOption)!,
    cancellationToken));

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);

static async Task<int> CompareAsync(
    string inputDirectory,
    string outputDirectory,
    string sourceRevision,
    CancellationToken cancellationToken)
{
    var workingDirectory = Directory.GetCurrentDirectory();
    var inputRoot = Path.GetFullPath(inputDirectory, workingDirectory);
    var outputRoot = Path.GetFullPath(outputDirectory, workingDirectory);
    if (!Directory.Exists(inputRoot))
    {
        throw new DirectoryNotFoundException($"The reproducibility input directory is missing: {inputRoot}");
    }

    if (Directory.Exists(outputRoot) && Directory.EnumerateFileSystemEntries(outputRoot).Any())
    {
        throw new InvalidOperationException($"The reproducibility output directory is not empty: {outputRoot}");
    }

    sourceRevision = sourceRevision.ToLowerInvariant();
    var expectedRids = new[]
    {
        "win-x64",
        "win-arm64",
        "linux-x64",
        "linux-arm64",
        "linux-musl-x64",
        "linux-musl-arm64",
        "osx-x64",
        "osx-arm64",
    };
    Directory.CreateDirectory(outputRoot);
    var results = new List<Dictionary<string, object>>();
    foreach (var rid in expectedRids)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var first = FindArtifactRoot(inputRoot, rid, 1);
        var second = FindArtifactRoot(inputRoot, rid, 2);
        var firstPackage = FindSingleFile(first, $"GitSail.{rid}.*.nupkg");
        var secondPackage = FindSingleFile(second, $"GitSail.{rid}.*.nupkg");
        var firstIdentity = ReadPackageIdentity(firstPackage);
        var secondIdentity = ReadPackageIdentity(secondPackage);
        if (firstIdentity.Id != $"GitSail.{rid}" ||
            firstIdentity != secondIdentity ||
            !string.Equals(firstIdentity.SourceRevision, sourceRevision, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The two '{rid}' packages do not have one matching identity and source revision.");
        }

        var firstPayload = ReadPayloadManifest(FindSingleFile(first, $"{rid}-payload-manifest.json"), rid);
        var secondPayload = ReadPayloadManifest(FindSingleFile(second, $"{rid}-payload-manifest.json"), rid);
        CompareMaps(firstPayload, secondPayload, $"{rid} Native AOT payload");

        var firstEntries = await ReadNormalizedPackageEntriesAsync(firstPackage, cancellationToken)
            .ConfigureAwait(false);
        var secondEntries = await ReadNormalizedPackageEntriesAsync(secondPackage, cancellationToken)
            .ConfigureAwait(false);
        CompareMaps(firstEntries, secondEntries, $"{rid} RID package contents");

        var comparedEvidence = new[]
        {
            $"{rid}-source-link.json",
            $"{rid}-symbols.json",
            $"{rid}-native-imports.json",
            $"{rid}-dependency-licenses.json",
            $"{rid}-vulnerabilities.json",
        };
        foreach (var evidenceName in comparedEvidence)
        {
            var firstEvidence = FindSingleFile(first, evidenceName);
            var secondEvidence = FindSingleFile(second, evidenceName);
            var firstHash = await ComputeHashAsync(firstEvidence, HashAlgorithmName.SHA256, cancellationToken)
                .ConfigureAwait(false);
            var secondHash = await ComputeHashAsync(secondEvidence, HashAlgorithmName.SHA256, cancellationToken)
                .ConfigureAwait(false);
            if (firstHash != secondHash)
            {
                throw new InvalidDataException(
                    $"The two '{rid}' builders produced different '{evidenceName}' records.");
            }
        }

        ValidateSbom(FindSingleFile(first, $"{rid}-cyclonedx.json"), rid, sourceRevision, "CycloneDX");
        ValidateSbom(FindSingleFile(second, $"{rid}-cyclonedx.json"), rid, sourceRevision, "CycloneDX");
        ValidateSbom(FindSingleFile(first, $"{rid}-spdx.json"), rid, sourceRevision, "SPDX");
        ValidateSbom(FindSingleFile(second, $"{rid}-spdx.json"), rid, sourceRevision, "SPDX");

        var firstPackageSha256 = await ComputeHashAsync(
            firstPackage,
            HashAlgorithmName.SHA256,
            cancellationToken).ConfigureAwait(false);
        var secondPackageSha256 = await ComputeHashAsync(
            secondPackage,
            HashAlgorithmName.SHA256,
            cancellationToken).ConfigureAwait(false);
        var selectedPackageDirectory = Path.Combine(outputRoot, "packages", rid);
        Directory.CreateDirectory(selectedPackageDirectory);
        await CopyFileAsync(
            firstPackage,
            Path.Combine(selectedPackageDirectory, Path.GetFileName(firstPackage)),
            cancellationToken).ConfigureAwait(false);
        var selectedEvidenceDirectory = Path.Combine(outputRoot, "evidence", rid);
        await CopyDirectoryAsync(
            FindEvidenceDirectory(first, rid),
            selectedEvidenceDirectory,
            cancellationToken).ConfigureAwait(false);

        results.Add(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["runtimeIdentifier"] = rid,
            ["version"] = firstIdentity.Version,
            ["builder1Artifact"] = Path.GetFileName(first),
            ["builder2Artifact"] = Path.GetFileName(second),
            ["payloadFileCount"] = firstPayload.Count,
            ["normalizedPackageEntryCount"] = firstEntries.Count,
            ["builder1PackageSha256"] = firstPackageSha256,
            ["builder2PackageSha256"] = secondPackageSha256,
            ["rawPackageBytesIdentical"] = firstPackageSha256 == secondPackageSha256,
            ["nativePayloadBytesIdentical"] = true,
            ["normalizedPackageContentsIdentical"] = true,
            ["comparedEvidence"] = comparedEvidence,
        });
    }

    await WriteReportAsync(
        Path.Combine(outputRoot, "reproducibility-comparison.json"),
        sourceRevision,
        results,
        cancellationToken).ConfigureAwait(false);
    Console.WriteLine("Verified two independent builders for all eight Native AOT package RIDs.");
    return 0;
}

static string FindArtifactRoot(string inputRoot, string rid, int builder)
{
    var name = $"native-{rid}-builder-{builder}";
    var matches = Directory.EnumerateDirectories(inputRoot, name, SearchOption.AllDirectories).ToArray();
    if (matches.Length != 1)
    {
        throw new DirectoryNotFoundException(
            $"Expected exactly one '{name}' artifact directory; found {matches.Length}.");
    }

    return matches[0];
}

static string FindSingleFile(string root, string pattern)
{
    var matches = Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).ToArray();
    if (matches.Length != 1)
    {
        throw new FileNotFoundException(
            $"Expected exactly one file matching '{pattern}' below '{root}'; found {matches.Length}.");
    }

    return matches[0];
}

static string FindEvidenceDirectory(string artifactRoot, string rid)
{
    var manifest = FindSingleFile(artifactRoot, $"{rid}-payload-manifest.json");
    return Path.GetDirectoryName(manifest) ??
        throw new InvalidDataException($"The '{rid}' evidence manifest has no parent directory.");
}

static (string Id, string Version, string SourceRevision) ReadPackageIdentity(string path)
{
    using var archive = ZipFile.OpenRead(path);
    var nuspec = archive.Entries.Single(entry =>
        entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) &&
        !entry.FullName.Contains('/'));
    using var stream = nuspec.Open();
    var document = XDocument.Load(stream, LoadOptions.None);
    var root = document.Root ?? throw new InvalidDataException($"Package '{path}' has no nuspec root.");
    var metadata = root.Element(root.Name.Namespace + "metadata") ??
        throw new InvalidDataException($"Package '{path}' has no metadata.");
    var repository = metadata.Element(root.Name.Namespace + "repository");
    return (
        metadata.Element(root.Name.Namespace + "id")?.Value ?? string.Empty,
        metadata.Element(root.Name.Namespace + "version")?.Value ?? string.Empty,
        repository?.Attribute("commit")?.Value ?? string.Empty);
}

static Dictionary<string, string> ReadPayloadManifest(string path, string rid)
{
    using var document = JsonDocument.Parse(File.ReadAllBytes(path));
    if (document.RootElement.GetProperty("runtimeIdentifier").GetString() != rid)
    {
        throw new InvalidDataException($"Payload manifest '{path}' represents the wrong RID.");
    }

    return document.RootElement.GetProperty("files").EnumerateArray().ToDictionary(
        file => file.GetProperty("path").GetString() ?? string.Empty,
        file => string.Join(
            '\n',
            file.GetProperty("kind").GetString(),
            file.GetProperty("size").GetInt64(),
            file.GetProperty("sha256").GetString()),
        StringComparer.Ordinal);
}

static async Task<Dictionary<string, string>> ReadNormalizedPackageEntriesAsync(
    string path,
    CancellationToken cancellationToken)
{
    using var archive = ZipFile.OpenRead(path);
    var variableEntries = archive.Entries.Count(entry => IsVariableNuGetContainerEntry(entry.FullName));
    if (variableEntries != 2)
    {
        throw new InvalidDataException(
            $"Package '{path}' contains {variableEntries} variable NuGet metadata entries instead of two.");
    }

    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var entry in archive.Entries
        .Where(entry => entry.Name.Length > 0 && !IsVariableNuGetContainerEntry(entry.FullName))
        .OrderBy(entry => entry.FullName, StringComparer.Ordinal))
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = entry.Open();
        result.Add(
            entry.FullName,
            $"{entry.Length}\n{await ComputeStreamHashAsync(stream, cancellationToken).ConfigureAwait(false)}");
    }

    return result;
}

static bool IsVariableNuGetContainerEntry(string path)
    => path == "_rels/.rels" ||
        path.StartsWith("package/services/metadata/core-properties/", StringComparison.Ordinal);

static void CompareMaps(
    IReadOnlyDictionary<string, string> first,
    IReadOnlyDictionary<string, string> second,
    string description)
{
    if (first.Count != second.Count ||
        first.Any(pair => !second.TryGetValue(pair.Key, out var value) || value != pair.Value))
    {
        var firstOnly = first.Keys.Except(second.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var secondOnly = second.Keys.Except(first.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var changed = first.Keys.Intersect(second.Keys, StringComparer.Ordinal)
            .Where(key => first[key] != second[key])
            .Order(StringComparer.Ordinal);
        throw new InvalidDataException(
            $"The two builders produced different {description}. " +
            $"Builder 1 only: {string.Join(", ", firstOnly)}; " +
            $"builder 2 only: {string.Join(", ", secondOnly)}; " +
            $"changed: {string.Join(", ", changed)}.");
    }
}

static void ValidateSbom(
    string path,
    string rid,
    string sourceRevision,
    string format)
{
    using var document = JsonDocument.Parse(File.ReadAllBytes(path));
    var root = document.RootElement;
    if (format == "CycloneDX")
    {
        var properties = root.GetProperty("metadata").GetProperty("component").GetProperty("properties");
        var recordedRid = properties.EnumerateArray().Single(property =>
            property.GetProperty("name").GetString() == "gitsail:runtimeIdentifier");
        var recordedRevision = properties.EnumerateArray().Single(property =>
            property.GetProperty("name").GetString() == "gitsail:sourceRevision");
        if (root.GetProperty("bomFormat").GetString() != "CycloneDX" ||
            root.GetProperty("specVersion").GetString() != "1.6" ||
            recordedRid.GetProperty("value").GetString() != rid ||
            recordedRevision.GetProperty("value").GetString() != sourceRevision)
        {
            throw new InvalidDataException($"CycloneDX document '{path}' has invalid release identity.");
        }
    }
    else if (root.GetProperty("spdxVersion").GetString() != "SPDX-2.3" ||
             root.GetProperty("documentNamespace").GetString()?.Contains(sourceRevision, StringComparison.Ordinal) !=
             true ||
             root.GetProperty("name").GetString()?.EndsWith($"-{rid}", StringComparison.Ordinal) != true)
    {
        throw new InvalidDataException($"SPDX document '{path}' has invalid release identity.");
    }
}

static async Task CopyDirectoryAsync(
    string sourceRoot,
    string destinationRoot,
    CancellationToken cancellationToken)
{
    foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(sourceRoot, source);
        var destination = Path.Combine(destinationRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await CopyFileAsync(source, destination, cancellationToken).ConfigureAwait(false);
    }
}

static async Task CopyFileAsync(
    string source,
    string destination,
    CancellationToken cancellationToken)
{
    await using var input = new FileStream(
        source,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    await using var output = new FileStream(
        destination,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        bufferSize: 64 * 1024,
        FileOptions.Asynchronous);
    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
}

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

static async Task<string> ComputeStreamHashAsync(Stream stream, CancellationToken cancellationToken)
{
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
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

static async Task WriteReportAsync(
    string path,
    string sourceRevision,
    IReadOnlyList<Dictionary<string, object>> results,
    CancellationToken cancellationToken)
{
    await using var output = new FileStream(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        bufferSize: 4096,
        FileOptions.Asynchronous);
    await using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
    writer.WriteStartObject();
    writer.WriteNumber("schemaVersion", 1);
    writer.WriteString("sourceRevision", sourceRevision);
    writer.WriteNumber("builderCountPerRuntimeIdentifier", 2);
    writer.WriteString(
        "normalization",
        "NuGet ZIP container metadata, _rels/.rels, and the generated core-properties entry are excluded; " +
        "every application, launcher, runtime asset, symbol, and remaining package entry must match byte for byte.");
    writer.WriteStartArray("runtimeIdentifiers");
    foreach (var result in results)
    {
        writer.WriteStartObject();
        foreach (var pair in result)
        {
            switch (pair.Value)
            {
                case string value:
                    writer.WriteString(pair.Key, value);
                    break;
                case int value:
                    writer.WriteNumber(pair.Key, value);
                    break;
                case bool value:
                    writer.WriteBoolean(pair.Key, value);
                    break;
                case string[] values:
                    writer.WriteStartArray(pair.Key);
                    foreach (var value in values)
                    {
                        writer.WriteStringValue(value);
                    }

                    writer.WriteEndArray();
                    break;
                default:
                    throw new InvalidDataException(
                        $"Unsupported reproducibility report value type '{pair.Value.GetType()}'.");
            }
        }

        writer.WriteEndObject();
    }

    writer.WriteEndArray();
    writer.WriteEndObject();
    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
}
