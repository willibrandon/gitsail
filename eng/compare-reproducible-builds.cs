#:package System.CommandLine

using System.Buffers.Binary;
using System.CommandLine;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
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
        var rawRuntimePayloadBytesIdentical = MapsAreEqual(
            firstPayload.RuntimeFiles,
            secondPayload.RuntimeFiles);
        var rawSymbolPayloadBytesIdentical = MapsAreEqual(
            firstPayload.SymbolFiles,
            secondPayload.SymbolFiles);
        var firstComparableRuntimeFiles = firstPayload.RuntimeFiles;
        var secondComparableRuntimeFiles = secondPayload.RuntimeFiles;
        var firstComparableSymbolFiles = firstPayload.SymbolFiles;
        var secondComparableSymbolFiles = secondPayload.SymbolFiles;
        var runtimeComparison = "byte-for-byte";
        var symbolComparison = "byte-for-byte";
        var normalizedPdbTemporaryPathOccurrences = 0;
        var embeddedPdbSourceRevisionOccurrences = 0;
        var normalizedMachOUuidFiles = 0;
        if (rid.StartsWith("win-", StringComparison.Ordinal))
        {
            CompareMaps(
                ReadSymbolFileMetadata(firstPayload.SymbolFiles),
                ReadSymbolFileMetadata(secondPayload.SymbolFiles),
                $"{rid} PDB metadata");
            var firstPdb = await ReadNormalizedPdbStreamsAsync(
                FindSingleFile(first, "git-tui.pdb"),
                sourceRevision,
                cancellationToken).ConfigureAwait(false);
            var secondPdb = await ReadNormalizedPdbStreamsAsync(
                FindSingleFile(second, "git-tui.pdb"),
                sourceRevision,
                cancellationToken).ConfigureAwait(false);
            CompareMaps(firstPdb.Streams, secondPdb.Streams, $"{rid} logical PDB streams");
            if (firstPdb.NormalizedTemporaryPathOccurrences == 0 ||
                firstPdb.NormalizedTemporaryPathOccurrences != secondPdb.NormalizedTemporaryPathOccurrences)
            {
                throw new InvalidDataException(
                    $"The two '{rid}' PDBs do not contain the same nonzero number of linker temporary paths.");
            }

            symbolComparison = "logical-pdb-streams";
            normalizedPdbTemporaryPathOccurrences = firstPdb.NormalizedTemporaryPathOccurrences;
            embeddedPdbSourceRevisionOccurrences = firstPdb.EmbeddedSourceRevisionOccurrences;
        }
        else if (rid.StartsWith("osx-", StringComparison.Ordinal))
        {
            var firstApplication = await ReadNormalizedMachOAsync(
                FindPublishedApplication(first, rid),
                isApplication: true,
                cancellationToken).ConfigureAwait(false);
            var secondApplication = await ReadNormalizedMachOAsync(
                FindPublishedApplication(second, rid),
                isApplication: true,
                cancellationToken).ConfigureAwait(false);
            firstComparableRuntimeFiles = ReplaceManifestHash(
                firstPayload.RuntimeFiles,
                "git-tui",
                firstApplication.NormalizedSha256);
            secondComparableRuntimeFiles = ReplaceManifestHash(
                secondPayload.RuntimeFiles,
                "git-tui",
                secondApplication.NormalizedSha256);

            const string dwarfPath = "git-tui.dSYM/Contents/Resources/DWARF/git-tui";
            var firstDwarf = await ReadNormalizedMachOAsync(
                Path.Combine(FindSymbolRoot(first, rid), "Contents", "Resources", "DWARF", "git-tui"),
                isApplication: false,
                cancellationToken).ConfigureAwait(false);
            var secondDwarf = await ReadNormalizedMachOAsync(
                Path.Combine(FindSymbolRoot(second, rid), "Contents", "Resources", "DWARF", "git-tui"),
                isApplication: false,
                cancellationToken).ConfigureAwait(false);
            if (firstApplication.Uuid != firstDwarf.Uuid || secondApplication.Uuid != secondDwarf.Uuid)
            {
                throw new InvalidDataException(
                    $"A '{rid}' executable does not have the same UUID as its retained dSYM image.");
            }

            firstComparableSymbolFiles = ReplaceManifestHash(
                firstPayload.SymbolFiles,
                dwarfPath,
                firstDwarf.NormalizedSha256);
            secondComparableSymbolFiles = ReplaceManifestHash(
                secondPayload.SymbolFiles,
                dwarfPath,
                secondDwarf.NormalizedSha256);
            runtimeComparison = rid == "osx-arm64"
                ? "mach-o-lc-uuid-and-linker-code-directory-normalized"
                : "mach-o-lc-uuid-normalized";
            symbolComparison = "mach-o-lc-uuid-normalized";
            normalizedMachOUuidFiles = 2;
        }
        CompareMaps(
            firstComparableRuntimeFiles,
            secondComparableRuntimeFiles,
            $"{rid} Native AOT runtime payload");
        if (!rid.StartsWith("win-", StringComparison.Ordinal))
        {
            CompareMaps(firstComparableSymbolFiles, secondComparableSymbolFiles, $"{rid} symbol payload");
        }

        var firstEntries = await ReadNormalizedPackageEntriesAsync(
            firstPackage,
            normalizeMachOUuid: rid.StartsWith("osx-", StringComparison.Ordinal),
            cancellationToken)
            .ConfigureAwait(false);
        var secondEntries = await ReadNormalizedPackageEntriesAsync(
            secondPackage,
            normalizeMachOUuid: rid.StartsWith("osx-", StringComparison.Ordinal),
            cancellationToken)
            .ConfigureAwait(false);
        CompareMaps(firstEntries.Entries, secondEntries.Entries, $"{rid} RID package contents");
        if (firstEntries.NormalizedMachOEntries != secondEntries.NormalizedMachOEntries ||
            firstEntries.NormalizedMachOEntries != (rid.StartsWith("osx-", StringComparison.Ordinal) ? 1 : 0))
        {
            throw new InvalidDataException(
                $"The two '{rid}' packages did not normalize the expected number of Mach-O entries.");
        }

        var byteComparedEvidence = new[]
        {
            $"{rid}-source-link.json",
            $"{rid}-dependency-licenses.json",
            $"{rid}-vulnerabilities.json",
        };
        foreach (var evidenceName in byteComparedEvidence)
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

        var firstNativeImportEvidence = ReadNativeImportEvidence(
            FindSingleFile(first, $"{rid}-native-imports.json"),
            rid,
            ignoreFileHashes: rid.StartsWith("osx-", StringComparison.Ordinal));
        var secondNativeImportEvidence = ReadNativeImportEvidence(
            FindSingleFile(second, $"{rid}-native-imports.json"),
            rid,
            ignoreFileHashes: rid.StartsWith("osx-", StringComparison.Ordinal));
        CompareMaps(firstNativeImportEvidence, secondNativeImportEvidence, $"{rid} native-import evidence");

        var firstSymbolEvidence = ReadSymbolEvidence(
            FindSingleFile(first, $"{rid}-symbols.json"),
            rid,
            sourceRevision,
            ignoreExecutableBuildIdentity: rid.StartsWith("osx-", StringComparison.Ordinal),
            ignoreSymbolHashes: rid.StartsWith("win-", StringComparison.Ordinal) ||
                rid.StartsWith("osx-", StringComparison.Ordinal));
        var secondSymbolEvidence = ReadSymbolEvidence(
            FindSingleFile(second, $"{rid}-symbols.json"),
            rid,
            sourceRevision,
            ignoreExecutableBuildIdentity: rid.StartsWith("osx-", StringComparison.Ordinal),
            ignoreSymbolHashes: rid.StartsWith("win-", StringComparison.Ordinal) ||
                rid.StartsWith("osx-", StringComparison.Ordinal));
        CompareMaps(firstSymbolEvidence, secondSymbolEvidence, $"{rid} symbol evidence");

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
        var selectedSymbolDirectory = Path.Combine(outputRoot, "symbols", rid);
        Directory.CreateDirectory(selectedSymbolDirectory);
        var firstSymbolRoot = FindSymbolRoot(first, rid);
        if (File.Exists(firstSymbolRoot))
        {
            await CopyFileAsync(
                firstSymbolRoot,
                Path.Combine(selectedSymbolDirectory, Path.GetFileName(firstSymbolRoot)),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await CopyDirectoryAsync(
                firstSymbolRoot,
                Path.Combine(selectedSymbolDirectory, Path.GetFileName(firstSymbolRoot)),
                cancellationToken).ConfigureAwait(false);
        }

        results.Add(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["runtimeIdentifier"] = rid,
            ["version"] = firstIdentity.Version,
            ["builder1Artifact"] = Path.GetFileName(first),
            ["builder2Artifact"] = Path.GetFileName(second),
            ["payloadFileCount"] = firstPayload.RuntimeFiles.Count + firstPayload.SymbolFiles.Count,
            ["runtimePayloadFileCount"] = firstPayload.RuntimeFiles.Count,
            ["symbolPayloadFileCount"] = firstPayload.SymbolFiles.Count,
            ["normalizedPackageEntryCount"] = firstEntries.Entries.Count,
            ["builder1PackageSha256"] = firstPackageSha256,
            ["builder2PackageSha256"] = secondPackageSha256,
            ["rawPackageBytesIdentical"] = firstPackageSha256 == secondPackageSha256,
            ["rawRuntimePayloadBytesIdentical"] = rawRuntimePayloadBytesIdentical,
            ["runtimePayloadContentsIdentical"] = true,
            ["runtimePayloadComparison"] = runtimeComparison,
            ["rawSymbolPayloadBytesIdentical"] = rawSymbolPayloadBytesIdentical,
            ["symbolComparison"] = symbolComparison,
            ["normalizedPdbTemporaryPathOccurrences"] = normalizedPdbTemporaryPathOccurrences,
            ["embeddedPdbSourceRevisionOccurrences"] = embeddedPdbSourceRevisionOccurrences,
            ["normalizedMachOUuidFiles"] = normalizedMachOUuidFiles,
            ["normalizedPackageContentsIdentical"] = true,
            ["byteComparedEvidence"] = byteComparedEvidence,
            ["semanticallyComparedEvidence"] = new[]
            {
                $"{rid}-symbols.json",
                $"{rid}-native-imports.json",
            },
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

static string FindSymbolRoot(string artifactRoot, string rid)
{
    if (rid.StartsWith("win-", StringComparison.Ordinal))
    {
        return FindSingleFile(artifactRoot, "git-tui.pdb");
    }

    if (rid.StartsWith("linux-", StringComparison.Ordinal))
    {
        return FindSingleFile(artifactRoot, "git-tui.dbg");
    }

    var matches = Directory.EnumerateDirectories(
        artifactRoot,
        "git-tui.dSYM",
        SearchOption.AllDirectories).ToArray();
    if (matches.Length != 1)
    {
        throw new DirectoryNotFoundException(
            $"Expected exactly one 'git-tui.dSYM' below '{artifactRoot}'; found {matches.Length}.");
    }

    return matches[0];
}

static string FindPublishedApplication(string artifactRoot, string rid)
{
    var fileName = rid.StartsWith("win-", StringComparison.Ordinal) ? "git-tui.exe" : "git-tui";
    var matches = Directory.EnumerateFiles(artifactRoot, fileName, SearchOption.AllDirectories)
        .Where(path => !path.Contains($"git-tui.dSYM{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}shims{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        .ToArray();
    if (matches.Length != 1)
    {
        throw new FileNotFoundException(
            $"Expected exactly one published '{fileName}' application below '{artifactRoot}'; " +
            $"found {matches.Length}.");
    }

    return matches[0];
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

static (
    Dictionary<string, string> RuntimeFiles,
    Dictionary<string, string> SymbolFiles) ReadPayloadManifest(string path, string rid)
{
    using var document = JsonDocument.Parse(File.ReadAllBytes(path));
    if (document.RootElement.GetProperty("runtimeIdentifier").GetString() != rid)
    {
        throw new InvalidDataException($"Payload manifest '{path}' represents the wrong RID.");
    }

    var runtimeFiles = new Dictionary<string, string>(StringComparer.Ordinal);
    var symbolFiles = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var file in document.RootElement.GetProperty("files").EnumerateArray())
    {
        var relativePath = file.GetProperty("path").GetString();
        var kind = file.GetProperty("kind").GetString();
        if (string.IsNullOrWhiteSpace(relativePath) || string.IsNullOrWhiteSpace(kind))
        {
            throw new InvalidDataException($"Payload manifest '{path}' contains incomplete file metadata.");
        }

        var value = string.Join(
            '\n',
            kind,
            file.GetProperty("size").GetInt64(),
            file.GetProperty("sha256").GetString());
        var destination = kind == "symbol" ? symbolFiles : runtimeFiles;
        if (!destination.TryAdd(relativePath, value))
        {
            throw new InvalidDataException($"Payload manifest '{path}' repeats '{relativePath}'.");
        }
    }

    if (runtimeFiles.Count == 0 || symbolFiles.Count == 0)
    {
        throw new InvalidDataException(
            $"Payload manifest '{path}' must contain both runtime and symbol files.");
    }

    return (runtimeFiles, symbolFiles);
}

static Dictionary<string, string> ReadSymbolFileMetadata(IReadOnlyDictionary<string, string> files)
    => files.ToDictionary(
        pair => pair.Key,
        pair => string.Join('\n', pair.Value.Split('\n').Take(2)),
        StringComparer.Ordinal);

static Dictionary<string, string> ReplaceManifestHash(
    IReadOnlyDictionary<string, string> files,
    string relativePath,
    string normalizedHash)
{
    if (!files.TryGetValue(relativePath, out var value))
    {
        throw new InvalidDataException($"Payload manifest does not contain '{relativePath}'.");
    }

    var fields = value.Split('\n');
    if (fields.Length != 3 || !IsSha256(normalizedHash))
    {
        throw new InvalidDataException($"Payload manifest metadata for '{relativePath}' is invalid.");
    }

    var result = new Dictionary<string, string>(files, StringComparer.Ordinal)
    {
        [relativePath] = string.Join('\n', fields[0], fields[1], normalizedHash),
    };
    return result;
}

static bool MapsAreEqual(
    IReadOnlyDictionary<string, string> first,
    IReadOnlyDictionary<string, string> second)
    => first.Count == second.Count &&
        first.All(pair => second.TryGetValue(pair.Key, out var value) && value == pair.Value);

static Dictionary<string, string> ReadNativeImportEvidence(
    string path,
    string rid,
    bool ignoreFileHashes)
{
    using var document = JsonDocument.Parse(File.ReadAllBytes(path));
    var root = document.RootElement;
    if (root.GetProperty("schemaVersion").GetInt32() != 1 ||
        root.GetProperty("runtimeIdentifier").GetString() != rid)
    {
        throw new InvalidDataException($"Native-import evidence '{path}' has invalid release identity.");
    }

    var result = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["schemaVersion"] = root.GetProperty("schemaVersion").GetRawText(),
        ["runtimeIdentifier"] = rid,
    };
    var files = root.GetProperty("files").EnumerateArray().ToArray();
    if (files.Length == 0)
    {
        throw new InvalidDataException($"Native-import evidence '{path}' has no inspected files.");
    }

    foreach (var file in files)
    {
        var relativePath = file.GetProperty("path").GetString();
        var hash = file.GetProperty("sha256").GetString();
        if (string.IsNullOrWhiteSpace(relativePath) || !IsSha256(hash))
        {
            throw new InvalidDataException($"Native-import evidence '{path}' has invalid file metadata.");
        }

        var imports = file.GetProperty("imports").EnumerateArray()
            .Select(import => string.Join(
                '\n',
                import.GetProperty("name").GetString(),
                import.GetProperty("allowed").GetBoolean()))
            .ToArray();
        if (imports.Length == 0 || imports.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException($"Native-import evidence '{path}' has incomplete imports.");
        }

        if (!result.TryAdd(
            $"file/{relativePath}",
            string.Join(
                '\n',
                ignoreFileHashes ? "<mach-o-lc-uuid-normalized>" : hash,
                string.Join("\n--import--\n", imports))))
        {
            throw new InvalidDataException($"Native-import evidence '{path}' repeats '{relativePath}'.");
        }
    }

    return result;
}

static Dictionary<string, string> ReadSymbolEvidence(
    string path,
    string rid,
    string sourceRevision,
    bool ignoreExecutableBuildIdentity,
    bool ignoreSymbolHashes)
{
    using var document = JsonDocument.Parse(File.ReadAllBytes(path));
    var root = document.RootElement;
    var expectedKind = rid.StartsWith("win-", StringComparison.Ordinal)
        ? "pdb"
        : rid.StartsWith("osx-", StringComparison.Ordinal)
            ? "dSYM"
            : "ELF debug file";
    if (root.GetProperty("schemaVersion").GetInt32() != 1 ||
        root.GetProperty("runtimeIdentifier").GetString() != rid ||
        root.GetProperty("sourceRevision").GetString() != sourceRevision ||
        root.GetProperty("symbolKind").GetString() != expectedKind ||
        root.GetProperty("sourceLinkRecord").GetString() != $"{rid}-source-link.json")
    {
        throw new InvalidDataException($"Symbol evidence '{path}' has invalid release identity.");
    }

    var executable = root.GetProperty("executable");
    var executableHash = executable.GetProperty("sha256").GetString();
    if (!IsSha256(executableHash))
    {
        throw new InvalidDataException($"Symbol evidence '{path}' has an invalid executable hash.");
    }

    var result = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["schemaVersion"] = root.GetProperty("schemaVersion").GetRawText(),
        ["runtimeIdentifier"] = rid,
        ["sourceRevision"] = sourceRevision,
        ["symbolKind"] = expectedKind,
        ["sourceLinkRecord"] = $"{rid}-source-link.json",
        ["executable"] = string.Join(
            '\n',
            executable.GetProperty("path").GetString(),
            executable.GetProperty("size").GetInt64(),
            ignoreExecutableBuildIdentity ? "<mach-o-lc-uuid-normalized>" : executableHash,
            ignoreExecutableBuildIdentity
                ? "<mach-o-lc-uuid-normalized>"
                : string.Join(
                    ',',
                    executable.GetProperty("buildIdentifiers").EnumerateArray()
                        .Select(identifier => identifier.GetString()))),
    };
    if (executable.TryGetProperty("referencedSymbolName", out var referencedSymbolName))
    {
        result["referencedSymbolName"] = referencedSymbolName.GetString() ?? string.Empty;
    }

    var symbolFiles = root.GetProperty("symbolFiles").EnumerateArray().ToArray();
    if (symbolFiles.Length == 0)
    {
        throw new InvalidDataException($"Symbol evidence '{path}' has no symbol files.");
    }

    foreach (var symbolFile in symbolFiles)
    {
        var relativePath = symbolFile.GetProperty("path").GetString();
        var hash = symbolFile.GetProperty("sha256").GetString();
        if (string.IsNullOrWhiteSpace(relativePath) || !IsSha256(hash))
        {
            throw new InvalidDataException($"Symbol evidence '{path}' has invalid symbol-file metadata.");
        }

        if (!result.TryAdd(
            $"symbolFile/{relativePath}",
            string.Join(
                '\n',
                symbolFile.GetProperty("size").GetInt64(),
                ignoreSymbolHashes ? "<logical-pdb-streams>" : hash)))
        {
            throw new InvalidDataException($"Symbol evidence '{path}' repeats '{relativePath}'.");
        }
    }

    return result;
}

static bool IsSha256(string? value)
    => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

static async Task<(string NormalizedSha256, string Uuid)> ReadNormalizedMachOAsync(
    string path,
    bool isApplication,
    CancellationToken cancellationToken)
{
    var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    return NormalizeMachO(bytes, path, isApplication);
}

static (string NormalizedSha256, string Uuid) NormalizeMachO(
    byte[] bytes,
    string description,
    bool isApplication)
{
    const uint machO64LittleEndianMagic = 0xFEEDFACF;
    const uint x64CpuType = 0x01000007;
    const uint arm64CpuType = 0x0100000C;
    const uint uuidLoadCommand = 0x1B;
    const uint codeSignatureLoadCommand = 0x1D;
    const int headerSize = 32;
    const int uuidCommandSize = 24;
    if (bytes.Length < headerSize ||
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, sizeof(uint))) !=
        machO64LittleEndianMagic)
    {
        throw new InvalidDataException(
            $"Apple artifact '{description}' is not a little-endian 64-bit Mach-O image.");
    }

    var cpuType = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, sizeof(uint)));
    if (cpuType != x64CpuType && cpuType != arm64CpuType)
    {
        throw new InvalidDataException(
            $"Apple artifact '{description}' does not target x64 or arm64.");
    }

    var commandCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, sizeof(uint)));
    var commandByteCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20, sizeof(uint)));
    if (commandCount == 0 || commandCount > 100_000 ||
        commandByteCount > bytes.Length - headerSize)
    {
        throw new InvalidDataException(
            $"Apple artifact '{description}' has an invalid Mach-O load-command table.");
    }

    var commandOffset = headerSize;
    var commandEnd = checked(headerSize + (int)commandByteCount);
    string? uuid = null;
    var uuidOffset = 0;
    var codeSignatureCount = 0;
    var codeSignatureOffset = 0;
    var codeSignatureSize = 0;
    for (var index = 0; index < commandCount; index++)
    {
        if (commandOffset + 8 > commandEnd)
        {
            throw new InvalidDataException(
                $"Apple artifact '{description}' has a truncated Mach-O load command.");
        }

        var command = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(commandOffset, sizeof(uint)));
        var commandSize = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(commandOffset + sizeof(uint), sizeof(uint)));
        if (commandSize < 8 || commandSize > commandEnd - commandOffset)
        {
            throw new InvalidDataException(
                $"Apple artifact '{description}' has an invalid Mach-O load-command size.");
        }

        if (command == uuidLoadCommand)
        {
            if (uuid is not null || commandSize != uuidCommandSize)
            {
                throw new InvalidDataException(
                    $"Apple artifact '{description}' must contain exactly one valid LC_UUID command.");
            }

            var uuidBytes = bytes.AsSpan(commandOffset + 8, 16);
            if (uuidBytes.IndexOfAnyExcept((byte)0) < 0)
            {
                throw new InvalidDataException($"Apple artifact '{description}' has an empty LC_UUID value.");
            }

            uuid = Convert.ToHexString(uuidBytes).ToLowerInvariant();
            uuidOffset = commandOffset + 8;
        }
        else if (command == codeSignatureLoadCommand)
        {
            if (commandSize != 16)
            {
                throw new InvalidDataException(
                    $"Apple artifact '{description}' has an invalid LC_CODE_SIGNATURE command.");
            }

            codeSignatureCount++;
            codeSignatureOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(commandOffset + 8, sizeof(uint))));
            codeSignatureSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(commandOffset + 12, sizeof(uint))));
        }

        commandOffset += checked((int)commandSize);
    }

    if (commandOffset != commandEnd || uuid is null ||
        (!isApplication && codeSignatureCount != 0) ||
        (isApplication && cpuType == x64CpuType && codeSignatureCount != 0) ||
        (isApplication && cpuType == arm64CpuType && codeSignatureCount != 1))
    {
        throw new InvalidDataException(
            $"Apple artifact '{description}' has unexpected UUID or code-signature load commands.");
    }

    var signature = codeSignatureCount == 1
        ? ValidateAnonymousLinkerSignature(bytes, codeSignatureOffset, codeSignatureSize, description)
        : ((int HashOffset, int CodeSlotCount, int CodeLimit, int PageSize)?)null;
    bytes.AsSpan(uuidOffset, 16).Clear();
    if (signature is { } value)
    {
        RecomputeCodeDirectoryHashes(
            bytes,
            value.HashOffset,
            value.CodeSlotCount,
            value.CodeLimit,
            value.PageSize);
    }

    return (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), uuid);
}

static (int HashOffset, int CodeSlotCount, int CodeLimit, int PageSize) ValidateAnonymousLinkerSignature(
    byte[] bytes,
    int signatureOffset,
    int signatureSize,
    string description)
{
    const uint embeddedSignatureMagic = 0xFADE0CC0;
    const uint codeDirectoryMagic = 0xFADE0C02;
    const uint supportedCodeDirectoryVersion = 0x00020400;
    const uint anonymousLinkerFlags = 0x00020002;
    const byte sha256HashType = 2;
    const byte sha256HashSize = 32;
    const byte pageSizePower = 12;
    const int superBlobHeaderSize = 20;
    const int codeDirectoryHeaderSize = 88;

    if (signatureOffset <= 0 || signatureSize < superBlobHeaderSize ||
        signatureOffset > bytes.Length - signatureSize)
    {
        throw new InvalidDataException(
            $"Apple artifact '{description}' has an invalid embedded signature range.");
    }

    var signature = bytes.AsSpan(signatureOffset, signatureSize);
    var superBlobLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(signature.Slice(4, 4)));
    if (BinaryPrimitives.ReadUInt32BigEndian(signature) != embeddedSignatureMagic ||
        superBlobLength < superBlobHeaderSize || superBlobLength > signature.Length ||
        BinaryPrimitives.ReadUInt32BigEndian(signature.Slice(8, 4)) != 1 ||
        BinaryPrimitives.ReadUInt32BigEndian(signature.Slice(12, 4)) != 0 ||
        BinaryPrimitives.ReadUInt32BigEndian(signature.Slice(16, 4)) != superBlobHeaderSize ||
        signature.Slice(superBlobLength).IndexOfAnyExcept((byte)0) >= 0)
    {
        throw new InvalidDataException(
            $"Apple artifact '{description}' does not contain one anonymous CodeDirectory.");
    }

    var codeDirectory = signature.Slice(superBlobHeaderSize, superBlobLength - superBlobHeaderSize);
    if (codeDirectory.Length < codeDirectoryHeaderSize)
    {
        throw new InvalidDataException(
            $"Apple artifact '{description}' has a truncated CodeDirectory.");
    }

    var codeDirectoryLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(codeDirectory.Slice(4, 4)));
    if (BinaryPrimitives.ReadUInt32BigEndian(codeDirectory) != codeDirectoryMagic ||
        codeDirectoryLength != codeDirectory.Length ||
        BinaryPrimitives.ReadUInt32BigEndian(codeDirectory.Slice(8, 4)) != supportedCodeDirectoryVersion ||
        BinaryPrimitives.ReadUInt32BigEndian(codeDirectory.Slice(12, 4)) != anonymousLinkerFlags ||
        BinaryPrimitives.ReadUInt32BigEndian(codeDirectory.Slice(24, 4)) != 0 ||
        codeDirectory[36] != sha256HashSize ||
        codeDirectory[37] != sha256HashType ||
        codeDirectory[39] != pageSizePower)
    {
        throw new InvalidDataException(
            $"Apple artifact '{description}' is not certificate-free linker metadata.");
    }

    var hashOffset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(codeDirectory.Slice(16, 4)));
    var identifierOffset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(codeDirectory.Slice(20, 4)));
    var codeSlotCount = checked((int)BinaryPrimitives.ReadUInt32BigEndian(codeDirectory.Slice(28, 4)));
    var codeLimit = checked((int)BinaryPrimitives.ReadUInt32BigEndian(codeDirectory.Slice(32, 4)));
    const int pageSize = 1 << pageSizePower;
    var expectedCodeSlotCount = checked((codeLimit + pageSize - 1) / pageSize);
    var identifier = "git-tui\0"u8;
    if (codeLimit != signatureOffset || codeSlotCount != expectedCodeSlotCount ||
        identifierOffset < codeDirectoryHeaderSize ||
        identifierOffset > codeDirectory.Length - identifier.Length ||
        !codeDirectory.Slice(identifierOffset, identifier.Length).SequenceEqual(identifier) ||
        hashOffset < identifierOffset + identifier.Length ||
        checked(hashOffset + checked(codeSlotCount * sha256HashSize)) != codeDirectory.Length)
    {
        throw new InvalidDataException(
            $"Apple artifact '{description}' has invalid CodeDirectory bounds.");
    }

    var absoluteHashOffset = checked(signatureOffset + superBlobHeaderSize + hashOffset);
    Span<byte> pageHash = stackalloc byte[sha256HashSize];
    for (var index = 0; index < codeSlotCount; index++)
    {
        var pageOffset = checked(index * pageSize);
        var pageLength = Math.Min(pageSize, codeLimit - pageOffset);
        if (!SHA256.TryHashData(bytes.AsSpan(pageOffset, pageLength), pageHash, out var bytesWritten) ||
            bytesWritten != sha256HashSize ||
            !pageHash.SequenceEqual(bytes.AsSpan(
                absoluteHashOffset + index * sha256HashSize,
                sha256HashSize)))
        {
            throw new InvalidDataException(
                $"Apple artifact '{description}' has an invalid linker CodeDirectory hash.");
        }
    }

    return (absoluteHashOffset, codeSlotCount, codeLimit, pageSize);
}

static void RecomputeCodeDirectoryHashes(
    byte[] bytes,
    int hashOffset,
    int codeSlotCount,
    int codeLimit,
    int pageSize)
{
    const int hashSize = 32;
    for (var index = 0; index < codeSlotCount; index++)
    {
        var pageOffset = checked(index * pageSize);
        var pageLength = Math.Min(pageSize, codeLimit - pageOffset);
        if (!SHA256.TryHashData(
            bytes.AsSpan(pageOffset, pageLength),
            bytes.AsSpan(hashOffset + index * hashSize, hashSize),
            out var bytesWritten) || bytesWritten != hashSize)
        {
            throw new InvalidOperationException("Could not normalize a Mach-O CodeDirectory hash.");
        }
    }
}

static async Task<(
    Dictionary<string, string> Streams,
    int NormalizedTemporaryPathOccurrences,
    int EmbeddedSourceRevisionOccurrences)> ReadNormalizedPdbStreamsAsync(
        string path,
        string sourceRevision,
        CancellationToken cancellationToken)
{
    var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    var expectedMagic = "Microsoft C/C++ MSF 7.00\r\n\u001aDS\0\0\0"u8;
    if (bytes.Length < 56 || !bytes.AsSpan(0, expectedMagic.Length).SequenceEqual(expectedMagic))
    {
        throw new InvalidDataException($"Windows symbol artifact '{path}' is not an MSF 7.0 PDB.");
    }

    var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(32, sizeof(uint)));
    var blockCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40, sizeof(uint)));
    var directoryByteCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(44, sizeof(uint)));
    var blockMapAddress = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(52, sizeof(uint)));
    if (blockSize is < 512 or > 65536 ||
        (blockSize & (blockSize - 1)) != 0 ||
        blockCount == 0 ||
        (long)blockCount * blockSize != bytes.LongLength ||
        directoryByteCount > bytes.Length)
    {
        throw new InvalidDataException($"Windows symbol artifact '{path}' has an invalid MSF superblock.");
    }

    var directoryBlockCount = DivideRoundUp(directoryByteCount, blockSize);
    var blockMapOffset = checked((long)blockMapAddress * blockSize);
    var blockMapByteCount = checked((long)directoryBlockCount * sizeof(uint));
    if (blockMapOffset < 0 ||
        blockMapOffset + blockMapByteCount > bytes.LongLength ||
        blockMapByteCount > blockSize)
    {
        throw new InvalidDataException($"Windows symbol artifact '{path}' has an invalid MSF block map.");
    }

    var directory = new byte[checked((int)directoryByteCount)];
    for (var index = 0; index < directoryBlockCount; index++)
    {
        var directoryBlock = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(checked((int)(blockMapOffset + (index * sizeof(uint)))), sizeof(uint)));
        var sourceOffset = GetBlockOffset(directoryBlock, blockSize, blockCount, bytes.Length, path);
        var destinationOffset = checked((int)((long)index * blockSize));
        var copyLength = Math.Min(checked((int)blockSize), directory.Length - destinationOffset);
        bytes.AsSpan(sourceOffset, copyLength).CopyTo(directory.AsSpan(destinationOffset, copyLength));
    }

    var directoryOffset = 0;
    var streamCount = ReadDirectoryUInt32(directory, ref directoryOffset, path);
    if (streamCount is < 5 or > 1_000_000)
    {
        throw new InvalidDataException(
            $"Windows symbol artifact '{path}' has an invalid logical stream count '{streamCount}'.");
    }

    var streamSizes = new uint[streamCount];
    for (var index = 0; index < streamSizes.Length; index++)
    {
        streamSizes[index] = ReadDirectoryUInt32(directory, ref directoryOffset, path);
    }

    var result = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["streamCount"] = streamCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
    var normalizedTemporaryPathOccurrences = 0;
    var sourceRevisionBytes = Encoding.UTF8.GetBytes(
        $"https://raw.githubusercontent.com/willibrandon/gitsail/{sourceRevision}/");
    var embeddedSourceRevisionOccurrences = 0;
    for (var streamIndex = 0; streamIndex < streamSizes.Length; streamIndex++)
    {
        var streamSize = streamSizes[streamIndex];
        var key = $"stream/{streamIndex:D6}";
        if (streamSize == uint.MaxValue)
        {
            if (streamIndex != 0)
            {
                result.Add(key, "nil");
            }

            continue;
        }

        if (streamSize > int.MaxValue)
        {
            throw new InvalidDataException(
                $"Windows symbol artifact '{path}' stream {streamIndex} exceeds the supported size.");
        }

        var stream = new byte[streamSize];
        var streamBlockCount = DivideRoundUp(streamSize, blockSize);
        for (var blockIndex = 0; blockIndex < streamBlockCount; blockIndex++)
        {
            var streamBlock = ReadDirectoryUInt32(directory, ref directoryOffset, path);
            var sourceOffset = GetBlockOffset(streamBlock, blockSize, blockCount, bytes.Length, path);
            var destinationOffset = checked((int)((long)blockIndex * blockSize));
            var copyLength = Math.Min(checked((int)blockSize), stream.Length - destinationOffset);
            bytes.AsSpan(sourceOffset, copyLength).CopyTo(stream.AsSpan(destinationOffset, copyLength));
        }

        if (streamIndex == 0)
        {
            continue;
        }

        normalizedTemporaryPathOccurrences += NormalizeLinkerTemporaryPathGuids(stream);
        embeddedSourceRevisionOccurrences += CountOccurrences(stream, sourceRevisionBytes);
        result.Add(
            key,
            $"{streamSize}\n{Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()}");
    }

    if (directoryOffset != directory.Length)
    {
        throw new InvalidDataException(
            $"Windows symbol artifact '{path}' has trailing or unparsed MSF directory data.");
    }

    if (embeddedSourceRevisionOccurrences == 0)
    {
        throw new InvalidDataException(
            $"Windows symbol artifact '{path}' does not embed Source Link for '{sourceRevision}'.");
    }

    return (result, normalizedTemporaryPathOccurrences, embeddedSourceRevisionOccurrences);
}

static int NormalizeLinkerTemporaryPathGuids(Span<byte> stream)
{
    var prefix = "\\Temp\\lnk{"u8;
    var suffix = "}.tmp"u8;
    const int guidLength = 36;
    var count = 0;
    for (var index = 0; index + prefix.Length + guidLength + suffix.Length <= stream.Length; index++)
    {
        if (!AsciiEqualsIgnoreCase(stream.Slice(index, prefix.Length), prefix))
        {
            continue;
        }

        var guid = stream.Slice(index + prefix.Length, guidLength);
        if (!IsAsciiGuid(guid) ||
            !AsciiEqualsIgnoreCase(
                stream.Slice(index + prefix.Length + guidLength, suffix.Length),
                suffix))
        {
            continue;
        }

        for (var guidIndex = 0; guidIndex < guid.Length; guidIndex++)
        {
            if (guid[guidIndex] != (byte)'-')
            {
                guid[guidIndex] = (byte)'0';
            }
        }

        count++;
        index += prefix.Length + guidLength + suffix.Length - 1;
    }

    return count;
}

static bool IsAsciiGuid(ReadOnlySpan<byte> value)
{
    if (value.Length != 36)
    {
        return false;
    }

    for (var index = 0; index < value.Length; index++)
    {
        if (index is 8 or 13 or 18 or 23)
        {
            if (value[index] != (byte)'-')
            {
                return false;
            }
        }
        else if (!IsAsciiHexDigit(value[index]))
        {
            return false;
        }
    }

    return true;
}

static bool IsAsciiHexDigit(byte value)
    => value is >= (byte)'0' and <= (byte)'9' or
        >= (byte)'A' and <= (byte)'F' or
        >= (byte)'a' and <= (byte)'f';

static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
{
    if (first.Length != second.Length)
    {
        return false;
    }

    for (var index = 0; index < first.Length; index++)
    {
        var firstValue = first[index] is >= (byte)'A' and <= (byte)'Z'
            ? first[index] + ((byte)'a' - (byte)'A')
            : first[index];
        var secondValue = second[index] is >= (byte)'A' and <= (byte)'Z'
            ? second[index] + ((byte)'a' - (byte)'A')
            : second[index];
        if (firstValue != secondValue)
        {
            return false;
        }
    }

    return true;
}

static int CountOccurrences(ReadOnlySpan<byte> source, ReadOnlySpan<byte> value)
{
    var count = 0;
    var offset = 0;
    while (offset <= source.Length - value.Length)
    {
        var relativeIndex = source[offset..].IndexOf(value);
        if (relativeIndex < 0)
        {
            break;
        }

        count++;
        offset += relativeIndex + value.Length;
    }

    return count;
}

static int GetBlockOffset(
    uint block,
    uint blockSize,
    uint blockCount,
    int fileLength,
    string path)
{
    var offset = checked((long)block * blockSize);
    if (block >= blockCount || offset < 0 || offset + blockSize > fileLength)
    {
        throw new InvalidDataException(
            $"Windows symbol artifact '{path}' refers to invalid MSF block '{block}'.");
    }

    return checked((int)offset);
}

static uint ReadDirectoryUInt32(byte[] directory, ref int offset, string path)
{
    if (offset < 0 || offset + sizeof(uint) > directory.Length)
    {
        throw new InvalidDataException($"Windows symbol artifact '{path}' has a truncated MSF directory.");
    }

    var value = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(offset, sizeof(uint)));
    offset += sizeof(uint);
    return value;
}

static int DivideRoundUp(uint value, uint divisor)
    => checked((int)((value + (divisor - 1L)) / divisor));

static async Task<(
    Dictionary<string, string> Entries,
    int NormalizedMachOEntries)> ReadNormalizedPackageEntriesAsync(
    string path,
    bool normalizeMachOUuid,
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
    var normalizedMachOEntries = 0;
    foreach (var entry in archive.Entries
        .Where(entry => entry.Name.Length > 0 && !IsVariableNuGetContainerEntry(entry.FullName))
        .OrderBy(entry => entry.FullName, StringComparer.Ordinal))
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = entry.Open();
        string hash;
        if (normalizeMachOUuid && entry.Name == "git-tui")
        {
            if (entry.Length > int.MaxValue)
            {
                throw new InvalidDataException(
                    $"Package entry '{entry.FullName}' exceeds the supported in-memory verification size.");
            }

            using var buffer = new MemoryStream(checked((int)entry.Length));
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            hash = NormalizeMachO(
                buffer.ToArray(),
                $"{path}!/{entry.FullName}",
                isApplication: true).NormalizedSha256;
            normalizedMachOEntries++;
        }
        else
        {
            hash = await ComputeStreamHashAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        result.Add(
            entry.FullName,
            $"{entry.Length}\n{hash}");
    }

    return (result, normalizedMachOEntries);
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
    writer.WriteNumber("schemaVersion", 2);
    writer.WriteString("sourceRevision", sourceRevision);
    writer.WriteNumber("builderCountPerRuntimeIdentifier", 2);
    writer.WriteString(
        "normalization",
        "NuGet ZIP container metadata, _rels/.rels, generated core-properties, and the PDB MSF block layout " +
        "are excluded. Mach-O LC_UUID bytes are zeroed in macOS applications, RID package entries, and dSYM " +
        "images; the certificate-free arm64 linker CodeDirectory hashes are recomputed for those normalized " +
        "bytes. Windows PDB logical streams match after replacing only the GUID in MSVC linker's generated " +
        "Temp\\lnk{GUID}.tmp module path. Every other application, launcher, runtime, symbol, and package-entry " +
        "byte matches exactly; raw UUIDs and hashes remain in symbol evidence.");
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
