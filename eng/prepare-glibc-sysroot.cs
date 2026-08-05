#:package System.CommandLine

using System.CommandLine;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

var ridOption = new Option<string?>("--rid")
{
    Description = "The native glibc Linux runtime identifier.",
    Arity = ArgumentArity.ExactlyOne,
};
var outputDirectoryOption = new Option<string?>("--output-directory")
{
    Description = "The directory that will receive the verified glibc 2.27 sysroot.",
    Arity = ArgumentArity.ExactlyOne,
};
var evidenceDirectoryOption = new Option<string?>("--evidence-directory")
{
    Description = "The directory that will receive the pinned sysroot input manifest.",
    Arity = ArgumentArity.ExactlyOne,
};
var rootCommand = new RootCommand(
    "Downloads, verifies, and extracts GitSail's pinned Ubuntu glibc 2.27 build sysroot.");
rootCommand.Options.Add(ridOption);
rootCommand.Options.Add(outputDirectoryOption);
rootCommand.Options.Add(evidenceDirectoryOption);
rootCommand.Validators.Add(result =>
{
    if (result.GetValue(ridOption) is not ("linux-x64" or "linux-arm64"))
    {
        result.AddError("Option '--rid' must be 'linux-x64' or 'linux-arm64'.");
    }

    if (string.IsNullOrWhiteSpace(result.GetValue(outputDirectoryOption)))
    {
        result.AddError("Option '--output-directory' is required.");
    }

    if (string.IsNullOrWhiteSpace(result.GetValue(evidenceDirectoryOption)))
    {
        result.AddError("Option '--evidence-directory' is required.");
    }
});
rootCommand.SetAction((parseResult, cancellationToken) => PrepareAsync(
    parseResult.GetValue(ridOption)!,
    parseResult.GetValue(outputDirectoryOption)!,
    parseResult.GetValue(evidenceDirectoryOption)!,
    cancellationToken));

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);

static async Task<int> PrepareAsync(
    string rid,
    string outputDirectory,
    string evidenceDirectory,
    CancellationToken cancellationToken)
{
    if (!OperatingSystem.IsLinux())
    {
        throw new PlatformNotSupportedException("The glibc sysroot is prepared only on native Linux builders.");
    }

    var expectedArchitecture = rid == "linux-x64"
        ? System.Runtime.InteropServices.Architecture.X64
        : System.Runtime.InteropServices.Architecture.Arm64;
    if (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != expectedArchitecture)
    {
        throw new PlatformNotSupportedException(
            $"Runtime identifier '{rid}' requires a native {expectedArchitecture} builder.");
    }

    var workingDirectory = Directory.GetCurrentDirectory();
    var outputRoot = Path.GetFullPath(outputDirectory, workingDirectory);
    var evidenceRoot = Path.GetFullPath(evidenceDirectory, workingDirectory);
    var manifestName = $"{rid}-glibc-sysroot.json";
    var retainedManifest = Path.Combine(evidenceRoot, manifestName);
    var installedManifest = Path.Combine(outputRoot, manifestName);
    var targetTriple = rid == "linux-x64" ? "x86_64-linux-gnu" : "aarch64-linux-gnu";
    var packages = GetPackages(rid);

    if (File.Exists(installedManifest))
    {
        VerifySysroot(outputRoot, targetTriple);
        Directory.CreateDirectory(evidenceRoot);
        File.Copy(installedManifest, retainedManifest, overwrite: true);
        Console.WriteLine($"Verified existing glibc 2.27 sysroot for {rid}: {outputRoot}");
        return 0;
    }

    if (Directory.Exists(outputRoot) && Directory.EnumerateFileSystemEntries(outputRoot).Any())
    {
        throw new InvalidOperationException(
            $"The sysroot output directory exists but is not a verified GitSail sysroot: {outputRoot}");
    }

    var parent = Path.GetDirectoryName(outputRoot) ??
        throw new InvalidOperationException($"The sysroot output has no parent directory: {outputRoot}");
    Directory.CreateDirectory(parent);
    var stagingRoot = Path.Combine(parent, $".{Path.GetFileName(outputRoot)}.staging-{Guid.NewGuid():N}");
    Directory.CreateDirectory(stagingRoot);
    try
    {
        var downloadRoot = Path.Combine(stagingRoot, "packages");
        var sysroot = Path.Combine(stagingRoot, "rootfs");
        Directory.CreateDirectory(downloadRoot);
        Directory.CreateDirectory(sysroot);

        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GitSail-build/1.0");
        foreach (var package in packages)
        {
            var packagePath = Path.Combine(downloadRoot, package.FileName);
            await DownloadVerifiedAsync(
                client,
                package.Url,
                package.Sha256,
                packagePath,
                cancellationToken).ConfigureAwait(false);
            await RunCheckedAsync(
                "dpkg-deb",
                ["--extract", packagePath, sysroot],
                workingDirectory,
                cancellationToken).ConfigureAwait(false);
        }

        VerifySysroot(stagingRoot, targetTriple);
        await WriteManifestAsync(
            Path.Combine(stagingRoot, manifestName),
            rid,
            targetTriple,
            packages,
            cancellationToken).ConfigureAwait(false);

        if (Directory.Exists(outputRoot))
        {
            if (Directory.EnumerateFileSystemEntries(outputRoot).Any())
            {
                throw new InvalidOperationException(
                    $"The sysroot output directory changed while it was being prepared: {outputRoot}");
            }

            Directory.Delete(outputRoot);
        }

        Directory.Move(stagingRoot, outputRoot);
    }
    finally
    {
        if (Directory.Exists(stagingRoot))
        {
            Directory.Delete(stagingRoot, recursive: true);
        }
    }

    VerifySysroot(outputRoot, targetTriple);
    Directory.CreateDirectory(evidenceRoot);
    File.Copy(installedManifest, retainedManifest, overwrite: true);
    Console.WriteLine($"Prepared pinned glibc 2.27 sysroot for {rid}: {outputRoot}");
    return 0;
}

static (string FileName, string Url, string Sha256)[] GetPackages(string rid)
{
    const string amd64Base = "https://security.ubuntu.com/ubuntu/";
    const string arm64Base = "https://ports.ubuntu.com/ubuntu-ports/";
    return rid == "linux-x64"
        ?
        [
            (
                "libc-dev-bin_2.27-3ubuntu1.5_amd64.deb",
                amd64Base + "pool/main/g/glibc/libc-dev-bin_2.27-3ubuntu1.5_amd64.deb",
                "19dba0f1f822b59ba7d672467ae9d77c679264bf30bd89af1c87323e3f1069b2"),
            (
                "libc6_2.27-3ubuntu1.5_amd64.deb",
                amd64Base + "pool/main/g/glibc/libc6_2.27-3ubuntu1.5_amd64.deb",
                "c4af07dc8a2fdc9c4f25f103cd03bcca7231c19d9ac9171fa7eb5eecb7fc46d7"),
            (
                "libc6-dev_2.27-3ubuntu1.5_amd64.deb",
                amd64Base + "pool/main/g/glibc/libc6-dev_2.27-3ubuntu1.5_amd64.deb",
                "9bbf759782ac78b05d761d41ad258f243d5ac73cee03ee456d4e69179d07975d"),
            (
                "gcc-8-base_8.4.0-1ubuntu1~18.04_amd64.deb",
                amd64Base + "pool/main/g/gcc-8/gcc-8-base_8.4.0-1ubuntu1~18.04_amd64.deb",
                "68ef7e8bb42275140fd9e22193994cb3026557aac020fbb08bd7db1c0f8840d1"),
            (
                "libgcc1_8.4.0-1ubuntu1~18.04_amd64.deb",
                amd64Base + "pool/main/g/gcc-8/libgcc1_8.4.0-1ubuntu1~18.04_amd64.deb",
                "116dadf4ceaba7150eb46a6598dd3defa62b1b2a6578fb494f4c932878634994"),
            (
                "libgcc-7-dev_7.5.0-3ubuntu1~18.04_amd64.deb",
                amd64Base + "pool/main/g/gcc-7/libgcc-7-dev_7.5.0-3ubuntu1~18.04_amd64.deb",
                "65b9b7817a6d103bd737fd35be5e9d462ccb9fa23076e0ae02e662404ee26694"),
            (
                "linux-libc-dev_4.15.0-213.224_amd64.deb",
                amd64Base + "pool/main/l/linux/linux-libc-dev_4.15.0-213.224_amd64.deb",
                "06d9f16644d3a1b2ab7dd01730e833d516b6f575d975855f496eea6463fef2f6"),
        ]
        :
        [
            (
                "libc-dev-bin_2.27-3ubuntu1.5_arm64.deb",
                arm64Base + "pool/main/g/glibc/libc-dev-bin_2.27-3ubuntu1.5_arm64.deb",
                "8540967e8d70f535d61eb404a81b18b5584eafe865fd2a6481d0a207aff6d99b"),
            (
                "libc6_2.27-3ubuntu1.5_arm64.deb",
                arm64Base + "pool/main/g/glibc/libc6_2.27-3ubuntu1.5_arm64.deb",
                "776eca0b14a2a9305650c46dfbabe317a647dceed068217c7173717cbd0f8811"),
            (
                "libc6-dev_2.27-3ubuntu1.5_arm64.deb",
                arm64Base + "pool/main/g/glibc/libc6-dev_2.27-3ubuntu1.5_arm64.deb",
                "823cfb7ac6d94b1ee42b1d81ad84d9cc8ea6c7976cf999d0b29c95c3113423ad"),
            (
                "gcc-8-base_8.4.0-1ubuntu1~18.04_arm64.deb",
                arm64Base + "pool/main/g/gcc-8/gcc-8-base_8.4.0-1ubuntu1~18.04_arm64.deb",
                "e66b16145b8610ae5cc6b090457539c940160b5dcb0bad2d51bb59ecef0c8c40"),
            (
                "libgcc1_8.4.0-1ubuntu1~18.04_arm64.deb",
                arm64Base + "pool/main/g/gcc-8/libgcc1_8.4.0-1ubuntu1~18.04_arm64.deb",
                "6e5afc6293e54fd4a11136b46dccf24c55989af7eccff9ecb3ba5a0b2069b3be"),
            (
                "libgcc-7-dev_7.5.0-3ubuntu1~18.04_arm64.deb",
                arm64Base + "pool/main/g/gcc-7/libgcc-7-dev_7.5.0-3ubuntu1~18.04_arm64.deb",
                "0bc7748ee1bf19a98a19d0891fc7fccec160417131d79fa7a1a75a3efae82a7b"),
            (
                "linux-libc-dev_4.15.0-213.224_arm64.deb",
                arm64Base + "pool/main/l/linux/linux-libc-dev_4.15.0-213.224_arm64.deb",
                "9e350db851f6cf12ae14d6a611ec2102e7d03004e9ea143c67e541fe7cf11f47"),
        ];
}

static async Task DownloadVerifiedAsync(
    HttpClient client,
    string url,
    string expectedSha256,
    string destination,
    CancellationToken cancellationToken)
{
    using var response = await client.GetAsync(
        url,
        HttpCompletionOption.ResponseHeadersRead,
        cancellationToken).ConfigureAwait(false);
    response.EnsureSuccessStatusCode();
    await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
    await using var output = new FileStream(
        destination,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        bufferSize: 64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    var buffer = new byte[64 * 1024];
    while (true)
    {
        var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            break;
        }

        hash.AppendData(buffer, 0, read);
        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
    }

    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    var actualSha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    if (!string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            $"Downloaded sysroot package '{url}' has SHA-256 '{actualSha256}', expected '{expectedSha256}'.");
    }
}

static void VerifySysroot(string outputRoot, string targetTriple)
{
    var sysroot = Path.Combine(outputRoot, "rootfs");
    var requiredFiles = new[]
    {
        Path.Combine(sysroot, "lib", targetTriple, "libc.so.6"),
        Path.Combine(sysroot, "usr", "include", "stdio.h"),
        Path.Combine(sysroot, "usr", "lib", targetTriple, "crt1.o"),
        Path.Combine(sysroot, "usr", "lib", targetTriple, "crti.o"),
        Path.Combine(sysroot, "usr", "lib", targetTriple, "libc.so"),
        Path.Combine(sysroot, "usr", "lib", "gcc", targetTriple, "7", "crtbeginS.o"),
        Path.Combine(sysroot, "usr", "lib", "gcc", targetTriple, "7", "libgcc.a"),
        Path.Combine(sysroot, "lib", targetTriple, "libgcc_s.so.1"),
    };
    var missing = requiredFiles.Where(file => !File.Exists(file)).ToArray();
    if (missing.Length > 0)
    {
        throw new InvalidDataException(
            $"The extracted glibc 2.27 sysroot is incomplete: {string.Join(", ", missing)}");
    }
}

static async Task WriteManifestAsync(
    string path,
    string rid,
    string targetTriple,
    IReadOnlyList<(string FileName, string Url, string Sha256)> packages,
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
    writer.WriteString("runtimeIdentifier", rid);
    writer.WriteString("glibcVersion", "2.27");
    writer.WriteString("distribution", "Ubuntu 18.04 LTS");
    writer.WriteString("targetTriple", targetTriple);
    writer.WriteStartArray("packages");
    foreach (var package in packages)
    {
        writer.WriteStartObject();
        writer.WriteString("fileName", package.FileName);
        writer.WriteString("url", package.Url);
        writer.WriteString("sha256", package.Sha256);
        writer.WriteEndObject();
    }

    writer.WriteEndArray();
    writer.WriteEndObject();
    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
}

static async Task RunCheckedAsync(
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

    var output = await standardOutput.ConfigureAwait(false);
    var error = await standardError.ConfigureAwait(false);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"Process '{fileName}' exited with code {process.ExitCode}.{Environment.NewLine}{output}{error}");
    }
}
