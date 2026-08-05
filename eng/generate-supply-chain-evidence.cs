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
    Description = "The Native AOT runtime identifier represented by the supply-chain evidence.",
    Arity = ArgumentArity.ExactlyOne,
};
var assetsFileOption = new Option<string?>("--assets-file")
{
    Description = "The restored project.assets.json for the GitSail application.",
    Arity = ArgumentArity.ExactlyOne,
};
var projectOption = new Option<string?>("--project")
{
    Description = "The GitSail application project scanned for known vulnerabilities.",
    Arity = ArgumentArity.ExactlyOne,
};
var packageDirectoryOption = new Option<string?>("--package-directory")
{
    Description = "The directory containing the RID-specific GitSail package.",
    Arity = ArgumentArity.ExactlyOne,
};
var evidenceDirectoryOption = new Option<string?>("--evidence-directory")
{
    Description = "The output directory for SBOM, license, and vulnerability evidence.",
    Arity = ArgumentArity.ExactlyOne,
};
var rootCommand = new RootCommand(
    "Generates SPDX and CycloneDX SBOMs and enforces dependency license and vulnerability policy.");
rootCommand.Options.Add(ridOption);
rootCommand.Options.Add(assetsFileOption);
rootCommand.Options.Add(projectOption);
rootCommand.Options.Add(packageDirectoryOption);
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

    foreach (var (option, name) in new[]
    {
        (assetsFileOption, "--assets-file"),
        (projectOption, "--project"),
        (packageDirectoryOption, "--package-directory"),
        (evidenceDirectoryOption, "--evidence-directory"),
    })
    {
        if (string.IsNullOrWhiteSpace(result.GetValue(option)))
        {
            result.AddError($"Option '{name}' is required.");
        }
    }
});
rootCommand.SetAction((parseResult, cancellationToken) => GenerateAsync(
    parseResult.GetValue(ridOption)!,
    parseResult.GetValue(assetsFileOption)!,
    parseResult.GetValue(projectOption)!,
    parseResult.GetValue(packageDirectoryOption)!,
    parseResult.GetValue(evidenceDirectoryOption)!,
    cancellationToken));

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);

static async Task<int> GenerateAsync(
    string rid,
    string assetsFile,
    string project,
    string packageDirectory,
    string evidenceDirectory,
    CancellationToken cancellationToken)
{
    var workingDirectory = Directory.GetCurrentDirectory();
    var assetsPath = RequireFile(assetsFile, workingDirectory, "assets file");
    var projectPath = RequireFile(project, workingDirectory, "project");
    var packageRoot = RequireDirectory(packageDirectory, workingDirectory, "package");
    var evidenceRoot = Path.GetFullPath(evidenceDirectory, workingDirectory);
    Directory.CreateDirectory(evidenceRoot);

    var sourceRevision = (await RunCheckedAsync(
        "git",
        ["rev-parse", "--verify", "HEAD"],
        workingDirectory,
        cancellationToken).ConfigureAwait(false)).Trim().ToLowerInvariant();
    if (sourceRevision.Length != 40 || sourceRevision.Any(character => !char.IsAsciiHexDigit(character)))
    {
        throw new InvalidDataException($"Git returned an invalid source revision: '{sourceRevision}'.");
    }

    var sourceTimestamp = DateTimeOffset.Parse((await RunCheckedAsync(
        "git",
        ["show", "-s", "--format=%cI", sourceRevision],
        workingDirectory,
        cancellationToken).ConfigureAwait(false)).Trim(),
        System.Globalization.CultureInfo.InvariantCulture);
    var created = sourceTimestamp.UtcDateTime.ToString(
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        System.Globalization.CultureInfo.InvariantCulture);

    await using var assetsStream = new FileStream(
        assetsPath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    using var assets = await JsonDocument.ParseAsync(assetsStream, cancellationToken: cancellationToken)
        .ConfigureAwait(false);
    var components = await LoadComponentsAsync(
        assets.RootElement,
        rid,
        cancellationToken).ConfigureAwait(false);
    var application = await LoadApplicationPackageAsync(
        packageRoot,
        rid,
        sourceRevision,
        cancellationToken).ConfigureAwait(false);

    await WriteCycloneDxAsync(
        Path.Combine(evidenceRoot, $"{rid}-cyclonedx.json"),
        rid,
        sourceRevision,
        created,
        application,
        components,
        cancellationToken).ConfigureAwait(false);
    await WriteSpdxAsync(
        Path.Combine(evidenceRoot, $"{rid}-spdx.json"),
        rid,
        sourceRevision,
        created,
        application,
        components,
        cancellationToken).ConfigureAwait(false);
    await WriteLicenseReportAsync(
        Path.Combine(evidenceRoot, $"{rid}-dependency-licenses.json"),
        rid,
        sourceRevision,
        components,
        cancellationToken).ConfigureAwait(false);
    await WriteVulnerabilityReportAsync(
        Path.Combine(evidenceRoot, $"{rid}-vulnerabilities.json"),
        rid,
        sourceRevision,
        projectPath,
        components,
        workingDirectory,
        cancellationToken).ConfigureAwait(false);

    Console.WriteLine(
        $"Generated supply-chain evidence for {application["id"]} {application["version"]} ({rid}) " +
        $"with {components.Count} restored components.");
    return 0;
}

static async Task<List<Dictionary<string, string>>> LoadComponentsAsync(
    JsonElement assets,
    string rid,
    CancellationToken cancellationToken)
{
    var targetName = $"net10.0/{rid}";
    if (!assets.GetProperty("targets").TryGetProperty(targetName, out var target))
    {
        throw new InvalidDataException($"The assets file has no '{targetName}' restore target.");
    }

    var libraries = assets.GetProperty("libraries");
    var packageFolders = assets.GetProperty("packageFolders")
        .EnumerateObject()
        .Select(property => property.Name)
        .ToArray();
    if (packageFolders.Length == 0)
    {
        throw new InvalidDataException("The assets file has no global NuGet package folder.");
    }

    var requested = new Dictionary<string, (string Version, string ContentHash, string[] Dependencies)>(
        StringComparer.OrdinalIgnoreCase);
    foreach (var package in target.EnumerateObject())
    {
        var (id, version) = SplitLibraryKey(package.Name);
        if (!libraries.TryGetProperty(package.Name, out var library) ||
            library.GetProperty("type").GetString() != "package")
        {
            continue;
        }

        var dependencies = package.Value.TryGetProperty("dependencies", out var dependencyObject)
            ? dependencyObject.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray()
            : [];
        requested[id] = (
            version,
            library.GetProperty("sha512").GetString() ?? string.Empty,
            dependencies);
    }

    var framework = assets.GetProperty("project").GetProperty("frameworks").GetProperty("net10.0");
    if (framework.TryGetProperty("downloadDependencies", out var downloadDependencies))
    {
        foreach (var dependency in downloadDependencies.EnumerateArray())
        {
            var id = dependency.GetProperty("name").GetString() ?? string.Empty;
            if (!id.Contains(rid, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var version = ParseExactVersion(dependency.GetProperty("version").GetString() ?? string.Empty);
            requested.TryAdd(id, (version, string.Empty, []));
        }
    }

    var components = new List<Dictionary<string, string>>();
    foreach (var package in requested.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = package.Key;
        var version = package.Value.Version;
        var packagePath = FindPackagePath(packageFolders, id, version);
        var nupkgPath = Directory.EnumerateFiles(packagePath, "*.nupkg", SearchOption.TopDirectoryOnly)
            .SingleOrDefault() ??
            throw new FileNotFoundException($"The restored NuGet archive for '{id}/{version}' is missing.");
        var nuspecPath = Directory.EnumerateFiles(packagePath, "*.nuspec", SearchOption.TopDirectoryOnly)
            .SingleOrDefault() ??
            throw new FileNotFoundException($"The restored nuspec for '{id}/{version}' is missing.");
        var sha256 = await ComputeHashAsync(nupkgPath, HashAlgorithmName.SHA256, cancellationToken)
            .ConfigureAwait(false);
        var sha512 = await ComputeHashAsync(nupkgPath, HashAlgorithmName.SHA512, cancellationToken)
            .ConfigureAwait(false);
        var archiveContentHash = Convert.ToBase64String(Convert.FromHexString(sha512));
        var hashRecord = Directory.EnumerateFiles(packagePath, "*.nupkg.sha512", SearchOption.TopDirectoryOnly)
            .SingleOrDefault() ??
            throw new FileNotFoundException($"The NuGet archive hash for '{id}/{version}' is missing.");
        var recordedArchiveHash = (await File.ReadAllTextAsync(hashRecord, cancellationToken)
            .ConfigureAwait(false)).Trim();
        if (recordedArchiveHash != archiveContentHash)
        {
            throw new InvalidDataException(
                $"The restored archive hash for '{id}/{version}' does not match NuGet's cache metadata.");
        }

        var metadata = LoadNuspecMetadata(nuspecPath);
        if (metadata["licenseType"] != "expression" || metadata["licenseExpression"] != "MIT")
        {
            throw new InvalidDataException(
                $"Dependency '{id}/{version}' has unapproved license '{metadata["licenseExpression"]}'.");
        }

        metadata["id"] = id;
        metadata["version"] = version;
        metadata["purl"] = CreatePurl(id, version);
        metadata["packageSha256"] = sha256;
        metadata["packageSha512"] = sha512;
        metadata["nugetContentHash"] = package.Value.ContentHash.Length == 0
            ? recordedArchiveHash
            : package.Value.ContentHash;
        metadata["archiveContentHash"] = archiveContentHash;
        metadata["dependencies"] = string.Join('\n', package.Value.Dependencies);
        metadata["relationship"] = ClassifyRelationship(id);
        components.Add(metadata);
    }

    return components;
}

static async Task<Dictionary<string, string>> LoadApplicationPackageAsync(
    string packageRoot,
    string rid,
    string sourceRevision,
    CancellationToken cancellationToken)
{
    var marker = $"GitSail.{rid}.";
    var packagePath = Directory.EnumerateFiles(packageRoot, "*.nupkg", SearchOption.TopDirectoryOnly)
        .SingleOrDefault(path => Path.GetFileName(path).StartsWith(marker, StringComparison.Ordinal)) ??
        throw new FileNotFoundException($"The '{rid}' GitSail package is missing from '{packageRoot}'.");
    using var archive = ZipFile.OpenRead(packagePath);
    var nuspecEntry = archive.Entries.Single(entry =>
        entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
    await using var nuspecStream = nuspecEntry.Open();
    var nuspec = await XDocument.LoadAsync(nuspecStream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
    var metadata = nuspec.Root?.Elements().Single().Elements().ToArray() ??
        throw new InvalidDataException("The GitSail package nuspec has no metadata.");
    var id = metadata.Single(element => element.Name.LocalName == "id").Value;
    var version = metadata.Single(element => element.Name.LocalName == "version").Value;
    var license = metadata.Single(element => element.Name.LocalName == "license");
    var repository = metadata.SingleOrDefault(element => element.Name.LocalName == "repository");
    if (id != $"GitSail.{rid}" ||
        license.Attribute("type")?.Value != "expression" ||
        license.Value != "MIT" ||
        repository?.Attribute("type")?.Value != "git" ||
        repository.Attribute("url")?.Value != "https://github.com/willibrandon/gitsail" ||
        !string.Equals(repository.Attribute("commit")?.Value, sourceRevision, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidDataException(
            "The RID package identity, license, repository, or source revision is invalid.");
    }

    return new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["id"] = id,
        ["version"] = version,
        ["purl"] = CreatePurl(id, version),
        ["packageSha256"] = await ComputeHashAsync(packagePath, HashAlgorithmName.SHA256, cancellationToken)
            .ConfigureAwait(false),
        ["packageSha512"] = await ComputeHashAsync(packagePath, HashAlgorithmName.SHA512, cancellationToken)
            .ConfigureAwait(false),
        ["licenseExpression"] = "MIT",
    };
}

static Dictionary<string, string> LoadNuspecMetadata(string nuspecPath)
{
    var document = XDocument.Load(nuspecPath, LoadOptions.None);
    var metadataElement = document.Root?.Elements().SingleOrDefault(element => element.Name.LocalName == "metadata") ??
        throw new InvalidDataException($"Nuspec '{nuspecPath}' has no metadata element.");
    var elements = metadataElement.Elements().ToArray();
    var license = elements.SingleOrDefault(element => element.Name.LocalName == "license");
    var repository = elements.SingleOrDefault(element => element.Name.LocalName == "repository");
    return new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["licenseType"] = license?.Attribute("type")?.Value ?? string.Empty,
        ["licenseExpression"] = license?.Value ?? string.Empty,
        ["licenseUrl"] = elements.SingleOrDefault(element => element.Name.LocalName == "licenseUrl")?.Value ??
            string.Empty,
        ["projectUrl"] = elements.SingleOrDefault(element => element.Name.LocalName == "projectUrl")?.Value ??
            string.Empty,
        ["repositoryUrl"] = repository?.Attribute("url")?.Value ?? string.Empty,
        ["repositoryCommit"] = repository?.Attribute("commit")?.Value ?? string.Empty,
    };
}

static async Task WriteCycloneDxAsync(
    string path,
    string rid,
    string sourceRevision,
    string created,
    IReadOnlyDictionary<string, string> application,
    IReadOnlyList<Dictionary<string, string>> components,
    CancellationToken cancellationToken)
{
    await using var output = CreateEvidenceFile(path);
    await using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
    writer.WriteStartObject();
    writer.WriteString("bomFormat", "CycloneDX");
    writer.WriteString("specVersion", "1.6");
    writer.WriteString("serialNumber", $"urn:uuid:{CreateDeterministicUuid(sourceRevision, rid)}");
    writer.WriteNumber("version", 1);
    writer.WriteStartObject("metadata");
    writer.WriteString("timestamp", created);
    writer.WriteStartObject("component");
    WriteCycloneDxComponent(writer, application, "application");
    writer.WriteStartArray("properties");
    WriteCycloneDxProperty(writer, "gitsail:runtimeIdentifier", rid);
    WriteCycloneDxProperty(writer, "gitsail:sourceRevision", sourceRevision);
    writer.WriteEndArray();
    writer.WriteEndObject();
    writer.WriteEndObject();
    writer.WriteStartArray("components");
    foreach (var component in components)
    {
        writer.WriteStartObject();
        WriteCycloneDxComponent(writer, component, "library");
        writer.WriteStartArray("properties");
        WriteCycloneDxProperty(writer, "gitsail:relationship", component["relationship"]);
        WriteCycloneDxProperty(writer, "nuget:contentHash", component["nugetContentHash"]);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    writer.WriteEndArray();
    writer.WriteStartArray("dependencies");
    writer.WriteStartObject();
    writer.WriteString("ref", application["purl"]);
    writer.WriteStartArray("dependsOn");
    foreach (var component in components)
    {
        writer.WriteStringValue(component["purl"]);
    }

    writer.WriteEndArray();
    writer.WriteEndObject();
    foreach (var component in components)
    {
        writer.WriteStartObject();
        writer.WriteString("ref", component["purl"]);
        writer.WriteStartArray("dependsOn");
        foreach (var dependency in component["dependencies"].Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var resolved = components.SingleOrDefault(candidate =>
                candidate["id"].Equals(dependency, StringComparison.OrdinalIgnoreCase)) ??
                throw new InvalidDataException(
                    $"Dependency '{dependency}' of '{component["id"]}' is absent from the restored graph.");
            writer.WriteStringValue(resolved["purl"]);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    writer.WriteEndArray();
    writer.WriteEndObject();
    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
}

static void WriteCycloneDxComponent(
    Utf8JsonWriter writer,
    IReadOnlyDictionary<string, string> component,
    string type)
{
    writer.WriteString("type", type);
    writer.WriteString("bom-ref", component["purl"]);
    writer.WriteString("name", component["id"]);
    writer.WriteString("version", component["version"]);
    writer.WriteStartArray("hashes");
    WriteHash(writer, "SHA-256", component["packageSha256"]);
    WriteHash(writer, "SHA-512", component["packageSha512"]);
    writer.WriteEndArray();
    writer.WriteStartArray("licenses");
    writer.WriteStartObject();
    writer.WriteString("expression", component["licenseExpression"]);
    writer.WriteEndObject();
    writer.WriteEndArray();
    writer.WriteString("purl", component["purl"]);
}

static void WriteHash(Utf8JsonWriter writer, string algorithm, string content)
{
    writer.WriteStartObject();
    writer.WriteString("alg", algorithm);
    writer.WriteString("content", content);
    writer.WriteEndObject();
}

static void WriteCycloneDxProperty(Utf8JsonWriter writer, string name, string value)
{
    writer.WriteStartObject();
    writer.WriteString("name", name);
    writer.WriteString("value", value);
    writer.WriteEndObject();
}

static async Task WriteSpdxAsync(
    string path,
    string rid,
    string sourceRevision,
    string created,
    IReadOnlyDictionary<string, string> application,
    IReadOnlyList<Dictionary<string, string>> components,
    CancellationToken cancellationToken)
{
    var namespaceValue =
        $"https://github.com/willibrandon/gitsail/sbom/{sourceRevision}/{rid}/{application["version"]}";
    await using var output = CreateEvidenceFile(path);
    await using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
    writer.WriteStartObject();
    writer.WriteString("spdxVersion", "SPDX-2.3");
    writer.WriteString("dataLicense", "CC0-1.0");
    writer.WriteString("SPDXID", "SPDXRef-DOCUMENT");
    writer.WriteString("name", $"GitSail-{application["version"]}-{rid}");
    writer.WriteString("documentNamespace", namespaceValue);
    writer.WriteStartObject("creationInfo");
    writer.WriteString("created", created);
    writer.WriteStartArray("creators");
    writer.WriteStringValue("Organization: GitSail contributors");
    writer.WriteStringValue("Tool: GitSail supply-chain evidence generator");
    writer.WriteEndArray();
    writer.WriteEndObject();
    writer.WriteStartArray("packages");
    WriteSpdxPackage(writer, application, "SPDXRef-Package-GitSail");
    foreach (var component in components)
    {
        WriteSpdxPackage(writer, component, CreateSpdxId(component));
    }

    writer.WriteEndArray();
    writer.WriteStartArray("relationships");
    WriteSpdxRelationship(writer, "SPDXRef-DOCUMENT", "DESCRIBES", "SPDXRef-Package-GitSail");
    foreach (var component in components)
    {
        var componentId = CreateSpdxId(component);
        if (component["relationship"] == "build-tool")
        {
            WriteSpdxRelationship(writer, componentId, "BUILD_DEPENDENCY_OF", "SPDXRef-Package-GitSail");
        }
        else
        {
            WriteSpdxRelationship(writer, "SPDXRef-Package-GitSail", "DEPENDS_ON", componentId);
        }

        foreach (var dependency in component["dependencies"].Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var resolved = components.SingleOrDefault(candidate =>
                candidate["id"].Equals(dependency, StringComparison.OrdinalIgnoreCase)) ??
                throw new InvalidDataException(
                    $"Dependency '{dependency}' of '{component["id"]}' is absent from the restored graph.");
            WriteSpdxRelationship(writer, componentId, "DEPENDS_ON", CreateSpdxId(resolved));
        }
    }

    writer.WriteEndArray();
    writer.WriteEndObject();
    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
}

static void WriteSpdxPackage(
    Utf8JsonWriter writer,
    IReadOnlyDictionary<string, string> component,
    string spdxId)
{
    writer.WriteStartObject();
    writer.WriteString("name", component["id"]);
    writer.WriteString("SPDXID", spdxId);
    writer.WriteString("versionInfo", component["version"]);
    writer.WriteString(
        "downloadLocation",
        $"https://www.nuget.org/packages/{Uri.EscapeDataString(component["id"])}/{component["version"]}");
    writer.WriteBoolean("filesAnalyzed", false);
    writer.WriteStartArray("checksums");
    WriteSpdxChecksum(writer, "SHA256", component["packageSha256"]);
    WriteSpdxChecksum(writer, "SHA512", component["packageSha512"]);
    writer.WriteEndArray();
    writer.WriteString("licenseConcluded", component["licenseExpression"]);
    writer.WriteString("licenseDeclared", component["licenseExpression"]);
    writer.WriteString("copyrightText", "NOASSERTION");
    writer.WriteStartArray("externalRefs");
    writer.WriteStartObject();
    writer.WriteString("referenceCategory", "PACKAGE-MANAGER");
    writer.WriteString("referenceType", "purl");
    writer.WriteString("referenceLocator", component["purl"]);
    writer.WriteEndObject();
    writer.WriteEndArray();
    writer.WriteEndObject();
}

static void WriteSpdxChecksum(Utf8JsonWriter writer, string algorithm, string value)
{
    writer.WriteStartObject();
    writer.WriteString("algorithm", algorithm);
    writer.WriteString("checksumValue", value);
    writer.WriteEndObject();
}

static void WriteSpdxRelationship(
    Utf8JsonWriter writer,
    string source,
    string relationship,
    string target)
{
    writer.WriteStartObject();
    writer.WriteString("spdxElementId", source);
    writer.WriteString("relationshipType", relationship);
    writer.WriteString("relatedSpdxElement", target);
    writer.WriteEndObject();
}

static async Task WriteLicenseReportAsync(
    string path,
    string rid,
    string sourceRevision,
    IReadOnlyList<Dictionary<string, string>> components,
    CancellationToken cancellationToken)
{
    await using var output = CreateEvidenceFile(path);
    await using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
    writer.WriteStartObject();
    writer.WriteNumber("schemaVersion", 1);
    writer.WriteString("runtimeIdentifier", rid);
    writer.WriteString("sourceRevision", sourceRevision);
    writer.WriteString("policy", "Every restored package must declare the approved MIT SPDX expression.");
    writer.WriteBoolean("passed", true);
    writer.WriteStartArray("packages");
    foreach (var component in components)
    {
        writer.WriteStartObject();
        foreach (var key in new[]
        {
            "id",
            "version",
            "relationship",
            "licenseType",
            "licenseExpression",
            "licenseUrl",
            "projectUrl",
            "repositoryUrl",
            "repositoryCommit",
            "packageSha256",
            "packageSha512",
            "nugetContentHash",
            "archiveContentHash",
        })
        {
            writer.WriteString(key, component[key]);
        }

        writer.WriteEndObject();
    }

    writer.WriteEndArray();
    writer.WriteEndObject();
    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
}

static async Task WriteVulnerabilityReportAsync(
    string path,
    string rid,
    string sourceRevision,
    string projectPath,
    IReadOnlyList<Dictionary<string, string>> components,
    string workingDirectory,
    CancellationToken cancellationToken)
{
    var result = await RunCapturedAsync(
        "dotnet",
        [
            "package",
            "list",
            "--project",
            projectPath,
            "--vulnerable",
            "--include-transitive",
            "--format",
            "json",
            "--output-version",
            "1",
            "--no-restore",
        ],
        workingDirectory,
        cancellationToken).ConfigureAwait(false);
    if (result.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"The NuGet vulnerability scan exited with code {result.ExitCode}." +
            $"{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}");
    }

    using var scan = JsonDocument.Parse(result.StandardOutput);
    if (scan.RootElement.TryGetProperty("problems", out var problems) && problems.GetArrayLength() > 0)
    {
        throw new InvalidDataException("The NuGet vulnerability scan reported a project evaluation problem.");
    }

    if (ContainsVulnerability(scan.RootElement))
    {
        throw new InvalidDataException("The exact restored graph contains a package with a known vulnerability.");
    }

    await using var output = CreateEvidenceFile(path);
    await using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
    writer.WriteStartObject();
    writer.WriteNumber("schemaVersion", 1);
    writer.WriteString("runtimeIdentifier", rid);
    writer.WriteString("sourceRevision", sourceRevision);
    writer.WriteString("scanner", "NuGet audit through .NET SDK 10.0.302");
    writer.WriteString("auditMode", "all direct and transitive dependencies");
    writer.WriteString("status", "no-known-vulnerabilities");
    writer.WriteStartArray("restoredComponents");
    foreach (var component in components)
    {
        writer.WriteStartObject();
        writer.WriteString("id", component["id"]);
        writer.WriteString("version", component["version"]);
        writer.WriteString("nugetContentHash", component["nugetContentHash"]);
        writer.WriteEndObject();
    }

    writer.WriteEndArray();
    writer.WriteStartObject("scannerResult");
    writer.WriteNumber("version", scan.RootElement.GetProperty("version").GetInt32());
    writer.WriteString("parameters", scan.RootElement.GetProperty("parameters").GetString());
    writer.WriteStartArray("sources");
    foreach (var source in scan.RootElement.GetProperty("sources").EnumerateArray())
    {
        writer.WriteStringValue(source.GetString());
    }

    writer.WriteEndArray();
    writer.WriteStartArray("projects");
    foreach (var project in scan.RootElement.GetProperty("projects").EnumerateArray())
    {
        var scannedPath = project.GetProperty("path").GetString() ?? string.Empty;
        writer.WriteStartObject();
        writer.WriteString("path", NormalizePath(Path.GetRelativePath(workingDirectory, scannedPath)));
        writer.WriteEndObject();
    }

    writer.WriteEndArray();
    writer.WriteEndObject();
    writer.WriteEndObject();
    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
}

static bool ContainsVulnerability(JsonElement element)
{
    if (element.ValueKind == JsonValueKind.Object)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals("vulnerabilities") &&
                property.Value.ValueKind == JsonValueKind.Array &&
                property.Value.GetArrayLength() > 0)
            {
                return true;
            }

            if (ContainsVulnerability(property.Value))
            {
                return true;
            }
        }
    }
    else if (element.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in element.EnumerateArray())
        {
            if (ContainsVulnerability(item))
            {
                return true;
            }
        }
    }

    return false;
}

static (string Id, string Version) SplitLibraryKey(string value)
{
    var separator = value.LastIndexOf('/');
    if (separator <= 0 || separator == value.Length - 1)
    {
        throw new InvalidDataException($"Invalid NuGet library key '{value}'.");
    }

    return (value[..separator], value[(separator + 1)..]);
}

static string ParseExactVersion(string value)
{
    var parts = value.Trim('[', ']').Split(',', StringSplitOptions.TrimEntries);
    if (parts.Length != 2 || parts[0].Length == 0 || parts[0] != parts[1])
    {
        throw new InvalidDataException($"NuGet download dependency version '{value}' is not exact.");
    }

    return parts[0];
}

static string FindPackagePath(IReadOnlyList<string> packageFolders, string id, string version)
{
    foreach (var folder in packageFolders)
    {
        var candidate = Path.Combine(folder, id.ToLowerInvariant(), version.ToLowerInvariant());
        if (Directory.Exists(candidate))
        {
            return candidate;
        }
    }

    throw new DirectoryNotFoundException($"Restored package '{id}/{version}' is missing from the global cache.");
}

static string ClassifyRelationship(string id)
    => id.Contains("ILCompiler", StringComparison.OrdinalIgnoreCase) ||
        id.Contains("ILLink", StringComparison.OrdinalIgnoreCase)
        ? "build-tool"
        : id.StartsWith("Microsoft.NETCore.App.Runtime.", StringComparison.OrdinalIgnoreCase) ||
          id.StartsWith("Microsoft.AspNetCore.App.Runtime.", StringComparison.OrdinalIgnoreCase)
            ? "native-runtime-contributor"
            : "application-dependency";

static string CreatePurl(string id, string version)
    => $"pkg:nuget/{Uri.EscapeDataString(id)}@{Uri.EscapeDataString(version)}";

static string CreateSpdxId(IReadOnlyDictionary<string, string> component)
{
    var normalized = new string(
    [
        .. component["id"].Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-'),
    ]);
    var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(component["purl"])))
        .ToLowerInvariant()[..12];
    return $"SPDXRef-Package-{normalized}-{suffix}";
}

static string CreateDeterministicUuid(string sourceRevision, string rid)
{
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"GitSail\n{sourceRevision}\n{rid}"))[..16];
    bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
    bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
    var text = Convert.ToHexString(bytes).ToLowerInvariant();
    return $"{text[..8]}-{text[8..12]}-{text[12..16]}-{text[16..20]}-{text[20..]}";
}

static string NormalizePath(string path)
    => path.Replace(Path.DirectorySeparatorChar, '/');

static string RequireFile(string path, string workingDirectory, string description)
{
    var fullPath = Path.GetFullPath(path, workingDirectory);
    if (!File.Exists(fullPath))
    {
        throw new FileNotFoundException($"The {description} is missing.", fullPath);
    }

    return fullPath;
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

static FileStream CreateEvidenceFile(string path)
    => new(
        path,
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

static async Task<string> RunCheckedAsync(
    string fileName,
    IReadOnlyList<string> arguments,
    string workingDirectory,
    CancellationToken cancellationToken)
{
    var result = await RunCapturedAsync(fileName, arguments, workingDirectory, cancellationToken)
        .ConfigureAwait(false);
    if (result.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"Process '{fileName}' exited with code {result.ExitCode}." +
            $"{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}");
    }

    return result.StandardOutput;
}

static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunCapturedAsync(
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
