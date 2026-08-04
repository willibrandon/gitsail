using GitSail.Domain;
using System.Collections.Immutable;

namespace GitSail.CommandLine;

/// <summary>
/// Reads direct path operands from the operating system's lossless process argument representation.
/// </summary>
internal static class NativeArgumentReader
{
    private const int MaximumArgumentCount = 1024 * 1024;
    private const int MaximumCommandLineBytes = 64 * 1024 * 1024;

    /// <summary>
    /// Reads every process argument following <c>--</c> as an exact native Git path.
    /// </summary>
    /// <param name="managedArguments">The managed arguments supplied to the application entry point.</param>
    /// <returns><see langword="null"/> when no terminator was supplied; otherwise the exact trailing paths.</returns>
    internal static ImmutableArray<GitPath>? ReadPathsAfterDoubleDash(string[] managedArguments)
    {
        ArgumentNullException.ThrowIfNull(managedArguments);
        var delimiterIndex = Array.FindIndex(
            managedArguments,
            static argument => string.Equals(argument, "--", StringComparison.Ordinal));
        if (delimiterIndex < 0)
        {
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            var paths = ImmutableArray.CreateBuilder<GitPath>(managedArguments.Length - delimiterIndex - 1);
            for (var index = delimiterIndex + 1; index < managedArguments.Length; index++)
            {
                try
                {
                    paths.Add(GitPath.FromWindowsPath(managedArguments[index]));
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidDataException("A path after '--' is empty or contains NUL.", exception);
                }
            }

            return paths.ToImmutable();
        }

        if (OperatingSystem.IsLinux())
        {
            return ParseUnixPathsAfterDoubleDash(
                ReadLinuxCommandLine(),
                managedArguments.Length,
                delimiterIndex);
        }

        if (OperatingSystem.IsMacOS())
        {
            return ExtractUnixPaths(
                ReadMacArguments(),
                managedArguments.Length,
                delimiterIndex);
        }

        throw new PlatformNotSupportedException(
            "Lossless direct path arguments are not supported on this operating system; use --pathspec-from-file=- --pathspec-file-nul.");
    }

    /// <summary>
    /// Parses a complete Linux-style NUL-delimited process command line into exact trailing paths.
    /// </summary>
    /// <param name="commandLine">The complete native command line including executable argument zero.</param>
    /// <param name="expectedManagedArgumentCount">The managed argument count excluding the executable.</param>
    /// <param name="delimiterIndex">The zero-based managed position of <c>--</c>.</param>
    /// <returns>The exact native path records following the terminator.</returns>
    internal static ImmutableArray<GitPath> ParseUnixPathsAfterDoubleDash(
        ReadOnlySpan<byte> commandLine,
        int expectedManagedArgumentCount,
        int delimiterIndex)
    {
        if (commandLine.Length > MaximumCommandLineBytes)
        {
            throw new InvalidDataException(
                $"The native process command line exceeds the {MaximumCommandLineBytes} byte limit.");
        }

        if (commandLine.IsEmpty || commandLine[^1] != 0)
        {
            throw new InvalidDataException("The native process command line is incomplete.");
        }

        var arguments = ImmutableArray.CreateBuilder<byte[]>();
        while (!commandLine.IsEmpty)
        {
            var terminator = commandLine.IndexOf((byte)0);
            if (terminator < 0)
            {
                throw new InvalidDataException("The native process command line ended before an argument terminator.");
            }

            arguments.Add(commandLine[..terminator].ToArray());
            if (arguments.Count > MaximumArgumentCount)
            {
                throw new InvalidDataException(
                    $"The native process argument count exceeds the {MaximumArgumentCount} record limit.");
            }

            commandLine = commandLine[(terminator + 1)..];
        }

        return ExtractUnixPaths(
            arguments.ToImmutable(),
            expectedManagedArgumentCount,
            delimiterIndex);
    }

    private static byte[] ReadLinuxCommandLine()
    {
        try
        {
            using var stream = new FileStream(
                "/proc/self/cmdline",
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            using var contents = new MemoryStream();
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var count = stream.Read(buffer);
                if (count == 0)
                {
                    return contents.ToArray();
                }

                if (contents.Length + count > MaximumCommandLineBytes)
                {
                    throw new InvalidDataException(
                        $"The native process command line exceeds the {MaximumCommandLineBytes} byte limit.");
                }

                contents.Write(buffer, 0, count);
            }
        }
        catch (FileNotFoundException exception)
        {
            throw new InvalidDataException(
                "Lossless direct path arguments require /proc/self/cmdline on Linux; use --pathspec-from-file=- --pathspec-file-nul.",
                exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new InvalidDataException(
                "Lossless direct path arguments require /proc/self/cmdline on Linux; use --pathspec-from-file=- --pathspec-file-nul.",
                exception);
        }
    }

    private static unsafe ImmutableArray<byte[]> ReadMacArguments()
    {
        var countPointer = MacNativeCommandLine.GetArgumentCount();
        var vectorPointer = MacNativeCommandLine.GetArgumentVector();
        if (countPointer is null || vectorPointer is null || *vectorPointer is null)
        {
            throw new InvalidDataException("macOS did not provide the native process argument vector.");
        }

        var count = *countPointer;
        if (count <= 0 || count > MaximumArgumentCount)
        {
            throw new InvalidDataException("macOS returned an invalid native process argument count.");
        }

        var arguments = ImmutableArray.CreateBuilder<byte[]>(count);
        var totalBytes = 0;
        var vector = *vectorPointer;
        for (var index = 0; index < count; index++)
        {
            var argument = vector[index];
            if (argument is null)
            {
                throw new InvalidDataException("macOS returned a null native process argument.");
            }

            var length = 0;
            while (argument[length] != 0)
            {
                length++;
                totalBytes++;
                if (totalBytes > MaximumCommandLineBytes)
                {
                    throw new InvalidDataException(
                        $"The native process command line exceeds the {MaximumCommandLineBytes} byte limit.");
                }
            }

            arguments.Add(new ReadOnlySpan<byte>(argument, length).ToArray());
            totalBytes++;
            if (totalBytes > MaximumCommandLineBytes)
            {
                throw new InvalidDataException(
                    $"The native process command line exceeds the {MaximumCommandLineBytes} byte limit.");
            }
        }

        return arguments.ToImmutable();
    }

    private static ImmutableArray<GitPath> ExtractUnixPaths(
        ImmutableArray<byte[]> arguments,
        int expectedManagedArgumentCount,
        int delimiterIndex)
    {
        if (expectedManagedArgumentCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedManagedArgumentCount));
        }

        if ((uint)delimiterIndex >= (uint)expectedManagedArgumentCount)
        {
            throw new ArgumentOutOfRangeException(nameof(delimiterIndex));
        }

        var managedTailCount = expectedManagedArgumentCount - delimiterIndex;
        if (arguments.Length < managedTailCount + 1)
        {
            throw new InvalidDataException(
                "The native process argument vector is shorter than the managed path-bearing command; use --pathspec-from-file=- --pathspec-file-nul.");
        }

        var nativeDelimiterIndex = arguments.Length - managedTailCount;
        if (!arguments[nativeDelimiterIndex].AsSpan().SequenceEqual("--"u8))
        {
            throw new InvalidDataException(
                "The native and managed process argument boundaries do not match; use --pathspec-from-file=- --pathspec-file-nul.");
        }

        var paths = ImmutableArray.CreateBuilder<GitPath>(arguments.Length - nativeDelimiterIndex - 1);
        for (var index = nativeDelimiterIndex + 1; index < arguments.Length; index++)
        {
            try
            {
                paths.Add(GitPath.FromUnixBytes(arguments[index]));
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("A path after '--' is empty or contains NUL.", exception);
            }
        }

        return paths.ToImmutable();
    }
}
