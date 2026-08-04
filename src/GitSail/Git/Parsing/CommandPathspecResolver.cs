using GitSail.Domain;
using System.Collections.Immutable;
using System.Text;

namespace GitSail.Git.Parsing;

/// <summary>
/// Resolves managed command operands and optional file input into exact native Git paths.
/// </summary>
internal static class CommandPathspecResolver
{
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Resolves direct operands followed by every record from the optional pathspec file.
    /// </summary>
    /// <param name="paths">The direct managed command-line operands.</param>
    /// <param name="nativePaths">The exact native operands following <c>--</c>, when present.</param>
    /// <param name="pathspecFile">The optional pathspec input file or <c>-</c>.</param>
    /// <param name="pathspecFileNul">Whether file records must be NUL-delimited.</param>
    /// <param name="cancellationToken">Signals bounded file-input cancellation.</param>
    /// <returns>The ordered native path collection.</returns>
    internal static async Task<ImmutableArray<GitPath>> ResolveAsync(
        ImmutableArray<string> paths,
        ImmutableArray<GitPath>? nativePaths,
        string? pathspecFile,
        bool pathspecFileNul,
        CancellationToken cancellationToken)
    {
        var result = (nativePaths ?? Convert(paths)).ToBuilder();
        if (pathspecFile is not null)
        {
            result.AddRange(await PathspecFileReader.ReadAsync(
                pathspecFile,
                pathspecFileNul,
                cancellationToken).ConfigureAwait(false));
        }

        return result.ToImmutable();
    }

    /// <summary>
    /// Converts managed command operands to the platform-native Git path representation.
    /// </summary>
    /// <param name="paths">The managed path operands.</param>
    /// <returns>The ordered native path collection.</returns>
    internal static ImmutableArray<GitPath> Convert(ImmutableArray<string> paths)
    {
        if (paths.IsDefaultOrEmpty)
        {
            return [];
        }

        return OperatingSystem.IsWindows()
            ? [.. paths.Select(GitPath.FromWindowsPath)]
            : [.. paths.Select(path => GitPath.FromUnixBytes(s_strictUtf8.GetBytes(path)))];
    }
}
