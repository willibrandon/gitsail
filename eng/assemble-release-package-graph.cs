#:package System.CommandLine

using System.CommandLine;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

var inputDirectoryOption = new Option<string?>("--input-directory")
{
    Description = "The directory containing eight downloaded RID packages and one pointer package.",
    Arity = ArgumentArity.ExactlyOne,
};
var outputDirectoryOption = new Option<string?>("--output-directory")
{
    Description = "The empty directory that will receive the exact nine-package release graph.",
    Arity = ArgumentArity.ExactlyOne,
};
var sourceRevisionOption = new Option<string?>("--source-revision")
{
    Description = "The full source revision represented by the package graph.",
    Arity = ArgumentArity.ExactlyOne,
};
var rootCommand = new RootCommand(
    "Assembles and verifies one pointer package and eight RID-specific Native AOT packages.");
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

    if (string.IsNullOrWhiteSpace(result.GetValue(sourceRevisionOption)))
    {
        result.AddError("Option '--source-revision' is required.");
    }
});
rootCommand.SetAction((parseResult, cancellationToken) => AssembleAsync(
    parseResult.GetValue(inputDirectoryOption)!,
    parseResult.GetValue(outputDirectoryOption)!,
    parseResult.GetValue(sourceRevisionOption)!,
    cancellationToken));

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);

static async Task<int> AssembleAsync(
    string inputDirectory,
    string outputDirectory,
    string sourceRevision,
    CancellationToken cancellationToken)
{
    var workingDirectory = Directory.GetCurrentDirectory();
    var inputRoot = Path.GetFullPath(inputDirectory, workingDirectory);
    var outputRoot = Path.GetFullPath(outputDirectory, workingDirectory);
    if (sourceRevision.Length is not (40 or 64) || sourceRevision.Any(character => !Uri.IsHexDigit(character)))
    {
        throw new ArgumentException("The source revision must be a full hexadecimal object ID.", nameof(sourceRevision));
    }

    if (!Directory.Exists(inputRoot))
    {
        throw new DirectoryNotFoundException($"The release input directory does not exist: {inputRoot}");
    }

    if (Directory.Exists(outputRoot) && Directory.EnumerateFileSystemEntries(outputRoot).Any())
    {
        throw new InvalidOperationException($"The release output directory is not empty: {outputRoot}");
    }

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
    var expectedIds = expectedRids
        .Select(rid => $"GitSail.{rid}")
        .Append("GitSail")
        .ToHashSet(StringComparer.Ordinal);
    var packagePaths = Directory.EnumerateFiles(inputRoot, "*.nupkg", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .ToArray();
    if (packagePaths.Length != expectedRids.Length + 1)
    {
        throw new InvalidDataException(
            $"The release input must contain exactly nine package files; found {packagePaths.Length}.");
    }

    var packages = new List<(string Path, string Id, string Version, string PackageType)>();
    foreach (var path in packagePaths)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var identity = ReadPackageIdentity(path);
        if (!expectedIds.Contains(identity.Id))
        {
            throw new InvalidDataException(
                $"Release input contains unexpected package '{identity.Id}': {path}");
        }

        if (!string.Equals(identity.SourceRevision, sourceRevision, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Package '{identity.Id}' represents source revision '{identity.SourceRevision}', " +
                $"not '{sourceRevision}'.");
        }

        if (identity.LicenseExpression != "MIT" ||
            identity.RepositoryUrl != "https://github.com/willibrandon/gitsail")
        {
            throw new InvalidDataException(
                $"Package '{identity.Id}' has invalid license or repository metadata.");
        }

        packages.Add((path, identity.Id, identity.Version, identity.PackageType));
    }

    var versions = packages.Select(package => package.Version).Distinct(StringComparer.Ordinal).ToArray();
    if (versions.Length != 1)
    {
        throw new InvalidDataException(
            $"Release package versions do not match: {string.Join(", ", versions)}");
    }

    var version = versions[0];
    var selected = new List<(string Path, string Id)>();
    foreach (var rid in expectedRids)
    {
        var id = $"GitSail.{rid}";
        var matches = packages.Where(package => package.Id == id).ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException(
                $"Release input must contain exactly one '{id}' package; found {matches.Length}.");
        }

        var expectedName = $"{id}.{version}.nupkg";
        if (Path.GetFileName(matches[0].Path) != expectedName)
        {
            throw new InvalidDataException(
                $"RID package '{id}' has filename '{Path.GetFileName(matches[0].Path)}', " +
                $"expected '{expectedName}'.");
        }

        if (matches[0].PackageType != "DotnetToolRidPackage")
        {
            throw new InvalidDataException(
                $"RID package '{id}' has package type '{matches[0].PackageType}', not 'DotnetToolRidPackage'.");
        }

        selected.Add((matches[0].Path, id));
    }

    var pointerPackages = packages.Where(package => package.Id == "GitSail").ToArray();
    if (pointerPackages.Length != 1)
    {
        throw new InvalidDataException(
            $"Release input must contain exactly one pointer package; found {pointerPackages.Length}.");
    }

    var expectedPointerName = $"GitSail.{version}.nupkg";
    if (pointerPackages.Any(package => Path.GetFileName(package.Path) != expectedPointerName))
    {
        throw new InvalidDataException(
            $"Every pointer package must be named '{expectedPointerName}'.");
    }

    if (pointerPackages[0].PackageType != "DotnetTool")
    {
        throw new InvalidDataException(
            $"The pointer package has package type '{pointerPackages[0].PackageType}', not 'DotnetTool'.");
    }

    selected.Add((pointerPackages[0].Path, "GitSail"));
    Directory.CreateDirectory(outputRoot);
    var outputPackages = new List<(string Path, string Id, long Size, string Sha256, string Sha512)>();
    foreach (var package in selected.OrderBy(package => package.Id, StringComparer.Ordinal))
    {
        var destination = Path.Combine(outputRoot, Path.GetFileName(package.Path));
        await CopyPackageAsync(package.Path, destination, cancellationToken).ConfigureAwait(false);
        var information = new FileInfo(destination);
        outputPackages.Add((
            destination,
            package.Id,
            information.Length,
            await ComputeHashAsync(
                destination,
                HashAlgorithmName.SHA256,
                cancellationToken).ConfigureAwait(false),
            await ComputeHashAsync(
                destination,
                HashAlgorithmName.SHA512,
                cancellationToken).ConfigureAwait(false)));
    }

    if (outputPackages.Count != expectedRids.Length + 1)
    {
        throw new InvalidDataException(
            $"The assembled release graph contains {outputPackages.Count} packages instead of nine.");
    }

    await WriteManifestAsync(
        Path.Combine(outputRoot, "release-package-graph.json"),
        sourceRevision,
        version,
        outputPackages,
        cancellationToken).ConfigureAwait(false);
    Console.WriteLine($"Assembled the exact nine-package GitSail {version} release graph.");
    return 0;
}

static (
    string Id,
    string Version,
    string PackageType,
    string LicenseExpression,
    string RepositoryUrl,
    string SourceRevision) ReadPackageIdentity(string path)
{
    using var archive = ZipFile.OpenRead(path);
    var nuspecs = archive.Entries
        .Where(entry => entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) &&
            !entry.FullName.Contains('/'))
        .ToArray();
    if (nuspecs.Length != 1)
    {
        throw new InvalidDataException(
            $"Package '{path}' must contain exactly one root nuspec; found {nuspecs.Length}.");
    }

    using var stream = nuspecs[0].Open();
    var document = XDocument.Load(stream, LoadOptions.None);
    var root = document.Root ?? throw new InvalidDataException($"Package '{path}' has an empty nuspec.");
    var metadata = root.Element(root.Name.Namespace + "metadata") ??
        throw new InvalidDataException($"Package '{path}' has no nuspec metadata.");
    var id = metadata.Element(root.Name.Namespace + "id")?.Value;
    var version = metadata.Element(root.Name.Namespace + "version")?.Value;
    var packageType = metadata.Element(root.Name.Namespace + "packageTypes")?
        .Elements(root.Name.Namespace + "packageType")
        .SingleOrDefault()?
        .Attribute("name")?
        .Value;
    var license = metadata.Element(root.Name.Namespace + "license");
    var repository = metadata.Element(root.Name.Namespace + "repository");
    if (string.IsNullOrWhiteSpace(id) ||
        string.IsNullOrWhiteSpace(version) ||
        string.IsNullOrWhiteSpace(packageType) ||
        license?.Attribute("type")?.Value != "expression" ||
        string.IsNullOrWhiteSpace(license.Value) ||
        repository?.Attribute("type")?.Value != "git" ||
        string.IsNullOrWhiteSpace(repository.Attribute("url")?.Value) ||
        string.IsNullOrWhiteSpace(repository.Attribute("commit")?.Value))
    {
        throw new InvalidDataException($"Package '{path}' has incomplete release metadata.");
    }

    return (
        id,
        version,
        packageType,
        license.Value,
        repository.Attribute("url")!.Value,
        repository.Attribute("commit")!.Value);
}

static async Task CopyPackageAsync(
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
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
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

static async Task WriteManifestAsync(
    string path,
    string sourceRevision,
    string version,
    IReadOnlyList<(string Path, string Id, long Size, string Sha256, string Sha512)> packages,
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
    writer.WriteString("version", version);
    writer.WriteNumber("packageCount", packages.Count);
    writer.WriteStartArray("packages");
    foreach (var package in packages.OrderBy(package => package.Id, StringComparer.Ordinal))
    {
        writer.WriteStartObject();
        writer.WriteString("id", package.Id);
        writer.WriteString("fileName", Path.GetFileName(package.Path));
        writer.WriteNumber("size", package.Size);
        writer.WriteString("sha256", package.Sha256);
        writer.WriteString("sha512", package.Sha512);
        writer.WriteEndObject();
    }

    writer.WriteEndArray();
    writer.WriteEndObject();
    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
}
