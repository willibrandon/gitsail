#:package System.CommandLine

using System.Buffers.Binary;
using System.CommandLine;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

var ridOption = new Option<string?>("--rid")
{
    Description = "The expected Native AOT runtime identifier.",
    Arity = ArgumentArity.ExactlyOne,
};
var publishDirectoryOption = new Option<string?>("--publish-directory")
{
    Description = "The directory containing the published Native AOT executable.",
    Arity = ArgumentArity.ExactlyOne,
};
var rootCommand = new RootCommand("Runs a staged Native AOT payload and verifies its Doctor report.");
rootCommand.Options.Add(ridOption);
rootCommand.Options.Add(publishDirectoryOption);
rootCommand.Validators.Add(result =>
{
    if (string.IsNullOrWhiteSpace(result.GetValue(ridOption)))
    {
        result.AddError("Option '--rid' is required.");
    }

    if (string.IsNullOrWhiteSpace(result.GetValue(publishDirectoryOption)))
    {
        result.AddError("Option '--publish-directory' is required.");
    }
});
rootCommand.SetAction((parseResult, cancellationToken) => VerifyAsync(
    parseResult.GetValue(ridOption)!,
    parseResult.GetValue(publishDirectoryOption)!,
    cancellationToken));

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);

static async Task<int> VerifyAsync(
    string rid,
    string publishDirectory,
    CancellationToken cancellationToken)
{
    var workingDirectory = Directory.GetCurrentDirectory();
    var executableName = rid.StartsWith("win-", StringComparison.Ordinal) ? "git-tui.exe" : "git-tui";
    var executablePath = Path.Combine(
        Path.GetFullPath(publishDirectory, workingDirectory),
        executableName);
    if (!File.Exists(executablePath))
    {
        throw new FileNotFoundException("The Native AOT executable is missing.", executablePath);
    }

    if (rid.StartsWith("osx-", StringComparison.Ordinal))
    {
        await VerifyMacOSMachOAsync(executablePath, rid, cancellationToken).ConfigureAwait(false);
    }

    _ = await RunCheckedAsync(
        executablePath,
        ["--version"],
        workingDirectory,
        echoOutput: true,
        cancellationToken).ConfigureAwait(false);
    var doctorJson = await RunCheckedAsync(
        executablePath,
        ["doctor", "--json"],
        workingDirectory,
        echoOutput: false,
        cancellationToken).ConfigureAwait(false);

    using var document = JsonDocument.Parse(doctorJson);
    var root = document.RootElement;
    if (!root.TryGetProperty("nativeAot", out var nativeAot) || !nativeAot.GetBoolean())
    {
        throw new InvalidDataException("The staged executable does not identify itself as Native AOT.");
    }

    if (!root.TryGetProperty("runtimeIdentifier", out var runtimeIdentifier) ||
        !string.Equals(runtimeIdentifier.GetString(), rid, StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            $"The staged executable Doctor report does not match runtime identifier '{rid}'.");
    }

    return 0;
}

static async Task VerifyMacOSMachOAsync(
    string path,
    string rid,
    CancellationToken cancellationToken)
{
    const uint machO64LittleEndianMagic = 0xFEEDFACF;
    const uint x64CpuType = 0x01000007;
    const uint arm64CpuType = 0x0100000C;
    const uint uuidLoadCommand = 0x1B;
    const uint codeSignatureLoadCommand = 0x1D;
    const int headerSize = 32;
    const int uuidCommandSize = 24;

    var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    if (bytes.Length < headerSize ||
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, sizeof(uint))) !=
        machO64LittleEndianMagic)
    {
        throw new InvalidDataException(
            $"The staged macOS executable '{path}' is not a little-endian 64-bit Mach-O image.");
    }

    var cpuType = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, sizeof(uint)));
    var expectedCpuType = rid switch
    {
        "osx-x64" => x64CpuType,
        "osx-arm64" => arm64CpuType,
        _ => throw new InvalidDataException($"Unsupported macOS runtime identifier '{rid}'."),
    };
    if (cpuType != expectedCpuType)
    {
        throw new InvalidDataException(
            $"The staged macOS executable '{path}' does not match runtime identifier '{rid}'.");
    }

    var commandCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, sizeof(uint)));
    var commandByteCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20, sizeof(uint)));
    if (commandCount == 0 || commandCount > 100_000 ||
        commandByteCount > bytes.Length - headerSize)
    {
        throw new InvalidDataException(
            $"The staged macOS executable '{path}' has an invalid Mach-O load-command table.");
    }

    var commandOffset = headerSize;
    var commandEnd = checked(headerSize + (int)commandByteCount);
    var uuidCount = 0;
    var codeSignatureCount = 0;
    var codeSignatureOffset = 0;
    var codeSignatureSize = 0;
    for (var index = 0; index < commandCount; index++)
    {
        if (commandOffset + 8 > commandEnd)
        {
            throw new InvalidDataException(
                $"The staged macOS executable '{path}' has a truncated Mach-O load command.");
        }

        var command = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(commandOffset, sizeof(uint)));
        var commandSize = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(commandOffset + sizeof(uint), sizeof(uint)));
        if (commandSize < 8 || commandSize > commandEnd - commandOffset)
        {
            throw new InvalidDataException(
                $"The staged macOS executable '{path}' has an invalid Mach-O load-command size.");
        }

        if (command == uuidLoadCommand)
        {
            if (commandSize != uuidCommandSize ||
                bytes.AsSpan(commandOffset + 8, 16).IndexOfAnyExcept((byte)0) < 0)
            {
                throw new InvalidDataException(
                    $"The staged macOS executable '{path}' has an invalid LC_UUID command.");
            }

            uuidCount++;
        }
        else if (command == codeSignatureLoadCommand)
        {
            if (commandSize != 16)
            {
                throw new InvalidDataException(
                    $"The staged macOS executable '{path}' has an invalid LC_CODE_SIGNATURE command.");
            }

            codeSignatureCount++;
            codeSignatureOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(commandOffset + 8, sizeof(uint))));
            codeSignatureSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(commandOffset + 12, sizeof(uint))));
        }

        commandOffset += checked((int)commandSize);
    }

    if (commandOffset != commandEnd || uuidCount != 1 ||
        (rid == "osx-x64" && codeSignatureCount != 0) ||
        (rid == "osx-arm64" && codeSignatureCount != 1))
    {
        throw new InvalidDataException(
            $"The staged macOS executable '{path}' has unexpected UUID or code-signature load commands.");
    }

    if (codeSignatureCount == 1)
    {
        ValidateAnonymousLinkerSignature(
            bytes,
            codeSignatureOffset,
            codeSignatureSize,
            path);
    }
}

static void ValidateAnonymousLinkerSignature(
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
            $"The staged macOS executable '{description}' has an invalid embedded signature range.");
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
            $"The staged macOS executable '{description}' does not contain one anonymous CodeDirectory.");
    }

    var codeDirectory = signature.Slice(superBlobHeaderSize, superBlobLength - superBlobHeaderSize);
    if (codeDirectory.Length < codeDirectoryHeaderSize)
    {
        throw new InvalidDataException(
            $"The staged macOS executable '{description}' has a truncated CodeDirectory.");
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
            $"The staged macOS executable '{description}' is not certificate-free linker metadata.");
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
            $"The staged macOS executable '{description}' has invalid CodeDirectory bounds.");
    }

    Span<byte> pageHash = stackalloc byte[sha256HashSize];
    for (var index = 0; index < codeSlotCount; index++)
    {
        var pageOffset = checked(index * pageSize);
        var pageLength = Math.Min(pageSize, codeLimit - pageOffset);
        if (!SHA256.TryHashData(bytes.AsSpan(pageOffset, pageLength), pageHash, out var bytesWritten) ||
            bytesWritten != sha256HashSize ||
            !pageHash.SequenceEqual(codeDirectory.Slice(hashOffset + index * sha256HashSize, sha256HashSize)))
        {
            throw new InvalidDataException(
                $"The staged macOS executable '{description}' has an invalid linker CodeDirectory hash.");
        }
    }
}

static async Task<string> RunCheckedAsync(
    string fileName,
    IReadOnlyList<string> arguments,
    string workingDirectory,
    bool echoOutput,
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
