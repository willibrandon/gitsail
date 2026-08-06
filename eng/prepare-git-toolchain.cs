#:package System.CommandLine

using System.CommandLine;
using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var versionOption = new Option<string?>("--git-version")
{
    Description = "The exact Git release version to build.",
    Arity = ArgumentArity.ExactlyOne,
};
var expectedSha256Option = new Option<string?>("--expected-sha256")
{
    Description = "The lowercase SHA-256 of the official Git source archive.",
    Arity = ArgumentArity.ExactlyOne,
};
var outputDirectoryOption = new Option<string?>("--output-directory")
{
    Description = "The new directory that will receive the verified Git installation.",
    Arity = ArgumentArity.ExactlyOne,
};
var rootCommand = new RootCommand(
    "Downloads, verifies, builds, and exposes one pinned Git compatibility toolchain.");
rootCommand.Options.Add(versionOption);
rootCommand.Options.Add(expectedSha256Option);
rootCommand.Options.Add(outputDirectoryOption);
rootCommand.Validators.Add(result =>
{
    if (string.IsNullOrWhiteSpace(result.GetValue(versionOption)))
    {
        result.AddError("Option '--git-version' is required.");
    }

    var hash = result.GetValue(expectedSha256Option);
    if (hash is null || hash.Length != 64 || hash.Any(static value => !char.IsAsciiHexDigit(value)))
    {
        result.AddError("Option '--expected-sha256' must be an exact 64-digit SHA-256 value.");
    }

    if (string.IsNullOrWhiteSpace(result.GetValue(outputDirectoryOption)))
    {
        result.AddError("Option '--output-directory' is required.");
    }
});
rootCommand.SetAction((parseResult, cancellationToken) => PrepareAsync(
    parseResult.GetValue(versionOption)!,
    parseResult.GetValue(expectedSha256Option)!.ToLowerInvariant(),
    parseResult.GetValue(outputDirectoryOption)!,
    cancellationToken));

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);

static async Task<int> PrepareAsync(
    string version,
    string expectedSha256,
    string outputDirectory,
    CancellationToken cancellationToken)
{
    if (OperatingSystem.IsWindows())
    {
        throw new PlatformNotSupportedException(
            "Pinned source-built Git compatibility toolchains require a Unix build host.");
    }

    if (!Version.TryParse(version, out var parsedVersion) || parsedVersion < new Version(2, 36))
    {
        throw new ArgumentException("The Git compatibility version must be 2.36 or newer.", nameof(version));
    }

    var outputRoot = Path.GetFullPath(outputDirectory, Directory.GetCurrentDirectory());
    var executable = Path.Combine(outputRoot, "bin", "git");
    var manifest = Path.Combine(outputRoot, "gitsail-git-toolchain.json");
    if (File.Exists(executable) && File.Exists(manifest))
    {
        using var existing = JsonDocument.Parse(
            await File.ReadAllTextAsync(manifest, cancellationToken).ConfigureAwait(false));
        if (string.Equals(
                existing.RootElement.GetProperty("version").GetString(),
                version,
                StringComparison.Ordinal) &&
            string.Equals(
                existing.RootElement.GetProperty("sourceSha256").GetString(),
                expectedSha256,
                StringComparison.Ordinal))
        {
            await ExposeGitBinAsync(outputRoot, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        throw new InvalidDataException(
            $"The existing Git toolchain at '{outputRoot}' does not match the requested input.");
    }

    if (Directory.Exists(outputRoot))
    {
        throw new InvalidDataException(
            $"The Git toolchain output directory already exists but is incomplete: {outputRoot}");
    }

    var temporaryRoot = Path.Combine(
        Path.GetTempPath(),
        $"gitsail-git-toolchain-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temporaryRoot);
    try
    {
        var archiveName = $"git-{version}.tar.xz";
        var archive = Path.Combine(temporaryRoot, archiveName);
        var sourceRoot = Path.Combine(temporaryRoot, "source");
        var installStagingRoot = Path.Combine(temporaryRoot, "install-staging");
        var stagedOutputRoot = Path.Combine(
            installStagingRoot,
            outputRoot.TrimStart(Path.DirectorySeparatorChar));
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(installStagingRoot);
        var sourceUri = new Uri(
            $"https://www.kernel.org/pub/software/scm/git/{archiveName}",
            UriKind.Absolute);
        await DownloadAsync(sourceUri, archive, cancellationToken).ConfigureAwait(false);

        await using (var archiveStream = File.OpenRead(archive))
        {
            var actualSha256 = Convert.ToHexString(
                await SHA256.HashDataAsync(archiveStream, cancellationToken).ConfigureAwait(false))
                .ToLowerInvariant();
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Git {version} source SHA-256 was {actualSha256}; expected {expectedSha256}.");
            }
        }

        await RunCheckedAsync(
            "tar",
            ["-xf", archive, "--directory", sourceRoot],
            temporaryRoot,
            cancellationToken).ConfigureAwait(false);
        var extractedRoot = Path.Combine(sourceRoot, $"git-{version}");
        if (!Directory.Exists(extractedRoot))
        {
            throw new InvalidDataException(
                $"The verified Git source archive did not contain 'git-{version}'.");
        }

        string[] commonMakeArguments =
        [
            $"prefix={outputRoot}",
            "NO_GETTEXT=YesPlease",
            "NO_TCLTK=YesPlease",
            "NO_OPENSSL=YesPlease",
            "NO_CURL=YesPlease",
            "NO_EXPAT=YesPlease",
            "NO_INSTALL_HARDLINKS=YesPlease",
        ];
        await RunCheckedAsync(
            "make",
            ["-j2", .. commonMakeArguments, "all"],
            extractedRoot,
            cancellationToken).ConfigureAwait(false);
        await RunCheckedAsync(
            "make",
            [$"DESTDIR={installStagingRoot}", .. commonMakeArguments, "install"],
            extractedRoot,
            cancellationToken).ConfigureAwait(false);
        var installedExecutable = Path.Combine(stagedOutputRoot, "bin", "git");
        if (!File.Exists(installedExecutable))
        {
            throw new FileNotFoundException(
                "The pinned Git build did not install its executable.",
                installedExecutable);
        }

        var versionOutput = await RunCheckedAsync(
            installedExecutable,
            ["--version"],
            stagedOutputRoot,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                versionOutput.StandardOutput.Trim(),
                $"git version {version}",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The built Git executable reported '{versionOutput.StandardOutput.Trim()}'.");
        }

        var manifestBuffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
            manifestBuffer,
            new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("version", version);
            writer.WriteString("source", sourceUri.AbsoluteUri);
            writer.WriteString("sourceSha256", expectedSha256);
            writer.WriteString("versionOutput", versionOutput.StandardOutput.Trim());
            writer.WriteEndObject();
        }

        var manifestBytes = new byte[manifestBuffer.WrittenCount + 1];
        manifestBuffer.WrittenSpan.CopyTo(manifestBytes);
        manifestBytes[^1] = (byte)'\n';
        await File.WriteAllBytesAsync(
            Path.Combine(stagedOutputRoot, "gitsail-git-toolchain.json"),
            manifestBytes,
            cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(outputRoot)!);
        Directory.Move(stagedOutputRoot, outputRoot);
        await ExposeGitBinAsync(outputRoot, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"Prepared Git {version} at {outputRoot}.");
        return 0;
    }
    finally
    {
        if (Directory.Exists(temporaryRoot))
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }
}

static async Task DownloadAsync(
    Uri sourceUri,
    string destinationPath,
    CancellationToken cancellationToken)
{
    using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    Exception? lastFailure = null;
    long? expectedLength = null;
    for (var attempt = 1; attempt <= 8; attempt++)
    {
        try
        {
            var offset = File.Exists(destinationPath)
                ? new FileInfo(destinationPath).Length
                : 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, sourceUri)
            {
                Version = System.Net.HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
            }

            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            {
                File.Delete(destinationPath);
                throw new InvalidDataException(
                    "The Git source server did not honor a resumable byte-range request.");
            }

            expectedLength = response.Content.Headers.ContentRange?.Length ??
                (offset == 0 ? response.Content.Headers.ContentLength : expectedLength);
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var destination = new FileStream(
                destinationPath,
                offset == 0 ? FileMode.CreateNew : FileMode.Append,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (expectedLength is { } totalLength && destination.Length != totalLength)
            {
                throw new InvalidDataException(
                    $"Downloaded {destination.Length:N0} bytes; expected {totalLength:N0} bytes.");
            }

            return;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or InvalidDataException)
        {
            lastFailure = exception;
            if (attempt == 8)
            {
                break;
            }

            await Task.Delay(
                TimeSpan.FromMilliseconds(250 * attempt),
                cancellationToken).ConfigureAwait(false);
        }
    }

    throw new InvalidOperationException(
        $"Could not download the pinned Git source from '{sourceUri}'.",
        lastFailure);
}

static async Task ExposeGitBinAsync(string outputRoot, CancellationToken cancellationToken)
{
    var binDirectory = Path.Combine(outputRoot, "bin");
    var githubPath = Environment.GetEnvironmentVariable("GITHUB_PATH");
    if (!string.IsNullOrWhiteSpace(githubPath))
    {
        await File.AppendAllTextAsync(
            githubPath,
            binDirectory + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);
    }
}

static async Task<(string StandardOutput, string StandardError)> RunCheckedAsync(
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
        throw new InvalidOperationException($"Could not start '{fileName}'.");
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
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        throw;
    }

    var output = await standardOutput.ConfigureAwait(false);
    var error = await standardError.ConfigureAwait(false);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"'{fileName}' exited with code {process.ExitCode}." + Environment.NewLine +
            output + Environment.NewLine + error);
    }

    return (output, error);
}
