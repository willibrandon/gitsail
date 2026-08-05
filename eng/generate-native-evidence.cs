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

    if (string.IsNullOrWhiteSpace(result.GetValue(evidenceDirectoryOption)))
    {
        result.AddError("Option '--evidence-directory' is required.");
    }
});
rootCommand.SetAction((parseResult, cancellationToken) => GenerateAsync(
    parseResult.GetValue(ridOption)!,
    parseResult.GetValue(publishDirectoryOption)!,
    parseResult.GetValue(packageDirectoryOption)!,
    parseResult.GetValue(evidenceDirectoryOption)!,
    cancellationToken));

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);

static async Task<int> GenerateAsync(
    string rid,
    string publishDirectory,
    string packageDirectory,
    string evidenceDirectory,
    CancellationToken cancellationToken)
{
    var workingDirectory = Directory.GetCurrentDirectory();
    var publishRoot = RequireDirectory(publishDirectory, workingDirectory, "publish");
    var packageRoot = RequireDirectory(packageDirectory, workingDirectory, "package");
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

    Console.WriteLine($"Generated Native AOT release evidence for {rid} in {evidenceRoot}.");
    return 0;
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
