using System.Security.Cryptography;
using System.Text;

namespace GitSail.Git.Execution;

/// <summary>
/// Reads a helper response only from the platform controlling terminal when no parent is available.
/// </summary>
internal static unsafe class CredentialPromptTerminal
{
    private const int MaximumResponseBytes = 64 * 1024;
    private const uint EchoInput = 0x0004;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Reads one bounded response without ever falling back to ordinary standard input.
    /// </summary>
    /// <param name="prompt">The untrusted helper prompt.</param>
    /// <param name="kind">The required response treatment.</param>
    /// <returns>Owned UTF-8 response bytes, or <see langword="null"/> when no terminal is available.</returns>
    internal static byte[]? Read(string prompt, CredentialPromptKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        try
        {
            return OperatingSystem.IsWindows()
                ? ReadWindows(prompt, kind)
                : ReadUnix(prompt, kind);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or DecoderFallbackException)
        {
            return null;
        }
    }

    private static byte[]? ReadUnix(string prompt, CredentialPromptKind kind)
    {
        using var terminal = new FileStream(
            "/dev/tty",
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite);
        var fileDescriptor = terminal.SafeFileHandle.DangerousGetHandle().ToInt32();
        Span<byte> originalAttributes = stackalloc byte[256];
        Span<byte> modifiedAttributes = stackalloc byte[256];
        var echoChanged = false;
        if (kind == CredentialPromptKind.Secret)
        {
            fixed (byte* originalPointer = originalAttributes)
            {
                if (UnixNative.GetTerminalAttributes(fileDescriptor, originalPointer) != 0)
                {
                    return null;
                }
            }

            originalAttributes.CopyTo(modifiedAttributes);
            fixed (byte* modifiedPointer = modifiedAttributes)
            {
                if (OperatingSystem.IsMacOS())
                {
                    *(ulong*)(modifiedPointer + 24) &= ~8UL;
                }
                else
                {
                    *(uint*)(modifiedPointer + 12) &= ~8U;
                }

                if (UnixNative.SetTerminalAttributes(fileDescriptor, actions: 0, modifiedPointer) != 0)
                {
                    return null;
                }
            }

            echoChanged = true;
        }

        try
        {
            var promptBytes = s_strictUtf8.GetBytes(FormatPrompt(prompt, kind));
            terminal.Write(promptBytes);
            terminal.Flush();
            var buffer = new byte[MaximumResponseBytes];
            var count = 0;
            try
            {
                while (count < buffer.Length)
                {
                    var value = terminal.ReadByte();
                    if (value < 0)
                    {
                        return null;
                    }

                    if (value is '\r' or '\n')
                    {
                        var response = buffer.AsSpan(0, count).ToArray();
                        return response;
                    }

                    buffer[count++] = (byte)value;
                }

                throw new InvalidDataException(
                    $"A credential response cannot exceed {MaximumResponseBytes} bytes.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
                CryptographicOperations.ZeroMemory(promptBytes);
            }
        }
        finally
        {
            if (echoChanged)
            {
                fixed (byte* originalPointer = originalAttributes)
                {
                    _ = UnixNative.SetTerminalAttributes(fileDescriptor, actions: 0, originalPointer);
                }

                terminal.WriteByte((byte)'\n');
                terminal.Flush();
            }
        }
    }

    private static byte[]? ReadWindows(string prompt, CredentialPromptKind kind)
    {
        using var input = WindowsNative.CreateFile(
            "CONIN$",
            GenericRead | GenericWrite,
            ShareRead | ShareWrite,
            securityAttributes: 0,
            OpenExisting,
            flagsAndAttributes: 0,
            templateFile: 0);
        using var output = WindowsNative.CreateFile(
            "CONOUT$",
            GenericRead | GenericWrite,
            ShareRead | ShareWrite,
            securityAttributes: 0,
            OpenExisting,
            flagsAndAttributes: 0,
            templateFile: 0);
        if (input.IsInvalid || output.IsInvalid ||
            WindowsNative.GetConsoleMode(input, out var originalMode) == 0)
        {
            return null;
        }

        var echoChanged = kind == CredentialPromptKind.Secret;
        if (echoChanged && WindowsNative.SetConsoleMode(input, originalMode & ~EchoInput) == 0)
        {
            return null;
        }

        var characters = new char[MaximumResponseBytes];
        var count = 0;
        try
        {
            WriteWindows(output, FormatPrompt(prompt, kind));
            var chunk = new char[256];
            fixed (char* chunkPointer = chunk)
            {
                while (count < characters.Length)
                {
                    if (WindowsNative.ReadConsole(
                            input,
                            chunkPointer,
                            (uint)chunk.Length,
                            out var charactersRead,
                            inputControl: 0) == 0 ||
                        charactersRead == 0)
                    {
                        return null;
                    }

                    for (var index = 0; index < charactersRead; index++)
                    {
                        if (chunk[index] is '\r' or '\n')
                        {
                            var responseCharacters = characters.AsSpan(0, count);
                            var byteCount = s_strictUtf8.GetByteCount(responseCharacters);
                            if (byteCount > MaximumResponseBytes)
                            {
                                throw new InvalidDataException(
                                    $"A credential response cannot exceed {MaximumResponseBytes} bytes.");
                            }

                            var response = new byte[byteCount];
                            _ = s_strictUtf8.GetBytes(responseCharacters, response);
                            return response;
                        }

                        if (count == characters.Length)
                        {
                            throw new InvalidDataException(
                                $"A credential response cannot exceed {MaximumResponseBytes} characters.");
                        }

                        characters[count++] = chunk[index];
                    }
                }
            }

            throw new InvalidDataException(
                $"A credential response cannot exceed {MaximumResponseBytes} characters.");
        }
        finally
        {
            Array.Clear(characters);
            if (echoChanged)
            {
                _ = WindowsNative.SetConsoleMode(input, originalMode);
                WriteWindows(output, Environment.NewLine);
            }
        }
    }

    private static string FormatPrompt(string prompt, CredentialPromptKind kind)
    {
        var safePrompt = CredentialPromptTextSanitizer.Sanitize(prompt);
        var suffix = kind == CredentialPromptKind.Confirmation ? " [yes/no] " : " ";
        return safePrompt + suffix;
    }

    private static void WriteWindows(Microsoft.Win32.SafeHandles.SafeFileHandle output, string value)
    {
        fixed (char* valuePointer = value)
        {
            if (WindowsNative.WriteConsole(
                    output,
                    valuePointer,
                    (uint)value.Length,
                    out _,
                    reserved: 0) == 0)
            {
                throw new IOException("The controlling console could not display a credential prompt.");
            }
        }
    }
}
