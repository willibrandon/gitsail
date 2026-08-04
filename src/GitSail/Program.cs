using GitSail.CommandLine;
using GitSail.Domain;
using System.Collections.Immutable;

namespace GitSail;

/// <summary>
/// Provides the native application entry point.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        ImmutableArray<GitPath>? nativePathsAfterDoubleDash;
        try
        {
            nativePathsAfterDoubleDash = NativeArgumentReader.ReadPathsAfterDoubleDash(args);
        }
        catch (Exception exception) when (exception is InvalidDataException or
            IOException or UnauthorizedAccessException or PlatformNotSupportedException or
            DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        using var cancellationSource = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
        };

        Console.CancelKeyPress += cancelHandler;
        try
        {
            return await ApplicationHost.RunAsync(
                args,
                cancellationSource.Token,
                nativePathsAfterDoubleDash).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }
}
